using System.Diagnostics;
using System.Text.Json;
using GodMode.Voice.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace GodMode.Voice.AI;

/// <summary>
/// Language model that runs on AMD Ryzen AI NPU via ONNX Runtime with VitisAI Execution Provider.
/// Falls back to DirectML (iGPU) then CPU if NPU is unavailable.
/// Uses Qwen2.5-0.5B-Instruct (INT8 quantized ONNX) with manual generation loop.
/// </summary>
public sealed class NpuOnnxModel : ILanguageModel, IDisposable
{
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private readonly InferenceConfig _config;
    private string _activeProvider = "unknown";

    public bool IsLoaded => _session is not null && _tokenizer is not null;
    public string ActiveProvider => _activeProvider;

    // Qwen2.5 special tokens
    private const string ImStart = "<|im_start|>";
    private const string ImEnd = "<|im_end|>";
    private const string EndOfText = "<|endoftext|>";

    public NpuOnnxModel()
    {
        _config = InferenceConfig.Load();
    }

    public async Task InitializeAsync(string modelPath)
    {
        if (IsLoaded) return;

        var fullPath = Path.GetFullPath(modelPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Model directory not found: {fullPath}");

        var modelFile = FindModelFile(fullPath);
        if (modelFile is null)
            throw new FileNotFoundException($"No .onnx model file found in: {fullPath}");

        var tokenizerFile = Path.Combine(fullPath, "tokenizer.json");
        if (!File.Exists(tokenizerFile))
            throw new FileNotFoundException($"tokenizer.json not found in: {fullPath}");

        await Task.Run(() =>
        {
            _tokenizer = LoadTokenizer(tokenizerFile);
            _session = CreateSession(modelFile);
        });
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        if (_session is null || _tokenizer is null)
            throw new InvalidOperationException("Model not loaded. Call InitializeAsync first.");

        var prompt = FormatChatPrompt(systemPrompt, userMessage);
        var inputIds = Encode(prompt);

        return await Task.Run(() => Generate(inputIds, ct), ct);
    }

    private string Generate(IReadOnlyList<int> promptTokenIds, CancellationToken ct)
    {
        Debug.Assert(_session is not null && _tokenizer is not null);

        var maxTokens = _config.MaxTokens;
        var tokens = new List<int>(promptTokenIds);
        var generatedTokens = new List<int>();

        var eosTokenId = GetTokenId(EndOfText);
        var imEndTokenId = GetTokenId(ImEnd);

        for (var i = 0; i < maxTokens; i++)
        {
            ct.ThrowIfCancellationRequested();

            var nextToken = RunInference(tokens);

            if (nextToken == eosTokenId || nextToken == imEndTokenId)
                break;

            tokens.Add(nextToken);
            generatedTokens.Add(nextToken);
        }

        return Decode(generatedTokens);
    }

    private int RunInference(List<int> tokens)
    {
        Debug.Assert(_session is not null);

        var seqLen = tokens.Count;
        var inputIdsTensor = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMaskTensor = new DenseTensor<long>(new[] { 1, seqLen });

        for (var i = 0; i < seqLen; i++)
        {
            inputIdsTensor[0, i] = tokens[i];
            attentionMaskTensor[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        using var results = _session.Run(inputs);
        var logits = results.First(r => r.Name == "logits").AsTensor<float>();

        // Get logits for the last token position
        var vocabSize = (int)logits.Dimensions[2];
        var lastTokenLogits = new float[vocabSize];
        for (var v = 0; v < vocabSize; v++)
            lastTokenLogits[v] = logits[0, seqLen - 1, v];

        return SampleToken(lastTokenLogits);
    }

    private int SampleToken(float[] logits)
    {
        var temperature = (float)_config.Temperature;

        if (temperature <= 0.01f)
            return ArgMax(logits);

        // Temperature scaling
        for (var i = 0; i < logits.Length; i++)
            logits[i] /= temperature;

        // Softmax
        var maxLogit = logits.Max();
        var expSum = 0.0f;
        for (var i = 0; i < logits.Length; i++)
        {
            logits[i] = MathF.Exp(logits[i] - maxLogit);
            expSum += logits[i];
        }
        for (var i = 0; i < logits.Length; i++)
            logits[i] /= expSum;

        // Top-p (nucleus) sampling
        return TopPSample(logits, 0.9f);
    }

    private static int TopPSample(float[] probs, float topP)
    {
        var indices = Enumerable.Range(0, probs.Length)
            .OrderByDescending(i => probs[i])
            .ToArray();

        var cumulative = 0.0f;
        var cutoff = -1;
        for (var i = 0; i < indices.Length; i++)
        {
            cumulative += probs[indices[i]];
            if (cumulative >= topP)
            {
                cutoff = i;
                break;
            }
        }

        if (cutoff < 0) cutoff = indices.Length - 1;

        // Renormalize
        var sum = 0.0f;
        for (var i = 0; i <= cutoff; i++)
            sum += probs[indices[i]];

        var r = Random.Shared.NextSingle() * sum;
        var acc = 0.0f;
        for (var i = 0; i <= cutoff; i++)
        {
            acc += probs[indices[i]];
            if (acc >= r)
                return indices[i];
        }

        return indices[0];
    }

    private static int ArgMax(float[] values)
    {
        var maxIdx = 0;
        var maxVal = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > maxVal)
            {
                maxVal = values[i];
                maxIdx = i;
            }
        }
        return maxIdx;
    }

    private string FormatChatPrompt(string systemPrompt, string userMessage) =>
        $"{ImStart}system\n{systemPrompt}{ImEnd}\n{ImStart}user\n{userMessage}{ImEnd}\n{ImStart}assistant\n";

    private IReadOnlyList<int> Encode(string text)
    {
        Debug.Assert(_tokenizer is not null);
        var encoded = _tokenizer.EncodeToIds(text);
        return encoded;
    }

    private string Decode(IReadOnlyList<int> tokenIds)
    {
        Debug.Assert(_tokenizer is not null);
        return _tokenizer.Decode(tokenIds) ?? string.Empty;
    }

    private int GetTokenId(string token)
    {
        Debug.Assert(_tokenizer is not null);
        var encoded = _tokenizer.EncodeToIds(token);
        return encoded.Count > 0 ? encoded[0] : -1;
    }

    private static Tokenizer LoadTokenizer(string tokenizerJsonPath)
    {
        var dir = Path.GetDirectoryName(tokenizerJsonPath)!;
        var vocabPath = Path.Combine(dir, "vocab.json");
        var mergesPath = Path.Combine(dir, "merges.txt");

        // If separate vocab.json and merges.txt exist, use them directly
        if (File.Exists(vocabPath) && File.Exists(mergesPath))
        {
            using var vocabStream = File.OpenRead(vocabPath);
            using var mergesStream = File.OpenRead(mergesPath);
            return BpeTokenizer.Create(vocabStream, mergesStream);
        }

        // Otherwise, extract vocab and merges from the unified tokenizer.json
        return LoadTokenizerFromJson(tokenizerJsonPath);
    }

    private static Tokenizer LoadTokenizerFromJson(string tokenizerJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");

        // Extract vocab to a temporary stream
        var vocabObj = model.GetProperty("vocab");
        using var vocabStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(vocabStream))
        {
            vocabObj.WriteTo(writer);
        }
        vocabStream.Position = 0;

        // Extract merges to a temporary stream (one merge per line)
        var mergesArr = model.GetProperty("merges");
        using var mergesStream = new MemoryStream();
        using (var sw = new StreamWriter(mergesStream, leaveOpen: true))
        {
            // BPE merges format: first line is a header comment
            sw.WriteLine("#version: 0.2");
            foreach (var merge in mergesArr.EnumerateArray())
                sw.WriteLine(merge.GetString());
        }
        mergesStream.Position = 0;

        return BpeTokenizer.Create(vocabStream, mergesStream);
    }

    private InferenceSession CreateSession(string modelFile)
    {
        var options = new SessionOptions();
        var provider = _config.ExecutionProvider?.ToLowerInvariant() ?? "auto";

        if (provider is "npu" or "auto")
        {
            if (TryAppendVitisAI(options, modelFile))
            {
                _activeProvider = "VitisAI (NPU)";
                return new InferenceSession(modelFile, options);
            }

            if (provider == "npu")
                throw new InvalidOperationException(
                    "NPU (VitisAI) execution provider not available. " +
                    "Install AMD Ryzen AI Software: https://www.amd.com/en/developer/resources/ryzen-ai-software.html");
        }

        if (provider is "directml" or "auto")
        {
            try
            {
                options.AppendExecutionProvider_DML();
                _activeProvider = "DirectML (GPU)";
                return new InferenceSession(modelFile, options);
            }
            catch
            {
                if (provider == "directml")
                    throw;
                options = new SessionOptions(); // Reset for CPU fallback
            }
        }

        _activeProvider = "CPU";
        return new InferenceSession(modelFile, options);
    }

    private static bool TryAppendVitisAI(SessionOptions options, string modelFile)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetDirectoryName(modelFile)!, ".vaip_cache");
            Directory.CreateDirectory(cacheDir);

            var providerOptions = new Dictionary<string, string>
            {
                ["target"] = "X1", // Phoenix/Hawk Point NPU target
                ["cache_dir"] = cacheDir,
                ["cache_key"] = "npu_model",
                ["enable_cache_file_io_in_mem"] = "0"
            };

            options.AppendExecutionProvider("VitisAI", providerOptions);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindModelFile(string directory)
    {
        // Prefer quantized model, then any .onnx file
        var quantized = Path.Combine(directory, "model_quantized.onnx");
        if (File.Exists(quantized)) return quantized;

        var standard = Path.Combine(directory, "model.onnx");
        if (File.Exists(standard)) return standard;

        return Directory.GetFiles(directory, "*.onnx").FirstOrDefault();
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}

using System.Diagnostics;
using System.Globalization;
using GodMode.Voice.AI;
using GodMode.Voice.Speech;
using GodMode.Voice.Tools;

namespace GodMode.Voice.Services;

public static class AssistantLog
{
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode", "logs");

    private static readonly string _logPath = Path.Combine(
        _logDir, $"inference-{DateTime.Now:yyyy-MM-dd}.log");

    public static string LogPath => _logPath;

    public static void Write(string label, string message)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var entry = $"[{timestamp}] [{label}]\n{message}\n{"---"}\n";
            File.AppendAllText(_logPath, entry);
        }
        catch
        {
            // Don't let logging failures break the app
        }
    }
}

public sealed class AssistantService
{
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ILanguageModel _model;
    private readonly ToolRegistry _toolRegistry;

    // Multi-turn parameter collection state
    private ToolCall? _pendingToolCall;
    private ITool? _pendingTool;
    private Queue<ToolParameter>? _missingParams;

    public event EventHandler<string>? TranscriptUpdated;
    public event EventHandler<string>? ResponseReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public bool IsModelLoaded => _model.IsLoaded;
    public bool IsCollectingParams => _pendingToolCall is not null;

    public AssistantService(
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer synthesizer,
        ILanguageModel model,
        ToolRegistry toolRegistry)
    {
        _recognizer = recognizer;
        _synthesizer = synthesizer;
        _model = model;
        _toolRegistry = toolRegistry;
    }

    public async Task InitializeModelAsync(string modelPath)
    {
        StatusChanged?.Invoke(this, "Loading AI model...");
        await _model.InitializeAsync(modelPath);
        GeneralStatusTool.SetModelsLoaded(1);
        StatusChanged?.Invoke(this, "Model loaded.");
    }

    public async Task<string> ListenAndProcessAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        StatusChanged?.Invoke(this, "Listening...");
        var transcript = await _recognizer.RecognizeSpeechAsync(ct);
        var sttMs = sw.ElapsedMilliseconds;

        if (string.IsNullOrWhiteSpace(transcript))
        {
            StatusChanged?.Invoke(this, $"No speech detected. [STT: {sttMs}ms]");
            return string.Empty;
        }

        TranscriptUpdated?.Invoke(this, transcript);
        AssistantLog.Write("TIMING", $"STT: {sttMs}ms");

        return await ProcessTextInternalAsync(transcript, ct, sttMs);
    }

    public Task<string> ProcessTextAsync(string userText, CancellationToken ct = default)
        => ProcessTextInternalAsync(userText, ct, sttMs: null);

    private async Task<string> ProcessTextInternalAsync(string userText, CancellationToken ct, long? sttMs)
    {
        try
        {
            // If we're collecting parameters for a pending tool call
            if (_pendingToolCall is not null)
                return await ContinueParameterCollectionAsync(userText, ct);

            AssistantLog.Write("USER", userText);

            // Generate AI response
            StatusChanged?.Invoke(this, "Thinking...");
            var sw = Stopwatch.StartNew();
            var systemPrompt = SystemPromptBuilder.Build(_toolRegistry);
            AssistantLog.Write("SYSTEM_PROMPT", systemPrompt);

            var llmOutput = await Task.Run(() => _model.GenerateAsync(systemPrompt, userText, ct));
            var inferenceMs = sw.ElapsedMilliseconds;
            AssistantLog.Write("LLM_RAW", llmOutput);
            AssistantLog.Write("TIMING", $"Inference: {inferenceMs}ms");

            // Parse for tool calls
            var toolCalls = ToolCallParser.Parse(llmOutput);
            AssistantLog.Write("PARSE", toolCalls.Count > 0
                ? $"Tool calls: {string.Join(", ", toolCalls.Select(t => t.ToolName))}"
                : "No tool calls parsed — fallback to raw output");

            string response;

            if (toolCalls.Count > 0)
            {
                var call = toolCalls[0];

                // Validate parameters (skip for respond tool)
                if (call.ToolName != "respond")
                {
                    var tool = _toolRegistry.GetTool(call.ToolName);
                    if (tool is not null)
                    {
                        var missingRequired = tool.Parameters
                            .Where(p => p.Required && !call.Arguments.ContainsKey(p.Name))
                            .ToList();
                        var missingOptional = tool.Parameters
                            .Where(p => !p.Required && !call.Arguments.ContainsKey(p.Name))
                            .ToList();

                        if (missingRequired.Count > 0 || missingOptional.Count > 0)
                        {
                            _pendingToolCall = call;
                            _pendingTool = tool;
                            _missingParams = new Queue<ToolParameter>(
                                missingRequired.Concat(missingOptional));

                            var param = _missingParams.Peek();
                            var askMessage = FormatParamQuestion(param, isFirst: true);

                            AssistantLog.Write("PARAM_ASK",
                                $"Required: {string.Join(", ", missingRequired.Select(p => p.Name))}. " +
                                $"Optional: {string.Join(", ", missingOptional.Select(p => p.Name))}. " +
                                $"Asking for: {param.Name}");

                            ResponseReceived?.Invoke(this, askMessage);
                            StatusChanged?.Invoke(this,
                                $"Waiting for input... {FormatTiming(sttMs, inferenceMs, null, null)}");
                            await _synthesizer.SpeakAsync(askMessage, ct);
                            return askMessage;
                        }
                    }
                }

                // Execute tool
                sw.Restart();
                StatusChanged?.Invoke(this, "Executing...");
                var results = await _toolRegistry.ExecuteAsync(toolCalls);
                var toolMs = sw.ElapsedMilliseconds;

                response = FormatToolResults(results);
                response = TimeFormatter.HumanizeTimestamps(response);
                response = TimeFormatter.HumanizeUptimeStrings(response);
                AssistantLog.Write("TOOL_RESULT", response);
                AssistantLog.Write("TIMING", $"Tool exec: {toolMs}ms");

                // Show result immediately, then speak
                ResponseReceived?.Invoke(this, response);

                sw.Restart();
                StatusChanged?.Invoke(this, "Speaking...");
                await _synthesizer.SpeakAsync(response, ct);
                var ttsMs = sw.ElapsedMilliseconds;

                StatusChanged?.Invoke(this, $"Ready. {FormatTiming(sttMs, inferenceMs, toolMs, ttsMs)}");
                AssistantLog.Write("TIMING",
                    $"Total: {(sttMs ?? 0) + inferenceMs + toolMs + ttsMs}ms | {FormatTiming(sttMs, inferenceMs, toolMs, ttsMs)}");
            }
            else
            {
                // Fallback: model didn't produce a tool call
                response = llmOutput.Trim();
                AssistantLog.Write("FALLBACK", "Model did not produce a tool call. Using raw output.");

                ResponseReceived?.Invoke(this, response);

                sw.Restart();
                StatusChanged?.Invoke(this, "Speaking...");
                await _synthesizer.SpeakAsync(response, ct);
                var ttsMs = sw.ElapsedMilliseconds;

                StatusChanged?.Invoke(this, $"Ready. {FormatTiming(sttMs, inferenceMs, null, ttsMs)}");
            }

            AssistantLog.Write("RESPONSE", response);
            return response;
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(this, "Cancelled.");
            return string.Empty;
        }
        catch (Exception ex)
        {
            var error = $"Error: {ex.Message}";
            AssistantLog.Write("ERROR", error);
            ErrorOccurred?.Invoke(this, error);
            StatusChanged?.Invoke(this, "Error occurred.");
            return error;
        }
    }

    private async Task<string> ContinueParameterCollectionAsync(string userText, CancellationToken ct)
    {
        try
        {
            AssistantLog.Write("USER", $"[param input] {userText}");

            // Check for cancellation phrases
            var trimmed = userText.Trim();
            if (IsCancellation(trimmed))
            {
                ClearPendingState();
                var msg = "Okay, cancelled.";
                ResponseReceived?.Invoke(this, msg);
                await _synthesizer.SpeakAsync(msg, ct);
                StatusChanged?.Invoke(this, "Ready.");
                return msg;
            }

            // Fill the current parameter (or skip optional ones)
            var param = _missingParams!.Peek();

            if (!param.Required && IsSkip(trimmed))
            {
                _missingParams.Dequeue();
                AssistantLog.Write("PARAM_SKIP", $"{param.Name} (optional, skipped)");
            }
            else
            {
                _missingParams.Dequeue();
                _pendingToolCall!.Arguments[param.Name] = trimmed;
                AssistantLog.Write("PARAM_FILL", $"{param.Name} = \"{trimmed}\"");
            }

            // More params needed?
            if (_missingParams.Count > 0)
            {
                var next = _missingParams.Peek();
                var askMessage = FormatParamQuestion(next, isFirst: false);
                ResponseReceived?.Invoke(this, askMessage);
                await _synthesizer.SpeakAsync(askMessage, ct);
                return askMessage;
            }

            // All params collected — execute the tool
            var call = _pendingToolCall!;
            ClearPendingState();

            var sw = Stopwatch.StartNew();
            StatusChanged?.Invoke(this, "Executing...");
            var result = await _toolRegistry.ExecuteAsync(call);
            var toolMs = sw.ElapsedMilliseconds;

            var response = result.Success ? result.Output ?? "Done." : $"Error: {result.Error}";
            response = TimeFormatter.HumanizeTimestamps(response);
            response = TimeFormatter.HumanizeUptimeStrings(response);

            AssistantLog.Write("TOOL_RESULT", response);
            ResponseReceived?.Invoke(this, response);

            sw.Restart();
            StatusChanged?.Invoke(this, "Speaking...");
            await _synthesizer.SpeakAsync(response, ct);
            var ttsMs = sw.ElapsedMilliseconds;

            StatusChanged?.Invoke(this, $"Ready. [Tool: {toolMs}ms | TTS: {ttsMs}ms]");
            return response;
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(this, "Cancelled.");
            return string.Empty;
        }
        catch (Exception ex)
        {
            ClearPendingState();
            var error = $"Error: {ex.Message}";
            AssistantLog.Write("ERROR", error);
            ErrorOccurred?.Invoke(this, error);
            StatusChanged?.Invoke(this, "Error occurred.");
            return error;
        }
    }

    private static bool IsCancellation(string text) =>
        text.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("nevermind", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("never mind", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("abort", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("stop", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkip(string text) =>
        text.Equals("skip", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("no thanks", StringComparison.OrdinalIgnoreCase);

    private static string FormatParamQuestion(ToolParameter param, bool isFirst)
    {
        var desc = param.Description.TrimEnd('.');
        if (param.Required)
        {
            return isFirst
                ? $"I need a bit more info. What's the {desc.ToLower()}?"
                : $"Got it. And what's the {desc.ToLower()}?";
        }
        else
        {
            return isFirst
                ? $"Would you also like to provide {desc.ToLower()}? Say skip if not."
                : $"Got it. Would you also like to provide {desc.ToLower()}? Say skip if not.";
        }
    }

    private void ClearPendingState()
    {
        _pendingToolCall = null;
        _pendingTool = null;
        _missingParams = null;
    }

    private static string FormatTiming(long? sttMs, long? inferenceMs, long? toolMs, long? ttsMs)
    {
        var parts = new List<string>();
        if (sttMs.HasValue) parts.Add($"STT: {sttMs}ms");
        if (inferenceMs.HasValue) parts.Add($"AI: {inferenceMs}ms");
        if (toolMs.HasValue) parts.Add($"Tool: {toolMs}ms");
        if (ttsMs.HasValue) parts.Add($"TTS: {ttsMs}ms");
        return parts.Count > 0 ? $"[{string.Join(" | ", parts)}]" : "";
    }

    private static string FormatToolResults(List<ToolResult> results)
    {
        if (results.Count == 1)
        {
            var r = results[0];
            return r.Success
                ? r.Output ?? "Done."
                : $"Error: {r.Error}";
        }

        var parts = new List<string>();
        foreach (var r in results)
        {
            var prefix = r.Success ? $"[{r.ToolName}]" : $"[{r.ToolName} ERROR]";
            var body = r.Success ? r.Output : r.Error;
            parts.Add($"{prefix} {body}");
        }
        return string.Join("\n\n", parts);
    }
}

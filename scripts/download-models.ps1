<#
.SYNOPSIS
    Downloads AI models required by GodMode Voice.

.DESCRIPTION
    Downloads Phi-4-mini-instruct ONNX (DirectML GPU variant), Qwen2.5-0.5B-Instruct
    ONNX (INT8 quantized for NPU), and Whisper base GGML models from HuggingFace
    into ~/.godmode/models/.

.NOTES
    Requires: huggingface-cli (pip install huggingface-hub)
    NPU model requires AMD Ryzen AI Software for NPU acceleration.
#>

$ErrorActionPreference = "Stop"
$modelsDir = Join-Path $env:USERPROFILE ".godmode" "models"
New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null

Write-Host "Models directory: $modelsDir" -ForegroundColor Cyan

# Check for huggingface-cli
$hfCli = Get-Command huggingface-cli -ErrorAction SilentlyContinue
if (-not $hfCli) {
    Write-Host "huggingface-cli not found. Install it with: pip install huggingface-hub" -ForegroundColor Red
    exit 1
}

# Download Phi-4-mini-instruct ONNX (DirectML INT4 GPU variant)
$phiDir = Join-Path $modelsDir "phi-4-mini-instruct-onnx-gpu"
if (Test-Path (Join-Path $phiDir "genai_config.json")) {
    Write-Host "Phi-4-mini (GPU) already downloaded, skipping." -ForegroundColor Green
} else {
    Write-Host "Downloading Phi-4-mini-instruct ONNX (DirectML INT4 GPU)..." -ForegroundColor Yellow
    & huggingface-cli download microsoft/Phi-4-mini-instruct-onnx --include "directml/directml-int4-awq-block-128/*" --local-dir $phiDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to download Phi-4-mini. You may need to accept the model license on HuggingFace." -ForegroundColor Red
        exit 1
    }

    # Move files from subdirectory to root for genai_config.json to be at top level
    $subDir = Join-Path $phiDir "directml" "directml-int4-awq-block-128"
    if (Test-Path $subDir) {
        Get-ChildItem -Path $subDir -Recurse | Move-Item -Destination $phiDir -Force
        Remove-Item (Join-Path $phiDir "directml") -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Phi-4-mini (GPU) downloaded successfully." -ForegroundColor Green
}

# Download Qwen2.5-0.5B-Instruct ONNX (INT8 quantized for NPU)
$qwenDir = Join-Path $modelsDir "qwen2.5-0.5b-instruct-onnx"
if (Test-Path (Join-Path $qwenDir "tokenizer.json")) {
    Write-Host "Qwen2.5-0.5B (NPU) already downloaded, skipping." -ForegroundColor Green
} else {
    Write-Host "Downloading Qwen2.5-0.5B-Instruct ONNX (INT8 quantized for NPU)..." -ForegroundColor Yellow
    & huggingface-cli download onnx-community/Qwen2.5-0.5B-Instruct --include "onnx/model_quantized.onnx" "tokenizer.json" "tokenizer_config.json" "vocab.json" "merges.txt" --local-dir $qwenDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to download Qwen2.5-0.5B model." -ForegroundColor Red
        exit 1
    }

    # Move model from onnx/ subdirectory to root
    $onnxSubDir = Join-Path $qwenDir "onnx"
    if (Test-Path $onnxSubDir) {
        Get-ChildItem -Path $onnxSubDir -File | Move-Item -Destination $qwenDir -Force
        Remove-Item $onnxSubDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Qwen2.5-0.5B (NPU) downloaded successfully." -ForegroundColor Green
}

# Download Whisper base GGML
$whisperDir = Join-Path $modelsDir "whisper"
$whisperModel = Join-Path $whisperDir "ggml-base.bin"
if (Test-Path $whisperModel) {
    Write-Host "Whisper base model already downloaded, skipping." -ForegroundColor Green
} else {
    New-Item -ItemType Directory -Path $whisperDir -Force | Out-Null
    Write-Host "Downloading Whisper base GGML model..." -ForegroundColor Yellow
    & huggingface-cli download ggerganov/whisper.cpp ggml-base.bin --local-dir $whisperDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to download Whisper model." -ForegroundColor Red
        exit 1
    }
    Write-Host "Whisper model downloaded successfully." -ForegroundColor Green
}

Write-Host "`nAll models downloaded successfully!" -ForegroundColor Green
Write-Host "Phi-4-mini (GPU):    $phiDir"
Write-Host "Qwen2.5-0.5B (NPU): $qwenDir"
Write-Host "Whisper:             $whisperModel"
Write-Host "`nDefault config path: $(Join-Path $env:USERPROFILE '.godmode' 'inference.json')"
Write-Host "`nTo use NPU model, add to inference.json:"
Write-Host "  `"npu_model_path`": `"$qwenDir`""
Write-Host "  `"execution_provider`": `"npu`"  (or `"auto`" to prefer NPU with DirectML fallback)"

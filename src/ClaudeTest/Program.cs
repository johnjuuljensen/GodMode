using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Test harness for Claude process invocation.
/// Confirms the correct JSON format and process lifecycle.
/// </summary>

// Configuration
var claudeConfigDir = @"C:\Users\JJJ\.claude-mega";
var timeoutSeconds = 30;
var multiTurn = false; // Set to true to test multi-turn conversation

Console.WriteLine("=== CLAUDE PROCESS TEST ===");
Console.WriteLine();

// Build process
var startInfo = new ProcessStartInfo
{
    FileName = "claude",
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8
};

// Add arguments - these are the working args for stream-json mode
string[] claudeArgs = [
    "--print",
    "--verbose",
    "--dangerously-skip-permissions",
    "--output-format=stream-json",
    "--input-format=stream-json"
];

foreach (var arg in claudeArgs)
{
    startInfo.ArgumentList.Add(arg);
}

// Set environment
startInfo.Environment["CLAUDE_CONFIG_DIR"] = claudeConfigDir;

Console.WriteLine($"Command: claude {string.Join(" ", claudeArgs)}");
Console.WriteLine($"CLAUDE_CONFIG_DIR: {claudeConfigDir}");
Console.WriteLine();

var process = new Process
{
    StartInfo = startInfo,
    EnableRaisingEvents = true
};

var outputLines = new List<string>();
var errorLines = new List<string>();

process.OutputDataReceived += (sender, e) =>
{
    if (e.Data != null)
    {
        outputLines.Add(e.Data);
        Console.WriteLine($"[STDOUT] {e.Data}");
    }
};

process.ErrorDataReceived += (sender, e) =>
{
    if (e.Data != null)
    {
        errorLines.Add(e.Data);
        Console.WriteLine($"[STDERR] {e.Data}");
    }
};

Console.WriteLine("Starting process...");
process.Start();
process.BeginOutputReadLine();
process.BeginErrorReadLine();

Console.WriteLine($"Process started with PID: {process.Id}");
Console.WriteLine();

// Helper to create input message in correct format
static string CreateInputJson(string text) => JsonSerializer.Serialize(new
{
    type = "user",
    message = new
    {
        role = "user",
        content = new object[]
        {
            new { type = "text", text }
        }
    }
});

// Send first message
var inputJson = CreateInputJson("Just say hi back");
Console.WriteLine($"Sending message: {inputJson}");
await process.StandardInput.WriteLineAsync(inputJson);
await process.StandardInput.FlushAsync();
Console.WriteLine("Message sent.");

if (multiTurn)
{
    // Wait for response
    Console.WriteLine("Waiting for response...");
    await Task.Delay(5000);

    // Send second message
    var secondJson = CreateInputJson("What is 2+2?");
    Console.WriteLine($"Sending message 2: {secondJson}");
    await process.StandardInput.WriteLineAsync(secondJson);
    await process.StandardInput.FlushAsync();
    Console.WriteLine("Message 2 sent.");

    // Wait for response
    await Task.Delay(5000);
}

// Close stdin to signal we're done
process.StandardInput.Close();
Console.WriteLine("Stdin closed - waiting for process to exit.");
Console.WriteLine();

// Wait with timeout
var exited = process.WaitForExit(timeoutSeconds * 1000);

if (!exited)
{
    Console.WriteLine("TIMEOUT! Killing process...");
    process.Kill(entireProcessTree: true);
    process.WaitForExit();
    Console.WriteLine("Process killed.");
}
else
{
    Console.WriteLine($"Process exited with code: {process.ExitCode}");
}

Console.WriteLine();
Console.WriteLine("=== SUMMARY ===");
Console.WriteLine($"Exit code: {process.ExitCode}");
Console.WriteLine($"Stdout lines: {outputLines.Count}");
Console.WriteLine($"Stderr lines: {errorLines.Count}");

if (errorLines.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== STDERR ===");
    foreach (var line in errorLines)
    {
        Console.WriteLine(line);
    }
}

/*
OUTPUT FORMAT:
- Line 1: {"type":"system","subtype":"init",...} - Session init with tools, model, etc.
- Line 2: {"type":"assistant","message":{...}} - The assistant's response
- Line 3: {"type":"result","subtype":"success",...} - Result summary with usage/cost

INPUT FORMAT:
{"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."}]}}
*/

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GodMode.Avalonia.Services;

public interface IEmbeddedServerService : IDisposable
{
	bool IsRunning { get; }
	string ServerUrl { get; }
	event Action<bool>? ServerStateChanged;
	Task StartAsync();
	void Stop();
}

public class EmbeddedServerService : IEmbeddedServerService
{
	private Process? _serverProcess;
	private readonly string _serverUrl = "http://localhost:31337";

	public bool IsRunning => _serverProcess is { HasExited: false };
	public string ServerUrl => _serverUrl;
	public event Action<bool>? ServerStateChanged;

	public async Task StartAsync()
	{
		if (IsRunning) return;

		// Find the server project relative to the app
		var serverDll = FindServerDll();
		if (serverDll == null)
		{
			Debug.WriteLine("EmbeddedServer: Could not find GodMode.Server.dll, skipping auto-start");
			return;
		}

		// Use the server's source directory as working dir so relative paths
		// in appsettings.json (e.g., "projects") resolve correctly
		var workingDir = FindServerSourceDirectory(serverDll) ?? Path.GetDirectoryName(serverDll)!;

		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = serverDll,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDir
		};

		// Set ASPNETCORE_URLS to ensure it binds to the right port
		startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = _serverUrl;

		try
		{
			_serverProcess = Process.Start(startInfo);
			if (_serverProcess != null)
			{
				_serverProcess.EnableRaisingEvents = true;
				_serverProcess.Exited += (_, _) => ServerStateChanged?.Invoke(false);

				// Wait a bit for server to start
				await Task.Delay(2000);

				if (IsRunning)
				{
					Debug.WriteLine($"EmbeddedServer: Started on {_serverUrl} (PID {_serverProcess.Id})");
					ServerStateChanged?.Invoke(true);
				}
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"EmbeddedServer: Failed to start: {ex.Message}");
		}
	}

	public void Stop()
	{
		if (_serverProcess is { HasExited: false })
		{
			try
			{
				_serverProcess.Kill(entireProcessTree: true);
				_serverProcess.WaitForExit(3000);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"EmbeddedServer: Error stopping: {ex.Message}");
			}
		}

		_serverProcess?.Dispose();
		_serverProcess = null;
		ServerStateChanged?.Invoke(false);
	}

	public void Dispose()
	{
		Stop();
	}

	private static string? FindServerSourceDirectory(string serverDll)
	{
		// Walk up from bin/Debug/net10.0/GodMode.Server.dll to find the source dir
		// containing appsettings.json
		var dir = Path.GetDirectoryName(serverDll);
		while (dir != null)
		{
			if (File.Exists(Path.Combine(dir, "appsettings.json")) &&
				File.Exists(Path.Combine(dir, "GodMode.Server.csproj")))
				return dir;
			dir = Path.GetDirectoryName(dir);
		}
		return null;
	}

	private static string? FindServerDll()
	{
		// Try common locations relative to the app binary
		var appDir = AppDomain.CurrentDomain.BaseDirectory;
		var candidates = new[]
		{
			// Dev: sibling project output
			Path.Combine(appDir, "..", "..", "..", "..", "GodMode.Server",
				"bin", "Debug", "net10.0", "GodMode.Server.dll"),
			// Dev: run from solution root
			Path.Combine(appDir, "..", "..", "..", "..", "..", "src", "GodMode.Server",
				"bin", "Debug", "net10.0", "GodMode.Server.dll"),
			// Published side-by-side
			Path.Combine(appDir, "server", "GodMode.Server.dll"),
			// Same directory
			Path.Combine(appDir, "GodMode.Server.dll")
		};

		return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
	}
}

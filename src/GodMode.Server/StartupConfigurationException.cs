namespace GodMode.Server;

/// <summary>The server's configuration cannot work, so it will not start. The message says why and what to change.</summary>
public sealed class StartupConfigurationException(string message, Exception? innerException = null) : Exception(message, innerException);

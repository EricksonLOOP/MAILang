namespace Mail.Cli;

/// <summary>
/// Thrown at startup for invalid CLI configuration: removed flags, unsupported appsettings keys, or malformed .env files.
/// </summary>
public sealed class MailConfigurationException(string message) : Exception(message);

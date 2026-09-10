namespace Mail.Compiler;

public sealed record CompilationOptions(int MaxModules = 100, int MaxImportDepth = 20);

internal static class PathIdentity
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

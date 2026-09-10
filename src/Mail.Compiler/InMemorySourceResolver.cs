namespace Mail.Compiler;

public sealed class InMemorySourceResolver : ISourceResolver
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public InMemorySourceResolver(IReadOnlyDictionary<string, string> files) => _files = files.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, PathIdentity.Comparer);

    public string? Resolve(string importerAbsPath, string relativePath)
    {
        var dir = Path.GetDirectoryName(importerAbsPath) ?? "/";
        var key = Path.GetFullPath(Path.Combine(dir, relativePath));
        return _files.ContainsKey(key) ? key : null;
    }

    public string ReadSource(string absolutePath) => _files[absolutePath];
}


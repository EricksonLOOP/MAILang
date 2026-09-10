namespace Mail.Compiler;

public interface ISourceResolver
{
    // Returns the canonical absolute path for a relative import, or null if not found.
    string? Resolve(string importerAbsPath, string relativePath);

    string Canonicalize(string path) => Path.GetFullPath(path);

    string ReadSource(string absolutePath);
}


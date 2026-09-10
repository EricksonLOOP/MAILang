namespace Mail.Compiler;

public sealed class FilesystemSourceResolver : ISourceResolver
{
    public string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                current = info.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }
    public string? Resolve(string importerAbsPath, string relativePath)
    {
        var full = Canonicalize(Path.Combine(Path.GetDirectoryName(importerAbsPath)!, relativePath));
        return File.Exists(full) ? full : null;
    }
    public string ReadSource(string absolutePath) => File.ReadAllText(absolutePath);
}

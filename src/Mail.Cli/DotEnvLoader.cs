using System.Collections;
using System.Text;

namespace Mail.Cli;

public sealed class DotEnvLoader
{
    private readonly IReadOnlyDictionary<string, string> _osSnapshot;
    private readonly IReadOnlyDictionary<string, string>? _fileValues;

    private DotEnvLoader(IReadOnlyDictionary<string, string> osSnapshot, IReadOnlyDictionary<string, string>? fileValues)
    {
        _osSnapshot = osSnapshot;
        _fileValues = fileValues;
    }

    public static DotEnvLoader Load(string? envFilePath = ".env")
    {
        var osSnapshot = CaptureOsSnapshot();
        if (envFilePath is null || !File.Exists(envFilePath))
            return new(osSnapshot, null);
        return new(osSnapshot, ParseDotEnvFile(envFilePath));
    }

    // For injecting test overrides without touching process env or reading a file.
    // OS snapshot is left empty so only the supplied overrides are resolved.
    public static DotEnvLoader ForTesting(IReadOnlyDictionary<string, string> overrides)
        => new(new Dictionary<string, string>(StringComparer.Ordinal), overrides);

    public string? Resolve(string varName)
    {
        if (_osSnapshot.TryGetValue(varName, out var osValue) && !string.IsNullOrEmpty(osValue))
            return osValue;
        if (_fileValues is not null && _fileValues.TryGetValue(varName, out var fv) && !string.IsNullOrEmpty(fv))
            return fv;
        return null;
    }

    private static IReadOnlyDictionary<string, string> CaptureOsSnapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value)
                snapshot[key] = value;
        return snapshot;
    }

    private static IReadOnlyDictionary<string, string> ParseDotEnvFile(string path)
    {
        var rawText = File.ReadAllText(path, Encoding.UTF8);

        // Strip UTF-8 BOM if present
        if (rawText.Length > 0 && rawText[0] == '﻿')
            rawText = rawText[1..];

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawLines = rawText.Split('\n');

        for (var i = 0; i < rawLines.Length; i++)
        {
            var lineNum = i + 1;
            var line = rawLines[i].TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith('#')) continue;

            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0)
                throw new MailConfigurationException(
                    $"[MAIL-ENV-001] .env line {lineNum}: missing '=' separator. Fix the file or remove the line.");

            var key      = line[..eqIdx];
            var rawValue = line[(eqIdx + 1)..];

            string value;
            if (rawValue.StartsWith('"'))
            {
                // Double-quoted value: must end with an unescaped '"'
                var sb = new StringBuilder(rawValue.Length);
                var closed = false;
                for (var j = 1; j < rawValue.Length; j++)
                {
                    var ch = rawValue[j];
                    if (ch == '\\' && j + 1 < rawValue.Length)
                    {
                        j++;
                        var next = rawValue[j];
                        sb.Append(next == '"' ? '"' : next == '\\' ? '\\' : next);
                    }
                    else if (ch == '"')
                    {
                        closed = true;
                        break;
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                if (!closed)
                    throw new MailConfigurationException(
                        $"[MAIL-ENV-002] .env line {lineNum}: unclosed double-quote in value for key '{key}'.");
                value = sb.ToString();
            }
            else
            {
                value = rawValue;
            }

            result[key] = value; // last occurrence wins
        }

        return result;
    }
}

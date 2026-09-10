using Mail.Contracts;
using System.Collections.Immutable;
using System.Text.Json;

namespace Mail.Runtime;

public sealed class ArgumentValidator
{
    public MailSchema Validate(
        ImmutableDictionary<string, JsonElement> raw,
        FieldContract[] contract,
        string toolName)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var fc in contract)
        {
            if (!raw.TryGetValue(fc.Name, out var element))
            {
                if (fc.Required)
                    throw new ArgumentValidationException(toolName, fc.Name, "Required field is missing.");
                continue;
            }

            fields[fc.Name] = ConvertElement(element, fc, toolName);
        }

        // Reject unknown fields
        foreach (var key in raw.Keys)
            if (!contract.Any(f => f.Name == key))
                throw new ArgumentValidationException(toolName, key, "Unknown field.");

        return new MailSchema(toolName + "Input", fields.ToImmutable());
    }

    private static MailValue ConvertElement(JsonElement element, FieldContract fc, string toolName)
    {
        return fc.Kind switch
        {
            MailTypeKind.String => element.ValueKind == JsonValueKind.String
                ? new MailString(element.GetString()!)
                : throw new ArgumentValidationException(toolName, fc.Name, $"Expected String, got {element.ValueKind}."),

            MailTypeKind.Bool => element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
                ? new MailBool(element.GetBoolean())
                : throw new ArgumentValidationException(toolName, fc.Name, $"Expected Bool, got {element.ValueKind}."),

            MailTypeKind.Int => ConvertInt(element, fc.Name, toolName),

            MailTypeKind.Schema => throw new ArgumentValidationException(toolName, fc.Name,
                "Nested schema arguments are not supported in the prototype."),

            _ => throw new ArgumentValidationException(toolName, fc.Name, $"Unsupported type kind {fc.Kind}."),
        };
    }

    private static MailInt ConvertInt(JsonElement element, string fieldName, string toolName)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw new ArgumentValidationException(toolName, fieldName, $"Expected Int, got {element.ValueKind}.");

        // Reject floating-point representations
        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
            throw new ArgumentValidationException(toolName, fieldName, "Int must not have decimal point or exponent.");

        if (!element.TryGetInt64(out var value))
            throw new ArgumentValidationException(toolName, fieldName, "Value exceeds Int64 range.");

        return new MailInt(value);
    }
}

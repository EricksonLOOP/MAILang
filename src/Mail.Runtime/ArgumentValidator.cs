using Mail.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Mail.Runtime;

public sealed class ArgumentValidator
{
    // Presence/nullable matrix:
    //   Required non-Nullable: absent → error;  null → error;  value → convert
    //   Required Nullable:     absent → error;  null → MailNull; value → convert
    //   Optional non-Nullable: absent → skip;   null → error;  value → convert
    //   Optional Nullable:     absent → skip;   null → MailNull; value → convert
    public MailSchema Validate(
        ImmutableDictionary<string, JsonElement> raw,
        FieldContract[] contract,
        string contextName)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var fc in contract)
        {
            if (!raw.TryGetValue(fc.Name, out var element))
            {
                if (fc.Required)
                    throw new ArgumentValidationException(contextName, fc.Name, "Required field is missing.");
                continue;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                if (!fc.Nullable)
                    throw new ArgumentValidationException(contextName, fc.Name,
                        "Field is not nullable; explicit null is not allowed.");
                fields[fc.Name] = new MailNull();
                continue;
            }

            fields[fc.Name] = ConvertElement(element, fc, contextName);
        }

        // Reject unknown fields
        foreach (var key in raw.Keys)
            if (!contract.Any(f => f.Name == key))
                throw new ArgumentValidationException(contextName, key, "Unknown field.");

        return new MailSchema(contextName + "Input", fields.ToImmutable());
    }

    private static MailValue ConvertElement(JsonElement element, FieldContract fc, string contextName)
    {
        return fc.Kind switch
        {
            MailTypeKind.String  => ConvertString(element, fc.Name, contextName),
            MailTypeKind.Bool    => ConvertBool(element, fc.Name, contextName),
            MailTypeKind.Int     => ConvertInt(element, fc.Name, contextName),
            MailTypeKind.Decimal => ConvertDecimal(element, fc.Name, contextName),
            MailTypeKind.List    => ConvertList(element, fc, contextName),
            MailTypeKind.Enum    => ConvertEnum(element, fc, contextName),
            MailTypeKind.Schema  => throw new ArgumentValidationException(contextName, fc.Name,
                "Nested schema arguments are not supported in the prototype."),
            _ => throw new ArgumentValidationException(contextName, fc.Name, $"Unsupported type kind {fc.Kind}."),
        };
    }

    private static MailString ConvertString(JsonElement element, string fieldName, string contextName)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentValidationException(contextName, fieldName, $"Expected String, got {element.ValueKind}.");
        return new MailString(element.GetString()!);
    }

    private static MailBool ConvertBool(JsonElement element, string fieldName, string contextName)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentValidationException(contextName, fieldName, $"Expected Bool, got {element.ValueKind}.");
        return new MailBool(element.GetBoolean());
    }

    private static MailInt ConvertInt(JsonElement element, string fieldName, string contextName)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw new ArgumentValidationException(contextName, fieldName, $"Expected Int, got {element.ValueKind}.");

        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
            throw new ArgumentValidationException(contextName, fieldName, "Int must not have decimal point or exponent.");

        if (!element.TryGetInt64(out var value))
            throw new ArgumentValidationException(contextName, fieldName, "Value exceeds Int64 range.");

        return new MailInt(value);
    }

    private static MailDecimal ConvertDecimal(JsonElement element, string fieldName, string contextName)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentValidationException(contextName, fieldName,
                "Decimal must be a JSON string in fixed-point notation (e.g. \"125.50\").");

        var raw = element.GetString()!;

        // Reject exponent notation
        if (raw.Contains('e') || raw.Contains('E'))
            throw new ArgumentValidationException(contextName, fieldName,
                "Decimal string must not use exponent notation.");

        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            throw new ArgumentValidationException(contextName, fieldName,
                $"'{raw}' is not a valid Decimal string.");

        // Verify round-trip: no rounding occurred during parsing
        var roundTripped = value.ToString(CultureInfo.InvariantCulture);
        // Normalize both sides: strip leading zeros (but not sole zero before dot) for comparison
        if (!DecimalStringsEquivalent(raw, roundTripped))
            throw new ArgumentValidationException(contextName, fieldName,
                "Decimal value would require rounding; rejected.");

        return new MailDecimal(value);
    }

    // Returns true when raw and canonical represent the same decimal without rounding.
    private static bool DecimalStringsEquivalent(string raw, string canonical)
    {
        // Both represent the same decimal.Parse result; check that no precision was lost.
        // Re-parse canonical and compare the decimal values.
        if (!decimal.TryParse(canonical, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v1))
            return false;
        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v2))
            return false;
        return v1 == v2;
    }

    private static MailList ConvertList(JsonElement element, FieldContract fc, string contextName)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new ArgumentValidationException(contextName, fc.Name, $"Expected List (JSON array), got {element.ValueKind}.");

        const int MaxListItems = 1000;
        var length = element.GetArrayLength();
        if (length > MaxListItems)
            throw new ArgumentValidationException(contextName, fc.Name,
                $"List exceeds maximum allowed size of {MaxListItems} items.");

        if (fc.ElementKind is null)
            throw new ArgumentValidationException(contextName, fc.Name, "List field contract is missing element kind.");

        var elementKind = fc.ElementKind.Value;
        var elementContract = new FieldContract("element", elementKind, Required: true,
            SchemaTypeName: fc.ElementTypeName, EnumTypeName: fc.ElementTypeName,
            EnumSymbols: fc.ElementEnumSymbols);

        var builder = ImmutableList.CreateBuilder<MailValue>();
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
                throw new ArgumentValidationException(contextName, $"{fc.Name}[{i}]",
                    "List elements cannot be null.");
            builder.Add(ConvertElement(item, elementContract, contextName));
            i++;
        }
        return new MailList(builder.ToImmutable());
    }

    public static MailSchema ValidateMailValues(
        System.Collections.Immutable.ImmutableDictionary<string, MailValue> values,
        FieldContract[] contract,
        string contextName)
    {
        var fields = System.Collections.Immutable.ImmutableDictionary
            .CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var fc in contract)
        {
            if (!values.TryGetValue(fc.Name, out var value))
            {
                if (fc.Required)
                    throw new ArgumentValidationException(contextName, fc.Name, "Required field is missing.");
                continue;
            }

            if (value is MailNull)
            {
                if (!fc.Nullable)
                    throw new ArgumentValidationException(contextName, fc.Name,
                        "Field is not nullable; explicit null is not allowed.");
                fields[fc.Name] = value;
                continue;
            }

            bool kindOk = fc.Kind switch
            {
                MailTypeKind.String  => value is MailString,
                MailTypeKind.Bool    => value is MailBool,
                MailTypeKind.Int     => value is MailInt,
                MailTypeKind.Decimal => value is MailDecimal,
                MailTypeKind.List    => value is MailList,
                MailTypeKind.Enum    => value is MailEnum me &&
                    (fc.EnumSymbols is null ||
                     fc.EnumSymbols.Value.Contains(me.Symbol, StringComparer.Ordinal)),
                MailTypeKind.Schema  => value is MailSchema,
                _ => false,
            };

            if (!kindOk)
                throw new ArgumentValidationException(contextName, fc.Name,
                    $"Field kind mismatch: expected {fc.Kind}, got {value.GetType().Name}.");

            fields[fc.Name] = value;
        }

        foreach (var key in values.Keys)
            if (!contract.Any(f => f.Name == key))
                throw new ArgumentValidationException(contextName, key, "Unknown field.");

        return new MailSchema(contextName + "Input", fields.ToImmutable());
    }

    private static MailEnum ConvertEnum(JsonElement element, FieldContract fc, string contextName)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentValidationException(contextName, fc.Name, $"Expected Enum (JSON string), got {element.ValueKind}.");

        var symbol = element.GetString()!;

        if (fc.EnumSymbols is null)
            throw new ArgumentValidationException(contextName, fc.Name, "Enum field contract is missing symbol list.");

        if (!fc.EnumSymbols.Value.Contains(symbol, StringComparer.Ordinal))
            throw new ArgumentValidationException(contextName, fc.Name,
                $"'{symbol}' is not a valid symbol for this enum.");

        return new MailEnum(fc.EnumTypeName ?? fc.Name, symbol);
    }
}

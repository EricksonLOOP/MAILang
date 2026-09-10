using System.Collections.Immutable;

namespace Mail.Contracts;

public abstract record MailValue;

public sealed record MailString(string Value) : MailValue;

public sealed record MailBool(bool Value) : MailValue;

public sealed record MailInt(long Value) : MailValue;

public sealed record MailDecimal(decimal Value) : MailValue;

// Element type is enforced at validation boundaries, not stored per-element.
public sealed record MailList(ImmutableList<MailValue> Elements) : MailValue;

public sealed record MailEnum(string TypeName, string Symbol) : MailValue;

// Represents explicit MAIL null inside a Nullable<T> field. Absent fields have no entry in the dict.
public sealed record MailNull : MailValue;

public sealed record MailSchema(string TypeName, ImmutableDictionary<string, MailValue> Fields) : MailValue
{
    public bool TryGetField(string name, out MailValue? value) => Fields.TryGetValue(name, out value);
}

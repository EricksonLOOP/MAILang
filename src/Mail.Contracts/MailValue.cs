using System.Collections.Immutable;

namespace Mail.Contracts;

public abstract record MailValue;

public sealed record MailString(string Value) : MailValue;

public sealed record MailBool(bool Value) : MailValue;

public sealed record MailInt(long Value) : MailValue;

public sealed record MailSchema(string TypeName, ImmutableDictionary<string, MailValue> Fields) : MailValue
{
    public bool TryGetField(string name, out MailValue? value) => Fields.TryGetValue(name, out value);
}

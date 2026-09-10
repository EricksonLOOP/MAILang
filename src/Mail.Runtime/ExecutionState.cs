using Mail.Compiler.Ast;
using Mail.Contracts;
using System.Collections.Immutable;

namespace Mail.Runtime;

public sealed class ExecutionState
{
    public MailValue Input { get; }
    public ImmutableDictionary<string, MailValue> Bindings { get; }

    public ExecutionState(MailValue input)
    {
        Input = input;
        Bindings = ImmutableDictionary<string, MailValue>.Empty;
    }

    private ExecutionState(MailValue input, ImmutableDictionary<string, MailValue> bindings)
    {
        Input = input;
        Bindings = bindings;
    }

    public ExecutionState Publish(string name, MailValue value) =>
        new(Input, Bindings.SetItem(name, value));

    public MailValue Resolve(Expr expr) => expr switch
    {
        NameExpr ne => ResolveBinding(ne.Name),
        FieldAccessExpr fa => ResolveField(fa),
        _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    private MailValue ResolveBinding(string name)
    {
        if (name == "input") return Input;
        if (Bindings.TryGetValue(name, out var value)) return value;
        throw new BindingNotFoundException(name);
    }

    private MailValue ResolveField(FieldAccessExpr fa)
    {
        var target = Resolve(fa.Target);
        if (target is not MailSchema schema)
            throw new InvalidOperationException(
                $"Cannot access field '{fa.Field}' on a non-schema value of type {target.GetType().Name}.");
        if (!schema.Fields.TryGetValue(fa.Field, out var field))
            throw new BindingNotFoundException($"{fa.Field} on {schema.TypeName}");
        return field;
    }
}

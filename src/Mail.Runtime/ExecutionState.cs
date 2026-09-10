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

    // Merge bindings from a child scope (branch result) into this state
    public ExecutionState MergeFrom(ExecutionState child)
    {
        var builder = Bindings.ToBuilder();
        foreach (var (k, v) in child.Bindings)
            if (!Bindings.ContainsKey(k))
                builder[k] = v;
        return new(Input, builder.ToImmutable());
    }

    public MailValue Resolve(Expr expr) => ExprEvaluator.Eval(expr, this);

    // Creates a minimal state used for evaluating guard expressions with only "input" bound
    public static ExecutionState WithInput(MailValue inputValue) =>
        new(inputValue);

    // Used by ExprEvaluator
    internal MailValue ResolveBinding(string name)
    {
        if (name == "input") return Input;
        if (Bindings.TryGetValue(name, out var value)) return value;
        throw new BindingNotFoundException(name);
    }

    // Used by ExprEvaluator
    internal MailValue ResolveField(FieldAccessExpr fa)
    {
        var target = ExprEvaluator.Eval(fa.Target, this);
        if (target is not MailSchema schema)
            throw new InvalidOperationException(
                $"Cannot access field '{fa.Field}' on a non-schema value of type {target.GetType().Name}.");
        if (!schema.Fields.TryGetValue(fa.Field, out var field))
            throw new BindingNotFoundException($"{fa.Field} on {schema.TypeName}");
        return field;
    }
}

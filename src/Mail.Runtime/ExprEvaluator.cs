using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

internal static class ExprEvaluator
{
    public static MailValue Eval(Expr expr, ExecutionState state) => expr switch
    {
        NameExpr ne          => state.ResolveBinding(ne.Name),
        FieldAccessExpr fa   => state.ResolveField(fa),
        StringLiteralExpr sl => new MailString(sl.Value),
        BoolLiteralExpr bl   => new MailBool(bl.Value),
        IntLiteralExpr il    => new MailInt(il.Value),
        NotExpr ne           => EvalNot(ne, state),
        BinaryExpr be        => EvalBinary(be, state),
        ConditionalExpr ce   => EvalConditional(ce, state),
        _                    => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    // Evaluate with a minimal state containing only one named binding (e.g., agent input)
    public static MailValue EvalWithInput(Expr expr, MailValue inputValue)
    {
        var state = ExecutionState.WithInput(inputValue);
        return Eval(expr, state);
    }

    public static bool EvalBool(Expr expr, ExecutionState state, string context)
    {
        var result = Eval(expr, state);
        if (result is MailBool b) return b.Value;
        throw new ContractViolationException(context, "Condition expression did not evaluate to Bool.");
    }

    private static MailValue EvalNot(NotExpr ne, ExecutionState state)
    {
        var val = Eval(ne.Operand, state);
        if (val is MailBool b) return new MailBool(!b.Value);
        throw new InvalidOperationException("'not' applied to non-Bool value.");
    }

    private static MailValue EvalBinary(BinaryExpr be, ExecutionState state)
    {
        switch (be.Op)
        {
            case BinaryOp.And:
                // Short-circuit: if left is false, return false without evaluating right
                var left = Eval(be.Left, state);
                if (left is MailBool { Value: false }) return new MailBool(false);
                var right = Eval(be.Right, state);
                return right is MailBool rb ? new MailBool(rb.Value) : throw new InvalidOperationException("'and' requires Bool operands.");

            case BinaryOp.Or:
                // Short-circuit: if left is true, return true without evaluating right
                var lv = Eval(be.Left, state);
                if (lv is MailBool { Value: true }) return new MailBool(true);
                var rv = Eval(be.Right, state);
                return rv is MailBool rb2 ? new MailBool(rb2.Value) : throw new InvalidOperationException("'or' requires Bool operands.");

            case BinaryOp.Eq:
                return new MailBool(ValuesEqual(Eval(be.Left, state), Eval(be.Right, state)));

            case BinaryOp.Ne:
                return new MailBool(!ValuesEqual(Eval(be.Left, state), Eval(be.Right, state)));

            case BinaryOp.Lt:
                return new MailBool(CompareInt(be.Op, Eval(be.Left, state), Eval(be.Right, state)));
            case BinaryOp.Le:
                return new MailBool(CompareInt(be.Op, Eval(be.Left, state), Eval(be.Right, state)));
            case BinaryOp.Gt:
                return new MailBool(CompareInt(be.Op, Eval(be.Left, state), Eval(be.Right, state)));
            case BinaryOp.Ge:
                return new MailBool(CompareInt(be.Op, Eval(be.Left, state), Eval(be.Right, state)));

            default:
                throw new InvalidOperationException($"Unknown binary operator: {be.Op}");
        }
    }

    private static MailValue EvalConditional(ConditionalExpr ce, ExecutionState state)
    {
        var cond = Eval(ce.Condition, state);
        if (cond is not MailBool b)
            throw new InvalidOperationException("Condition in 'if/then/else' expression did not evaluate to Bool.");
        return b.Value ? Eval(ce.Then, state) : Eval(ce.Else, state);
    }

    private static bool ValuesEqual(MailValue a, MailValue b) => (a, b) switch
    {
        (MailString  sa, MailString  sb) => sa.Value  == sb.Value,
        (MailBool    ba, MailBool    bb) => ba.Value  == bb.Value,
        (MailInt     ia, MailInt     ib) => ia.Value  == ib.Value,
        (MailDecimal da, MailDecimal db) => da.Value  == db.Value,
        (MailEnum    ea, MailEnum    eb) => ea.TypeName == eb.TypeName && ea.Symbol == eb.Symbol,
        (MailNull,       MailNull      ) => true,
        _ => false,
    };

    private static bool CompareInt(BinaryOp op, MailValue left, MailValue right)
    {
        if (left is not MailInt li || right is not MailInt ri)
            throw new InvalidOperationException($"Operator '{op}' requires Int operands.");
        return op switch
        {
            BinaryOp.Lt => li.Value <  ri.Value,
            BinaryOp.Le => li.Value <= ri.Value,
            BinaryOp.Gt => li.Value >  ri.Value,
            BinaryOp.Ge => li.Value >= ri.Value,
            _           => throw new InvalidOperationException($"Unexpected comparison op: {op}"),
        };
    }
}

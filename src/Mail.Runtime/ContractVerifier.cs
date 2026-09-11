using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

/// <summary>
/// Verifies that C# tool implementations registered in IToolRegistry
/// are compatible with their declarations in the ValidatedPlan.
/// </summary>
public static class ContractVerifier
{
    public static void Verify(ValidatedPlan plan, IToolRegistry registry)
    {
        // Collect all tool names referenced (from agent allow lists + call steps)
        var referencedTools = new HashSet<string>(plan.Tools.Keys, StringComparer.Ordinal);

        foreach (var agent in plan.Agents.Values)
            foreach (var entry in agent.AllowedTools)
                referencedTools.Add(entry.ToolName);

        foreach (var wf in plan.Workflows.Values)
            CollectCallToolNames(wf.Items, referencedTools);

        foreach (var toolName in referencedTools)
        {
            if (!registry.IsRegistered(toolName))
                throw new ToolContractMismatchException(toolName,
                    "No implementation registered for this tool.");

            if (!plan.Tools.TryGetValue(toolName, out var decl))
                continue; // Validated by compiler; won't happen for valid plans

            VerifyFields(toolName, "input",  decl.Input,  registry.InputContract(toolName),  plan);
            VerifyFields(toolName, "output", decl.Output, registry.OutputContract(toolName), plan);
        }
    }

    private static void CollectCallToolNames(IReadOnlyList<WorkflowItem> items, HashSet<string> names)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case StepItem si:
                    CollectFromStepBody(si.Step.Body, names);
                    break;
                case IfItem ii:
                    CollectCallToolNames(ii.Then, names);
                    if (ii.Else is not null) CollectCallToolNames(ii.Else, names);
                    break;
            }
        }
    }

    private static void CollectFromStepBody(StepBody body, HashSet<string> names)
    {
        switch (body)
        {
            case CallBody cb:
                names.Add(cb.ToolName);
                break;
            case ConditionalStepBody csb:
                CollectFromStepBody(csb.Then, names);
                CollectFromStepBody(csb.Else, names);
                break;
            case WorkflowCallBody:
                // Workflow calls don't register tools directly; their tools are
                // collected via the workflow's own items in the outer loop.
                break;
        }
    }

    private static void VerifyFields(
        string toolName,
        string direction,
        IReadOnlyList<FieldDecl> declared,
        FieldContract[] registered,
        ValidatedPlan plan)
    {
        foreach (var fd in declared)
        {
            var reg = registered.FirstOrDefault(r => r.Name == fd.Name);
            if (reg is null)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}' declared in .mail but absent from registered contract.");

            var expectedKind = TypeRefToKind(fd.Type, plan);
            if (expectedKind != reg.Kind)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}' declared as {expectedKind} but registered as {reg.Kind}.");

            // Check Nullable consistency
            var declaredNullable = fd.Type is NullableTypeRef;
            if (declaredNullable != reg.Nullable)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}': MAIL declares Nullable={declaredNullable} but registered contract has Nullable={reg.Nullable}.");

            // Check Optional consistency
            if (fd.Optional != !reg.Required)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}': MAIL declares Optional={fd.Optional} but registered contract has Required={reg.Required}.");

            // Check EnumSymbols consistency
            if (expectedKind == MailTypeKind.Enum)
            {
                var declaredName = fd.Type is NullableTypeRef ntr
                    ? (ntr.Inner is NamedTypeRef nr2 ? nr2.Name : "")
                    : (fd.Type is NamedTypeRef nr ? nr.Name : "");
                IReadOnlyList<string> declaredSymbols = plan.Enums.TryGetValue(declaredName, out var ed)
                    ? (IReadOnlyList<string>)ed.Symbols
                    : System.Array.Empty<string>();
                var registeredSymbols = reg.EnumSymbols?.ToArray() ?? System.Array.Empty<string>();
                if (!declaredSymbols.OrderBy(s => s, StringComparer.Ordinal)
                        .SequenceEqual(registeredSymbols.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal))
                    throw new ToolContractMismatchException(toolName,
                        $"{direction} field '{fd.Name}' enum symbols mismatch: " +
                        $"declared [{string.Join(", ", declaredSymbols.OrderBy(s => s, StringComparer.Ordinal))}] " +
                        $"vs registered [{string.Join(", ", registeredSymbols.OrderBy(s => s, StringComparer.Ordinal))}].");
            }
        }
    }

    private static MailTypeKind TypeRefToKind(TypeRef type, ValidatedPlan plan)
    {
        switch (type)
        {
            case PrimitiveTypeRef pt:
                return pt.Kind switch
                {
                    PrimitiveKind.String  => MailTypeKind.String,
                    PrimitiveKind.Bool    => MailTypeKind.Bool,
                    PrimitiveKind.Int     => MailTypeKind.Int,
                    PrimitiveKind.Decimal => MailTypeKind.Decimal,
                    _ => throw new InvalidOperationException($"Unknown primitive kind {pt.Kind}."),
                };
            case ListTypeRef:
                return MailTypeKind.List;
            case NullableTypeRef nr:
                return TypeRefToKind(nr.Inner, plan); // Nullable unwraps to inner kind for comparison
            case NamedTypeRef nt:
                return plan.Enums.ContainsKey(nt.Name) ? MailTypeKind.Enum : MailTypeKind.Schema;
            case QualifiedNameTypeRef qr:
                var qKey = $"{qr.Alias}.{qr.Name}";
                return plan.Enums.ContainsKey(qKey) ? MailTypeKind.Enum : MailTypeKind.Schema;
            default:
                throw new InvalidOperationException($"Unknown type ref {type.GetType().Name}.");
        }
    }
}

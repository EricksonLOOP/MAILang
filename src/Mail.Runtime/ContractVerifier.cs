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
        var referencedTools = new HashSet<string>(StringComparer.Ordinal);

        foreach (var agent in plan.Agents.Values)
            foreach (var toolName in agent.AllowedTools)
                referencedTools.Add(toolName);

        foreach (var step in plan.Workflow.Steps)
            if (step.Body is CallBody cb)
                referencedTools.Add(cb.ToolName);

        foreach (var toolName in referencedTools)
        {
            if (!registry.IsRegistered(toolName))
                throw new ToolContractMismatchException(toolName,
                    "No implementation registered for this tool.");

            if (!plan.Tools.TryGetValue(toolName, out var decl))
                continue; // Validated by compiler; won't happen for valid plans

            VerifyFields(toolName, "input",  decl.Input,  registry.InputContract(toolName));
            VerifyFields(toolName, "output", decl.Output, registry.OutputContract(toolName));
        }
    }

    private static void VerifyFields(
        string toolName,
        string direction,
        IReadOnlyList<FieldDecl> declared,
        FieldContract[] registered)
    {
        foreach (var fd in declared)
        {
            var reg = registered.FirstOrDefault(r => r.Name == fd.Name);
            if (reg is null)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}' declared in .mail but absent from registered contract.");

            var expectedKind = TypeRefToKind(fd.Type);
            if (expectedKind != reg.Kind)
                throw new ToolContractMismatchException(toolName,
                    $"{direction} field '{fd.Name}' declared as {expectedKind} but registered as {reg.Kind}.");
        }
    }

    private static MailTypeKind TypeRefToKind(TypeRef type) => type switch
    {
        PrimitiveTypeRef pt => pt.Kind switch
        {
            PrimitiveKind.String => MailTypeKind.String,
            PrimitiveKind.Bool   => MailTypeKind.Bool,
            PrimitiveKind.Int    => MailTypeKind.Int,
            _ => throw new InvalidOperationException($"Unknown primitive kind {pt.Kind}."),
        },
        NamedTypeRef => MailTypeKind.Schema,
        _ => throw new InvalidOperationException($"Unknown type ref {type.GetType().Name}."),
    };
}

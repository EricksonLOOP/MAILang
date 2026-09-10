using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;

namespace Mail.Cli;

public static class ToolRegistryFactory
{
    public static ToolRegistry Build(ValidatedPlan plan) => Build(plan, out _);

    public static ToolRegistry Build(ValidatedPlan plan, out GenerateTokenTool? tokenTool)
    {
        var registry = new ToolRegistry();
        tokenTool = null;

        foreach (var (toolName, toolDecl) in plan.Tools)
        {
            var inputContract  = DeclToContracts(toolDecl.Input);
            var outputContract = DeclToContracts(toolDecl.Output);

            IToolImplementation impl;
            if (toolName == "GenerateToken")
            {
                var gt = new GenerateTokenTool();
                tokenTool = gt;
                impl = gt;
            }
            else
            {
                impl = new EchoTool(toolName, outputContract);
            }

            registry.Register(toolName, impl, inputContract, outputContract);
        }

        return registry;
    }

    public static FieldContract[] DeclToContracts(IReadOnlyList<Mail.Compiler.Ast.FieldDecl> fields) =>
        fields.Select(f => new FieldContract(
            f.Name,
            f.Type is Mail.Compiler.Ast.PrimitiveTypeRef pt ? pt.Kind switch
            {
                Mail.Compiler.Ast.PrimitiveKind.String => MailTypeKind.String,
                Mail.Compiler.Ast.PrimitiveKind.Bool   => MailTypeKind.Bool,
                Mail.Compiler.Ast.PrimitiveKind.Int    => MailTypeKind.Int,
                _ => MailTypeKind.String,
            } : MailTypeKind.Schema,
            Required: true)).ToArray();
}

// Stub that echoes matching input fields and fills defaults for others
file sealed class EchoTool(string toolName, FieldContract[] outputContract) : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var builder = System.Collections.Immutable.ImmutableDictionary
            .CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var fc in outputContract)
        {
            if (input.Fields.TryGetValue(fc.Name, out var inputVal))
                builder[fc.Name] = inputVal;
            else
                builder[fc.Name] = fc.Kind switch
                {
                    MailTypeKind.String => new MailString("simulated-value"),
                    MailTypeKind.Bool   => new MailBool(true),
                    MailTypeKind.Int    => new MailInt(0),
                    _                  => new MailString("unknown"),
                };
        }

        return Task.FromResult(new MailSchema(toolName + "Output", builder.ToImmutable()));
    }
}

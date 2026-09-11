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
            var inputContract  = FieldContractBuilder.FromDecls(toolDecl.Input,  plan);
            var outputContract = FieldContractBuilder.FromDecls(toolDecl.Output, plan);

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
                    MailTypeKind.String  => new MailString("simulated-value"),
                    MailTypeKind.Bool    => new MailBool(true),
                    MailTypeKind.Int     => new MailInt(0),
                    MailTypeKind.Decimal => new MailDecimal(0m),
                    MailTypeKind.List    => new MailList(System.Collections.Immutable.ImmutableList<MailValue>.Empty),
                    MailTypeKind.Enum    => fc.EnumSymbols is { Length: > 0 }
                        ? new MailEnum(fc.EnumTypeName ?? fc.Name, fc.EnumSymbols.Value[0])
                        : new MailString("unknown-enum"),
                    MailTypeKind.Schema  => new MailSchema(fc.SchemaTypeName ?? fc.Name,
                        System.Collections.Immutable.ImmutableDictionary<string, MailValue>.Empty),
                    _                    => new MailString("unknown"),
                };
        }

        return Task.FromResult(new MailSchema(toolName + "Output", builder.ToImmutable()));
    }
}

# C# embedding

[Manual home](README.md) · [Runtime behavior](runtime.md)

## Run the example

The [Embedding project](../examples/docs/csharp/Embedding.csproj) references the compiler and runtime projects and includes a complete [Program.cs](../examples/docs/csharp/Program.cs). From the repository root:

```powershell
dotnet run --project examples/docs/csharp/Embedding.csproj -- examples/docs/tools.mail
dotnet run --project examples/docs/csharp/Embedding.csproj -- examples/docs/agent.mail
```

Both print `{"text":"hello"}`. The first uses a registered C# tool. The second uses a local fixed-response provider, demonstrating that a provider can return final output without requesting a tool. Neither requires credentials.

## Compile and resolve imports

API fragments:

```csharp
var (plan, diagnostics) = Compiler.Compile(source, filePath);
var (filePlan, fileDiagnostics) = Compiler.CompileFile(
    path, new FilesystemSourceResolver());
```

`Compile` handles source text; imports require a resolver and otherwise produce a diagnostic. Use `CompileFile` for normal files and module graphs. `InMemorySourceResolver` supports tests and generated source sets. The optional third `CompileFile` argument is `new CompilationOptions(MaxModules: 100, MaxImportDepth: 20)`; these are the defaults.

Check the returned plan and diagnostics before execution. Diagnostics include severity, code, message, and original source location. The compiler reads each module once per compilation; it provides no persistent cache or atomic filesystem snapshot. `load` and `run` can therefore observe different file contents.

`ValidatedPlan` contains schemas, enums, tools, agents, and workflows. `EntryWorkflow` is the entry declaration and `Workflow` is its compatibility alias. Imported workflow dictionary keys are canonical internal identities. Use resolved call `TargetName` values rather than constructing keys from import aliases.

## Register a tool

Implement this interface:

```csharp
Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct);
```

Register the implementation with `ToolRegistry.Register(name, implementation, inputContract, outputContract)`. Contracts are `FieldContract[]`; the example registers a required `String` field named `text` for both sides. All declared tools must have matching registrations before execution, even if unused.

`FieldContract` includes `Name`, `Kind`, `Required`, `Nullable`, `SchemaTypeName`, `EnumTypeName`, `EnumSymbols`, `ElementKind`, and `ElementTypeName`. Match the declaration rather than using broad placeholder contracts. `ContractVerifier.Verify` runs before execution starts and can throw directly, outside the executor's result-producing try/catch. Catch host configuration/registration exceptions separately from checking `ExecutionResult.Succeeded`.

Use `MailString`, `MailBool`, `MailInt`, `MailDecimal`, `MailList`, `MailEnum`, `MailNull`, and `MailSchema` to construct host values. Schemas carry a type name and immutable field dictionary; lists use immutable lists. `SchemaConverter.ToJson` serializes supported values; `FromJson(json, typeName, contract)` parses an object through field contracts. Current nested-null serialization and complex contract limitations are listed in [runtime boundaries](runtime.md#current-capability-boundaries).

## Implement a model provider

Implement this interface:

```csharp
Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
```

`ModelRequest` supplies `ModelId`, ordered `Messages`, `AvailableTools`, and `ExpectedOutputSchemaJson`. A `ToolDefinition` contains `Name`, `InputSchemaJson`, and `OutputSchemaJson`. Message variants are `SystemMessage`, `UserMessage`, `AssistantMessage`, and `ToolResultMessage`.

Return `ModelResponse(ToolCalls, Text)`. A nonempty tool-call list triggers dispatch; each `ToolCallRequest` has a call ID, tool name, and immutable dictionary of JSON arguments. Duplicate call IDs within a response are rejected. After dispatch, the next request includes matching tool results. To finish, return no tool calls and JSON text conforming to the expected output. A response with neither usable calls nor text fails.

Register with `ProviderRegistry.Register(providerName, implementation)`. The `providerName` must match the `provider` declaration name in the `.mail` file. The bundled `SimulatedModelProvider` accepts a scripted response sequence. For HTTP providers declared in `.mail`, the CLI resolves them automatically; in a C# host, register any `IModelProvider` implementation under the declared name. Supply appropriate client lifetime and cancellation handling in your host.

## Execute and observe

Construct `WorkflowExecutor(plan, tools, providers, limits)`, then call:

```csharp
var result = await executor.RunAsync(input, cancellationToken);
```

An optional fourth argument sets the execution ID. Inspect `Succeeded`, `Output`, `ErrorMessage`, `RunId`, `Status`, `TerminalStatus`, `Failure`, and `Events` on the result. `Status` falls back to success/failure when `TerminalStatus` is absent. Construct and validate root input in the host: the executor does not perform complete root-input shape validation before execution.

`ExecutionLimits.Default` supplies 10 tool calls and 5 model calls per agent with a 30-second timeout. `ExecutionLimits.Validated(maxToolCalls, maxModelCalls, timeout)` validates these basic limits. Init properties `MaxTotalModelCalls`, `MaxTotalToolAttempts`, and `MaxTotalActivations` configure aggregate limits. Pass cancellation tokens through tools and providers; cancellation does not roll back completed side effects.

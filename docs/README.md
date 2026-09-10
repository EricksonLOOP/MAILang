# MAILang developer manual

MAILang (MAIL, Multi-Agent Infrastructure Language) describes typed data, external tools, model-driven agents, and workflows that connect them. A `.mail` file declares behavior; a host supplies tool implementations and model providers.

This manual describes the checked-out compiler and runtime, including their current limitations. It does not treat design proposals as implemented features.

## Learn the language

1. [Getting started](getting-started.md): build, validate, and run your first program.
2. [Syntax and types](syntax.md): lexical rules, data declarations, expressions, and JSON representations.
3. [Agents and tools](agents-and-tools.md): prompts, permissions, contracts, and implementations.
4. [Workflows and control flow](workflows.md): steps, state, conditionals, loops, and imports.

## Integrate and operate

- [CLI and Python SDK](integrations.md): configuration, callbacks, errors, and cancellation.
- [C# embedding](csharp.md): compile programs and implement tools and model providers.
- [Integration protocol](protocol.md): UTF-8 JSON messages over standard input/output.
- [Runtime and troubleshooting](runtime.md): execution semantics, limits, diagnostics, and capability boundaries.
- [Example catalog](../examples/docs/README.md): commands, fixtures, and expected results.

Code fences marked `mail` are complete programs. Fences marked `text` and described as fragments/templates require surrounding declarations. The runnable files are the source of truth for tutorials.

## Implementation map

| Topic | Authority |
| --- | --- |
| Tokens and literal syntax | [Lexer](../src/Mail.Compiler/Lexer/Lexer.cs) |
| Declaration order and grammar | [Parser](../src/Mail.Compiler/Parser/Parser.cs), [AST](../src/Mail.Compiler/Ast/Nodes.cs) |
| Types, bindings, and control-flow validation | [Semantic validator](../src/Mail.Compiler/Semantics/SemanticValidator.cs) |
| Module resolution | [Module graph builder](../src/Mail.Compiler/Semantics/ModuleGraphBuilder.cs) |
| Execution | [Workflow executor](../src/Mail.Runtime/WorkflowExecutor.cs), [agent runner](../src/Mail.Runtime/AgentRunner.cs) |
| Public integration | [Python SDK](../sdk/python/mail_runtime/runtime.py), [protocol DTOs](../src/Mail.Cli/Integration/ProtocolMessages.cs) |
| Executable behavior examples | [Compiler/runtime tests](../tests/Mail.Tests), [SDK tests](../sdk/python/tests/test_integration.py) |

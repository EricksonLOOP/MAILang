<img src='.\public\assets\mailang_logo_marca.svg' width='100%' height='100%'/>

# MAIL — Multi-Agent Infrastructure Language
A declarative language for designing, connecting, governing, and executing AI agents.

## Documentation

Start with the [MAILang developer manual](docs/README.md):

- [Getting started](docs/getting-started.md) and [runnable examples](examples/docs/README.md)
- [Syntax and types](docs/syntax.md), [agents and tools](docs/agents-and-tools.md), and [workflows](docs/workflows.md)
- [CLI and Python SDK](docs/integrations.md), [C# embedding](docs/csharp.md), and [integration protocol](docs/protocol.md)
- [Runtime behavior and troubleshooting](docs/runtime.md)

## Multi-file programs

`examples/composition/main.mail` demonstrates Python → conditional routing → imported
workflow → imported agent → Python tool. Run `examples/composition/run.py` with the
local Python SDK installed (or `PYTHONPATH=sdk/python`) and `MAIL_EXE` set to the CLI
executable. Both branches print a token returned by Python.

Imports precede declarations: `import "types.mail" as Types`. Types, tools, agents,
and workflows in another file use `Types.Name`. Aliases are case-sensitive and local
to the importing file; imports are not re-exported. Relative paths are resolved from
the importing file, including paths with spaces and Unicode. Only local relative
`.mail` paths are supported. Filesystem links are resolved when supported by .NET.

The entry file must contain exactly one workflow; imported modules may contain any
number. `workflow Alias.Name input expression` invokes a child in a step. Its input
must have the declared type, including nominal schema identity. Children receive
only their input; output is validated before the parent binding is published.
Every imported workflow is validated, even if no selected branch executes it.
Tool registration names stay simple and unique across the entire graph. Load returns
all tools, including unused ones, and run recompiles before executing.

C# callers use `Compiler.CompileFile(path, new FilesystemSourceResolver())`; tests can
use `InMemorySourceResolver`. `Compile(source, path)` still handles single-file source
and reports a diagnostic for imports without a resolver. `CompilationOptions`
configures `MaxModules` (100) and `MaxImportDepth` (20). Each module is read once per
compilation; there is no persistent cache or atomic filesystem snapshot guarantee.
`ValidatedPlan.Workflows` uses canonical internal keys; resolve calls using their
`TargetName`, rather than constructing a key from a caller's alias. `Workflow` remains
an alias for `EntryWorkflow`.

Subworkflows share the root execution ID, cancellation token, and deadline (30 seconds
by default). Each invocation emits an operation ID and parent operation relationship.
`ExecutionLimits` exposes aggregate defaults of 100 model calls, 1,000 tool attempts,
and 1,000 step activations. These limits apply across all subworkflows; per-agent
limits (5 model calls and 10 tool calls by default) still apply independently.

The bundled VS Code extension provides syntax highlighting; compiler diagnostics
include original file/line/column locations. Cross-file editor navigation is not a
language-server feature in this release.

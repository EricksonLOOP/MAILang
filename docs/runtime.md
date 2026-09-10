# Runtime behavior and troubleshooting

[Manual home](README.md)

## Execution lifecycle

Compilation lexes, parses, resolves imports, and validates declarations, references, types, and binding availability. It produces a validated plan or diagnostics. Before running, the host's registrations are checked against all declared tool contracts.

A run creates independent execution state, evaluates the workflow input predicate, executes items in order, resolves `finish with`, validates the result, evaluates the output predicate, and returns success. Failures stop execution. Agent activations have their own conversation and per-agent budget. Model-requested tool calls within one response execute sequentially.

The usual status path is `Created → Running → Succeeded` or `Failed`; cancellation passes through `Cancelling → Cancelled`. The public enum also contains `Suspended` and suspension reasons, but there is no MAILang pause/resume/approval syntax or usable suspension protocol in this release. Event-name constants for retries or approvals likewise do not establish implemented features.

Published values are immutable workflow bindings. Branches merge only compiler-approved shared bindings for later use; each loop iteration starts with fresh locals. Child workflows get an isolated binding scope and explicit input while sharing root execution budgets and cancellation.

## Limits

| Limit | C# defaults / CLI defaults | Integration mode (Python SDK) |
| --- | --- | --- |
| Model calls per agent | 5 | 10 |
| Tool calls per agent | 10 | 20 |
| Root deadline | 30 seconds | 5 minutes |
| Total model calls | 100 | 100 |
| Total tool attempts | 1,000 | 1,000 |
| Total activations | 1,000 | 1,000 |

CLI configuration changes its first three values; C# hosts can configure all execution limits. Integration mode currently hardcodes its limits. A loop also has its explicit positive `max` bound. Imports default to 100 modules and depth 20. Tool-argument list validation limits arrays to 1,000 items; this is not a universal limit on every list boundary.

Limits apply across nested workflow calls; invoking a child does not reset the root deadline or aggregate counts. Exceeding a budget fails the execution. Cancellation is cooperative: providers and tools should honor their token. A synchronous Python callback already running in a thread cannot be forcibly stopped. There is no automatic rollback, durable checkpoint, or retry of an external side effect.

## Validation boundaries

| Boundary | Current checks |
| --- | --- |
| Compiler | References, argument names/presence, expression categories, conditional types, binding scope, imports and workflow-call identity |
| Tool registration | Declared versus registered contracts, including unused tools |
| Direct tool call | Required input presence, input predicate, output predicate; implementation may add stronger validation |
| Agent tool call | Authorization/guard, argument contracts, required output presence; no tool predicate evaluation |
| Agent invocation | Input predicate when input is supplied, final declared output parsing, output predicate |
| Child workflow | Explicit declared input validation, predicates, output validation before publication |
| Root workflow | Predicates and final output validation; no complete root input validation before items run |

Agent-issued tool output presence checks are not complete recursive type checks; direct calls do not even perform that output-presence check in the executor. Validate inputs and results in host implementations. Agent final JSON parsing and child workflow value validation are separate boundaries, subject to the type limitations below.

## Events and diagnostics

C# `ExecutionResult.Events` exposes timestamped events with execution ID, sequence, optional step/activation/operation/attempt/call IDs, duration, and parent operation ID. Use execution ID to group a run, activation ID for a particular step activation, call ID to match model tool requests, and parent operation ID to follow child workflows. Not every field is populated on every event.

Useful event kinds include `execution.created`, `execution.started`, `activation.started`, `activation.confirmed`, `branch.selected`, `loop.started`, `loop.break`, `loop.completed`, `model.requested`, `tool.denied`, `tool.started`, `tool.completed`, `contract.rejected`, `subworkflow.started`, and `execution.ended`. The CLI prints readable events to stderr. The integration protocol returns a reduced event structure after execution; the Python SDK currently discards it.

Compiler diagnostics format as `[CODE] message (file:line:column)`. Imported-file locations are retained. The VS Code extension provides syntax highlighting, not a language server or cross-file navigation.

| Symptom / diagnostic | Action |
| --- | --- |
| `MAIL-PARSE`, unterminated string | Check fixed property order, braces, string escapes, and missing `save as`/`finish with` |
| `MAIL-E001`, `MAIL-E008` | Remove duplicate declarations/fields or shadowing bindings |
| `MAIL-E002` | Declare or import the referenced type |
| `MAIL-E003` / `MAIL-E004` / `MAIL-E005` | Fix the tool allow/call reference or agent name |
| `MAIL-E006` / `MAIL-E007` | Fix field names; ensure bindings exist on all paths before use |
| `MAIL-E009` / `MAIL-E010` | Match argument/result types |
| `MAIL-SEM` | Read the message: Boolean condition, incompatible branches, loop parameters, or another semantic restriction |
| `MAIL-IMPORT-NOT-FOUND`, `MAIL-IMPORT-CYCLE` | Correct relative paths or remove dependency cycles |
| `MAIL-IMPORT-ALIAS`, `MAIL-SYMBOL-NOT-FOUND` | Check aliases and direct imports; imports are not re-exported |
| `MAIL-ENTRY-WORKFLOW`, `MAIL-WORKFLOW-RECURSION` | Keep one entry workflow and remove recursive child calls |
| `MAIL-TOOL-NAME-COLLISION` | Give tools globally unique simple names |
| `MAIL-PROMPT-EMPTY`, `MAIL-PROMPT-DUPLICATE` | Supply one nonempty prompt after `model` |
| SDK `ToolError` | Register every declared tool, even unused imports |
| Agent output rejected | Return JSON matching declared fields/types, not Markdown fences or prose |
| Loop exhausted or fell through | Reach `break` within `max`; ensure each executed path breaks or continues |

Always read the diagnostic text as well as its code: some lexical failures use broad legacy codes. Registration failures may throw before an execution result exists.

## Current capability boundaries

- **Syntax versus values:** no object/list/enum/null/decimal literal, unary minus, arithmetic, indexing, interpolation, general function definitions, or assignment. Use host tools for transformations. Equality works for String/Bool/Int; other scalar equality is not implemented consistently.
- **Entry input:** CLI and integration mode decode a flat JSON object containing strings, booleans, and integers. Nested objects, arrays, and null become raw JSON strings; decimal/enum strings are not converted to their declared types. Non-object integration input becomes an empty schema. The CLI rejects duplicate root keys; integration keeps the first. Use a C# host with explicitly constructed and validated values for advanced input types.
- **Agent input:** declaring a typed input does not ensure callers supplied it or that it matches; explicitly pass correct input and include needed values in context. A missing input can skip the input predicate.
- **Static type checking:** tool-call arguments are checked for names, required presence, and valid expressions, but are not fully compared against declared field types. Direct dispatch checks required input presence without full type validation. Some control-flow/result checks compare broad type categories. Successful compilation alone does not guarantee all runtime values match their contracts.
- **Enums and complex tools:** agent output parsing/schema generation currently lacks enum metadata. CLI/integration tool registration maps Decimal to String, non-primitive references to Schema, and all fields to required; optional, nullable, list, enum, and decimal tool contracts can therefore fail registration. C# hosts can supply accurate contracts, but nested metadata and model-facing tool schemas remain shallow. Tool lists of enums, for example, lack the symbols needed for validation. Treat the type showcase as compilation coverage, not a promise of all combinations at all host boundaries.
- **Nullable serialization:** top-level `MailNull` serializes, but null values nested inside a schema/list are not handled by `SchemaConverter.ToJsonNode`. Runtime list parsing rejects null elements. Optional field access has no presence guard syntax.
- **Contracts:** direct calls evaluate tool predicates; agent-requested calls do not. Agent tool output checks only required-field presence; direct-call output checks depend on the implementation and explicit predicate. Implement critical checks inside tools.
- **Simulation:** simulated providers implement scripts or narrow adaptive behavior, not language-model reasoning. CLI and integration simulators differ. For arbitrary agent behavior, provide a custom C# provider or configure an external model.
- **Orchestration:** no parallel/fan-out syntax, automatic retries, persistent memory, workflow recursion, dynamic imports, package registry, pause/resume, approvals, or durable recovery. Host APIs/enums suggesting those concepts do not make them language features.

## Maintaining this manual

When adding language behavior, update the relevant guide and runnable example together. Validate complete `.mail` files and Markdown `mail` fences, run the offline Python/C# examples, and check relative links. Keep syntax fragments labeled so they cannot be mistaken for standalone programs. Recheck limitations against implementation changes rather than silently assuming a proposal has shipped.

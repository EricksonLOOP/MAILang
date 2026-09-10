# Agents and tools

[Manual home](README.md) · Next: [Workflows](workflows.md)

## Declare a tool

Tool declaration fragment:

```text
tool Echo {
    input { text: String }
    require input { input.text != "" }
    output { text: String }
    require output { output.text != "" }
}
```

The order is fixed: `input { fields }`, optional `require input`, `output { fields }`, optional `require output`. Both field blocks are required, but can be empty. A tool declares inline fields, not `input SchemaName` or an implementation body.

`require input { expression }` and `require output { expression }` each contain one Boolean expression. Combine predicates with `and`/`or`. Input predicates see `input`; output predicates can see `input` and `output`. For direct workflow calls, an input rejection prevents dispatch; an output rejection occurs after the tool has executed and cannot undo its effects.

Call fragment, inside a workflow:

```text
step echo {
    call Echo { text: input.text }
    save as reply
}
```

Arguments are named expressions. Required fields must be supplied, unknown arguments are rejected, and values must match the declared types. Supply each argument once. The result is a schema-like value with the tool's output fields, accessed as `reply.text`. See the complete [tools.mail](../examples/docs/tools.mail).

Implement the tool in the host. Python callback fragment:

```python
runtime.register_tool("Echo", lambda args: {"text": args["text"]})
```

Callbacks receive one dictionary, not keyword-expanded arguments, and return JSON-serializable output matching the declaration. Names are case-sensitive and must cover every declared tool in the imported program graph, even unused tools. See [Python integration](integrations.md#python-sdk) and [C# tools](csharp.md).

## Declare an agent

Agent declaration fragment:

```text
agent Writer {
    model assistant
    system "Call Echo and return its JSON result."
    input Request
    require input { input.text != "" }
    output Reply
    require output { output.text != "" }
    tools { allow Echo when input.text != "" }
}
```

The order is fixed: `model`, optional `system`, optional typed `input` with its predicate, mandatory `output` with its optional predicate, then mandatory `tools { ... }`. An empty `tools {}` permits an agent with no tools. Input and output use type references rather than inline field blocks. See the complete [agent.mail](../examples/docs/agent.mail).

`model assistant` is a logical binding name, not a quoted provider model ID. The host maps it to a registered provider and concrete model. Multiple agents can share a binding. Provider selection and credentials live in the host configuration.

`system` accepts one ordinary or triple-quoted string. Empty/whitespace-only prompts and duplicate system properties are rejected. The runtime appends its own instructions for using tools and returning JSON. Without `system`, it supplies a default prompt. Prompt text is literal: it does not interpolate workflow bindings.

Multiline prompt fragment:

```text
system """You summarize support requests.
Use only information from the supplied context.
Return the required JSON output."""
```

## Invoke an agent and supply context

Invocation fragment:

```text
step write {
    agent Writer input input context { input }
    save as reply
}
```

The first `input` introduces the argument; the second is the workflow's `input` binding. Typed agent input supplies data for predicates and tool guards. `context { names }` supplies the named values sent to the model. These are separate channels: passing typed input does not automatically place it in model context. Include it in `context` when the model needs it.

Context is mandatory syntactically but may be empty (`context {}`). Entries must be existing binding names, not arbitrary field expressions. For an agent without typed input, use `agent Writer context { input }`; an explicit input argument is rejected for such an agent. For typed agents, always pass a correctly typed value explicitly: current validation does not fully enforce this requirement.

Each invocation starts its own model conversation. The runtime sends a system message and a user context message, then repeats model requests, authorized tool dispatch, and tool results until the provider returns final JSON. There is no automatic conversation memory between steps. To carry knowledge forward, save a result and include its binding in the next agent's context.

## Tool permissions

Permission fragments:

```text
tools {
    allow Search
    allow Admin.Update when input.isAdmin
}
```

An agent can request only declared tools in its allow list. `when` must be Boolean and requires typed agent input. False guards filter advertised tools, and authorization is checked again before dispatch. Imported tools can use `Alias.Tool`; registration uses their simple, globally unique tool names.

Allow rules govern model-requested calls. A workflow's direct `call` does not go through an agent's allow list. Neither a prompt nor a tool declaration registers host code.

## Contract enforcement boundaries

Workflow and agent input/output predicates and tool argument contracts are distinct checks. Current behavior is not uniform:

- Direct tool steps evaluate the tool's `require input` and `require output` predicates.
- Agent-issued tool calls validate arguments and required output-field presence, but do not evaluate the tool declaration's predicates. Put critical checks in the tool implementation too.
- Agent input predicates run only when an input expression is supplied; typed input compatibility is not fully checked by the compiler.
- Agent final output is parsed against its declared type, but enum metadata is not passed into this path, so enum-based agent output is currently unsupported.

These are implementation limitations, not additional syntax. See [runtime boundaries](runtime.md#current-capability-boundaries) before relying on a contract as a complete validation layer.

# Workflows and control flow

[Manual home](README.md) · Next: [Integrations](integrations.md)

## Create a workflow

Workflow template (replace uppercase placeholders with declarations/expressions):

```text
workflow Name {
    input INPUT_TYPE
    require input { INPUT_CONDITION }
    output OUTPUT_TYPE
    require output { OUTPUT_CONDITION }
    WORKFLOW_ITEMS
    finish with RESULT_EXPRESSION
}
```

Input/output types and final `finish with` are required. Each `require` block is optional and must immediately follow the matching type. Workflow items are steps, workflow-level conditionals, and loops. `finish with` occurs once at the end; it is not a general early-return statement.

Items execute sequentially. A binding published by `save as` becomes available to later items. There is no mutable assignment, implicit global state, or forward reference to future results. Step/loop result bindings cannot shadow existing bindings. A step name identifies execution; its `save as` name identifies the value.

## Step bodies

A step executes exactly one body and saves its result:

```text
step name {
    BODY
    save as result
}
```

`BODY` is one of these fragments:

```text
call Tool { field: expression }
agent Agent input expression context { binding1, binding2 }
workflow Alias.Child input expression
if condition { BODY } else { BODY }
```

Agent input is optional in the grammar; context is required. Tool argument blocks can be empty. There is no expression-only step or inline tool implementation. Use `finish with expression` to return a computed scalar or a previously supplied/produced schema value.

## Three conditional forms

### Workflow conditional: choose a sequence

Fragment from [routing.mail](../examples/docs/routing.mail):

```text
if input.urgent {
    step choose { call Echo { text: "urgent" } save as selected }
} else {
    step choose { call Echo { text: "normal" } save as selected }
}
```

Each branch may contain multiple workflow items, including nested conditionals and loops. `else` is optional. Only the chosen branch executes. A new binding is available after the conditional only when both branches publish it with compatible types. A binding created in one branch alone is unavailable afterward.

### Step conditional: choose one value-producing body

Fragment:

```text
step route {
    if input.urgent {
        call Echo { text: "urgent" }
    } else {
        call Echo { text: "normal" }
    }
    save as reply
}
```

Both branches are mandatory and must produce compatible types. Each branch contains a step body, not a list of steps or its own `save as`. The enclosing step publishes the chosen value once. Bodies can themselves be conditional.

### Inline conditional: choose an expression

Fragment:

```text
call Echo { text: if input.urgent then "urgent" else "normal" }
```

Both expressions are mandatory and must have compatible types. Only the selected expression is evaluated. This form works where an expression is expected, such as an argument or `finish with`.

## Bounded loops

Loop fragment from [loop.mail](../examples/docs/loop.mail):

```text
loop count {
    params { value: Int = 0 }
    output Int
    max 4
    if value >= input.target {
        break value
    } else {
        step increment { call Increment { value: value } save as next }
        continue { value: next.value }
    }
    save as total
}
```

The order is `params`, `output`, `max`, body items, then `save as`. Parameters have explicit types and initial expressions evaluated in the outer scope. `max` is a positive integer literal, not an expression.

`break expression` exits the nearest loop and produces its declared output. `continue { name: expression ... }` starts the next iteration with a complete set of replacement parameters; missing or unknown parameter names are rejected. The loop's `save as` publishes the break value outside the loop.

Each iteration sees outer bindings and current parameters, with fresh local step bindings. Iteration-local results do not escape. Parameters cannot shadow outer names; nested loops are allowed. Every executed path must reach `break` or `continue`; falling through the body fails at runtime. Continuing on the last permitted iteration fails rather than returning a partial value.

The example uses an external `Increment` tool because MAILang has no arithmetic expression. Target `3` takes four iterations (three increments, then a break); target `4` exceeds `max 4`. There is no `for`, `while`, unbounded loop, or implicit retry statement.

## Imports and child workflows

Import/declaration fragments:

```text
import "types.mail" as Types
import "workers.mail" as Workers
import "child.mail" as Child

workflow Main {
    input Types.Request
    output Types.Reply
    step delegate {
        workflow Child.Process input input
        save as reply
    }
    finish with reply
}
```

Imports must precede all declarations. Paths are local relative `.mail` paths resolved from the importing file, not the shell working directory. Spaces and Unicode are supported. Absolute paths, remote URLs, package imports, and non-`.mail` files are unsupported. Parent-relative paths are local paths, not a module sandbox.

Aliases are case-sensitive and local to one module. Use `Alias.Type`, `Alias.Tool`, `Alias.Agent`, and `workflow Alias.Workflow input expression`. Imports are not re-exported; import a dependency directly in each file that uses it. There is no wildcard import or export declaration. Tool registration names must remain unique across the whole module graph.

The entry file has exactly one workflow; a module may have many. Child invocation requires the qualified `Alias.Name` form. Child input must match the declared type, including nominal schema identity: two separately declared schemas with identical fields are not interchangeable at this boundary. Share a type module to share identity.

Children receive only their explicit input, not parent bindings. Their output is validated before the parent result binding is published. Parent and children share execution ID, cancellation, deadline, and aggregate budgets. Import cycles and workflow recursion are rejected; all imported workflows are validated even when a branch would never execute them.

Run the existing [composition example](../examples/composition/main.mail) with [its Python runner](../examples/composition/run.py). It demonstrates shared types, imported agents and tools, conditional routing, and child workflow invocation. See [example commands](../examples/docs/README.md).

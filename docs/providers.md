# Providers

[Manual home](README.md) · [Agents and tools](agents-and-tools.md) · [Integrations](integrations.md)

A `provider` block declares how MAILang communicates with a model. Every `agent` declaration references exactly one `provider`; multiple agents can share one block. Provider declarations appear in `.mail` files alongside the schemas, tools, and agents that use them.

## Simulated provider

```text
provider Sim {
  type simulated
}
```

The simulated provider does not call an external model. When a script is supplied via `provider_config` at runtime, the simulator replays those steps in order. When no script is supplied, the simulator uses adaptive behavior: it calls the first available tool with context-derived arguments, then returns the latest tool result as the final JSON.

Configure a deterministic script in the Python SDK:

```python
provider_config={"providers": {"Sim": {"script": [
    {"type": "tool_call", "tool": "Echo", "args": {"text": "hello"}},
    {"type": "text", "template": "last_tool_result"}
]}}}
```

The script is consumed in order across model requests within one run. Exhaustion fails execution. Only `tool_call` and `text` with `template: "last_tool_result"` are supported. See [integrations](integrations.md#provider-configuration) for the flat legacy format and MAIL-PROTO-002.

## HTTP provider

An HTTP provider calls an external API. Only POST is supported (MAIL-CONFIG-003 for any other method). Credentials are resolved from the OS environment or the `.env` file at startup and are never logged.

```text
provider DeepSeek {
  base_url "https://api.deepseek.com"
  api_key  env("DEEPSEEK_API_KEY")
  call {
    ...
  }
  response {
    ...
  }
}
```

All four properties — `base_url`, `api_key`, `call`, and `response` — are required (MAIL-SEM-P03).

`base_url` is the root URL; the call `path` is appended to it. `api_key env("VAR")` reads the named environment variable; embedding a string literal is rejected (MAIL-SEM-P10). If the variable is absent or empty at startup, the run fails with MAIL-CONFIG-002.

## `call` block

Defines the HTTP request sent to the provider on each model turn.

```text
call {
  method POST
  path   "/v1/chat/completions"
  headers {
    "Content-Type"  "application/json"
    "Authorization" "Bearer " + api_key
  }
  body {
    "model"    $model
    "messages" $messages { ... }
    "tools"    $tools    { ... }
  }
}
```

`method` defaults to POST and is the only accepted value. `path` is appended to `base_url`.

### Header values

Each header entry is `"Header-Name" value`. A value is one of:

- A string literal: `"application/json"`
- A field reference: `api_key` or `base_url`
- Parts joined with `+`: `"Bearer " + api_key`

Duplicate header names are rejected (MAIL-SEM-P04).

### Body values

Each body entry is `"key" value`. A value is one of:

- String literal: `"gpt-4o"`
- Integer literal: `128`
- Boolean: `true` or `false`
- Nested object: `{ "key" value ... }`
- Array literal: `[ item item ... ]` — items optionally separated by commas; may contain any value type including nested arrays and objects
- Interpolation: `$varName`
- Structured interpolation: `$messages { ... }`, `$tools { ... }`, `$calls { ... }`
- Spread (inside `[ ]` only): `...$calls { ... }` — expands each tool call in the current assistant message as a separate element; zero elements when there are no calls
- Conditional element (inside `[ ]` only): `?$text { ... }` — emits the element object only when the message text is non-null and non-empty

Duplicate body keys are rejected (MAIL-SEM-P05). Nested arrays are never flattened; `[ [ 1 2 ] ]` produces an array containing an array.

### Interpolation variables

Interpolations are context-sensitive. `$messages`, `$tools`, and `$calls` open a new scope where their own variables are available.

**Root context** (direct body fields):

| Variable | Produces |
| --- | --- |
| `$model` | Model ID string |
| `$system` | System prompt text; `null` when no system prompt |
| `$output_schema` | Agent expected-output schema as a JSON string |
| `$messages { type → { ... } }` | Array of message objects; one object per message in the conversation, mapped by type |
| `$tools { "key" value ... }` | Array of tool definition objects; omitted (`null`) when the agent has no available tools |

**Inside `$messages { ... }` mapping bodies** (type keys: `system`, `user`, `assistant`, `tool_result`):

| Variable | Available in | Produces |
| --- | --- | --- |
| `$text` | `system`, `user`, `assistant` | Message text; `null` when an assistant message has no text |
| `$call_id` | `tool_result` | Correlation call ID |
| `$tool_name` | `tool_result` | Tool name |
| `$result` | `tool_result` | Tool result as a parsed JSON node |
| `$result_json` | `tool_result` | Tool result as a JSON string |
| `$calls { "key" value ... }` | `assistant` only | Array of tool call objects; omitted when the message has no tool calls |
| `...$calls { ... }` (inside `[ ]`) | `assistant` only | Spreads tool calls as individual array elements; zero elements when none |
| `?$text { ... }` (inside `[ ]`) | `system`, `user`, `assistant` | Emits the element object only when text is non-null and non-empty |

`$text` is not a valid variable for `tool_result`; use `$result` or `$result_json` instead (MAIL-SEM-P07).  
`$calls` and `...$calls` are only valid inside the `assistant` message mapping (MAIL-SEM-P12, MAIL-SEM-P13).  
`?$text` is not valid inside `tool_result` mappings (MAIL-SEM-P14).  
Duplicate message type keys in a `$messages` block are rejected (MAIL-SEM-P06).

**Inside `$tools { ... }` item template** (one object rendered per available tool):

| Variable | Produces |
| --- | --- |
| `$name` | Tool name |
| `$input_schema` | Tool input schema as a JSON node |
| `$output_schema` | Tool output schema as a JSON node |

**Inside `$calls { ... }` item template** (one object rendered per tool call in an assistant message):

| Variable | Produces |
| --- | --- |
| `$call_id` | Call ID |
| `$tool_name` | Tool name |
| `$args` | Arguments as a JSON object |
| `$args_json` | Arguments as a JSON string |

Unknown interpolation variables are rejected at compile time (MAIL-SEM-P07).

## `response` block

Defines how to extract text and tool calls from the HTTP response.

```text
response {
  finish_reason {
    path       "choices[0].finish_reason"
    stop       "stop"
    tool_calls "tool_calls"
  }
  text "choices[0].message.content"
  tool_calls "choices[0].message.tool_calls[*]" {
    id        "id"
    tool_name "function.name"
    args      "function.arguments"
  }
}
```

The `response` block must declare at least `text` or `tool_calls` (MAIL-SEM-P08).

**`finish_reason` block** (optional): reads a scalar from the response to determine the branch.

- `path` — selector for the finish-reason field.
- `stop` — the string value that means "return text."
- `tool_calls` — the string value that means "parse tool calls."

When `finish_reason` is omitted, the runtime infers the branch from the presence of data at the `tool_calls` selector.

**`text`** — selector for the response text. When the selector matches multiple string nodes they are joined with `\n` and a MAIL-WARN is written to stderr.

**`tool_calls`** — outer selector followed by a block of sub-selectors applied per item:
- `id` — call ID.
- `tool_name` — tool name.
- `args` — arguments; may be a JSON object or a JSON string encoding an object.

All selectors are validated at compile time (MAIL-SEM-P09).

## Selector syntax

Selectors navigate JSON using dot-separated segments with optional bracket expressions:

| Syntax | Meaning |
| --- | --- |
| `field` | Object property |
| `field.nested` | Nested property |
| `field[0]` | Zero-based array index |
| `field[*]` | Wildcard — fans out over all array items |
| `field[key=value]` | Filter — keeps items where string property `key` equals `value` |

Segments can be chained: `choices[0].message.tool_calls[*]` navigates to the first choice, then to message, then fans out over all tool calls. Filter and wildcard segments increase cardinality; field and index segments reduce it.

Response body reads up to 4 MiB; larger responses fail with MAIL-PROVIDER-003.

## Assigning a provider to an agent

Inside an agent declaration, `provider` must come before `model`:

```text
agent Writer {
  provider DeepSeek
  model    "deepseek-chat"
  system   "..."
  output   Reply
  tools    { allow Echo }
}
```

`provider` is the name of a declared `provider` block in the same file or via an import alias. `model` is the literal model ID string forwarded to that provider on every request. Multiple agents can share one `provider` block.

## Error reference

| Code | Condition |
| --- | --- |
| `MAIL-SEM-P01` | Agent references an undeclared provider |
| `MAIL-SEM-P02` | Agent has an empty model ID |
| `MAIL-SEM-P03` | HTTP provider is missing `base_url`, `api_key`, `call`, or `response` |
| `MAIL-SEM-P04` | Duplicate header name in `call.headers` |
| `MAIL-SEM-P05` | Duplicate key in a body field list |
| `MAIL-SEM-P06` | Duplicate message type in a `$messages` block |
| `MAIL-SEM-P07` | Unknown interpolation variable for the current context |
| `MAIL-SEM-P08` | `response` block declares neither `text` nor `tool_calls` |
| `MAIL-SEM-P09` | Invalid selector path syntax |
| `MAIL-SEM-P10` | `api_key` uses a string literal instead of `env("VAR")` |
| `MAIL-SEM-P11` | Agent uses old `model identifier` syntax without a `provider` line |
| `MAIL-SEM-P12` | `$calls` used outside an `assistant` message mapping |
| `MAIL-SEM-P13` | `...$calls` used outside an `assistant` message mapping |
| `MAIL-SEM-P14` | `?$text` used in a `tool_result` mapping or at root body level |
| `MAIL-CONFIG-002` | API key env var is absent or empty at runtime |
| `MAIL-CONFIG-003` | HTTP provider method is not POST |
| `MAIL-PROVIDER-003` | HTTP response body exceeds 4 MiB |
| `MAIL-WARN` | Text selector matched multiple strings; concatenated with newline |

## Anthropic native Messages API

The `examples/providers/anthropic.mail` file contains a ready-to-use Anthropic provider. It uses array literals, spread, and conditional elements to meet Anthropic's content-block format:

```mail
assistant -> {
  "role"    "assistant"
  "content" [
    ?$text { "type" "text" "text" $text }
    ...$calls {
      "type"  "tool_use"
      "id"    $call_id
      "name"  $tool_name
      "input" $args
    }
  ]
}
tool_result -> {
  "role"    "user"
  "content" [
    {
      "type"        "tool_result"
      "tool_use_id" $call_id
      "content"     $result_json
    }
  ]
}
```

**Scope:** supports text messages and JSON tool cycles (call → result → response). Tool arguments must be JSON objects.

**Limitations:**
- `thinking` content blocks are not preserved — the `ModelResponse` contract does not carry them.
- When the model returns interleaved text and tool_use blocks (e.g., text, tool_use, text), the original order is not maintained. The template always serializes text before tool_use blocks in the assistant content array.

## Complete example

The following is a working OpenAI-compatible provider declaration, an agent that uses it, and a matching workflow. Set `DEEPSEEK_API_KEY` (or another OpenAI-compatible key) in the OS environment or `.env` before running.

```mail
schema Request { text: String }
schema Reply   { text: String }

tool Echo {
  input  { text: String }
  output { text: String }
}

provider DeepSeek {
  base_url "https://api.deepseek.com"
  api_key  env("DEEPSEEK_API_KEY")
  call {
    method POST
    path   "/v1/chat/completions"
    headers {
      "Content-Type"  "application/json"
      "Authorization" "Bearer " + api_key
    }
    body {
      "model"    $model
      "messages" $messages {
        system    -> { "role" "system"    "content" $text }
        user      -> { "role" "user"      "content" $text }
        assistant -> {
          "role"       "assistant"
          "content"    $text
          "tool_calls" $calls {
            "id"   $call_id
            "type" "function"
            "function" {
              "name"      $tool_name
              "arguments" $args_json
            }
          }
        }
        tool_result -> {
          "role"         "tool"
          "tool_call_id" $call_id
          "content"      $result_json
        }
      }
      "tools" $tools {
        "type"     "function"
        "function" {
          "name"       $name
          "parameters" $input_schema
        }
      }
    }
  }
  response {
    finish_reason {
      path       "choices[0].finish_reason"
      stop       "stop"
      tool_calls "tool_calls"
    }
    text "choices[0].message.content"
    tool_calls "choices[0].message.tool_calls[*]" {
      id        "id"
      tool_name "function.name"
      args      "function.arguments"
    }
  }
}

agent Writer {
  provider DeepSeek
  model    "deepseek-chat"
  system   "Call Echo with the supplied text and return its result as JSON."
  input    Request
  output   Reply
  tools    { allow Echo }
}

workflow EchoViaAgent {
  input  Request
  output Reply
  step write {
    agent Writer input input context { input }
    save as reply
  }
  finish with reply
}
```

### Execution cycle

When the workflow reaches the `agent Writer` step:

1. The runtime builds a `ModelRequest` containing the system prompt, the user context, and the Echo tool definition. The provider constructs a POST body from the `call` template and sends it to `https://api.deepseek.com/v1/chat/completions`.

2. The model returns a response with `finish_reason: "tool_calls"` and one tool call for Echo. The provider parses the call using the `tool_calls` selector, extracts the call ID, tool name, and arguments, and returns a `ModelResponse` with those tool calls.

3. The runtime dispatches Echo (via the registered host callback), collects the result, and sends a follow-up request. The `tool_result` message mapping serializes the result as `"content": result_json`.

4. The model produces a final text response (`finish_reason: "stop"`). The provider extracts it via the `text` selector and returns it as the agent output.

5. The output is parsed against `Reply` and the workflow continues to `finish with reply`.

Validate the example before running:

```powershell
& $env:MAIL_EXE validate examples/my-agent.mail
```

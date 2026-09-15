# CLI and SDKs

[Manual home](README.md) · [C# embedding](csharp.md) · [Protocol](protocol.md)

## CLI

After the [build/setup](getting-started.md), use:

```text
mail validate file.mail
mail run file.mail [--input JSON]
mail integrate
```

Here `mail` represents the built executable, named `Mail.Cli.exe` on Windows. `validate` compiles the entire module graph without executing tools or models. Successful validation exits 0; compiler or execution failure generally exits 1; usage errors and missing files exit 2. Malformed configuration/input can surface as unhandled exceptions rather than these normalized codes.

`run` prints final output JSON to stdout and diagnostics/events to stderr. `integrate` needs no file argument and reserves stdout for protocol messages. `--input` takes JSON text, not a filename. In PowerShell, read a fixture into a variable and pass that variable, as shown in getting started.

Providers and model bindings are declared directly in the `.mail` file using `provider` blocks. A simulated provider requires no credentials:

```mail
provider Sim {
  type simulated
}

agent MyAgent {
  provider Sim
  model "my-model"
  ...
}
```

An HTTP provider is self-described in the file (base URL, API key env var, call/response mappings). Credentials are resolved from the OS environment and from a `.env` file in the working directory at startup. `Limits` can be supplied via the `--limits` flag or left at defaults (10 tool calls and 5 model calls per agent, 5-minute timeout).

The standalone tool registry supplies a special `GenerateToken` implementation and demonstration echo implementations for other declared tools. It does not discover your Python functions. The ordinary CLI simulator scripts only the first top-level agent step; it is not a general workflow simulator. Use the SDK tutorial for deterministic multi-step execution.

## Python SDK

### Installation

**Bundled CLI (default):** Install from PyPI and no further setup is required:

```bash
pip install mail-runtime
```

The package includes a platform-specific CLI binary resolved automatically at runtime. Supported platforms: Windows x64, Linux x64, macOS x64/arm64. On an unsupported platform `MailRuntime()` raises `ProcessError` with a descriptive message.

**From source (contributors / unsupported platforms):** Build the CLI and point the SDK at it:

```powershell
dotnet build Mail.slnx
$env:MAIL_EXE = (Resolve-Path src/Mail.Cli/bin/Debug/net10.0/Mail.Cli.exe).Path
pip install -e sdk/python
```

Pass the path when constructing the runtime:

```python
import os
from mail_runtime import MailRuntime

async with MailRuntime(executable_path=os.environ["MAIL_EXE"]) as rt:
    ...
```

### Public API

```python
MailRuntime(executable_path: str | None = None)
runtime.register_tool(name: str, fn: Callable) -> None
await runtime.run(path: str, *, input: dict, provider_config: dict | None = None)
```

Use `async with` to start the CLI's `integrate` subprocess, complete protocol handshake, and clean up the process. `run` resolves the path to an absolute path, loads/compiles it, checks registrations for all declared tools, then requests execution (which recompiles). The return annotation says `dict`, but scalar workflow output is returned as the corresponding decoded JSON value; the loop tutorial returns an integer.

Complete callback/agent usage is in [run.py](../examples/docs/run.py). Minimal host snippet using the bundled CLI:

```python
import asyncio
from mail_runtime import MailRuntime

async def main():
    async with MailRuntime() as runtime:
        runtime.register_tool("Echo", lambda args: {"text": args["text"]})
        result = await runtime.run("examples/docs/agent.mail", input={"text": "hello"})
        print(result)  # {'text': 'hello'}

asyncio.run(main())
```

Synchronous callbacks run in a thread pool. `async def` callbacks are awaited. Multiple tool requests can be handled without blocking the protocol reader; make shared callback state safe for concurrency. Re-registering the same name replaces its callback. A callback exception is sent as `tool_error` and fails the execution.

### Provider configuration

Providers and model bindings are declared in the `.mail` file using `provider` blocks; see [Providers](providers.md). HTTP providers read credentials from the OS environment or the `.env` file and need no `provider_config`.

For simulated providers, pass a script to `run` so the simulator makes deterministic tool calls instead of using adaptive behavior. There are two `provider_config` shapes:

**Namespaced (recommended):** works for any number of simulated providers. The key under `providers` must match the name declared in the `.mail` file; an unknown name produces MAIL-PROTO-003.

```python
provider_config={"providers": {"Sim": {"script": [
    {"type": "tool_call", "tool": "Echo", "args": {"text": "hello"}},
    {"type": "text", "template": "last_tool_result"}
]}}}
```

**Flat (legacy):** `provider_config={"script": [...]}` is accepted only when the plan has exactly one `type simulated` provider. Raises MAIL-PROTO-002 when the count is anything other than one.

The script is consumed across model requests within a run. Exhaustion fails execution. Only `tool_call` and `text` with `template: "last_tool_result"` are supported; arbitrary text templates are not implemented.

### `.env` file

The CLI and `integrate` mode both load a `.env` file from the working directory at startup. An absent file is silently ignored. This file is the standard way to supply API keys for HTTP providers without exposing them in the `.mail` source.

**Format rules:**

- One entry per line: `KEY=value`. The key is everything before the first `=`; the value is everything after. Keys and values are **not trimmed** — `KEY = value` produces a key with a trailing space.
- Lines whose first non-whitespace character is `#` are full-line comments. There are no inline comments; `KEY=value # note` stores `# note` as part of the value.
- Blank lines are ignored. UTF-8 BOM on the first character is silently stripped.
- Double-quoted values (`KEY="value"`) support `\"` and `\\` escapes; the closing `"` must be on the same line.
- Duplicate keys: last occurrence wins.
- Empty string values (`KEY=` or `KEY=""`) are treated as absent.
- **Resolution order:** OS environment variable takes precedence over the `.env` file. A variable already set in the OS environment is never overridden by the file.

Errors: MAIL-ENV-001 (line missing `=`), MAIL-ENV-002 (unclosed double-quote).

### Errors and cancellation

| Exception exported by `mail_runtime` | Meaning |
| --- | --- |
| `MailError` | Base class for SDK errors |
| `ProtocolError` | Rejected handshake or fatal protocol error |
| `LoadError` | Compilation failed; inspect `.diagnostics` |
| `RunError` | Execution failed; inspect `.error` |
| `ToolError` | A declared tool has no registered callback |
| `ProcessError` | Process/reader failure or handshake/load timeout |

Diagnostic objects contain `severity`, `code`, and `message`; currently protocol codes may be null with the formatted code/location embedded in the message. Native Python/OS exceptions can also escape process creation or user code setup.

To cancel, create an `asyncio.Task` for `runtime.run(...)`, cancel that task, and handle `asyncio.CancelledError`. The SDK sends a cancellation request. Already-running synchronous callbacks cannot be forcibly interrupted and may still complete. The SDK exposes no public event callback, `load`, pause, or resume API. Runtime events arrive in a batch and are ignored by the current SDK.

Handshake timeout is 10 seconds; load timeout is 30 seconds. Integration runs use a 5-minute execution timeout, 10 model calls and 20 tool calls per agent, with shared aggregate limits described in [runtime behavior](runtime.md).

## Java SDK

The [Java SDK](../sdk/java/README.md) supports Java 17+, synchronous and asynchronous
tool callbacks, concurrent executions, cancellation and automatic subprocess cleanup.
It communicates with `mail integrate` using protocol v1. The Maven build can bundle
the CLI so `MailRuntime.start()` locates and starts it without a separate installation
or PATH configuration. See the SDK README for local Maven installation, binary
packaging and integration tests. The artifact is not yet published to Maven Central.

## JavaScript

[sdk/js/poc.js](../sdk/js/poc.js) is a protocol proof of concept, not a supported SDK with the Python API. Use it as a starting point for a client of the [documented protocol](protocol.md).

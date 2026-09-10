# CLI and Python SDK

[Manual home](README.md) · [C# embedding](csharp.md) · [Protocol](protocol.md)

## CLI

After the [build/setup](getting-started.md), use:

```text
mail validate file.mail
mail run file.mail [--provider simulated|deepseek] [--input JSON]
mail integrate
```

Here `mail` represents the built executable, named `Mail.Cli.exe` on Windows. `validate` compiles the entire module graph without executing tools or models. Successful validation exits 0; compiler or execution failure generally exits 1; usage errors and missing files exit 2. Malformed configuration/input can surface as unhandled exceptions rather than these normalized codes.

`run` prints final output JSON to stdout and diagnostics/events to stderr. `integrate` needs no file argument and reserves stdout for protocol messages. `--input` takes JSON text, not a filename. In PowerShell, read a fixture into a variable and pass that variable, as shown in getting started.

Standalone `run` reads `appsettings.json` from the current working directory. Example configuration:

```json
{
  "Provider": "simulated",
  "ModelBindings": {
    "assistant": {"Provider": "deepseek", "ModelId": "YOUR_MODEL_ID"}
  },
  "Limits": {
    "MaxToolCallsPerAgent": 10,
    "MaxModelCallsPerAgent": 5,
    "TimeoutSeconds": 30
  }
}
```

The CLI provider flag overrides `Provider`. With `simulated`, logical models are bound to the simulator. With `deepseek`, a matching configured binding is used; otherwise the CLI uses `DEEPSEEK_MODEL`, falling back to the model string `deepseek-v4-flash` currently in source. `DEEPSEEK_API_KEY` supplies credentials. These are implementation defaults, not a guarantee of external model availability; set the desired model explicitly.

The standalone tool registry supplies a special `GenerateToken` implementation and demonstration echo implementations for other declared tools. It does not discover your Python functions. The ordinary CLI simulator scripts only the first top-level agent step; it is not a general workflow simulator. Use the SDK tutorial for deterministic multi-step execution.

## Python SDK

Install from the repository with `python -m pip install -e sdk/python`, or set `PYTHONPATH` as in [getting started](getting-started.md). There are no required third-party runtime dependencies.

The public API is:

```python
MailRuntime(executable_path: str, *, provider: str = "simulated")
runtime.register_tool(name: str, fn: Callable) -> None
await runtime.run(path: str, *, input: dict, provider_config: dict | None = None)
```

Use `async with` to start the CLI's `integrate` subprocess, complete protocol handshake, and clean up the process. `run` resolves the path to an absolute path, loads/compiles it, checks registrations for all declared tools, then requests execution (which recompiles). The return annotation says `dict`, but scalar workflow output is returned as the corresponding decoded JSON value; the loop tutorial returns an integer.

Complete callback/agent usage is in [run.py](../examples/docs/run.py). Minimal host snippet, assuming `MAIL_EXE` is configured and commands run from the repository root:

```python
import asyncio
import os
from mail_runtime import MailRuntime

async def main():
    async with MailRuntime(os.environ["MAIL_EXE"]) as runtime:
        runtime.register_tool("Echo", lambda args: {"text": args["text"]})
        result = await runtime.run("examples/docs/agent.mail", input={"text": "hello"})
        print(result)  # {'text': 'hello'}

asyncio.run(main())
```

Synchronous callbacks run in a thread pool. `async def` callbacks are awaited. Multiple tool requests can be handled without blocking the protocol reader; make shared callback state safe for concurrency. Re-registering the same name replaces its callback. A callback exception is sent as `tool_error` and fails the execution.

### Provider configuration

The integration simulator requests the first available tool using context-derived arguments, then returns the latest tool result as final JSON. It does not interpret prompts. For deterministic multi-call behavior, pass this fragment to `run`:

```python
provider_config={"script": [
    {"type": "tool_call", "tool": "Echo", "args": {"text": "hello"}},
    {"type": "text", "template": "last_tool_result"}
]}
```

The script is consumed across model requests within that run. Exhaustion fails execution. Only `tool_call` and `text` with template `last_tool_result` are supported; arbitrary text templates are not implemented.

For an external model, instantiate `MailRuntime(executable_path, provider="deepseek")` and pass `provider_config={"model": "YOUR_MODEL_ID"}`. Set `DEEPSEEK_API_KEY` in the environment, or supply `api_key` in the config. Integration mode defaults to `deepseek-chat` when no model is provided; it does not read CLI `appsettings.json` or use `DEEPSEEK_MODEL`. These calls require credentials and network access and are not used in the offline tutorials.

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

## JavaScript

[sdk/js/poc.js](../sdk/js/poc.js) is a protocol proof of concept, not a supported SDK with the Python API. Use it as a starting point for a client of the [documented protocol](protocol.md).

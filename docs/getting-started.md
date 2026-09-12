# Getting started

[Manual home](README.md) · Next: [Syntax and types](syntax.md)

## Install the SDK

The Python SDK bundles the MAIL CLI — installing the package is all that is required:

```bash
pip install mail-runtime
```

Python 3.11 or later is required. Platform wheels are published for Windows x64. Linux and macOS wheels will be added in future releases; on those platforms you can install from source (see below).

After installation the CLI is immediately available through the SDK:

```python
from mail_runtime import MailRuntime

async with MailRuntime() as rt:
    result = await rt.run("workflow.mail", input={...})
```

## Build from source (contributors / unsupported platforms)

Install the .NET 10 SDK. Run commands from the `MAILang` repository directory (the directory containing `Mail.slnx`):

```powershell
# Windows — build and set MAIL_EXE for the SDK
dotnet build Mail.slnx
$env:MAIL_EXE = (Resolve-Path src/Mail.Cli/bin/Debug/net10.0/Mail.Cli.exe).Path
pip install -e sdk/python
```

On Linux/macOS set `MAIL_EXE` to `src/Mail.Cli/bin/Debug/net10.0/Mail.Cli` (no `.exe` extension). Pass it as an override when constructing the runtime:

```python
import os
from mail_runtime import MailRuntime

async with MailRuntime(executable_path=os.environ["MAIL_EXE"]) as rt:
    ...
```

The Python SDK needs an absolute path to the executable, not a command string such as `dotnet Mail.Cli.dll`.

## Write a first workflow

[hello.mail](../examples/docs/hello.mail) is a complete program:

```mail
schema Message { text: String }
workflow Hello {
    input Message
    output Message
    finish with input
}
```

`schema` names a data shape. `workflow` declares the entry point's input and output types. The built-in binding `input` contains the supplied value. `finish with` returns an expression; here it returns the original message. No agent or tool is needed for this workflow.

Validate and run it:

```powershell
& $env:MAIL_EXE validate examples/docs/hello.mail
$payload = Get-Content examples/docs/hello.input.json -Raw
& $env:MAIL_EXE run examples/docs/hello.mail --input $payload
```

Validation prints `No errors found.` The run prints this JSON to stdout, with execution events on stderr:

```json
{"text":"Hello, MAILang!"}
```

## Connect real tool implementations

Run the offline tutorials (requires the SDK installed or `MAIL_EXE` set as described above):

```bash
python examples/docs/run.py
```

The runner registers an `Echo` callback and an asynchronous `Increment` callback, then checks a direct tool call, an agent, both routing branches, loop completion, and an expected iteration-limit failure. It requires no API key.

A tool declaration is a contract, not Python code. `register_tool("Echo", callback)` connects that contract to a function. A direct `call Echo` is deterministic; an `agent` asks a model provider to choose tool calls and produce a result. The tutorial's simulated provider calls the available tool and returns its result.

## Continue learning

Read the [example catalog](../examples/docs/README.md) for expected outputs and imported workflows. Then follow [agents and tools](agents-and-tools.md) and [workflows](workflows.md) to write your own programs. Use [C# embedding](csharp.md) for fully typed host input and custom providers.

The standalone CLI registers demonstration tool implementations. Use the Python SDK or C# embedding to execute your own tools. See [integration limitations](runtime.md#current-capability-boundaries) before using nested or non-primitive workflow inputs.

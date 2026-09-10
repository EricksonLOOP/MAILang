# Getting started

[Manual home](README.md) · Next: [Syntax and types](syntax.md)

## Build the runtime

Install the .NET 10 SDK. Python examples require Python 3.11 or later. Run commands from the `MAILang` repository directory (the directory containing `Mail.slnx`). These commands use PowerShell:

```powershell
dotnet build Mail.slnx
$env:MAIL_EXE = (Resolve-Path src/Mail.Cli/bin/Debug/net10.0/Mail.Cli.exe).Path
```

On Linux/macOS the apphost is `src/Mail.Cli/bin/Debug/net10.0/Mail.Cli` without `.exe`; set `MAIL_EXE` to its absolute path. The Python SDK needs an executable path, not a command string such as `dotnet Mail.Cli.dll`.

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

Set the local SDK path and run the offline tutorials:

```powershell
$env:PYTHONPATH = (Resolve-Path sdk/python).Path
python examples/docs/run.py
```

Alternatively, install the SDK with `python -m pip install -e sdk/python`. The runner registers an `Echo` callback and an asynchronous `Increment` callback, then checks a direct tool call, an agent, both routing branches, loop completion, and an expected iteration-limit failure. It requires no API key.

A tool declaration is a contract, not Python code. `register_tool("Echo", callback)` connects that contract to a function. A direct `call Echo` is deterministic; an `agent` asks a model provider to choose tool calls and produce a result. The tutorial's simulated provider calls the available tool and returns its result.

## Continue learning

Read the [example catalog](../examples/docs/README.md) for expected outputs and imported workflows. Then follow [agents and tools](agents-and-tools.md) and [workflows](workflows.md) to write your own programs. Use [C# embedding](csharp.md) for fully typed host input and custom providers.

The standalone CLI registers demonstration tool implementations. Use the Python SDK or C# embedding to execute your own tools. See [integration limitations](runtime.md#current-capability-boundaries) before using nested or non-primitive workflow inputs.

# Runnable documentation examples

[Developer manual](../../docs/README.md)

Run commands from the repository root after building `Mail.slnx`. In PowerShell:

```powershell
$env:MAIL_EXE = (Resolve-Path src/Mail.Cli/bin/Debug/net10.0/Mail.Cli.exe).Path
$env:PYTHONPATH = (Resolve-Path sdk/python).Path
python examples/docs/run.py
python examples/docs/check_docs.py
python examples/composition/run.py
dotnet run --project examples/docs/csharp/Embedding.csproj -- examples/docs/tools.mail
dotnet run --project examples/docs/csharp/Embedding.csproj -- examples/docs/agent.mail
```

The [runner](run.py) supplies inputs and checks expected outputs automatically. The separate [hello fixture](hello.input.json) is also usable from the CLI.

| File | Input | Expected output / purpose |
| --- | --- | --- |
| [hello.mail](hello.mail) | `{"text":"Hello, MAILang!"}` | Same object; no tool/model calls |
| [tools.mail](tools.mail) | `{"text":"hello"}` | `{"text":"hello"}` from Python Echo |
| [agent.mail](agent.mail) | `{"text":"hello"}` | `{"text":"hello"}` through simulated agent and Python tool |
| [routing.mail](routing.mail) | `{"urgent":false}` / `{"urgent":true}` | `{"text":"normal"}` / `{"text":"urgent"}` |
| [loop.mail](loop.mail) | `{"target":0}` / `{"target":3}` | `0` / `3` |
| [loop.mail](loop.mail) | `{"target":4}` | Expected maximum-iterations error |
| [types.mail](types.mail) | Compilation only | All field forms; advanced root inputs require a typed host |
| [composition/main.mail](../composition/main.mail) | Provided by its runner | Two outputs with token `python-hello`, one per routing branch |
| [C# host](csharp/Program.cs) | Constructs typed input | `{"text":"hello"}` with either tools.mail or agent.mail |

Validate individual programs with `& $env:MAIL_EXE validate examples/docs/agent.mail`. Validate imported modules through their entry file, not individually: a declaration-only module is not a standalone program.

The composition files are reused from the existing examples. All tutorial execution is offline; real model behavior is not simulated beyond the explicitly documented provider behavior.

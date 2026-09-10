"""Offline tutorial checks. Set MAIL_EXE to the built CLI executable."""
import asyncio
import json
import os
from pathlib import Path
from mail_runtime import MailRuntime, RunError

HERE = Path(__file__).resolve().parent

async def main():
    async with MailRuntime(os.environ["MAIL_EXE"]) as runtime:
        runtime.register_tool("Echo", lambda args: {"text": args["text"]})
        async def increment(args):
            return {"value": args["value"] + 1}
        runtime.register_tool("Increment", increment)
        cases = [
            ("hello.mail", json.loads((HERE / "hello.input.json").read_text()), {"text": "Hello, MAILang!"}),
            ("tools.mail", {"text": "hello"}, {"text": "hello"}),
            ("agent.mail", {"text": "hello"}, {"text": "hello"}),
            ("routing.mail", {"urgent": False}, {"text": "normal"}),
            ("routing.mail", {"urgent": True}, {"text": "urgent"}),
            ("loop.mail", {"target": 0}, 0),
            ("loop.mail", {"target": 3}, 3),
        ]
        for file, data, expected in cases:
            result = await runtime.run(str(HERE / file), input=data)
            assert result == expected, (file, result, expected)
            print(f"{file}: {json.dumps(result)}")
        try:
            await runtime.run(str(HERE / "loop.mail"), input={"target": 4})
        except RunError as exc:
            assert "iteration" in str(exc).lower(), str(exc)
            print("loop.mail: expected iteration-limit failure")
        else:
            raise AssertionError("Expected iteration-limit failure")

if __name__ == "__main__":
    asyncio.run(main())

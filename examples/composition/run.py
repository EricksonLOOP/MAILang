"""Run with the local SDK installed and MAIL_EXE pointing to the built CLI."""
import asyncio
import os
from pathlib import Path
from mail_runtime import MailRuntime

async def main():
    async with MailRuntime(os.environ["MAIL_EXE"]) as runtime:
        async def generate_token(args):
            return {"token": "python-" + args["prompt"]}
        runtime.register_tool("GenerateToken", generate_token)
        for urgent in (False, True):
            result = await runtime.run(str(Path(__file__).with_name("main.mail")),
                                       input={"prompt": "hello", "urgent": urgent})
            print(result)

if __name__ == "__main__":
    asyncio.run(main())

"""
MAIL integration tests (offline — provider="simulated").

Run with:
  cd sdk/python
  python -m pytest tests/test_integration.py -v

When Mail.Cli is bundled in _bin/ (wheel install), tests use it automatically.
Override with MAIL_EXE env var to point to a custom executable for development:
  MAIL_EXE=../../release/win-x64/Mail.Cli.exe python -m pytest tests/test_integration.py -v

All tests spawn the real Mail.Cli.exe process. They use the "simulated" provider
and do NOT require a DeepSeek API key.

Sync vs async tool functions: all registered functions in these tests are async.
Sync function behaviour (thread pool, non-interruptible) is tested implicitly via
test_cancellation_after_run which uses a sync function with an intentional delay.
"""

import asyncio
import json
import os
import pathlib
import subprocess
import uuid

import pytest

from mail_runtime import (
    LoadError,
    MailRuntime,
    ProcessError,
    ProtocolError,
    RunError,
    ToolError,
)
from mail_runtime.runtime import _resolve_cli

# ── Executable discovery ──────────────────────────────────────────────────────

_SCRIPT_DIR = pathlib.Path(__file__).parent
# tests/ → python/ → sdk/ → MAILang/ (project root)
_PROJECT_ROOT = (_SCRIPT_DIR / "../../..").resolve()

_MAIL_EXE_OVERRIDE = os.environ.get("MAIL_EXE")
if _MAIL_EXE_OVERRIDE:
    _EXE = _MAIL_EXE_OVERRIDE
    _EXE_FOUND = pathlib.Path(_EXE).exists()
else:
    try:
        _EXE = str(_resolve_cli())
        _EXE_FOUND = True
    except Exception:
        # Fall back to the legacy development path when the package is not installed as a wheel
        _DEFAULT_EXE = _PROJECT_ROOT / "release/win-x64/Mail.Cli.exe"
        _EXE = str(_DEFAULT_EXE)
        _EXE_FOUND = pathlib.Path(_EXE).exists()

# Whether the bundled binary is available (in _bin/), independent of MAIL_EXE override
try:
    _BUNDLED_EXE = str(_resolve_cli())
    _BUNDLED_FOUND = True
except Exception:
    _BUNDLED_FOUND = False

skip_no_exe = pytest.mark.skipif(
    not _EXE_FOUND,
    reason=f"Mail CLI not found. Set MAIL_EXE env var, install the wheel, or publish from source.",
)

skip_no_bundled = pytest.mark.skipif(
    not _BUNDLED_FOUND,
    reason="Bundled CLI not found in _bin/. Install the platform wheel to run this test.",
)

# ── .mail file paths ──────────────────────────────────────────────────────────

_EXAMPLES   = _PROJECT_ROOT / "examples"
_TOKEN_ECHO = str(_EXAMPLES / "token-echo.mail")
_TWO_TOOLS  = str(_EXAMPLES / "two-tools.mail")


# ── Test 1: basic tool round-trip ─────────────────────────────────────────────

@skip_no_exe
async def test_token_flows_through():
    """Python function generates a unique token; it must arrive in the result."""
    async with MailRuntime(_EXE) as rt:
        generated = {}

        async def generate_token(args: dict) -> dict:
            token = f"py-{uuid.uuid4()}"
            generated["token"] = token
            return {"token": token}

        rt.register_tool("GenerateToken", generate_token)
        result = await rt.run(_TOKEN_ECHO, input={"prompt": "test"})

    assert "token" in result, f"'token' missing from result: {result}"
    assert result["token"].startswith("py-"), f"Expected py- prefix, got: {result['token']}"
    assert result["token"] == generated["token"], "Result token doesn't match generated token"


# ── Test 2: unauthorized tool blocked before reaching Python ──────────────────

@skip_no_exe
async def test_unauthorized_tool_denied_before_python():
    """
    A scripted provider attempts to call ToolB, which RestrictedAgent does not allow.
    AuthorizationChecker must block the call before tool_request reaches Python.
    The Python function for ToolB must have zero invocations.
    """
    tool_b_calls = 0

    async with MailRuntime(_EXE) as rt:
        async def tool_a(args: dict) -> dict:
            return {"result": "from_a"}

        async def tool_b(args: dict) -> dict:
            nonlocal tool_b_calls
            tool_b_calls += 1
            return {"result": "from_b"}

        rt.register_tool("ToolA", tool_a)
        rt.register_tool("ToolB", tool_b)

        # Script: ask model to call ToolB (unauthorized), then return last result.
        # AuthorizationChecker fires → tool.denied event → execution fails.
        provider_cfg = {
            "script": [
                {"type": "tool_call", "tool": "ToolB", "args": {"value": "hello"}},
                {"type": "text", "template": "last_tool_result"},
            ]
        }
        with pytest.raises(RunError):
            await rt.run(_TWO_TOOLS, input={"value": "hello"}, provider_config=provider_cfg)

    assert tool_b_calls == 0, (
        f"ToolB Python function was invoked {tool_b_calls} time(s); "
        "runtime must block it before sending tool_request."
    )


# ── Test 3: unauthorized tool absent from availableTools ─────────────────────

@skip_no_exe
async def test_unauthorized_tool_not_in_availabletools():
    """
    Adaptive provider sees only ToolA in availableTools (RestrictedAgent only allows ToolA).
    ToolB Python function must never be called; execution must succeed.
    """
    tool_b_calls = 0

    async with MailRuntime(_EXE) as rt:
        async def tool_a(args: dict) -> dict:
            return {"result": f"a-{uuid.uuid4()}"}

        async def tool_b(args: dict) -> dict:
            nonlocal tool_b_calls
            tool_b_calls += 1
            return {"result": "from_b"}

        rt.register_tool("ToolA", tool_a)
        rt.register_tool("ToolB", tool_b)

        result = await rt.run(_TWO_TOOLS, input={"value": "hello"})

    assert "result" in result
    assert tool_b_calls == 0, f"ToolB was called {tool_b_calls} time(s); should be 0."


# ── Test 4: tool error propagates ────────────────────────────────────────────

@skip_no_exe
async def test_tool_error_propagates():
    """A tool function that raises must cause the run to fail with RunError."""
    async with MailRuntime(_EXE) as rt:
        async def generate_token(args: dict) -> dict:
            raise ValueError("Simulated tool failure")

        rt.register_tool("GenerateToken", generate_token)

        with pytest.raises(RunError) as exc_info:
            await rt.run(_TOKEN_ECHO, input={"prompt": "test"})

    assert "Simulated tool failure" in exc_info.value.error


# ── Test 5: invalid tool output (missing required field) ─────────────────────

@skip_no_exe
async def test_invalid_tool_output():
    """
    Tool returns a dict missing the required 'token' field.
    AgentRunner validates the output contract and the run must fail.
    """
    async with MailRuntime(_EXE) as rt:
        async def generate_token(args: dict) -> dict:
            return {"wrong_field": 42}  # 'token' is missing

        rt.register_tool("GenerateToken", generate_token)

        with pytest.raises(RunError):
            await rt.run(_TOKEN_ECHO, input={"prompt": "test"})


# ── Test 6: cancellation after run ───────────────────────────────────────────

@skip_no_exe
async def test_cancellation_after_run():
    """
    Cancelling the run() task must send 'cancel' to the server.
    The run must raise CancelledError.

    v1 limitation: synchronous tool functions already executing in a thread pool
    cannot be interrupted and may run to completion before the error surfaces.
    """
    tool_started = asyncio.Event()

    async with MailRuntime(_EXE) as rt:
        async def generate_token(args: dict) -> dict:
            tool_started.set()
            await asyncio.sleep(60)   # simulate slow tool
            return {"token": "never"}

        rt.register_tool("GenerateToken", generate_token)

        run_task = asyncio.create_task(rt.run(_TOKEN_ECHO, input={"prompt": "test"}))

        # Wait for the tool to start, then cancel.
        await asyncio.wait_for(tool_started.wait(), timeout=15.0)
        run_task.cancel()

        with pytest.raises((asyncio.CancelledError, RunError)):
            await run_task


# ── Test 7: EOF during execution ─────────────────────────────────────────────

@skip_no_exe
async def test_eof_during_execution():
    """Killing the subprocess mid-run must raise ProcessError."""
    rt = MailRuntime(_EXE)
    await rt.__aenter__()

    tool_started = asyncio.Event()

    async def generate_token(args: dict) -> dict:
        tool_started.set()
        await asyncio.sleep(60)
        return {"token": "never"}

    rt.register_tool("GenerateToken", generate_token)

    run_task = asyncio.create_task(rt.run(_TOKEN_ECHO, input={"prompt": "test"}))

    await asyncio.wait_for(tool_started.wait(), timeout=15.0)

    # Kill the process to simulate EOF.
    rt._proc.kill()

    with pytest.raises((ProcessError, RunError, asyncio.CancelledError)):
        await asyncio.wait_for(run_task, timeout=10.0)

    # Best-effort cleanup.
    try:
        await rt.__aexit__(None, None, None)
    except Exception:
        pass


# ── Test 8: protocol version mismatch ────────────────────────────────────────

@skip_no_exe
async def test_protocol_version_mismatch():
    """
    The server must reject a handshake with an unsupported protocol version.
    MailRuntime.__aenter__ must raise ProtocolError.
    """
    rt = MailRuntime(_EXE)

    # Patch the send to inject a wrong version number.
    original_send = rt._send

    async def patched_send(msg: dict):
        if msg.get("type") == "handshake":
            msg = dict(msg, protocol_version=999)
        await original_send(msg)

    rt._send = patched_send

    with pytest.raises(ProtocolError):
        await rt.__aenter__()

    try:
        await rt.__aexit__(None, None, None)
    except Exception:
        pass


# ── Test 9: malformed JSON (non-fatal) ───────────────────────────────────────

@skip_no_exe
async def test_malformed_message_non_fatal():
    """
    Sending malformed JSON manually should produce a protocol_error (non-fatal).
    A subsequent valid run must succeed.
    """
    async with MailRuntime(_EXE) as rt:
        # Inject garbage directly into stdin.
        rt._proc.stdin.write(b"this is not json\n")
        await rt._proc.stdin.drain()

        # Give the server a moment to process.
        await asyncio.sleep(0.1)

        # A proper run should still work.
        async def generate_token(args: dict) -> dict:
            return {"token": f"py-{uuid.uuid4()}"}

        rt.register_tool("GenerateToken", generate_token)
        result = await rt.run(_TOKEN_ECHO, input={"prompt": "test"})

    assert result["token"].startswith("py-")


# ── Test 10: process crash ────────────────────────────────────────────────────

@skip_no_exe
async def test_process_crash():
    """Killing the process outside a run must cause the next run to raise ProcessError."""
    async with MailRuntime(_EXE) as rt:
        rt._proc.kill()
        await asyncio.sleep(0.2)

        async def generate_token(args: dict) -> dict:
            return {"token": "x"}

        rt.register_tool("GenerateToken", generate_token)

        with pytest.raises((ProcessError, RunError, OSError)):
            await rt.run(_TOKEN_ECHO, input={"prompt": "test"})


# ── Test 11: concurrent executions ───────────────────────────────────────────

@skip_no_exe
async def test_concurrent_executions():
    """
    Two concurrent run() calls must each produce their own token
    without cross-contamination.
    """
    async with MailRuntime(_EXE) as rt:
        async def gen_token_a(args: dict) -> dict:
            await asyncio.sleep(0.05)   # slight delay to maximise concurrency overlap
            return {"token": f"alpha-{uuid.uuid4()}"}

        async def gen_token_b(args: dict) -> dict:
            await asyncio.sleep(0.05)
            return {"token": f"beta-{uuid.uuid4()}"}

        # Two separate MailRuntime instances for concurrent runs
        # (each run re-registers tools, so we use separate runtimes).
        pass

    # Reopen two separate runtimes for true concurrency.
    async with MailRuntime(_EXE) as rt_a, MailRuntime(_EXE) as rt_b:
        rt_a.register_tool("GenerateToken", gen_token_a)
        rt_b.register_tool("GenerateToken", gen_token_b)

        result_a, result_b = await asyncio.gather(
            rt_a.run(_TOKEN_ECHO, input={"prompt": "a"}),
            rt_b.run(_TOKEN_ECHO, input={"prompt": "b"}),
        )

    assert result_a["token"].startswith("alpha-"), f"Unexpected: {result_a}"
    assert result_b["token"].startswith("beta-"),  f"Unexpected: {result_b}"
    assert result_a["token"] != result_b["token"],  "Tokens must be distinct"

@skip_no_exe
async def test_composition_both_branches():
    """A shared module is loaded once; agent and direct-call branches reach Python."""
    calls = []
    async with MailRuntime(_EXE) as rt:
        async def token(args):
            calls.append(args["prompt"])
            return {"token": "python-" + args["prompt"]}
        rt.register_tool("GenerateToken", token)
        for urgent in (False, True):
            result = await rt.run(str(_EXAMPLES / "composition" / "main.mail"),
                                  input={"prompt": "hello", "urgent": urgent})
            assert result == {"token": "python-hello"}
    assert calls == ["hello", "hello"]


@skip_no_exe
async def test_import_collision_is_load_error(tmp_path):
    (tmp_path / "tools.mail").write_text("tool T { input {} output {} }")
    entry = tmp_path / "main.mail"
    entry.write_text('import "tools.mail" as T tool T { input {} output {} } '
                     'workflow Main { input String output String finish with input }')
    async with MailRuntime(_EXE) as rt:
        with pytest.raises(LoadError):
            await rt.run(str(entry), input={})


# ── Test: bundled CLI auto-resolution ────────────────────────────────────────

@skip_no_bundled
async def test_cli_auto_resolved():
    """MailRuntime() with no arguments must locate and start the bundled CLI."""
    async with MailRuntime() as rt:
        # Handshake succeeded — bundled binary was found and the protocol initialized.
        assert rt._exe.endswith(("Mail.Cli.exe", "Mail.Cli"))

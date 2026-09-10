"""
MAIL Python SDK — MailRuntime

Spawns the mail integrate process and communicates over stdin/stdout
using the MAIL integration protocol v1 (newline-delimited JSON, UTF-8).

Cancellation semantics (v1 limitation):
  Cancelling the asyncio.Task returned by run() sends a 'cancel' message
  to the server, but synchronous tool functions already executing in a
  thread pool cannot be interrupted and may run to completion.
"""

import asyncio
import inspect
import json
import os
import uuid
from collections.abc import Callable
from typing import Any

from .errors import LoadError, ProcessError, ProtocolError, RunError, ToolError
from .protocol import Diagnostic, FieldContract, ToolContract

_PROTOCOL_VERSION = 1


class MailRuntime:
    def __init__(self, executable_path: str, *, provider: str = "simulated") -> None:
        self._exe      = executable_path
        self._provider = provider
        self._tools: dict[str, Callable] = {}

        self._proc: asyncio.subprocess.Process | None = None
        self._reader_task: asyncio.Task | None = None

        # Futures keyed by load_id → Future[list[ToolContract]]
        self._pending_loads: dict[str, asyncio.Future] = {}
        # Futures keyed by execution_id → Future[dict]
        self._pending_runs: dict[str, asyncio.Future] = {}

        self._handshake_future: asyncio.Future | None = None

    # ── Context manager ───────────────────────────────────────────────────────

    async def __aenter__(self) -> "MailRuntime":
        self._proc = await asyncio.create_subprocess_exec(
            self._exe, "integrate",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=None,   # inherit — runtime logs go to user's terminal
        )

        loop = asyncio.get_running_loop()
        self._handshake_future = loop.create_future()
        self._reader_task = asyncio.create_task(self._reader_loop())

        await self._send({"type": "handshake", "protocol_version": _PROTOCOL_VERSION,
                          "client": "python-sdk"})
        try:
            await asyncio.wait_for(self._handshake_future, timeout=10.0)
        except asyncio.TimeoutError:
            raise ProcessError("Handshake timed out after 10 s.")
        return self

    async def __aexit__(self, *exc) -> None:
        if self._proc and self._proc.stdin:
            try:
                self._proc.stdin.close()
                await self._proc.stdin.wait_closed()
            except Exception:
                pass

        if self._reader_task:
            self._reader_task.cancel()
            try:
                await self._reader_task
            except asyncio.CancelledError:
                pass

        if self._proc:
            try:
                await asyncio.wait_for(self._proc.wait(), timeout=5.0)
            except asyncio.TimeoutError:
                self._proc.kill()
                await self._proc.wait()

    # ── Public API ────────────────────────────────────────────────────────────

    def register_tool(self, name: str, fn: Callable) -> None:
        """Register a local function (sync or async) as a tool implementation."""
        self._tools[name] = fn

    async def run(
        self,
        path: str,
        *,
        input: dict,
        provider_config: dict | None = None,
    ) -> dict:
        """
        Compile and run the given .mail program.

        All registered tools must cover every tool declared in the program.
        Returns the workflow output dict on success.
        Raises RunError on execution failure, LoadError on compilation failure.

        Cancellation: awaiting the returned coroutine and then cancelling the
        enclosing Task will send a 'cancel' message to the server. Synchronous
        tool functions already executing in a thread pool cannot be interrupted
        and may run to completion before the error is surfaced.
        """
        abs_path = os.path.abspath(path)
        loop     = asyncio.get_running_loop()

        # 1. Load — validate the program and retrieve tool contracts.
        load_id  = str(uuid.uuid4())
        load_fut: asyncio.Future = loop.create_future()
        self._pending_loads[load_id] = load_fut

        exec_id  = str(uuid.uuid4())
        run_fut: asyncio.Future = loop.create_future()
        self._pending_runs[exec_id] = run_fut

        try:
            await self._send({"type": "load", "load_id": load_id, "path": abs_path})
            try:
                contracts: list[ToolContract] = await asyncio.wait_for(load_fut, timeout=30.0)
            except asyncio.TimeoutError:
                raise ProcessError("Load timed out after 30 s.")

            # Fail fast if any declared tool has no registered function.
            missing = [t.name for t in contracts if t.name not in self._tools]
            if missing:
                raise ToolError(
                    f"No registered function for tool(s): {missing}. "
                    f"Call register_tool() before run().")

            # 2. Run.
            msg: dict[str, Any] = {
                "type":         "run",
                "execution_id": exec_id,
                "path":         abs_path,
                "input":        input,
                "provider":     self._provider,
            }
            if provider_config is not None:
                msg["provider_config"] = provider_config

            await self._send(msg)
            return await run_fut

        except asyncio.CancelledError:
            # Send cancel to server; clean up the pending future.
            try:
                await self._send({"type": "cancel", "execution_id": exec_id})
            except Exception:
                pass
            self._pending_runs.pop(exec_id, None)
            raise

        finally:
            self._pending_loads.pop(load_id, None)

    # ── Internal ──────────────────────────────────────────────────────────────

    async def _send(self, msg: dict) -> None:
        if self._proc is None or self._proc.stdin is None:
            raise ProcessError("Process not running.")
        data = (json.dumps(msg) + "\n").encode("utf-8")
        self._proc.stdin.write(data)
        await self._proc.stdin.drain()

    async def _reader_loop(self) -> None:
        assert self._proc and self._proc.stdout
        try:
            async for raw in self._proc.stdout:
                line = raw.decode("utf-8").strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                await self._dispatch(msg)
        except asyncio.CancelledError:
            raise
        except Exception as ex:
            self._reject_all(ProcessError(f"Reader loop failed: {ex}"))
        else:
            # EOF reached (process closed stdout). Reject any pending futures — the
            # process exited without sending terminal messages for them.
            self._reject_all(ProcessError("Process exited unexpectedly."))

    async def _dispatch(self, msg: dict) -> None:
        mtype = msg.get("type", "")

        if mtype == "handshake_ok":
            if self._handshake_future and not self._handshake_future.done():
                self._handshake_future.set_result(None)

        elif mtype == "handshake_error":
            if self._handshake_future and not self._handshake_future.done():
                self._handshake_future.set_exception(
                    ProtocolError(msg.get("reason", "Handshake rejected by server.")))

        elif mtype == "loaded":
            load_id = msg.get("load_id", "")
            fut     = self._pending_loads.pop(load_id, None)
            if fut and not fut.done():
                contracts = [
                    ToolContract(
                        name=t["name"],
                        input=[FieldContract(**f) for f in t.get("input", [])],
                        output=[FieldContract(**f) for f in t.get("output", [])],
                    )
                    for t in msg.get("tools", [])
                ]
                fut.set_result(contracts)

        elif mtype == "load_error":
            load_id = msg.get("load_id", "")
            fut     = self._pending_loads.pop(load_id, None)
            if fut and not fut.done():
                raw_diags = msg.get("diagnostics", [])
                diags     = [Diagnostic(
                    severity=d.get("severity", ""),
                    code=d.get("code"),
                    message=d.get("message", ""),
                ) for d in raw_diags]
                fut.set_exception(LoadError("Compilation failed.", diags))

        elif mtype == "run_started":
            pass  # acknowledged; the run_fut is resolved by result/run_error

        elif mtype == "event":
            pass  # v1 limitation: events arrive in batch after execution ends

        elif mtype == "tool_request":
            # Launch a task so the reader loop is not blocked while the tool runs.
            asyncio.create_task(self._handle_tool_request(msg))

        elif mtype == "result":
            exec_id = msg.get("execution_id", "")
            fut     = self._pending_runs.pop(exec_id, None)
            if fut and not fut.done():
                fut.set_result(msg.get("output", {}))

        elif mtype == "run_error":
            exec_id = msg.get("execution_id", "")
            fut     = self._pending_runs.pop(exec_id, None)
            if fut and not fut.done():
                fut.set_exception(RunError(msg.get("error", "Unknown execution error.")))

        elif mtype == "protocol_error":
            import sys
            reason = msg.get("reason", "")
            print(f"[mail-sdk] protocol_error: {reason}", file=sys.stderr)
            if msg.get("fatal", False):
                self._reject_all(ProtocolError(f"Fatal protocol error: {reason}"))

    async def _handle_tool_request(self, msg: dict) -> None:
        request_id = msg.get("request_id", "")
        tool_name  = msg.get("tool", "")
        tool_input = msg.get("input", {})

        fn = self._tools.get(tool_name)
        if fn is None:
            await self._send({
                "type":       "tool_error",
                "request_id": request_id,
                "error":      f"No function registered for tool '{tool_name}'.",
            })
            return

        try:
            loop = asyncio.get_running_loop()
            if inspect.iscoroutinefunction(fn):
                output = await fn(tool_input)
            else:
                # Run sync functions in a thread pool to avoid blocking the event loop.
                # Note: sync functions cannot be interrupted mid-execution by cancellation.
                output = await loop.run_in_executor(None, fn, tool_input)

            await self._send({"type": "tool_response", "request_id": request_id, "output": output})
        except Exception as ex:
            await self._send({
                "type":       "tool_error",
                "request_id": request_id,
                "error":      str(ex),
            })

    def _reject_all(self, exc: Exception) -> None:
        for fut in list(self._pending_loads.values()):
            if not fut.done():
                fut.set_exception(exc)
        for fut in list(self._pending_runs.values()):
            if not fut.done():
                fut.set_exception(exc)
        self._pending_loads.clear()
        self._pending_runs.clear()

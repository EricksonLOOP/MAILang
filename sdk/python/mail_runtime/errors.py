class MailError(Exception):
    """Base exception for all MAIL SDK errors."""


class ProtocolError(MailError):
    """Protocol version mismatch or framing error."""


class LoadError(MailError):
    """Compilation failed. Attribute 'diagnostics' is a list of dicts."""

    def __init__(self, message: str, diagnostics: list) -> None:
        super().__init__(message)
        self.diagnostics = diagnostics


class RunError(MailError):
    """Execution failed. Attribute 'error' is the server error string."""

    def __init__(self, error: str) -> None:
        super().__init__(error)
        self.error = error


class ToolError(MailError):
    """A registered tool function raised an exception."""


class ProcessError(MailError):
    """The subprocess exited unexpectedly or could not be started."""

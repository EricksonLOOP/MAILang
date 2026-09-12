from .runtime import MailRuntime
from .errors import (
    MailError,
    ProtocolError,
    LoadError,
    RunError,
    ToolError,
    ProcessError,
)

__version__     = "0.2.0"
__cli_version__ = "0.2.0"

__all__ = [
    "MailRuntime",
    "MailError",
    "ProtocolError",
    "LoadError",
    "RunError",
    "ToolError",
    "ProcessError",
    "__version__",
    "__cli_version__",
]

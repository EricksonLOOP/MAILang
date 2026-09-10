from dataclasses import dataclass, field


@dataclass
class FieldContract:
    name: str
    kind: str       # "String" | "Bool" | "Int" | "Schema"
    required: bool


@dataclass
class ToolContract:
    name: str
    input: list[FieldContract]
    output: list[FieldContract]


@dataclass
class Diagnostic:
    severity: str
    code: str | None
    message: str

from dataclasses import dataclass, field


@dataclass
class FieldContract:
    name: str
    kind: str       # "String" | "Bool" | "Int" | "Decimal" | "List" | "Schema" | "Enum"
    required: bool
    nullable: bool | None = field(default=None)
    enum_symbols: list[str] | None = field(default=None)
    element_kind: str | None = field(default=None)
    element_type: str | None = field(default=None)
    element_enum_symbols: list[str] | None = field(default=None)


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

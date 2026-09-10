# Syntax and types

[Manual home](README.md) · Next: [Agents and tools](agents-and-tools.md)

## File structure and names

A file contains imports followed by declarations: `enum`, `schema`, `tool`, `agent`, and `workflow`. The entry file must declare exactly one workflow. Imported modules can declare zero or more workflows. Declarations may refer to declarations appearing later; execution bindings must already be available when used.

Names and keywords are case-sensitive: `String` is a type, while `string` is an ordinary name. Identifiers start with a letter or underscore and continue with letters, digits, or underscores; the lexer recognizes Unicode letters. Prefer descriptive identifiers that do not reuse keywords. The parser accepts some keywords as identifiers, but expression and declaration positions can make them ambiguous.

Whitespace and indentation do not delimit blocks; braces do. There are no semicolons. Use `//` for line comments; block comments and `#` comments are unsupported. Field declarations are whitespace-separated, without commas. Commas are optional between enum symbols, tool arguments, context names, loop parameters, and `continue` arguments.

## Strings and literals

Expression fragments:

```text
"hello"
"quote: \"; newline: \n; Unicode: \u0041"
"""A multiline prompt.
Its indentation and newlines are retained."""
true
false
42
```

Ordinary double-quoted strings cannot contain physical newlines. Escapes are `\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\u` followed by four hexadecimal digits.

Triple-quoted strings allow newlines, normalize CRLF to LF, and decode only escaped quotes and backslashes. Other backslash sequences remain literal. There is no interpolation or automatic indentation stripping. Triple-quoted strings work in expressions as well as system prompts; import paths require ordinary quoted strings.

Integer literals are non-negative decimal digits within signed 64-bit range. Negative integers can arrive from a host, but unary minus is not syntax. The language has no decimal, null, enum-symbol, list, or object-constructor literal. A type being declarable does not mean values of that type can be constructed in an expression.

## Schemas and enums

Declaration fragments:

```text
enum Status { Ready, Done }
schema Address { city: String }
schema Customer {
    name: String
    active: Bool
    visits: Int
    balance: Decimal
    address: Address
    status: Status
    tags: List<String>
    note: Nullable<String>
    nickname: String optional
}
```

`schema Name { field: Type ... }` declares named fields. `enum Name { Symbol ... }` declares a nonempty set of distinct, case-sensitive symbols. Duplicate fields, declarations in conflicting namespaces, and duplicate enum symbols are rejected. Named types must resolve locally or through an import alias.

The complete [types.mail](../examples/docs/types.mail) demonstrates every field form. It is a compilation example: the CLI/SDK entry-input decoder does not support all these types. See [runtime boundaries](runtime.md#current-capability-boundaries).

| Type | Value representation at typed JSON boundaries |
| --- | --- |
| `String` | JSON string |
| `Bool` | JSON boolean |
| `Int` | Integer within signed 64-bit range |
| `Decimal` | Decimal encoded as a JSON string, such as `"12.50"`, not a JSON number |
| Named schema | JSON object matching its fields |
| Named enum | JSON string exactly matching a declared symbol |
| `List<T>` | JSON array of values of `T` |
| `Nullable<T>` | A value of `T` or JSON null |

The parser accepts nested type references, but runtime support varies by boundary. Nested `Nullable<Nullable<T>>` is rejected. Recursive schemas are unsupported by runtime JSON-schema generation. Runtime list parsing rejects null elements, including when a nullable element type is declared. Do not assume every nested schema/list combination works in tool contracts; their public contract metadata is shallower than the language type tree.

## Optional is different from nullable

| Field declaration | May be absent? | May be null? |
| --- | --- | --- |
| `name: String` | No | No |
| `name: Nullable<String>` | No | Yes |
| `name: String optional` | Yes | No |
| `name: Nullable<String> optional` | Yes | Yes |

`optional` follows the field's type. It changes presence requirements, not the type of present values. There is no default-value syntax, null literal, null-coalescing operator, or presence-test operator. Reading an absent field can fail at runtime; normalize such values in host tools before using them in workflow expressions.

## Expressions

Expression fragments:

```text
input
input.customer.name
previous.text
(input.count >= 1) and not input.disabled
if input.urgent then "urgent" else "normal"
```

Names reference bindings, and `.` reads a schema field. There is no indexing, function-call expression, arithmetic, concatenation, assignment expression, or general-purpose collection transformation. Perform those operations in tools. `=` is used only to initialize loop parameters.

Precedence from highest to lowest:

| Level | Syntax | Behavior |
| --- | --- | --- |
| 1 | literals, names, field access, `(expression)` | Parentheses group expressions |
| 2 | `==`, `!=`, `<`, `<=`, `>`, `>=` | Comparisons cannot be chained |
| 3 | `not expression` | Boolean negation |
| 4 | `left and right` | Short-circuits when left is false |
| 5 | `left or right` | Short-circuits when left is true |
| 6 | `if condition then value else value` | Evaluates only the selected value |

Conditions must be `Bool`; there is no truthiness conversion. Ordering comparisons require `Int`. Use equality on matching `String`, `Bool`, or `Int` values. The compiler accepts some additional scalar categories, but runtime equality implements only these three; enum/decimal/null comparisons are not reliable. Schema/list equality is rejected.

Use `a < b and b < c`, not `a < b < c`. Comparisons bind more tightly than `not`, so `not a == b` means `not (a == b)`. Inline conditional branches must have compatible types. Put a conditional in parentheses when using it inside another operator expression.

Imported declaration references such as `Types.Customer` identify declarations; they do not create runtime values. In particular, `Status.Ready` is not an enum literal.

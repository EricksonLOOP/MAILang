namespace Mail.Compiler.Lexer;

public enum TokenKind
{
    // Keywords
    Schema,
    Tool,
    Agent,
    Workflow,
    Step,
    Call,
    Finish,
    With,
    Save,
    As,
    Context,
    Input,
    Output,
    Model,
    Tools,
    Allow,
    System,

    // Type keywords
    KwString,
    KwBool,
    KwInt,

    // String literals
    StringLiteral,
    TripleStringLiteral,

    // Punctuation
    Colon,
    Dot,
    Comma,
    LBrace,
    RBrace,

    // Identifier (any name not matched as keyword)
    Identifier,

    // End of file
    Eof,

    // Error token (invalid character)
    Error,
}

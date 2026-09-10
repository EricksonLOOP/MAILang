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

    // Conditional keywords
    If,
    Else,
    When,
    Require,
    Then,
    Not,
    And,
    Or,
    True,
    False,

    // Type keywords
    KwString,
    KwBool,
    KwInt,

    // String literals
    StringLiteral,
    TripleStringLiteral,

    // Integer literal
    IntLiteral,

    // Punctuation
    Colon,
    Dot,
    Comma,
    LBrace,
    RBrace,
    LParen,
    RParen,

    // Comparison operators
    EqEq,
    BangEq,
    Lt,
    LtEq,
    Gt,
    GtEq,

    // Identifier (any name not matched as keyword)
    Identifier,

    // End of file
    Eof,

    // Error token (invalid character)
    Error,
}

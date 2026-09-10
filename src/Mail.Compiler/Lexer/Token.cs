using Mail.Contracts;

namespace Mail.Compiler.Lexer;

public sealed record Token(TokenKind Kind, string Text, SourceLocation Location);

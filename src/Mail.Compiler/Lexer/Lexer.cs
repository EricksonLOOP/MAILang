using Mail.Contracts;
using System.Text;

namespace Mail.Compiler.Lexer;

public sealed class Lexer(string source, string filePath)
{
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private readonly List<Diagnostic> _errors = [];

    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["import"]   = TokenKind.Import,
        ["schema"]   = TokenKind.Schema,
        ["tool"]     = TokenKind.Tool,
        ["agent"]    = TokenKind.Agent,
        ["workflow"] = TokenKind.Workflow,
        ["step"]     = TokenKind.Step,
        ["call"]     = TokenKind.Call,
        ["finish"]   = TokenKind.Finish,
        ["with"]     = TokenKind.With,
        ["save"]     = TokenKind.Save,
        ["as"]       = TokenKind.As,
        ["context"]  = TokenKind.Context,
        ["input"]    = TokenKind.Input,
        ["output"]   = TokenKind.Output,
        ["model"]    = TokenKind.Model,
        ["tools"]    = TokenKind.Tools,
        ["allow"]    = TokenKind.Allow,
        ["system"]   = TokenKind.System,
        ["if"]       = TokenKind.If,
        ["else"]     = TokenKind.Else,
        ["when"]     = TokenKind.When,
        ["require"]  = TokenKind.Require,
        ["then"]     = TokenKind.Then,
        ["not"]      = TokenKind.Not,
        ["and"]      = TokenKind.And,
        ["or"]       = TokenKind.Or,
        ["true"]     = TokenKind.True,
        ["false"]    = TokenKind.False,
        ["String"]   = TokenKind.KwString,
        ["Bool"]     = TokenKind.KwBool,
        ["Int"]      = TokenKind.KwInt,
        ["Decimal"]  = TokenKind.KwDecimal,
        ["enum"]     = TokenKind.Enum,
        ["optional"] = TokenKind.Optional,
        ["loop"]     = TokenKind.Loop,
        ["break"]    = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["max"]      = TokenKind.Max,
        ["params"]   = TokenKind.Params,
    };

    public (IReadOnlyList<Token> Tokens, IReadOnlyList<Diagnostic> Errors) Tokenize()
    {
        var tokens = new List<Token>();

        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= source.Length)
            {
                tokens.Add(new Token(TokenKind.Eof, "", Location()));
                break;
            }

            var loc = Location();
            var ch = source[_pos];

            if (char.IsLetter(ch) || ch == '_')
            {
                tokens.Add(ReadWord(loc));
                continue;
            }

            if (char.IsDigit(ch))
            {
                tokens.Add(ReadInteger(loc));
                continue;
            }

            switch (ch)
            {
                case '"':  tokens.Add(ReadString(loc)); break;
                case ':':  tokens.Add(Single(TokenKind.Colon, loc)); break;
                case '.':  tokens.Add(Single(TokenKind.Dot, loc)); break;
                case ',':  tokens.Add(Single(TokenKind.Comma, loc)); break;
                case '{':  tokens.Add(Single(TokenKind.LBrace, loc)); break;
                case '}':  tokens.Add(Single(TokenKind.RBrace, loc)); break;
                case '(':  tokens.Add(Single(TokenKind.LParen, loc)); break;
                case ')':  tokens.Add(Single(TokenKind.RParen, loc)); break;
                case '=':
                    if (_pos + 1 < source.Length && source[_pos + 1] == '=')
                    {
                        _pos += 2; _col += 2;
                        tokens.Add(new Token(TokenKind.EqEq, "==", loc));
                    }
                    else
                    {
                        tokens.Add(Single(TokenKind.Assign, loc));
                    }
                    break;
                case '!':
                    if (_pos + 1 < source.Length && source[_pos + 1] == '=')
                    {
                        _pos += 2; _col += 2;
                        tokens.Add(new Token(TokenKind.BangEq, "!=", loc));
                    }
                    else
                    {
                        _errors.Add(new Diagnostic(DiagnosticSeverity.Error, "MAIL-PARSE",
                            $"Unexpected character '!'. Did you mean '!='?", loc));
                        tokens.Add(new Token(TokenKind.Error, "!", loc));
                        Advance();
                    }
                    break;
                case '<':
                    if (_pos + 1 < source.Length && source[_pos + 1] == '=')
                    {
                        _pos += 2; _col += 2;
                        tokens.Add(new Token(TokenKind.LtEq, "<=", loc));
                    }
                    else
                    {
                        tokens.Add(Single(TokenKind.Lt, loc));
                    }
                    break;
                case '>':
                    if (_pos + 1 < source.Length && source[_pos + 1] == '=')
                    {
                        _pos += 2; _col += 2;
                        tokens.Add(new Token(TokenKind.GtEq, ">=", loc));
                    }
                    else
                    {
                        tokens.Add(Single(TokenKind.Gt, loc));
                    }
                    break;
                default:
                    _errors.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.DuplicateName,
                        $"Unexpected character '{ch}'",
                        loc));
                    tokens.Add(new Token(TokenKind.Error, ch.ToString(), loc));
                    Advance();
                    break;
            }
        }

        return (tokens, _errors);
    }

    private Token ReadInteger(SourceLocation loc)
    {
        var start = _pos;
        while (_pos < source.Length && char.IsDigit(source[_pos]))
            Advance();
        var text = source[start.._pos];
        return new Token(TokenKind.IntLiteral, text, loc);
    }

    private Token Single(TokenKind kind, SourceLocation loc)
    {
        var text = source[_pos].ToString();
        Advance();
        return new Token(kind, text, loc);
    }

    private Token ReadWord(SourceLocation loc)
    {
        var start = _pos;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            Advance();
        var text = source[start.._pos];
        var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        return new Token(kind, text, loc);
    }

    private Token ReadString(SourceLocation loc)
    {
        if (_pos + 2 < source.Length && source[_pos + 1] == '"' && source[_pos + 2] == '"')
            return ReadTripleString(loc);
        return ReadSingleString(loc);
    }

    private Token ReadSingleString(SourceLocation loc)
    {
        Advance(); // consume opening "
        var sb = new StringBuilder();

        while (_pos < source.Length)
        {
            var ch = source[_pos];

            if (ch == '"')
            {
                Advance();
                return new Token(TokenKind.StringLiteral, sb.ToString(), loc);
            }

            if (ch == '\n' || ch == '\r')
                break;

            if (ch == '\\')
            {
                Advance();
                if (_pos >= source.Length) break;
                var esc = source[_pos];
                switch (esc)
                {
                    case '"':  sb.Append('"');  Advance(); break;
                    case '\\': sb.Append('\\'); Advance(); break;
                    case '/':  sb.Append('/');  Advance(); break;
                    case 'b':  sb.Append('\b'); Advance(); break;
                    case 'f':  sb.Append('\f'); Advance(); break;
                    case 'n':  sb.Append('\n'); Advance(); break;
                    case 'r':  sb.Append('\r'); Advance(); break;
                    case 't':  sb.Append('\t'); Advance(); break;
                    case 'u':
                        Advance();
                        var hexLoc = Location();
                        if (_pos + 4 <= source.Length && IsHex4(_pos))
                        {
                            var code = Convert.ToInt32(source[_pos..(_pos + 4)], 16);
                            sb.Append((char)code);
                            _pos += 4; _col += 4;
                        }
                        else
                        {
                            _errors.Add(new Diagnostic(DiagnosticSeverity.Error,
                                "MAIL-PARSE", "Invalid \\u escape: expected 4 hex digits.", hexLoc));
                        }
                        break;
                    default:
                        _errors.Add(new Diagnostic(DiagnosticSeverity.Error,
                            "MAIL-PARSE", $"Invalid escape sequence '\\{esc}'.", Location()));
                        Advance();
                        break;
                }
                continue;
            }

            sb.Append(ch);
            Advance();
        }

        _errors.Add(new Diagnostic(DiagnosticSeverity.Error,
            DiagnosticCodes.StringUnterminated, "Unterminated string literal.", loc));
        return new Token(TokenKind.Error, sb.ToString(), loc);
    }

    private Token ReadTripleString(SourceLocation loc)
    {
        Advance(); Advance(); Advance(); // consume opening """
        var sb = new StringBuilder();

        while (_pos < source.Length)
        {
            if (_pos + 2 < source.Length && source[_pos] == '"' && source[_pos + 1] == '"' && source[_pos + 2] == '"')
            {
                Advance(); Advance(); Advance(); // consume closing """
                return new Token(TokenKind.TripleStringLiteral, sb.ToString(), loc);
            }

            var ch = source[_pos];

            if (ch == '\r' && _pos + 1 < source.Length && source[_pos + 1] == '\n')
            {
                sb.Append('\n');
                _pos += 2;
                _line++;
                _col = 1;
                continue;
            }

            if (ch == '\n')
            {
                sb.Append('\n');
                AdvanceNewline();
                continue;
            }

            if (ch == '\\' && _pos + 1 < source.Length)
            {
                var next = source[_pos + 1];
                if (next == '"')
                {
                    sb.Append('"');
                    Advance(); Advance();
                    continue;
                }
                if (next == '\\')
                {
                    sb.Append('\\');
                    Advance(); Advance();
                    continue;
                }
                // other backslash: preserve as-is
                sb.Append(ch);
                Advance();
                if (_pos < source.Length && source[_pos] != '\n' && source[_pos] != '\r')
                {
                    sb.Append(source[_pos]);
                    Advance();
                }
                continue;
            }

            sb.Append(ch);
            Advance();
        }

        _errors.Add(new Diagnostic(DiagnosticSeverity.Error,
            DiagnosticCodes.StringUnterminated, "Unterminated triple-quoted string.", loc));
        return new Token(TokenKind.Error, sb.ToString(), loc);
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < source.Length)
        {
            var ch = source[_pos];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                Advance();
            }
            else if (ch == '\n')
            {
                _pos++;
                _line++;
                _col = 1;
            }
            else if (ch == '/' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            {
                // line comment
                while (_pos < source.Length && source[_pos] != '\n')
                    _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private bool IsHex4(int pos)
    {
        for (var i = 0; i < 4; i++)
            if (!Uri.IsHexDigit(source[pos + i])) return false;
        return true;
    }

    private void Advance()
    {
        _pos++;
        _col++;
    }

    private void AdvanceNewline()
    {
        _pos++;
        _line++;
        _col = 1;
    }

    private SourceLocation Location() => new(filePath, _line, _col);
}

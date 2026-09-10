using Mail.Compiler.Lexer;
using Mail.Contracts;
using Xunit;

namespace Mail.Tests;

public class LexerTests
{
    private static IReadOnlyList<Token> Lex(string source)
    {
        var (tokens, errors) = new Lexer(source, "test.mail").Tokenize();
        Assert.Empty(errors);
        return tokens;
    }

    [Fact]
    public void Keywords_are_recognized()
    {
        var tokens = Lex("schema tool agent workflow step call finish with save as context input output model tools allow");
        Assert.Equal(TokenKind.Schema,   tokens[0].Kind);
        Assert.Equal(TokenKind.Tool,     tokens[1].Kind);
        Assert.Equal(TokenKind.Agent,    tokens[2].Kind);
        Assert.Equal(TokenKind.Workflow, tokens[3].Kind);
        Assert.Equal(TokenKind.Step,     tokens[4].Kind);
        Assert.Equal(TokenKind.Call,     tokens[5].Kind);
        Assert.Equal(TokenKind.Finish,   tokens[6].Kind);
        Assert.Equal(TokenKind.With,     tokens[7].Kind);
        Assert.Equal(TokenKind.Save,     tokens[8].Kind);
        Assert.Equal(TokenKind.As,       tokens[9].Kind);
        Assert.Equal(TokenKind.Context,  tokens[10].Kind);
        Assert.Equal(TokenKind.Input,    tokens[11].Kind);
        Assert.Equal(TokenKind.Output,   tokens[12].Kind);
        Assert.Equal(TokenKind.Model,    tokens[13].Kind);
        Assert.Equal(TokenKind.Tools,    tokens[14].Kind);
        Assert.Equal(TokenKind.Allow,    tokens[15].Kind);
    }

    [Fact]
    public void Type_keywords_are_recognized()
    {
        var tokens = Lex("String Bool Int");
        Assert.Equal(TokenKind.KwString, tokens[0].Kind);
        Assert.Equal(TokenKind.KwBool,   tokens[1].Kind);
        Assert.Equal(TokenKind.KwInt,    tokens[2].Kind);
    }

    [Fact]
    public void Punctuation_is_recognized()
    {
        var tokens = Lex(": . , { }");
        Assert.Equal(TokenKind.Colon,  tokens[0].Kind);
        Assert.Equal(TokenKind.Dot,    tokens[1].Kind);
        Assert.Equal(TokenKind.Comma,  tokens[2].Kind);
        Assert.Equal(TokenKind.LBrace, tokens[3].Kind);
        Assert.Equal(TokenKind.RBrace, tokens[4].Kind);
    }

    [Fact]
    public void Identifiers_are_recognized()
    {
        var tokens = Lex("MySchema _foo bar123");
        Assert.All(tokens.Take(3), t => Assert.Equal(TokenKind.Identifier, t.Kind));
        Assert.Equal("MySchema", tokens[0].Text);
        Assert.Equal("_foo",     tokens[1].Text);
        Assert.Equal("bar123",   tokens[2].Text);
    }

    [Fact]
    public void Line_comments_are_skipped()
    {
        var tokens = Lex("schema // this is a comment\ntool");
        Assert.Equal(TokenKind.Schema, tokens[0].Kind);
        Assert.Equal(TokenKind.Tool,   tokens[1].Kind);
        Assert.Equal(TokenKind.Eof,    tokens[2].Kind);
    }

    [Fact]
    public void Invalid_character_produces_error_with_location()
    {
        var (_, errors) = new Lexer("schema @ tool", "test.mail").Tokenize();
        Assert.Single(errors);
        Assert.Equal(DiagnosticSeverity.Error, errors[0].Severity);
        Assert.Equal(1, errors[0].Location.Line);
        Assert.Equal(8, errors[0].Location.Column);
    }

    [Fact]
    public void Location_tracks_across_newlines()
    {
        var tokens = Lex("schema\ntool");
        Assert.Equal(1, tokens[0].Location.Line);
        Assert.Equal(2, tokens[1].Location.Line);
        Assert.Equal(1, tokens[1].Location.Column);
    }

    [Fact]
    public void Eof_token_is_last()
    {
        var tokens = Lex("schema");
        Assert.Equal(TokenKind.Eof, tokens[^1].Kind);
    }

    // ── String literals ───────────────────────────────────────────────────────

    [Fact]
    public void System_keyword_is_recognized()
    {
        var tokens = Lex("system");
        Assert.Equal(TokenKind.System, tokens[0].Kind);
    }

    [Fact]
    public void Simple_string_is_lexed()
    {
        var tokens = Lex("\"hello\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("hello", tokens[0].Text);
    }

    [Fact]
    public void String_json_escapes_are_decoded()
    {
        var tokens = Lex("\"a\\\"b\\\\c\\/d\\be\\fe\\ne\\re\\t\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("a\"b\\c/d\be\fe\ne\re\t", tokens[0].Text);
    }

    [Fact]
    public void String_unicode_escape_is_decoded()
    {
        var tokens = Lex("\"\\u0041\""); // U+0041 = 'A'
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("A", tokens[0].Text);
    }

    [Fact]
    public void String_unicode_content_is_preserved()
    {
        var tokens = Lex("\"こんにちは\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("こんにちは", tokens[0].Text);
    }

    [Fact]
    public void Unterminated_single_string_produces_diagnostic()
    {
        var (_, errors) = new Lexer("\"abc", "test.mail").Tokenize();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCodes.StringUnterminated, errors[0].Code);
        Assert.Equal(1, errors[0].Location.Line);
        Assert.Equal(1, errors[0].Location.Column);
    }

    [Fact]
    public void Invalid_escape_in_single_string_produces_diagnostic()
    {
        var (_, errors) = new Lexer("\"\\q\"", "test.mail").Tokenize();
        Assert.Single(errors);
        Assert.Equal(DiagnosticSeverity.Error, errors[0].Severity);
    }

    [Fact]
    public void Triple_string_is_lexed()
    {
        var tokens = Lex("\"\"\"hello\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("hello", tokens[0].Text);
    }

    [Fact]
    public void Triple_string_crlf_normalized_to_lf()
    {
        var tokens = Lex("\"\"\"line1\r\nline2\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("line1\nline2", tokens[0].Text);
    }

    [Fact]
    public void Triple_string_escaped_quote_and_backslash()
    {
        var tokens = Lex("\"\"\"a\\\"b\\\\c\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("a\"b\\c", tokens[0].Text);
    }

    [Fact]
    public void Triple_string_other_backslash_preserved()
    {
        // \q is not a recognized escape in triple strings — preserved as \q
        var tokens = Lex("\"\"\"\\q\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("\\q", tokens[0].Text);
    }

    [Fact]
    public void Triple_string_preserves_indentation()
    {
        var tokens = Lex("\"\"\"  indented\n  line\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("  indented\n  line", tokens[0].Text);
    }

    [Fact]
    public void Triple_string_treats_interpolation_as_literal()
    {
        var tokens = Lex("\"\"\"{foo}\"\"\"");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal("{foo}", tokens[0].Text);
    }

    [Fact]
    public void Unterminated_triple_string_produces_diagnostic()
    {
        var (_, errors) = new Lexer("\"\"\"abc", "test.mail").Tokenize();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCodes.StringUnterminated, errors[0].Code);
        Assert.Equal(1, errors[0].Location.Line);
        Assert.Equal(1, errors[0].Location.Column);
    }

    [Fact]
    public void Token_after_multiline_string_has_correct_location()
    {
        // triple string spanning 2 lines; 'schema' should be on line 3
        var tokens = Lex("\"\"\"line1\nline2\"\"\"\nschema");
        Assert.Equal(TokenKind.TripleStringLiteral, tokens[0].Kind);
        Assert.Equal(TokenKind.Schema, tokens[1].Kind);
        Assert.Equal(3, tokens[1].Location.Line);
        Assert.Equal(1, tokens[1].Location.Column);
    }
}

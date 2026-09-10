using Mail.Contracts;
using MailLexer = Mail.Compiler.Lexer.Lexer;
using MailParser = Mail.Compiler.Parser.Parser;
using Mail.Compiler.Semantics;

namespace Mail.Compiler;

public static class Compiler
{
    public static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Compile(
        string source,
        string filePath)
    {
        // ── Lex ──────────────────────────────────────────────────────────────
        var lexer = new MailLexer(source, filePath);
        (IReadOnlyList<Lexer.Token> tokens, IReadOnlyList<Diagnostic> lexErrors) = lexer.Tokenize();

        if (lexErrors.Any(d => d.Severity == DiagnosticSeverity.Error))
            return (null, lexErrors);

        // ── Parse ─────────────────────────────────────────────────────────────
        var parser = new MailParser(tokens);
        (Ast.ProgramNode? program, IReadOnlyList<Diagnostic> parseErrors) = parser.Parse();

        if (parseErrors.Any(d => d.Severity == DiagnosticSeverity.Error) || program is null)
            return (null, Concat(lexErrors, parseErrors));

        // ── Validate ─────────────────────────────────────────────────────────
        var validator = new SemanticValidator(filePath);
        (ValidatedPlan? plan, IReadOnlyList<Diagnostic> semErrors) = validator.Validate(program);

        return (plan, Concat(lexErrors, parseErrors, semErrors));
    }

    private static IReadOnlyList<Diagnostic> Concat(params IReadOnlyList<Diagnostic>[] lists)
    {
        var result = new List<Diagnostic>();
        foreach (var list in lists) result.AddRange(list);
        return result;
    }
}

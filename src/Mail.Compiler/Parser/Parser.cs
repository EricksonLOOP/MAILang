using Mail.Compiler.Ast;
using Mail.Compiler.Lexer;
using Mail.Contracts;

namespace Mail.Compiler.Parser;

public sealed class Parser(IReadOnlyList<Token> tokens)
{
    private int _pos;
    private readonly List<Diagnostic> _errors = [];

    public (ProgramNode? Program, IReadOnlyList<Diagnostic> Errors) Parse()
    {
        var decls = new List<Declaration>();
        while (!IsEof())
        {
            var decl = ParseDeclaration();
            if (decl is not null)
                decls.Add(decl);
        }
        return (_errors.Any(d => d.Severity == DiagnosticSeverity.Error)
            ? null
            : new ProgramNode(decls), _errors);
    }

    // ── Declarations ─────────────────────────────────────────────────────────

    private Declaration? ParseDeclaration()
    {
        var tok = Current();
        return tok.Kind switch
        {
            TokenKind.Schema   => ParseSchema(),
            TokenKind.Tool     => ParseTool(),
            TokenKind.Agent    => ParseAgent(),
            TokenKind.Workflow => ParseWorkflow(),
            _ => UnexpectedDeclaration(tok),
        };
    }

    private SchemaDecl? ParseSchema()
    {
        var loc = Current().Location;
        Expect(TokenKind.Schema);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);
        var fields = ParseFieldDecls();
        Expect(TokenKind.RBrace);
        return new SchemaDecl(name, fields, loc);
    }

    private ToolDecl? ParseTool()
    {
        var loc = Current().Location;
        Expect(TokenKind.Tool);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);
        Expect(TokenKind.Input);
        Expect(TokenKind.LBrace);
        var input = ParseFieldDecls();
        Expect(TokenKind.RBrace);
        Expect(TokenKind.Output);
        Expect(TokenKind.LBrace);
        var output = ParseFieldDecls();
        Expect(TokenKind.RBrace);
        Expect(TokenKind.RBrace);
        return new ToolDecl(name, input, output, loc);
    }

    private AgentDecl? ParseAgent()
    {
        var loc = Current().Location;
        Expect(TokenKind.Agent);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);
        Expect(TokenKind.Model);
        var modelName = ExpectIdentifier();
        if (modelName is null) return null;

        SystemPromptNode? systemPrompt = null;
        var seenSystem = false;

        while (IsKind(TokenKind.System))
        {
            var sysLoc = Current().Location;
            Advance(); // consume 'system'

            if (IsKind(TokenKind.StringLiteral) || IsKind(TokenKind.TripleStringLiteral))
            {
                var text = Current().Text;
                var strLoc = Current().Location;
                Advance();

                if (seenSystem)
                {
                    Error(DiagnosticCodes.PromptDuplicate, "Duplicate 'system' property in agent.", sysLoc);
                }
                else if (text.Trim().Length == 0)
                {
                    Error(DiagnosticCodes.PromptEmpty, "System prompt cannot be empty or whitespace.", strLoc);
                    seenSystem = true;
                }
                else
                {
                    systemPrompt = new SystemPromptNode(text, sysLoc);
                    seenSystem = true;
                }
            }
            else
            {
                Error("Expected string literal after 'system'.", Current().Location);
            }
        }

        Expect(TokenKind.Output);
        var outputType = ParseTypeRef();
        Expect(TokenKind.Tools);
        Expect(TokenKind.LBrace);
        var allowed = ParseAllowList();
        Expect(TokenKind.RBrace);
        Expect(TokenKind.RBrace);
        return new AgentDecl(name, modelName, outputType, allowed, loc, systemPrompt);
    }

    private WorkflowDecl? ParseWorkflow()
    {
        var loc = Current().Location;
        Expect(TokenKind.Workflow);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);
        Expect(TokenKind.Input);
        var inputType = ParseTypeRef();
        Expect(TokenKind.Output);
        var outputType = ParseTypeRef();
        var steps = ParseSteps();
        Expect(TokenKind.Finish);
        Expect(TokenKind.With);
        var finish = ParseExpr();
        Expect(TokenKind.RBrace);
        return new WorkflowDecl(name, inputType, outputType, steps, finish, loc);
    }

    // ── Fields and types ─────────────────────────────────────────────────────

    private List<FieldDecl> ParseFieldDecls()
    {
        var fields = new List<FieldDecl>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var fieldName = ExpectIdentifier();
            if (fieldName is null) { SkipToNextRBrace(); break; }
            Expect(TokenKind.Colon);
            var type = ParseTypeRef();
            fields.Add(new FieldDecl(fieldName, type));
        }
        return fields;
    }

    private TypeRef ParseTypeRef()
    {
        var tok = Current();
        if (tok.Kind == TokenKind.KwString) { Advance(); return new PrimitiveTypeRef(PrimitiveKind.String); }
        if (tok.Kind == TokenKind.KwBool)   { Advance(); return new PrimitiveTypeRef(PrimitiveKind.Bool); }
        if (tok.Kind == TokenKind.KwInt)    { Advance(); return new PrimitiveTypeRef(PrimitiveKind.Int); }
        if (tok.Kind == TokenKind.Identifier)
        {
            Advance();
            return new NamedTypeRef(tok.Text, tok.Location);
        }
        Error($"Expected type, got '{tok.Text}'", tok.Location);
        return new PrimitiveTypeRef(PrimitiveKind.String); // error recovery placeholder
    }

    // ── Allow list ───────────────────────────────────────────────────────────

    private List<string> ParseAllowList()
    {
        var names = new List<string>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            Expect(TokenKind.Allow);
            var n = ExpectIdentifier();
            if (n is not null) names.Add(n);
        }
        return names;
    }

    // ── Steps ────────────────────────────────────────────────────────────────

    private List<StepDecl> ParseSteps()
    {
        var steps = new List<StepDecl>();
        while (IsKind(TokenKind.Step) && !IsEof())
            steps.Add(ParseStep());
        return steps;
    }

    private StepDecl ParseStep()
    {
        var loc = Current().Location;
        Expect(TokenKind.Step);
        var name = ExpectIdentifier() ?? "?";
        Expect(TokenKind.LBrace);
        var body = ParseStepBody();
        Expect(TokenKind.Save);
        Expect(TokenKind.As);
        var saveAs = ExpectIdentifier() ?? "?";
        Expect(TokenKind.RBrace);
        return new StepDecl(name, body, saveAs, loc);
    }

    private StepBody ParseStepBody()
    {
        if (IsKind(TokenKind.Call))
            return ParseCallBody();
        if (IsKind(TokenKind.Agent))
            return ParseAgentBody();
        Error($"Expected 'call' or 'agent' in step body, got '{Current().Text}'", Current().Location);
        return new CallBody("?", new Dictionary<string, Expr>());
    }

    private CallBody ParseCallBody()
    {
        Expect(TokenKind.Call);
        var toolName = ExpectIdentifier() ?? "?";
        Expect(TokenKind.LBrace);
        var args = ParseArgList();
        Expect(TokenKind.RBrace);
        return new CallBody(toolName, args);
    }

    private AgentBody ParseAgentBody()
    {
        Expect(TokenKind.Agent);
        var agentName = ExpectIdentifier() ?? "?";
        Expect(TokenKind.Context);
        Expect(TokenKind.LBrace);
        var names = ParseNameList();
        Expect(TokenKind.RBrace);
        return new AgentBody(agentName, names);
    }

    private Dictionary<string, Expr> ParseArgList()
    {
        var args = new Dictionary<string, Expr>(StringComparer.Ordinal);
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var key = ExpectIdentifier();
            if (key is null) break;
            Expect(TokenKind.Colon);
            var expr = ParseExpr();
            args[key] = expr;
            if (IsKind(TokenKind.Comma)) Advance();
        }
        return args;
    }

    private List<string> ParseNameList()
    {
        var names = new List<string>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var n = ExpectIdentifier();
            if (n is not null) names.Add(n);
            if (IsKind(TokenKind.Comma)) Advance();
        }
        return names;
    }

    // ── Expressions ──────────────────────────────────────────────────────────

    private Expr ParseExpr()
    {
        var tok = Current();
        if (tok.Kind != TokenKind.Identifier && !IsKeyword(tok.Kind))
        {
            Error($"Expected identifier in expression, got '{tok.Text}'", tok.Location);
            return new NameExpr("?", tok.Location);
        }
        Advance();
        Expr expr = new NameExpr(tok.Text, tok.Location);
        while (IsKind(TokenKind.Dot))
        {
            var dotLoc = Current().Location;
            Advance();
            var field = ExpectIdentifier();
            expr = new FieldAccessExpr(expr, field ?? "?", dotLoc);
        }
        return expr;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Token Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];

    private bool IsEof() => Current().Kind == TokenKind.Eof;

    private bool IsKind(TokenKind kind) => Current().Kind == kind;

    private void Advance()
    {
        if (_pos < tokens.Count - 1) _pos++;
    }

    private void Expect(TokenKind kind)
    {
        if (Current().Kind == kind) { Advance(); return; }
        var tok = Current();
        Error($"Expected '{KindName(kind)}', got '{tok.Text}'", tok.Location);
    }

    private string? ExpectIdentifier()
    {
        var tok = Current();
        // Allow any word-like token as an identifier (covers keyword-named schemas etc.)
        if (tok.Kind == TokenKind.Identifier || IsKeyword(tok.Kind))
        {
            Advance();
            return tok.Text;
        }
        Error($"Expected identifier, got '{tok.Text}'", tok.Location);
        return null;
    }

    private static bool IsKeyword(TokenKind kind) =>
        kind is TokenKind.Schema or TokenKind.Tool or TokenKind.Agent or TokenKind.Workflow
             or TokenKind.Step or TokenKind.Call or TokenKind.Finish or TokenKind.With
             or TokenKind.Save or TokenKind.As or TokenKind.Context or TokenKind.Input
             or TokenKind.Output or TokenKind.Model or TokenKind.Tools or TokenKind.Allow
             or TokenKind.System;

    private void SkipToNextRBrace()
    {
        while (!IsEof() && !IsKind(TokenKind.RBrace))
            Advance();
    }

    private Declaration? UnexpectedDeclaration(Token tok)
    {
        Error($"Expected 'schema', 'tool', 'agent', or 'workflow', got '{tok.Text}'", tok.Location);
        SkipToNextRBrace();
        if (IsKind(TokenKind.RBrace)) Advance();
        return null;
    }

    private void Error(string code, string message, SourceLocation loc) =>
        _errors.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, loc));

    private void Error(string message, SourceLocation loc) =>
        Error("MAIL-PARSE", message, loc);

    private static string KindName(TokenKind kind) => kind switch
    {
        TokenKind.Schema    => "schema",
        TokenKind.Tool      => "tool",
        TokenKind.Agent     => "agent",
        TokenKind.Workflow  => "workflow",
        TokenKind.Step      => "step",
        TokenKind.Call      => "call",
        TokenKind.Finish    => "finish",
        TokenKind.With      => "with",
        TokenKind.Save      => "save",
        TokenKind.As        => "as",
        TokenKind.Context   => "context",
        TokenKind.Input     => "input",
        TokenKind.Output    => "output",
        TokenKind.Model     => "model",
        TokenKind.Tools     => "tools",
        TokenKind.Allow     => "allow",
        TokenKind.System    => "system",
        TokenKind.LBrace    => "{",
        TokenKind.RBrace    => "}",
        TokenKind.Colon     => ":",
        TokenKind.Dot       => ".",
        TokenKind.Comma     => ",",
        TokenKind.Identifier => "<identifier>",
        _ => kind.ToString(),
    };
}

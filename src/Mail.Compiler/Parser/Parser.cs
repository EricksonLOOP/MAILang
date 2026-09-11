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
        var imports = new List<ImportDecl>();
        while (IsKind(TokenKind.Import))
            imports.Add(ParseImport());

        var decls = new List<Declaration>();
        while (!IsEof())
        {
            var decl = ParseDeclaration();
            if (decl is not null)
                decls.Add(decl);
        }
        return (_errors.Any(d => d.Severity == DiagnosticSeverity.Error)
            ? null
            : new ProgramNode(imports, decls), _errors);
    }

    private ImportDecl ParseImport()
    {
        var loc = Current().Location;
        Expect(TokenKind.Import);
        string path = "";
        if (IsKind(TokenKind.StringLiteral)) { path = Current().Text; Advance(); }
        else Error("Expected string path after 'import'.", Current().Location);
        Expect(TokenKind.As);
        var alias = ExpectIdentifier() ?? "?";
        return new ImportDecl(path, alias, loc);
    }

    // ── Declarations ─────────────────────────────────────────────────────────

    private Declaration? ParseDeclaration()
    {
        var tok = Current();
        return tok.Kind switch
        {
            TokenKind.Schema    => ParseSchema(),
            TokenKind.Tool      => ParseTool(),
            TokenKind.Agent     => ParseAgent(),
            TokenKind.Workflow  => ParseWorkflow(),
            TokenKind.Enum      => ParseEnum(),
            TokenKind.Provider  => ParseProvider(),
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

    private EnumDecl? ParseEnum()
    {
        var loc = Current().Location;
        Expect(TokenKind.Enum);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);
        var symbols = ParseSymbolList();
        Expect(TokenKind.RBrace);
        return new EnumDecl(name, symbols, loc);
    }

    private List<string> ParseSymbolList()
    {
        var symbols = new List<string>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var sym = ExpectIdentifier();
            if (sym is not null) symbols.Add(sym);
            if (IsKind(TokenKind.Comma)) Advance();
        }
        return symbols;
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
        var requireInput = TryParseRequireBlock(isInput: true);   // immediately after input block
        Expect(TokenKind.Output);
        Expect(TokenKind.LBrace);
        var output = ParseFieldDecls();
        Expect(TokenKind.RBrace);
        var requireOutput = TryParseRequireBlock(isInput: false);  // immediately after output block
        Expect(TokenKind.RBrace);
        return new ToolDecl(name, input, output, loc, requireInput, requireOutput);
    }

    private AgentDecl? ParseAgent()
    {
        var loc = Current().Location;
        Expect(TokenKind.Agent);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);

        string providerRef = "";
        string modelId = "";

        if (IsKind(TokenKind.Provider))
        {
            // New syntax: provider ProviderRef model "model-id"
            Advance();
            var pRef = ExpectIdentifier() ?? "?";
            if (IsKind(TokenKind.Dot))
            {
                Advance();
                pRef += "." + (ExpectIdentifier() ?? "?");
            }
            providerRef = pRef;

            Expect(TokenKind.Model);
            if (!IsKind(TokenKind.StringLiteral))
            {
                Error("Expected string literal for model ID after 'model'.", Current().Location);
            }
            else
            {
                modelId = Current().Text;
                Advance();
            }
        }
        else if (IsKind(TokenKind.Model))
        {
            // Old syntax (provider ref = "" signals migration needed — validated by SemanticValidator P11)
            Advance();
            modelId = ExpectIdentifier() ?? "?";
        }
        else
        {
            Error("Expected 'provider' or 'model' in agent declaration.", Current().Location);
        }

        SystemPromptNode? systemPrompt = null;
        var seenSystem = false;

        while (IsKind(TokenKind.System))
        {
            var sysLoc = Current().Location;
            Advance();

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

        // Optional typed input
        TypeRef? inputType = null;
        Expr? requireInput = null;
        if (IsKind(TokenKind.Input))
        {
            Advance();
            inputType = ParseTypeRef();
            requireInput = TryParseRequireBlock(isInput: true);
        }

        Expect(TokenKind.Output);
        var outputType = ParseTypeRef();
        var requireOutput = TryParseRequireBlock(isInput: false);
        Expect(TokenKind.Tools);
        Expect(TokenKind.LBrace);
        var allowed = ParseAllowList();
        Expect(TokenKind.RBrace);
        Expect(TokenKind.RBrace);
        return new AgentDecl(name, providerRef, modelId, inputType, outputType, allowed, loc,
            systemPrompt, requireInput, requireOutput);
    }

    // ── Provider declarations ─────────────────────────────────────────────────

    private ProviderDecl? ParseProvider()
    {
        var loc = Current().Location;
        Expect(TokenKind.Provider);
        var name = ExpectIdentifier();
        if (name is null) return null;
        Expect(TokenKind.LBrace);

        // Simulated provider: type simulated
        if (IsKindIdentifier("type"))
        {
            Advance();
            if (IsKindIdentifier("simulated"))
            {
                Advance();
                Expect(TokenKind.RBrace);
                return new ProviderDecl(name, IsSimulated: true, null, null, null, null, loc);
            }
            Error("Unknown provider type. Expected 'simulated' for simulated providers.", Current().Location);
            SkipToNextRBrace();
            if (IsKind(TokenKind.RBrace)) Advance();
            return null;
        }

        // HTTP provider
        string? baseUrl = null;
        EnvExpr? apiKey = null;
        ProviderCallDecl? call = null;
        ProviderResponseDecl? response = null;

        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            if (IsKindIdentifier("base_url"))
            {
                Advance();
                baseUrl = ExpectStringLiteralValue();
            }
            else if (IsKindIdentifier("api_key"))
            {
                Advance();
                apiKey = ParseEnvExpr();
            }
            else if (IsKind(TokenKind.Call))
            {
                Advance();
                call = ParseCallBlock();
            }
            else if (IsKind(TokenKind.Response))
            {
                var rLoc = Current().Location;
                Advance();
                response = ParseResponseBlock(rLoc);
            }
            else
            {
                Error($"Unknown provider property '{Current().Text}'.", Current().Location);
                Advance();
            }
        }
        Expect(TokenKind.RBrace);
        return new ProviderDecl(name, IsSimulated: false, baseUrl, apiKey, call, response, loc);
    }

    private EnvExpr? ParseEnvExpr()
    {
        var loc = Current().Location;
        Expect(TokenKind.Env);
        Expect(TokenKind.LParen);
        if (!IsKind(TokenKind.StringLiteral))
        {
            Error("Expected string literal (env var name) inside env(...).", Current().Location);
            return null;
        }
        var varName = Current().Text;
        Advance();
        Expect(TokenKind.RParen);
        return new EnvExpr(varName, loc);
    }

    private ProviderCallDecl? ParseCallBlock()
    {
        var loc = Current().Location;
        Expect(TokenKind.LBrace);

        string method = "POST";
        string path = "/";
        var headers = new List<HeaderDecl>();
        var body = new List<BodyFieldDecl>();

        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            if (IsKind(TokenKind.Method))
            {
                Advance();
                method = ExpectIdentifier() ?? "POST";
            }
            else if (IsKindIdentifier("path"))
            {
                Advance();
                path = ExpectStringLiteralValue() ?? "/";
            }
            else if (IsKind(TokenKind.Headers))
            {
                Advance();
                Expect(TokenKind.LBrace);
                while (!IsKind(TokenKind.RBrace) && !IsEof())
                {
                    if (!IsKind(TokenKind.StringLiteral))
                    {
                        Error("Expected string header name.", Current().Location);
                        Advance();
                        continue;
                    }
                    var headerName = Current().Text;
                    Advance();
                    var headerValue = ParseHeaderValue();
                    headers.Add(new HeaderDecl(headerName, headerValue));
                }
                Expect(TokenKind.RBrace);
            }
            else if (IsKind(TokenKind.Body))
            {
                Advance();
                Expect(TokenKind.LBrace);
                body = ParseBodyFields();
                Expect(TokenKind.RBrace);
            }
            else
            {
                Error($"Unknown call block property '{Current().Text}'.", Current().Location);
                Advance();
            }
        }
        Expect(TokenKind.RBrace);
        return new ProviderCallDecl(method, path, headers, body, loc);
    }

    private HeaderValue ParseHeaderValue()
    {
        var parts = new List<HeaderValue>();
        parts.Add(ParseSingleHeaderPart());
        while (IsKind(TokenKind.Plus))
        {
            Advance();
            parts.Add(ParseSingleHeaderPart());
        }
        return parts.Count == 1 ? parts[0] : new ConcatHeaderValue(parts);
    }

    private HeaderValue ParseSingleHeaderPart()
    {
        if (IsKind(TokenKind.StringLiteral))
        {
            var text = Current().Text;
            Advance();
            return new LiteralHeaderValue(text);
        }
        var name = ExpectIdentifier() ?? "?";
        return new FieldHeaderValue(name);
    }

    private List<BodyFieldDecl> ParseBodyFields()
    {
        var fields = new List<BodyFieldDecl>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            if (!IsKind(TokenKind.StringLiteral))
            {
                Error("Expected string key in body field.", Current().Location);
                Advance();
                continue;
            }
            var key = Current().Text;
            Advance();
            var value = ParseBodyValue();
            fields.Add(new BodyFieldDecl(key, value));
        }
        return fields;
    }

    private BodyValue ParseBodyValue()
    {
        var tok = Current();

        // Nested object: { key value ... }
        if (tok.Kind == TokenKind.LBrace)
        {
            Advance();
            var fields = ParseBodyFields();
            Expect(TokenKind.RBrace);
            return new ObjectBodyValue(fields);
        }

        // Interpolation: $varName or $messages/tools/calls { ... }
        if (tok.Kind == TokenKind.Dollar)
        {
            Advance();
            var varName = ExpectIdentifier() ?? "?";
            if (varName == "messages" && IsKind(TokenKind.LBrace))
            {
                Advance();
                var mappings = ParseMessageMappings();
                Expect(TokenKind.RBrace);
                return new MessagesBodyValue(mappings);
            }
            if (varName == "tools" && IsKind(TokenKind.LBrace))
            {
                Advance();
                var template = ParseBodyFields();
                Expect(TokenKind.RBrace);
                return new ToolsBodyValue(template);
            }
            if (varName == "calls" && IsKind(TokenKind.LBrace))
            {
                Advance();
                var template = ParseBodyFields();
                Expect(TokenKind.RBrace);
                return new CallsBodyValue(template);
            }
            return new InterpolationBodyValue(varName);
        }

        // String literal
        if (tok.Kind == TokenKind.StringLiteral)
        {
            Advance();
            return new StringBodyValue(tok.Text);
        }

        // Integer literal
        if (tok.Kind == TokenKind.IntLiteral)
        {
            Advance();
            if (long.TryParse(tok.Text, out var n))
                return new IntBodyValue(n);
            Error("Integer literal out of range.", tok.Location);
            return new IntBodyValue(0);
        }

        // Boolean
        if (tok.Kind == TokenKind.True)  { Advance(); return new BoolBodyValue(true); }
        if (tok.Kind == TokenKind.False) { Advance(); return new BoolBodyValue(false); }

        Error($"Expected body value, got '{tok.Text}'.", tok.Location);
        Advance();
        return new StringBodyValue("");
    }

    private List<MessageMappingDecl> ParseMessageMappings()
    {
        var mappings = new List<MessageMappingDecl>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var typeName = ExpectIdentifier() ?? "?";
            Expect(TokenKind.Arrow);
            Expect(TokenKind.LBrace);
            var fields = ParseBodyFields();
            Expect(TokenKind.RBrace);
            mappings.Add(new MessageMappingDecl(typeName, fields));
        }
        return mappings;
    }

    private ProviderResponseDecl ParseResponseBlock(SourceLocation loc)
    {
        Expect(TokenKind.LBrace);
        FinishReasonDecl? finishReason = null;
        string? textSelector = null;
        ProviderToolCallsDecl? toolCalls = null;

        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            if (IsKind(TokenKind.FinishReason))
            {
                Advance();
                Expect(TokenKind.LBrace);
                string? selector = null, stopVal = null, tcVal = null;
                while (!IsKind(TokenKind.RBrace) && !IsEof())
                {
                    var key = ExpectIdentifier() ?? "?";
                    var val = IsKind(TokenKind.StringLiteral) ? Current().Text : null;
                    if (IsKind(TokenKind.StringLiteral)) Advance();
                    if (key == "path")        selector = val;
                    else if (key == "stop")        stopVal  = val;
                    else if (key == "tool_calls")  tcVal    = val;
                    else Error($"Unknown finish_reason key '{key}'. Expected path, stop, or tool_calls.", Current().Location);
                }
                Expect(TokenKind.RBrace);
                finishReason = new FinishReasonDecl(selector ?? "", stopVal ?? "", tcVal ?? "");
            }
            else
            {
                var key = ExpectIdentifier() ?? "?";
                if (key == "text")
                {
                    textSelector = IsKind(TokenKind.StringLiteral) ? Current().Text : null;
                    if (IsKind(TokenKind.StringLiteral)) Advance();
                }
                else if (key == "tool_calls")
                {
                    var tcSelector = IsKind(TokenKind.StringLiteral) ? Current().Text : "";
                    if (IsKind(TokenKind.StringLiteral)) Advance();
                    Expect(TokenKind.LBrace);
                    string? idSel = null, nameSel = null, argsSel = null;
                    while (!IsKind(TokenKind.RBrace) && !IsEof())
                    {
                        var k = ExpectIdentifier() ?? "?";
                        var v = IsKind(TokenKind.StringLiteral) ? Current().Text : null;
                        if (IsKind(TokenKind.StringLiteral)) Advance();
                        if (k == "id")             idSel   = v;
                        else if (k == "tool_name") nameSel = v;
                        else if (k == "args")      argsSel = v;
                        else Error($"Unknown tool_calls key '{k}'. Expected id, tool_name, or args.", Current().Location);
                    }
                    Expect(TokenKind.RBrace);
                    toolCalls = new ProviderToolCallsDecl(tcSelector, idSel ?? "", nameSel ?? "", argsSel ?? "");
                }
                else
                {
                    Error($"Unknown response key '{key}'. Expected text or tool_calls.", Current().Location);
                }
            }
        }
        Expect(TokenKind.RBrace);
        return new ProviderResponseDecl(finishReason, textSelector, toolCalls, loc);
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
        var requireInput = TryParseRequireBlock(isInput: true);    // immediately after input TypeRef
        Expect(TokenKind.Output);
        var outputType = ParseTypeRef();
        var requireOutput = TryParseRequireBlock(isInput: false);   // immediately after output TypeRef
        var items = ParseWorkflowItems();
        Expect(TokenKind.Finish);
        Expect(TokenKind.With);
        var finish = ParseExpr();
        Expect(TokenKind.RBrace);
        return new WorkflowDecl(name, inputType, outputType, items, finish, loc,
            requireInput, requireOutput);
    }

    // ── Require blocks ────────────────────────────────────────────────────────

    private Expr? TryParseRequireBlock(bool isInput)
    {
        if (!IsKind(TokenKind.Require)) return null;
        var kind = isInput ? TokenKind.Input : TokenKind.Output;
        // Peek: require input or require output
        if (_pos + 1 >= tokens.Count) return null;
        if (tokens[_pos + 1].Kind != kind) return null;

        Advance(); // consume 'require'
        Advance(); // consume 'input'/'output'
        Expect(TokenKind.LBrace);
        var expr = ParseExpr();
        Expect(TokenKind.RBrace);
        return expr;
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
            var optional = false;
            if (IsKind(TokenKind.Optional))
            {
                optional = true;
                Advance();
            }
            fields.Add(new FieldDecl(fieldName, type, optional));
        }
        return fields;
    }

    private TypeRef ParseTypeRef()
    {
        var tok = Current();
        if (tok.Kind == TokenKind.KwString)  { Advance(); return new PrimitiveTypeRef(PrimitiveKind.String); }
        if (tok.Kind == TokenKind.KwBool)    { Advance(); return new PrimitiveTypeRef(PrimitiveKind.Bool); }
        if (tok.Kind == TokenKind.KwInt)     { Advance(); return new PrimitiveTypeRef(PrimitiveKind.Int); }
        if (tok.Kind == TokenKind.KwDecimal) { Advance(); return new PrimitiveTypeRef(PrimitiveKind.Decimal); }
        if (tok.Kind == TokenKind.Identifier)
        {
            if (tok.Text == "List")
            {
                Advance();
                Expect(TokenKind.Lt);
                var elementType = ParseTypeRef();
                Expect(TokenKind.Gt);
                return new ListTypeRef(elementType, tok.Location);
            }
            if (tok.Text == "Nullable")
            {
                Advance();
                Expect(TokenKind.Lt);
                var inner = ParseTypeRef();
                Expect(TokenKind.Gt);
                return new NullableTypeRef(inner, tok.Location);
            }
            Advance();
            // Check for qualified name: Alias.TypeName
            if (IsKind(TokenKind.Dot))
            {
                Advance(); // consume '.'
                var memberName = ExpectIdentifier() ?? "?";
                return new QualifiedNameTypeRef(tok.Text, memberName, tok.Location);
            }
            return new NamedTypeRef(tok.Text, tok.Location);
        }
        Error($"Expected type, got '{tok.Text}'", tok.Location);
        return new PrimitiveTypeRef(PrimitiveKind.String);
    }

    // ── Allow list ───────────────────────────────────────────────────────────

    private List<AllowedToolEntry> ParseAllowList()
    {
        var entries = new List<AllowedToolEntry>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            Expect(TokenKind.Allow);
            var n = ParseDeclarationReference();
            if (n is null) { Advance(); continue; }
            Expr? guard = null;
            if (IsKind(TokenKind.When))
            {
                Advance();
                guard = ParseExpr();
            }
            entries.Add(new AllowedToolEntry(n, guard));
        }
        return entries;
    }

    // ── Workflow items ────────────────────────────────────────────────────────

    private List<WorkflowItem> ParseWorkflowItems(bool insideLoop = false)
    {
        var items = new List<WorkflowItem>();
        while (!IsEof())
        {
            if (IsKind(TokenKind.Step))
            {
                items.Add(new StepItem(ParseStep()));
            }
            else if (IsKind(TokenKind.If))
            {
                items.Add(ParseIfItem(insideLoop));
            }
            else if (IsKind(TokenKind.Loop))
            {
                items.Add(ParseLoopItem());
            }
            else if (insideLoop && IsKind(TokenKind.Break))
            {
                items.Add(ParseBreakItem());
            }
            else if (insideLoop && IsKind(TokenKind.Continue))
            {
                items.Add(ParseContinueItem());
            }
            else
            {
                break;
            }
        }
        return items;
    }

    // ── Loop items ────────────────────────────────────────────────────────────

    private LoopItem ParseLoopItem()
    {
        var loc = Current().Location;
        Expect(TokenKind.Loop);
        var name = ExpectIdentifier() ?? "?";
        Expect(TokenKind.LBrace);

        Expect(TokenKind.Params);
        Expect(TokenKind.LBrace);
        var loopParams = ParseLoopParams();
        Expect(TokenKind.RBrace);

        Expect(TokenKind.Output);
        var outputType = ParseTypeRef();

        Expect(TokenKind.Max);
        var maxTok = Current();
        int maxIterations = 1;
        if (maxTok.Kind == TokenKind.IntLiteral)
        {
            if (int.TryParse(maxTok.Text, out var parsed) && parsed > 0)
                maxIterations = parsed;
            else
                Error("'max' value must be a positive integer.", maxTok.Location);
            Advance();
        }
        else
        {
            Error($"Expected integer after 'max', got '{maxTok.Text}'.", maxTok.Location);
        }

        var body = ParseWorkflowItems(insideLoop: true);

        Expect(TokenKind.Save);
        Expect(TokenKind.As);
        var saveAs = ExpectIdentifier() ?? "?";
        Expect(TokenKind.RBrace);

        return new LoopItem(name, loopParams, outputType, maxIterations, body, saveAs, loc);
    }

    private List<LoopParam> ParseLoopParams()
    {
        var list = new List<LoopParam>();
        while (!IsKind(TokenKind.RBrace) && !IsEof())
        {
            var loc = Current().Location;
            var paramName = ExpectIdentifier();
            if (paramName is null) { SkipToNextRBrace(); break; }
            Expect(TokenKind.Colon);
            var paramType = ParseTypeRef();
            Expect(TokenKind.Assign);
            var initExpr = ParseExpr();
            list.Add(new LoopParam(paramName, paramType, initExpr, loc));
            if (IsKind(TokenKind.Comma)) Advance();
        }
        return list;
    }

    private LoopBreakItem ParseBreakItem()
    {
        var loc = Current().Location;
        Expect(TokenKind.Break);
        var expr = ParseExpr();
        return new LoopBreakItem(expr, loc);
    }

    private LoopContinueItem ParseContinueItem()
    {
        var loc = Current().Location;
        Expect(TokenKind.Continue);
        Expect(TokenKind.LBrace);
        var args = ParseArgList();
        Expect(TokenKind.RBrace);
        return new LoopContinueItem(args, loc);
    }

    private IfItem ParseIfItem(bool insideLoop = false)
    {
        var loc = Current().Location;
        Expect(TokenKind.If);
        var condition = ParseExpr();
        Expect(TokenKind.LBrace);
        var thenItems = ParseWorkflowItems(insideLoop);
        Expect(TokenKind.RBrace);
        List<WorkflowItem>? elseItems = null;
        if (IsKind(TokenKind.Else))
        {
            Advance();
            Expect(TokenKind.LBrace);
            elseItems = ParseWorkflowItems(insideLoop);
            Expect(TokenKind.RBrace);
        }
        return new IfItem(condition, thenItems, elseItems, loc);
    }

    // ── Steps ────────────────────────────────────────────────────────────────

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
        if (IsKind(TokenKind.If))
            return ParseConditionalStepBody();
        if (IsKind(TokenKind.Workflow))
            return ParseWorkflowCallBody();
        Error($"Expected 'call', 'agent', 'workflow', or 'if' in step body, got '{Current().Text}'", Current().Location);
        return new CallBody("?", new Dictionary<string, Expr>());
    }

    private WorkflowCallBody ParseWorkflowCallBody()
    {
        var loc = Current().Location;
        Expect(TokenKind.Workflow);
        var alias = ExpectIdentifier() ?? "?";
        Expect(TokenKind.Dot);
        var workflowName = ExpectIdentifier() ?? "?";
        Expect(TokenKind.Input);
        var inputExpr = ParseExpr();
        return new WorkflowCallBody(alias, workflowName, inputExpr, loc);
    }

    private ConditionalStepBody ParseConditionalStepBody()
    {
        var loc = Current().Location;
        Expect(TokenKind.If);
        var condition = ParseExpr();
        Expect(TokenKind.LBrace);
        var thenBody = ParseStepBody();
        Expect(TokenKind.RBrace);
        if (!IsKind(TokenKind.Else))
            Error("Expected 'else' in step-level conditional — both branches must produce a value.", Current().Location);
        Advance(); // consume 'else'
        Expect(TokenKind.LBrace);
        var elseBody = ParseStepBody();
        Expect(TokenKind.RBrace);
        return new ConditionalStepBody(condition, thenBody, elseBody, loc);
    }

    private CallBody ParseCallBody()
    {
        Expect(TokenKind.Call);
        var toolName = ParseDeclarationReference() ?? "?";
        Expect(TokenKind.LBrace);
        var args = ParseArgList();
        Expect(TokenKind.RBrace);
        return new CallBody(toolName, args);
    }

    private AgentBody ParseAgentBody()
    {
        Expect(TokenKind.Agent);
        var agentName = ParseDeclarationReference() ?? "?";

        // Optional: input expr
        Expr? inputExpr = null;
        if (IsKind(TokenKind.Input))
        {
            Advance();
            inputExpr = ParseExpr();
        }

        Expect(TokenKind.Context);
        Expect(TokenKind.LBrace);
        var names = ParseNameList();
        Expect(TokenKind.RBrace);
        return new AgentBody(agentName, inputExpr, names);
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

    // ── Expressions — precedence-climbing ────────────────────────────────────
    //
    // Precedence (low → high):
    //   5. if cond then expr else expr   (ConditionalExpr)
    //   4. or
    //   3. and
    //   2. not expr                      (prefix)
    //   1. == != < <= > >=              (comparison, non-chainable)
    //   0. atom: literal, (expr), name.field

    private Expr ParseExpr() => ParseConditionalExpr();

    private Expr ParseConditionalExpr()
    {
        if (IsKind(TokenKind.If))
        {
            var loc = Current().Location;
            Advance();
            var cond = ParseOrExpr();
            Expect(TokenKind.Then);
            var then = ParseConditionalExpr();
            Expect(TokenKind.Else);
            var @else = ParseConditionalExpr();
            return new ConditionalExpr(cond, then, @else, loc);
        }
        return ParseOrExpr();
    }

    private Expr ParseOrExpr()
    {
        var left = ParseAndExpr();
        while (IsKind(TokenKind.Or))
        {
            var loc = Current().Location;
            Advance();
            var right = ParseAndExpr();
            left = new BinaryExpr(BinaryOp.Or, left, right, loc);
        }
        return left;
    }

    private Expr ParseAndExpr()
    {
        var left = ParseNotExpr();
        while (IsKind(TokenKind.And))
        {
            var loc = Current().Location;
            Advance();
            var right = ParseNotExpr();
            left = new BinaryExpr(BinaryOp.And, left, right, loc);
        }
        return left;
    }

    private Expr ParseNotExpr()
    {
        if (IsKind(TokenKind.Not))
        {
            var loc = Current().Location;
            Advance();
            var operand = ParseNotExpr();
            return new NotExpr(operand, loc);
        }
        return ParseComparisonExpr();
    }

    private Expr ParseComparisonExpr()
    {
        var left = ParseAtom();
        var op = Current().Kind switch
        {
            TokenKind.EqEq   => (BinaryOp?)BinaryOp.Eq,
            TokenKind.BangEq => BinaryOp.Ne,
            TokenKind.Lt     => BinaryOp.Lt,
            TokenKind.LtEq   => BinaryOp.Le,
            TokenKind.Gt     => BinaryOp.Gt,
            TokenKind.GtEq   => BinaryOp.Ge,
            _                => null,
        };
        if (op is null) return left;
        var loc = Current().Location;
        Advance();
        var right = ParseAtom();

        // Reject chaining: a < b < c is a compile error
        var next = Current().Kind;
        if (next is TokenKind.EqEq or TokenKind.BangEq or TokenKind.Lt
                 or TokenKind.LtEq or TokenKind.Gt   or TokenKind.GtEq)
        {
            Error("Chained comparisons are not allowed. Use parentheses or 'and' to combine conditions.", Current().Location);
        }

        return new BinaryExpr(op.Value, left, right, loc);
    }

    private Expr ParseAtom()
    {
        var tok = Current();

        // Parenthesized expression
        if (tok.Kind == TokenKind.LParen)
        {
            Advance();
            var inner = ParseExpr();
            Expect(TokenKind.RParen);
            return inner;
        }

        // String literal
        if (tok.Kind == TokenKind.StringLiteral || tok.Kind == TokenKind.TripleStringLiteral)
        {
            Advance();
            return new StringLiteralExpr(tok.Text, tok.Location);
        }

        // Integer literal
        if (tok.Kind == TokenKind.IntLiteral)
        {
            Advance();
            if (!long.TryParse(tok.Text, out var intVal))
            {
                Error($"Integer literal '{tok.Text}' is out of range for Int64.", tok.Location);
                intVal = 0;
            }
            return new IntLiteralExpr(intVal, tok.Location);
        }

        // Bool literals
        if (tok.Kind == TokenKind.True)  { Advance(); return new BoolLiteralExpr(true,  tok.Location); }
        if (tok.Kind == TokenKind.False) { Advance(); return new BoolLiteralExpr(false, tok.Location); }

        // Name / field-access (existing atoms)
        if (tok.Kind != TokenKind.Identifier && !IsKeyword(tok.Kind))
        {
            Error($"Expected expression, got '{tok.Text}'", tok.Location);
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

    private string? ParseDeclarationReference()
    {
        var name = ExpectIdentifier();
        if (IsKind(TokenKind.Dot))
        {
            Advance();
            name += "." + (ExpectIdentifier() ?? "?");
        }
        return name;
    }

    private string? ExpectIdentifier()
    {
        var tok = Current();
        if (tok.Kind == TokenKind.Identifier || IsKeyword(tok.Kind))
        {
            Advance();
            return tok.Text;
        }
        Error($"Expected identifier, got '{tok.Text}'", tok.Location);
        return null;
    }

    private static bool IsKeyword(TokenKind kind) =>
        kind is TokenKind.Schema      or TokenKind.Tool      or TokenKind.Agent       or TokenKind.Workflow
             or TokenKind.Step        or TokenKind.Call      or TokenKind.Finish      or TokenKind.With
             or TokenKind.Save        or TokenKind.As        or TokenKind.Context     or TokenKind.Input
             or TokenKind.Output      or TokenKind.Model     or TokenKind.Tools       or TokenKind.Allow
             or TokenKind.System      or TokenKind.If        or TokenKind.Else        or TokenKind.When
             or TokenKind.Require     or TokenKind.Then      or TokenKind.Not         or TokenKind.And
             or TokenKind.Or          or TokenKind.True      or TokenKind.False       or TokenKind.Enum
             or TokenKind.Optional    or TokenKind.Loop      or TokenKind.Break       or TokenKind.Continue
             or TokenKind.Max         or TokenKind.Params    or TokenKind.Provider    or TokenKind.Env
             or TokenKind.Response    or TokenKind.Method    or TokenKind.Headers     or TokenKind.Body
             or TokenKind.FinishReason;

    private void SkipToNextRBrace()
    {
        while (!IsEof() && !IsKind(TokenKind.RBrace))
            Advance();
    }

    private Declaration? UnexpectedDeclaration(Token tok)
    {
        Error($"Expected 'schema', 'tool', 'agent', 'workflow', 'enum', or 'provider', got '{tok.Text}'", tok.Location);
        SkipToNextRBrace();
        if (IsKind(TokenKind.RBrace)) Advance();
        return null;
    }

    private bool IsKindIdentifier(string name) =>
        Current().Kind == TokenKind.Identifier && Current().Text == name;

    private string? ExpectStringLiteralValue()
    {
        if (IsKind(TokenKind.StringLiteral))
        {
            var text = Current().Text;
            Advance();
            return text;
        }
        Error("Expected string literal.", Current().Location);
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
        TokenKind.If        => "if",
        TokenKind.Else      => "else",
        TokenKind.When      => "when",
        TokenKind.Require   => "require",
        TokenKind.Then      => "then",
        TokenKind.Not       => "not",
        TokenKind.And       => "and",
        TokenKind.Or        => "or",
        TokenKind.True      => "true",
        TokenKind.False     => "false",
        TokenKind.LBrace    => "{",
        TokenKind.RBrace    => "}",
        TokenKind.LParen    => "(",
        TokenKind.RParen    => ")",
        TokenKind.Colon     => ":",
        TokenKind.Dot       => ".",
        TokenKind.Comma     => ",",
        TokenKind.EqEq      => "==",
        TokenKind.BangEq    => "!=",
        TokenKind.Lt        => "<",
        TokenKind.LtEq      => "<=",
        TokenKind.Gt        => ">",
        TokenKind.GtEq      => ">=",
        TokenKind.Identifier   => "<identifier>",
        TokenKind.Enum         => "enum",
        TokenKind.Optional     => "optional",
        TokenKind.KwDecimal    => "Decimal",
        TokenKind.Loop         => "loop",
        TokenKind.Break        => "break",
        TokenKind.Continue     => "continue",
        TokenKind.Max          => "max",
        TokenKind.Params       => "params",
        TokenKind.Assign       => "=",
        TokenKind.Provider     => "provider",
        TokenKind.Env          => "env",
        TokenKind.Response     => "response",
        TokenKind.Method       => "method",
        TokenKind.Headers      => "headers",
        TokenKind.Body         => "body",
        TokenKind.FinishReason => "finish_reason",
        TokenKind.Arrow        => "->",
        TokenKind.Dollar       => "$",
        TokenKind.Plus         => "+",
        _ => kind.ToString(),
    };
}

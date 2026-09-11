using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Mail.Runtime;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Mail.Tests;

/// <summary>
/// Tests for the full MAIL type system: Decimal, List, Enum, Nullable, Optional.
/// Covers ArgumentValidator, DeclaredSchema, SchemaConverter, ContractVerifier, and the parser.
/// </summary>
public class TypeContractTests
{
    // ── ArgumentValidator: Decimal ─────────────────────────────────────────────

    [Fact]
    public void Decimal_valid_fixed_string_is_accepted()
    {
        var result = ValidateSingle("amount", MailTypeKind.Decimal, "\"125.50\"");
        var d = Assert.IsType<MailDecimal>(result);
        Assert.Equal(125.50m, d.Value);
    }

    [Fact]
    public void Decimal_integer_string_is_accepted()
    {
        var result = ValidateSingle("amount", MailTypeKind.Decimal, "\"100\"");
        var d = Assert.IsType<MailDecimal>(result);
        Assert.Equal(100m, d.Value);
    }

    [Fact]
    public void Decimal_negative_is_accepted()
    {
        var result = ValidateSingle("amount", MailTypeKind.Decimal, "\"-42.5\"");
        var d = Assert.IsType<MailDecimal>(result);
        Assert.Equal(-42.5m, d.Value);
    }

    [Fact]
    public void Decimal_numeric_json_is_rejected()
    {
        var ex = Assert.Throws<ArgumentValidationException>(() =>
            ValidateSingle("amount", MailTypeKind.Decimal, "125.50"));
        Assert.Contains("JSON string", ex.Message);
    }

    [Fact]
    public void Decimal_exponent_string_is_rejected()
    {
        var ex = Assert.Throws<ArgumentValidationException>(() =>
            ValidateSingle("amount", MailTypeKind.Decimal, "\"1.25e2\""));
        Assert.Contains("exponent", ex.Message);
    }

    [Fact]
    public void Decimal_invalid_string_is_rejected()
    {
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateSingle("amount", MailTypeKind.Decimal, "\"not-a-number\""));
    }

    // ── ArgumentValidator: List ────────────────────────────────────────────────

    [Fact]
    public void List_valid_string_array_is_accepted()
    {
        var fc = new FieldContract("tags", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.String);
        var result = ValidateField(fc, "[\"a\",\"b\",\"c\"]");
        var list = Assert.IsType<MailList>(result);
        Assert.Equal(3, list.Elements.Count);
        Assert.Equal("a", ((MailString)list.Elements[0]).Value);
    }

    [Fact]
    public void List_empty_array_is_accepted()
    {
        var fc = new FieldContract("tags", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.String);
        var result = ValidateField(fc, "[]");
        var list = Assert.IsType<MailList>(result);
        Assert.Empty(list.Elements);
    }

    [Fact]
    public void List_wrong_element_type_is_rejected()
    {
        var fc = new FieldContract("counts", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.Int);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateField(fc, "[\"not-an-int\"]"));
    }

    [Fact]
    public void List_non_array_json_is_rejected()
    {
        var fc = new FieldContract("tags", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.String);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateField(fc, "\"not-an-array\""));
    }

    [Fact]
    public void List_null_element_is_rejected()
    {
        var fc = new FieldContract("tags", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.String);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateField(fc, "[\"a\", null]"));
    }

    // ── ArgumentValidator: Enum ────────────────────────────────────────────────

    [Fact]
    public void Enum_valid_symbol_is_accepted()
    {
        var symbols = ImmutableArray.Create("Pending", "Shipped", "Delivered");
        var fc = new FieldContract("status", MailTypeKind.Enum, Required: true,
            EnumTypeName: "OrderStatus", EnumSymbols: symbols);
        var result = ValidateField(fc, "\"Shipped\"");
        var e = Assert.IsType<MailEnum>(result);
        Assert.Equal("Shipped", e.Symbol);
        Assert.Equal("OrderStatus", e.TypeName);
    }

    [Fact]
    public void Enum_unknown_symbol_is_rejected()
    {
        var symbols = ImmutableArray.Create("Pending", "Shipped");
        var fc = new FieldContract("status", MailTypeKind.Enum, Required: true,
            EnumTypeName: "OrderStatus", EnumSymbols: symbols);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateField(fc, "\"Cancelled\""));
    }

    [Fact]
    public void Enum_case_mismatch_is_rejected()
    {
        var symbols = ImmutableArray.Create("Pending", "Shipped");
        var fc = new FieldContract("status", MailTypeKind.Enum, Required: true,
            EnumTypeName: "OrderStatus", EnumSymbols: symbols);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateField(fc, "\"shipped\"")); // lowercase — must be rejected
    }

    [Fact]
    public void Enum_numeric_json_is_rejected()
    {
        var symbols = ImmutableArray.Create("A", "B");
        var fc = new FieldContract("e", MailTypeKind.Enum, Required: true,
            EnumTypeName: "MyEnum", EnumSymbols: symbols);
        Assert.Throws<ArgumentValidationException>(() => ValidateField(fc, "1"));
    }

    // ── ArgumentValidator: Nullable presence matrix ────────────────────────────

    [Fact]
    public void Required_nullable_accepts_null_json()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: true, Nullable: true);
        var result = ValidateField(fc, "null");
        Assert.IsType<MailNull>(result);
    }

    [Fact]
    public void Required_nullable_accepts_value()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: true, Nullable: true);
        var result = ValidateField(fc, "\"hello\"");
        Assert.IsType<MailString>(result);
    }

    [Fact]
    public void Required_nullable_rejects_absent()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: true, Nullable: true);
        Assert.Throws<ArgumentValidationException>(() => ValidateAbsent(fc));
    }

    [Fact]
    public void Required_non_nullable_rejects_null()
    {
        var fc = new FieldContract("name", MailTypeKind.String, Required: true, Nullable: false);
        Assert.Throws<ArgumentValidationException>(() => ValidateField(fc, "null"));
    }

    [Fact]
    public void Required_non_nullable_rejects_absent()
    {
        var fc = new FieldContract("name", MailTypeKind.String, Required: true, Nullable: false);
        Assert.Throws<ArgumentValidationException>(() => ValidateAbsent(fc));
    }

    [Fact]
    public void Optional_non_nullable_accepts_absent()
    {
        var fc = new FieldContract("reason", MailTypeKind.String, Required: false, Nullable: false);
        var schema = ValidateWithContract([fc], "{}");
        Assert.False(schema.Fields.ContainsKey("reason"));
    }

    [Fact]
    public void Optional_non_nullable_rejects_null()
    {
        var fc = new FieldContract("reason", MailTypeKind.String, Required: false, Nullable: false);
        Assert.Throws<ArgumentValidationException>(() =>
            ValidateWithContract([fc], "{\"reason\": null}"));
    }

    [Fact]
    public void Optional_nullable_accepts_absent()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: false, Nullable: true);
        var schema = ValidateWithContract([fc], "{}");
        Assert.False(schema.Fields.ContainsKey("note"));
    }

    [Fact]
    public void Optional_nullable_accepts_null()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: false, Nullable: true);
        var schema = ValidateWithContract([fc], "{\"note\": null}");
        Assert.True(schema.Fields.ContainsKey("note"));
        Assert.IsType<MailNull>(schema.Fields["note"]);
    }

    [Fact]
    public void Optional_nullable_accepts_value()
    {
        var fc = new FieldContract("note", MailTypeKind.String, Required: false, Nullable: true);
        var schema = ValidateWithContract([fc], "{\"note\": \"hi\"}");
        Assert.IsType<MailString>(schema.Fields["note"]);
    }

    // ── SchemaConverter: serialization round-trip ─────────────────────────────

    [Fact]
    public void SchemaConverter_serializes_Decimal_as_string()
    {
        var json = SchemaConverter.ToJson(new MailDecimal(125.50m));
        Assert.Equal("\"125.50\"", json);
    }

    [Fact]
    public void SchemaConverter_serializes_List_as_array()
    {
        var list = new MailList(ImmutableList.Create<MailValue>(
            new MailString("a"), new MailString("b")));
        var json = SchemaConverter.ToJson(list);
        Assert.Equal("[\"a\",\"b\"]", json);
    }

    [Fact]
    public void SchemaConverter_serializes_Enum_as_string()
    {
        var json = SchemaConverter.ToJson(new MailEnum("Status", "Active"));
        Assert.Equal("\"Active\"", json);
    }

    [Fact]
    public void SchemaConverter_serializes_MailNull_as_null()
    {
        var json = SchemaConverter.ToJson(new MailNull());
        Assert.Equal("null", json);
    }

    // ── Parser: new type constructs ────────────────────────────────────────────

    [Fact]
    public void Parser_parses_Decimal_field_type()
    {
        var source = """
            schema Item { amount: Decimal }
            tool Noop { input { x: String } output { y: String } }
            agent A { model M output Item tools { allow Noop } }
            workflow W { input Item output Item step s { call Noop { x: input.amount } save as s } finish with input }
            """;
        // Parser should reject this (amount is Decimal, not String) — we just test parsing succeeds
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        // Should have no parse errors (semantic may warn about type mismatch but that's expected)
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-PARSE");
    }

    [Fact]
    public void Parser_parses_enum_declaration()
    {
        var source = """
            enum Status { Pending, Shipped, Delivered }
            schema Order { id: String  status: Status }
            tool Noop { input { x: String } output { y: String } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Order tools { allow Noop } }
            workflow W { input Order output Order step s { call Noop { x: input.id } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);
        Assert.DoesNotContain(errors, e => e.Severity == Mail.Contracts.DiagnosticSeverity.Error);
        Assert.True(plan.Enums.ContainsKey("Status"));
        Assert.Equal(["Pending", "Shipped", "Delivered"], plan.Enums["Status"].Symbols);
    }

    [Fact]
    public void Parser_parses_nullable_field_type()
    {
        var source = """
            schema Request { note: Nullable<String> }
            tool Noop { input { x: String } output { y: String } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Request tools { allow Noop } }
            workflow W { input Request output Request step s { call Noop { x: "x" } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);
        Assert.DoesNotContain(errors, e => e.Severity == Mail.Contracts.DiagnosticSeverity.Error);
        var field = plan.Schemas["Request"].Fields.Single(f => f.Name == "note");
        Assert.IsType<NullableTypeRef>(field.Type);
    }

    [Fact]
    public void Parser_parses_list_field_type()
    {
        var source = """
            schema Batch { items: List<String> }
            tool Noop { input { x: String } output { y: String } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Batch tools { allow Noop } }
            workflow W { input Batch output Batch step s { call Noop { x: "x" } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);
        Assert.DoesNotContain(errors, e => e.Severity == Mail.Contracts.DiagnosticSeverity.Error);
        var field = plan.Schemas["Batch"].Fields.Single(f => f.Name == "items");
        Assert.IsType<ListTypeRef>(field.Type);
    }

    [Fact]
    public void Parser_parses_optional_field_modifier()
    {
        var source = """
            schema Req { id: String  note: String optional }
            tool Noop { input { x: String } output { y: String } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Req tools { allow Noop } }
            workflow W { input Req output Req step s { call Noop { x: input.id } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);
        Assert.DoesNotContain(errors, e => e.Severity == Mail.Contracts.DiagnosticSeverity.Error);
        var noteField = plan.Schemas["Req"].Fields.Single(f => f.Name == "note");
        Assert.True(noteField.Optional);
        var idField = plan.Schemas["Req"].Fields.Single(f => f.Name == "id");
        Assert.False(idField.Optional);
    }

    // ── Semantic validation: enum constraints ─────────────────────────────────

    [Fact]
    public void Semantic_rejects_undeclared_enum_reference()
    {
        var source = """
            schema Order { status: UndeclaredEnum }
            tool Noop { input { x: String } output { y: String } }
            agent A { model M output Order tools { allow Noop } }
            workflow W { input Order output Order step s { call Noop { x: "x" } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.Null(plan);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.SchemaNotDeclared);
    }

    [Fact]
    public void Semantic_rejects_Nullable_Nullable()
    {
        var source = """
            schema Req { note: Nullable<Nullable<String>> }
            tool Noop { input { x: String } output { y: String } }
            agent A { model M output Req tools { allow Noop } }
            workflow W { input Req output Req step s { call Noop { x: "x" } save as s } finish with input }
            """;
        var (plan, errors) = MailCompiler.Compile(source, "test.mail");
        Assert.Null(plan);
        Assert.Contains(errors, e => e.Message.Contains("Nullable<Nullable<T>>"));
    }

    // ── ContractVerifier: Nullable flag consistency ───────────────────────────

    [Fact]
    public void ContractVerifier_rejects_nullable_mismatch_declared_not_registered()
    {
        var source = """
            schema Req { note: Nullable<String> }
            tool MyTool { input { note: Nullable<String> } output { ok: Bool } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Req tools { allow MyTool } }
            workflow W { input Req output Req step s { call MyTool { note: input.note } save as s } finish with input }
            """;
        var (plan, _) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);

        var tools = new ToolRegistry();
        // Register WITHOUT Nullable: true — should be caught by ContractVerifier
        tools.Register(
            "MyTool",
            new NullableTestTool(),
            [new FieldContract("note", MailTypeKind.String, Required: true, Nullable: false)], // mismatch
            [new FieldContract("ok", MailTypeKind.Bool, Required: true)]);

        Assert.Throws<ToolContractMismatchException>(() => ContractVerifier.Verify(plan, tools));
    }

    [Fact]
    public void ContractVerifier_accepts_matching_nullable_contract()
    {
        var source = """
            schema Req { note: Nullable<String> }
            tool MyTool { input { note: Nullable<String> } output { ok: Bool } }
            provider Sim { type simulated }
            agent A { provider Sim model "m" output Req tools { allow MyTool } }
            workflow W { input Req output Req step s { call MyTool { note: input.note } save as s } finish with input }
            """;
        var (plan, _) = MailCompiler.Compile(source, "test.mail");
        Assert.NotNull(plan);

        var tools = new ToolRegistry();
        tools.Register(
            "MyTool",
            new NullableTestTool(),
            [new FieldContract("note", MailTypeKind.String, Required: true, Nullable: true)], // matches
            [new FieldContract("ok", MailTypeKind.Bool, Required: true)]);

        // Should not throw
        ContractVerifier.Verify(plan, tools);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MailValue ValidateSingle(string name, MailTypeKind kind, string jsonValue)
    {
        var fc = new FieldContract(name, kind, Required: true);
        return ValidateField(fc, jsonValue);
    }

    private static MailValue ValidateField(FieldContract fc, string jsonValue)
    {
        var schema = ValidateWithContract([fc], $"{{\"{fc.Name}\": {jsonValue}}}");
        return schema.Fields[fc.Name];
    }

    private static void ValidateAbsent(FieldContract fc)
    {
        ValidateWithContract([fc], "{}");
    }

    private static MailSchema ValidateWithContract(FieldContract[] contract, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rawArgs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            rawArgs[prop.Name] = prop.Value.Clone();

        var validator = new ArgumentValidator();
        return validator.Validate(rawArgs.ToImmutable(), contract, "TestContext");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class NullableTestTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct) =>
        Task.FromResult(new MailSchema("MyToolOutput",
            ImmutableDictionary<string, MailValue>.Empty.Add("ok", new MailBool(true))));
}

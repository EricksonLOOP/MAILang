using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;
using Mail.Mcp;

namespace Mail.Tests;

public sealed class McpValueConverterTests
{
    // --- ToArguments ---

    [Fact]
    public void ToArguments_converts_string_field()
    {
        var schema = Schema("MyTool", ("text", new MailString("hello")));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Equal("hello", args["text"]);
    }

    [Fact]
    public void ToArguments_converts_bool_field()
    {
        var schema = Schema("T", ("flag", new MailBool(true)));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Equal(true, args["flag"]);
    }

    [Fact]
    public void ToArguments_converts_int_field()
    {
        var schema = Schema("T", ("count", new MailInt(42L)));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Equal(42L, args["count"]);
    }

    [Fact]
    public void ToArguments_converts_decimal_field()
    {
        var schema = Schema("T", ("amount", new MailDecimal(3.14m)));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Equal(3.14m, args["amount"]);
    }

    [Fact]
    public void ToArguments_converts_enum_field()
    {
        var schema = Schema("T", ("status", new MailEnum("Status", "Active")));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Equal("Active", args["status"]);
    }

    [Fact]
    public void ToArguments_converts_null_field()
    {
        var schema = Schema("T", ("name", new MailNull()));
        var args = McpValueConverter.ToArguments(schema);

        Assert.Null(args["name"]);
    }

    [Fact]
    public void ToArguments_converts_list_of_strings()
    {
        var list = new MailList(ImmutableList.Create<MailValue>(
            new MailString("a"), new MailString("b")));
        var schema = Schema("T", ("tags", list));
        var args = McpValueConverter.ToArguments(schema);

        var arr = Assert.IsType<object?[]>(args["tags"]);
        Assert.Equal(["a", "b"], arr);
    }

    [Fact]
    public void ToArguments_converts_nested_schema()
    {
        var nested = new MailSchema("Inner", ImmutableDictionary<string, MailValue>.Empty
            .Add("x", new MailString("v")));
        var schema = Schema("T", ("obj", nested));
        var args = McpValueConverter.ToArguments(schema);

        var dict = Assert.IsType<Dictionary<string, object?>>(args["obj"]);
        Assert.Equal("v", dict["x"]);
    }

    // --- FromJsonElement ---

    [Fact]
    public void FromJsonElement_extracts_required_string_field()
    {
        var json = Json("{\"text\":\"hello\"}");
        var contracts = new[]
        {
            new FieldContract("text", MailTypeKind.String, Required: true)
        };

        var result = McpValueConverter.FromJsonElement(json, contracts, "mytool");

        var value = Assert.IsType<MailString>(result.Fields["text"]);
        Assert.Equal("hello", value.Value);
    }

    [Fact]
    public void FromJsonElement_extracts_bool_field()
    {
        var json = Json("{\"flag\":true}");
        var contracts = new[] { new FieldContract("flag", MailTypeKind.Bool, Required: true) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        Assert.Equal(new MailBool(true), result.Fields["flag"]);
    }

    [Fact]
    public void FromJsonElement_extracts_int_field()
    {
        var json = Json("{\"count\":7}");
        var contracts = new[] { new FieldContract("count", MailTypeKind.Int, Required: true) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        Assert.Equal(new MailInt(7L), result.Fields["count"]);
    }

    [Fact]
    public void FromJsonElement_extracts_decimal_field()
    {
        var json = Json("{\"price\":9.99}");
        var contracts = new[] { new FieldContract("price", MailTypeKind.Decimal, Required: true) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        Assert.Equal(new MailDecimal(9.99m), result.Fields["price"]);
    }

    [Fact]
    public void FromJsonElement_extracts_enum_field()
    {
        var json = Json("{\"status\":\"Active\"}");
        var contracts = new[] { new FieldContract("status", MailTypeKind.Enum, Required: true,
            EnumTypeName: "Status") };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        var value = Assert.IsType<MailEnum>(result.Fields["status"]);
        Assert.Equal("Status", value.TypeName);
        Assert.Equal("Active", value.Symbol);
    }

    [Fact]
    public void FromJsonElement_extracts_string_list()
    {
        var json = Json("{\"tags\":[\"a\",\"b\",\"c\"]}");
        var contracts = new[] { new FieldContract("tags", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.String) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        var list = Assert.IsType<MailList>(result.Fields["tags"]);
        Assert.Equal(3, list.Elements.Count);
        Assert.Equal("a", Assert.IsType<MailString>(list.Elements[0]).Value);
    }

    [Fact]
    public void FromJsonElement_extracts_int_list()
    {
        var json = Json("{\"ids\":[1,2,3]}");
        var contracts = new[] { new FieldContract("ids", MailTypeKind.List, Required: true,
            ElementKind: MailTypeKind.Int) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        var list = Assert.IsType<MailList>(result.Fields["ids"]);
        Assert.Equal(new MailInt(2L), list.Elements[1]);
    }

    [Fact]
    public void FromJsonElement_null_for_nullable_field_returns_MailNull()
    {
        var json = Json("{\"name\":null}");
        var contracts = new[] { new FieldContract("name", MailTypeKind.String, Required: false,
            Nullable: true) };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        Assert.IsType<MailNull>(result.Fields["name"]);
    }

    [Fact]
    public void FromJsonElement_optional_absent_field_not_in_output()
    {
        var json = Json("{\"text\":\"hi\"}");
        var contracts = new[]
        {
            new FieldContract("text", MailTypeKind.String, Required: true),
            new FieldContract("extra", MailTypeKind.String, Required: false)
        };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        Assert.False(result.Fields.ContainsKey("extra"));
    }

    [Fact]
    public void FromJsonElement_missing_required_field_throws_McpToolException()
    {
        var json = Json("{\"other\":\"x\"}");
        var contracts = new[] { new FieldContract("required_field", MailTypeKind.String, Required: true) };

        var ex = Assert.Throws<McpToolException>(
            () => McpValueConverter.FromJsonElement(json, contracts, "mytool"));
        Assert.Contains("required_field", ex.Message);
        Assert.Contains("mytool", ex.Message);
    }

    [Fact]
    public void FromJsonElement_non_object_root_throws_McpToolException()
    {
        var json = Json("\"just a string\"");
        var contracts = Array.Empty<FieldContract>();

        Assert.Throws<McpToolException>(
            () => McpValueConverter.FromJsonElement(json, contracts, "t"));
    }

    [Fact]
    public void FromJsonElement_wrong_field_type_throws_McpToolException()
    {
        var json = Json("{\"count\":\"not-a-number\"}");
        var contracts = new[] { new FieldContract("count", MailTypeKind.Int, Required: true) };

        Assert.Throws<McpToolException>(
            () => McpValueConverter.FromJsonElement(json, contracts, "t"));
    }

    [Fact]
    public void FromJsonElement_null_on_non_nullable_throws_McpToolException()
    {
        var json = Json("{\"name\":null}");
        var contracts = new[] { new FieldContract("name", MailTypeKind.String, Required: true) };

        Assert.Throws<McpToolException>(
            () => McpValueConverter.FromJsonElement(json, contracts, "t"));
    }

    // --- FromTextJson ---

    [Fact]
    public void FromTextJson_parses_valid_json_text()
    {
        var contracts = new[] { new FieldContract("echo", MailTypeKind.String, Required: true) };

        var result = McpValueConverter.FromTextJson("{\"echo\":\"pong\"}", contracts, "t");

        Assert.Equal("pong", Assert.IsType<MailString>(result.Fields["echo"]).Value);
    }

    [Fact]
    public void FromTextJson_invalid_json_throws_McpToolException()
    {
        var contracts = Array.Empty<FieldContract>();

        var ex = Assert.Throws<McpToolException>(
            () => McpValueConverter.FromTextJson("not json", contracts, "t"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    // --- Schema field (heuristic) ---

    [Fact]
    public void FromJsonElement_schema_field_uses_SchemaTypeName()
    {
        var json = Json("{\"user\":{\"name\":\"Alice\",\"age\":30}}");
        var contracts = new[] { new FieldContract("user", MailTypeKind.Schema, Required: true,
            SchemaTypeName: "UserSchema") };

        var result = McpValueConverter.FromJsonElement(json, contracts, "t");

        var nested = Assert.IsType<MailSchema>(result.Fields["user"]);
        Assert.Equal("UserSchema", nested.TypeName);
        Assert.Equal("Alice", Assert.IsType<MailString>(nested.Fields["name"]).Value);
        Assert.Equal(new MailInt(30L), nested.Fields["age"]);
    }

    // --- Helpers ---

    private static MailSchema Schema(string typeName, params (string name, MailValue value)[] fields) =>
        new(typeName, fields.ToImmutableDictionary(f => f.name, f => f.value));

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}

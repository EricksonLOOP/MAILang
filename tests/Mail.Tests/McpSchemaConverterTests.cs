using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;
using Mail.Mcp;

namespace Mail.Tests;

public sealed class McpSchemaConverterTests
{
    // --- Helpers ---

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ObjectSchema(string propertiesJson, string[]? required = null)
    {
        var req = required is { Length: > 0 }
            ? $", \"required\": [{string.Join(",", required.Select(r => $"\"{r}\""))}]"
            : string.Empty;
        return Schema($"{{\"type\":\"object\",\"properties\":{{{propertiesJson}}}{req}}}");
    }

    // --- Primitive types ---

    [Fact]
    public void String_field_maps_to_String_kind()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"text\":{\"type\":\"string\"}", required: ["text"]));

        Assert.Single(contracts);
        Assert.Equal("text", contracts[0].Name);
        Assert.Equal(MailTypeKind.String, contracts[0].Kind);
        Assert.True(contracts[0].Required);
        Assert.False(contracts[0].Nullable);
    }

    [Fact]
    public void Boolean_field_maps_to_Bool_kind()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"flag\":{\"type\":\"boolean\"}", required: ["flag"]));

        Assert.Equal(MailTypeKind.Bool, contracts[0].Kind);
    }

    [Fact]
    public void Integer_field_maps_to_Int_kind()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"count\":{\"type\":\"integer\"}", required: ["count"]));

        Assert.Equal(MailTypeKind.Int, contracts[0].Kind);
    }

    [Fact]
    public void Number_field_maps_to_Decimal_kind()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"amount\":{\"type\":\"number\"}", required: ["amount"]));

        Assert.Equal(MailTypeKind.Decimal, contracts[0].Kind);
    }

    // --- Required vs optional ---

    [Fact]
    public void Field_absent_from_required_array_is_not_required()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"text\":{\"type\":\"string\"}"));

        Assert.False(contracts[0].Required);
    }

    // --- Nullable ---

    [Fact]
    public void Type_array_with_null_marks_field_nullable()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"name\":{\"type\":[\"string\",\"null\"]}", required: ["name"]));

        Assert.Equal(MailTypeKind.String, contracts[0].Kind);
        Assert.True(contracts[0].Nullable);
        Assert.True(contracts[0].Required);
    }

    [Fact]
    public void Null_first_in_type_array_also_works()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"name\":{\"type\":[\"null\",\"integer\"]}"));

        Assert.Equal(MailTypeKind.Int, contracts[0].Kind);
        Assert.True(contracts[0].Nullable);
    }

    // --- Enum ---

    [Fact]
    public void Enum_field_maps_to_Enum_kind_with_symbols()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"status\":{\"enum\":[\"active\",\"inactive\"]}", required: ["status"]));

        Assert.Equal(MailTypeKind.Enum, contracts[0].Kind);
        Assert.Equal(["active", "inactive"], contracts[0].EnumSymbols!.Value.ToArray());
    }

    // --- Arrays ---

    [Fact]
    public void String_array_maps_to_List_with_String_element()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", required: ["tags"]));

        Assert.Equal(MailTypeKind.List, contracts[0].Kind);
        Assert.Equal(MailTypeKind.String, contracts[0].ElementKind);
    }

    [Fact]
    public void Integer_array_maps_to_List_with_Int_element()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}"));

        Assert.Equal(MailTypeKind.List, contracts[0].Kind);
        Assert.Equal(MailTypeKind.Int, contracts[0].ElementKind);
    }

    [Fact]
    public void Object_array_maps_to_List_with_Schema_element()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}"));

        Assert.Equal(MailTypeKind.List, contracts[0].Kind);
        Assert.Equal(MailTypeKind.Schema, contracts[0].ElementKind);
        Assert.NotNull(contracts[0].ElementTypeName);
    }

    [Fact]
    public void Enum_array_maps_to_List_with_Enum_element()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"roles\":{\"type\":\"array\",\"items\":{\"enum\":[\"admin\",\"user\"]}}"));

        Assert.Equal(MailTypeKind.List, contracts[0].Kind);
        Assert.Equal(MailTypeKind.Enum, contracts[0].ElementKind);
        Assert.Equal(["admin", "user"], contracts[0].ElementEnumSymbols!.Value.ToArray());
    }

    // --- Object field ---

    [Fact]
    public void Object_field_maps_to_Schema_kind()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(
            ObjectSchema("\"user\":{\"type\":\"object\"}", required: ["user"]));

        Assert.Equal(MailTypeKind.Schema, contracts[0].Kind);
        Assert.NotNull(contracts[0].SchemaTypeName);
    }

    // --- Empty properties ---

    [Fact]
    public void Missing_properties_returns_empty_array()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(Schema("{\"type\":\"object\"}"));
        Assert.Empty(contracts);
    }

    // --- Multiple fields ---

    [Fact]
    public void Multiple_fields_all_converted()
    {
        var contracts = McpSchemaConverter.FromJsonSchema(ObjectSchema(
            "\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"integer\"},\"c\":{\"type\":\"boolean\"}",
            required: ["a", "b"]));

        Assert.Equal(3, contracts.Length);
        Assert.True(contracts.First(c => c.Name == "a").Required);
        Assert.True(contracts.First(c => c.Name == "b").Required);
        Assert.False(contracts.First(c => c.Name == "c").Required);
    }

    // --- Rejection of unsupported constructs ---

    [Theory]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    [InlineData("allOf")]
    [InlineData("$ref")]
    public void Unsupported_construct_throws_McpBindingException(string keyword)
    {
        var value = keyword == "$ref" ? "\"#/defs/Foo\"" : "[]";
        var schema = ObjectSchema($"\"x\":{{\"{keyword}\":{value}}}");

        var ex = Assert.Throws<McpBindingException>(() => McpSchemaConverter.FromJsonSchema(schema));
        Assert.Contains(keyword, ex.Message);
    }

    [Fact]
    public void Nested_array_List_of_List_throws_McpBindingException()
    {
        var schema = ObjectSchema("\"nested\":{\"type\":\"array\",\"items\":{\"type\":\"array\"}}");

        var ex = Assert.Throws<McpBindingException>(() => McpSchemaConverter.FromJsonSchema(schema));
        Assert.Contains("List<List<T>>", ex.Message);
    }

    [Fact]
    public void Type_array_with_two_non_null_types_throws_McpBindingException()
    {
        var schema = ObjectSchema("\"x\":{\"type\":[\"string\",\"integer\"]}");

        Assert.Throws<McpBindingException>(() => McpSchemaConverter.FromJsonSchema(schema));
    }

    [Fact]
    public void Field_with_no_type_and_no_enum_throws_McpBindingException()
    {
        var schema = ObjectSchema("\"x\":{\"description\":\"only description\"}");

        Assert.Throws<McpBindingException>(() => McpSchemaConverter.FromJsonSchema(schema));
    }

    [Fact]
    public void Non_string_enum_value_throws_McpBindingException()
    {
        var schema = ObjectSchema("\"x\":{\"enum\":[1,2,3]}");

        Assert.Throws<McpBindingException>(() => McpSchemaConverter.FromJsonSchema(schema));
    }
}

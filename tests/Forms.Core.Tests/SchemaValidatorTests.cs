using System.Text.Json;
using Forms.Core.Schema;
using static Forms.Core.Tests.TestData;

namespace Forms.Core.Tests;

public sealed class SchemaValidatorTests
{
    private static SchemaError SingleError(FormSchema schema) => Assert.Single(SchemaValidator.Validate(schema));

    [Fact]
    public void Sample_schema_is_valid() => Assert.Empty(SchemaValidator.Validate(LeaveRequest));

    [Fact]
    public void Schema_needs_at_least_one_field() =>
        Assert.Equal("fields", SingleError(new FormSchema { Fields = [] }).Path);

    [Theory]
    [InlineData("1abc")]
    [InlineData("Name")]
    [InlineData("first-name")]
    [InlineData("")]
    public void Field_key_must_be_a_simple_identifier(string key) =>
        Assert.Equal("fields[0].key", SingleError(FormOf(Field(key, FieldType.Text) with { Label = "Label" })).Path);

    [Fact]
    public void Field_keys_must_be_unique()
    {
        var error = SingleError(FormOf(Field("name", FieldType.Text), Field("name", FieldType.Number)));
        Assert.Equal("fields[1].key", error.Path);
    }

    [Fact]
    public void Select_needs_unique_options()
    {
        Assert.Equal("fields[0].options", SingleError(FormOf(Field("pick", FieldType.Select))).Path);

        var duplicate = SingleError(FormOf(Field("pick", FieldType.Select) with { Options = Options("a", "a") }));
        Assert.Equal("fields[0].options[1].value", duplicate.Path);
    }

    [Fact]
    public void Options_are_only_for_select_fields() =>
        Assert.Equal("fields[0].options", SingleError(FormOf(Field("name", FieldType.Text) with { Options = Options("a") })).Path);

    [Theory]
    [InlineData(FieldType.Number, "minLength")]
    [InlineData(FieldType.Text, "min")]
    [InlineData(FieldType.Date, "maxItems")]
    [InlineData(FieldType.Integer, "pattern")]
    public void Constraint_must_fit_the_field_type(FieldType type, string constraint)
    {
        var constraints = constraint switch
        {
            "minLength" => new FieldConstraints { MinLength = 1 },
            "min" => new FieldConstraints { Min = 1 },
            "maxItems" => new FieldConstraints { MaxItems = 1 },
            _ => new FieldConstraints { Pattern = "x" },
        };

        var error = SingleError(FormOf(Field("f", type) with { Constraints = constraints }));
        Assert.Equal($"fields[0].constraints.{constraint}", error.Path);
    }

    [Fact]
    public void Minimum_cannot_exceed_maximum() =>
        Assert.Equal("fields[0].constraints",
            SingleError(FormOf(Field("n", FieldType.Number) with { Constraints = new() { Min = 10, Max = 1 } })).Path);

    [Theory]
    [InlineData("(a)\\1")]           // backreference
    [InlineData("(?=a)a")]           // lookahead
    [InlineData("[unclosed")]        // syntax error
    public void Pattern_must_work_without_backtracking(string pattern)
    {
        var error = SingleError(FormOf(Field("t", FieldType.Text) with { Constraints = new() { Pattern = pattern } }));
        Assert.Equal("fields[0].constraints.pattern", error.Path);
    }

    [Fact]
    public void Condition_must_reference_an_existing_other_field()
    {
        var unknown = SingleError(FormOf(Field("a", FieldType.Text) with { VisibleWhen = Condition.When("missing", ConditionOperator.IsEmpty) }));
        Assert.Equal("fields[0].visibleWhen.field", unknown.Path);

        var self = SingleError(FormOf(Field("a", FieldType.Text) with { VisibleWhen = Condition.When("a", ConditionOperator.IsEmpty) }));
        Assert.Equal("fields[0].visibleWhen.field", self.Path);
    }

    [Fact]
    public void Ordering_operator_needs_a_number_or_date_field()
    {
        var error = SingleError(FormOf(
            Field("name", FieldType.Text),
            Field("b", FieldType.Text) with { VisibleWhen = Condition.When("name", ConditionOperator.GreaterThan, "m") }));
        Assert.Equal("fields[1].visibleWhen.operator", error.Path);
    }

    [Fact]
    public void Condition_value_must_match_the_field_type_and_options()
    {
        var wrongType = SingleError(FormOf(
            Field("age", FieldType.Integer),
            Field("b", FieldType.Text) with { VisibleWhen = Condition.When("age", ConditionOperator.GreaterThan, "eighteen") }));
        Assert.Equal("fields[1].visibleWhen.value", wrongType.Path);

        var notAnOption = SingleError(FormOf(
            Field("kind", FieldType.Select) with { Options = Options("a", "b") },
            Field("b", FieldType.Text) with { VisibleWhen = Condition.When("kind", ConditionOperator.Equals, "c") }));
        Assert.Equal("fields[1].visibleWhen.value", notAnOption.Path);
    }

    [Fact]
    public void Condition_shape_must_be_a_leaf_or_one_composite()
    {
        var both = new Condition { Field = "a", Operator = ConditionOperator.IsEmpty, All = [Condition.When("a", ConditionOperator.IsEmpty)] };
        var error = SingleError(FormOf(Field("a", FieldType.Text), Field("b", FieldType.Text) with { VisibleWhen = both }));
        Assert.Equal("fields[1].visibleWhen", error.Path);
    }

    [Fact]
    public void Visibility_cycles_are_rejected()
    {
        var error = SingleError(FormOf(
            Field("a", FieldType.Text) with { VisibleWhen = Condition.When("c", ConditionOperator.IsNotEmpty) },
            Field("b", FieldType.Text) with { VisibleWhen = Condition.When("a", ConditionOperator.IsNotEmpty) },
            Field("c", FieldType.Text) with { VisibleWhen = Condition.When("b", ConditionOperator.IsNotEmpty) }));

        Assert.Equal("fields", error.Path);
        Assert.Contains("a -> c -> b -> a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deeply_nested_conditions_are_rejected()
    {
        var condition = Condition.When("a", ConditionOperator.IsEmpty);
        for (var i = 0; i < SchemaValidator.MaxConditionDepth; i++)
        {
            condition = new Condition { All = [condition] };
        }

        var errors = SchemaValidator.Validate(FormOf(Field("a", FieldType.Text), Field("b", FieldType.Text) with { VisibleWhen = condition }));
        Assert.Contains(errors, e => e.Message.Contains("nested", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_must_compare_compatible_fields()
    {
        var schema = new FormSchema
        {
            Fields = [Field("start", FieldType.Date), Field("count", FieldType.Integer)],
            Rules = [new CrossFieldRule { Id = "r", Left = "start", Operator = ConditionOperator.LessThan, Right = "count", Message = "m" }],
        };

        Assert.Equal("rules[0]", SingleError(schema).Path);
    }

    [Fact]
    public void Rule_ids_must_be_unique()
    {
        var rule = new CrossFieldRule { Id = "r", Left = "a", Operator = ConditionOperator.LessThan, Right = "b", Message = "m" };
        var schema = new FormSchema { Fields = [Field("a", FieldType.Number), Field("b", FieldType.Number)], Rules = [rule, rule] };

        Assert.Equal("rules[1].id", SingleError(schema).Path);
    }

    [Fact]
    public void Field_key_with_a_trailing_newline_is_rejected() =>
        Assert.Equal("fields[0].key", SingleError(FormOf(Field("name\n", FieldType.Text) with { Label = "Label" })).Path);

    [Fact]
    public void Null_list_elements_are_reported_not_thrown()
    {
        Assert.Equal("fields", SingleError(new FormSchema { Fields = [null!] }).Path);
        Assert.Equal("fields[0].options[0]", SingleError(FormOf(Field("pick", FieldType.Select) with { Options = [null!] })).Path);
        Assert.Equal("rules[0]", SingleError(new FormSchema { Fields = [Field("a", FieldType.Text)], Rules = [null!] }).Path);

        var condition = new Condition { All = [null!] };
        Assert.Equal("fields[1].visibleWhen.all[0]",
            SingleError(FormOf(Field("a", FieldType.Text), Field("b", FieldType.Text) with { VisibleWhen = condition })).Path);
    }

    [Fact]
    public void Unknown_operator_values_are_rejected()
    {
        var condition = Condition.When("a", (ConditionOperator)42, "x");
        Assert.Equal("fields[1].visibleWhen",
            SingleError(FormOf(Field("a", FieldType.Text), Field("b", FieldType.Text) with { VisibleWhen = condition })).Path);

        // The JSON converter does not accept integers in place of names either.
        Assert.Throws<JsonException>(() => FormSchemaJson.Deserialize(
            """{ "fields": [ { "key": "a", "label": "A", "type": "text" }, { "key": "b", "label": "B", "type": "text", "visibleWhen": { "field": "a", "operator": 42, "value": "x" } } ] }"""));
    }

    [Fact]
    public void Size_limits_are_enforced()
    {
        var options = Enumerable.Range(0, SchemaValidator.MaxOptions + 1).Select(i => new FieldOption { Value = $"o{i}", Label = $"Option {i}" }).ToList();
        Assert.Equal("fields[0].options", SingleError(FormOf(Field("pick", FieldType.Select) with { Options = options })).Path);

        var tooMany = Enumerable.Range(0, SchemaValidator.MaxConditionValues + 1).Select(i => $"v{i}").ToArray();
        var condition = Condition.When("a", ConditionOperator.In, tooMany);
        Assert.Equal("fields[1].visibleWhen.value",
            SingleError(FormOf(Field("a", FieldType.Text), Field("b", FieldType.Text) with { VisibleWhen = condition })).Path);
    }
}
using System.Text.Json.Nodes;
using Forms.Core.Schema;
using Forms.Core.Validation;
using static Forms.Core.Tests.TestData;

namespace Forms.Core.Tests;

public sealed class FormValidatorTests
{
    private static ValidationResult ValidateLeave(Action<JsonObject>? change = null)
    {
        var data = JsonNode.Parse(ValidLeave)!.AsObject();
        change?.Invoke(data);
        return FormValidator.Validate(LeaveRequest, Json(data.ToJsonString()));
    }

    private static void AssertSingleError(ValidationResult result, string field, string code)
    {
        var error = Assert.Single(result.Errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Valid_submission_passes_and_is_normalised()
    {
        var result = ValidateLeave();

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("Jane Doe", result.Data["employeeName"]!.GetValue<string>());
        Assert.Equal(5m, result.Data["workingDays"]!.GetValue<decimal>());
        Assert.Equal(
            ["employeeName", "email", "leaveType", "startDate", "endDate", "workingDays", "notify"],
            result.Data.Select(p => p.Key));
    }

    [Fact]
    public void Data_must_be_an_object()
    {
        var result = FormValidator.Validate(LeaveRequest, Json("[1, 2]"));
        AssertSingleError(result, "$", ErrorCodes.NotAnObject);
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        var result = ValidateLeave(d => d["salary"] = 1000);
        AssertSingleError(result, "salary", ErrorCodes.UnknownField);
    }

    [Theory]
    [InlineData("employeeName")]
    [InlineData("email")]
    [InlineData("startDate")]
    public void Missing_required_field_is_reported(string field)
    {
        var result = ValidateLeave(d => d.Remove(field));
        AssertSingleError(result, field, ErrorCodes.Required);
    }

    [Fact]
    public void Whitespace_only_text_counts_as_missing()
    {
        var result = ValidateLeave(d => d["employeeName"] = "   ");
        AssertSingleError(result, "employeeName", ErrorCodes.Required);
    }

    [Fact]
    public void Explicit_null_counts_as_missing()
    {
        var result = ValidateLeave(d => d["email"] = null);
        AssertSingleError(result, "email", ErrorCodes.Required);
    }

    [Theory]
    [InlineData("workingDays", "\"5\"")]
    [InlineData("workingDays", "5.5")]
    [InlineData("startDate", "\"02.11.2026\"")]
    [InlineData("startDate", "\"2026-02-30\"")]
    [InlineData("employeeName", "42")]
    [InlineData("notify", "\"manager\"")]
    [InlineData("notify", "[1]")]
    public void Wrong_json_type_is_a_type_error(string field, string rawValue)
    {
        var result = ValidateLeave(d => d[field] = JsonNode.Parse(rawValue));
        AssertSingleError(result, field, ErrorCodes.Type);
    }

    [Fact]
    public void All_errors_are_reported_not_only_the_first()
    {
        var result = FormValidator.Validate(LeaveRequest, Json("{}"));

        Assert.Equal(
            ["employeeName", "email", "leaveType", "startDate", "endDate", "workingDays"],
            result.Errors.Select(e => e.Field));
        Assert.All(result.Errors, e => Assert.Equal(ErrorCodes.Required, e.Code));
    }

    [Theory]
    [InlineData("J", ErrorCodes.MinLength)]
    [InlineData("not-an-email", ErrorCodes.Email)]
    public void Text_constraints_are_checked(string value, string code)
    {
        var field = code == ErrorCodes.Email ? "email" : "employeeName";
        var result = ValidateLeave(d => d[field] = value);
        AssertSingleError(result, field, code);
    }

    [Theory]
    [InlineData(0, ErrorCodes.Min)]
    [InlineData(31, ErrorCodes.Max)]
    public void Number_bounds_are_checked(int days, string code)
    {
        var result = ValidateLeave(d => d["workingDays"] = days);
        AssertSingleError(result, "workingDays", code);
    }

    [Fact]
    public void Select_value_must_be_an_option()
    {
        var result = ValidateLeave(d => d["leaveType"] = "sabbatical");
        AssertSingleError(result, "leaveType", ErrorCodes.Option);
    }

    [Fact]
    public void Multi_select_checks_options_duplicates_and_count()
    {
        Assert.Equal(ErrorCodes.Option, Assert.Single(ValidateLeave(d => d["notify"] = new JsonArray("ceo")).Errors).Code);
        Assert.Equal(ErrorCodes.DuplicateItem, Assert.Single(ValidateLeave(d => d["notify"] = new JsonArray("hr", "hr")).Errors).Code);
        Assert.Equal(ErrorCodes.MaxItems, Assert.Single(ValidateLeave(d => d["notify"] = new JsonArray("hr", "team", "manager")).Errors).Code);
    }

    [Fact]
    public void Hidden_field_is_not_required_and_is_dropped_from_the_output()
    {
        // otherReason is required but only visible when leaveType is "other".
        var result = ValidateLeave(d => d["otherReason"] = "ignored because the field is hidden");

        Assert.True(result.IsValid);
        Assert.False(result.Data.ContainsKey("otherReason"));
    }

    [Fact]
    public void Visible_field_becomes_required()
    {
        var result = ValidateLeave(d => d["leaveType"] = "other");
        AssertSingleError(result, "otherReason", ErrorCodes.Required);
    }

    [Fact]
    public void Required_when_condition_applies_only_when_it_holds()
    {
        // Sick leave of 3 days or fewer needs no certificate.
        var shortSick = ValidateLeave(d => { d["leaveType"] = "sick"; d["workingDays"] = 3; });
        Assert.True(shortSick.IsValid);

        var longSick = ValidateLeave(d => { d["leaveType"] = "sick"; d["workingDays"] = 4; });
        AssertSingleError(longSick, "certificateNumber", ErrorCodes.Required);
    }

    [Theory]
    [InlineData("AB-123456", true)]
    [InlineData("ab-123456", false)]
    [InlineData("AB-123456-extra", false)]
    public void Pattern_must_match_the_whole_value(string value, bool valid)
    {
        var result = ValidateLeave(d => { d["leaveType"] = "sick"; d["certificateNumber"] = value; });

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            AssertSingleError(result, "certificateNumber", ErrorCodes.Pattern);
        }
    }

    [Fact]
    public void Cross_field_rule_is_checked()
    {
        var result = ValidateLeave(d => d["endDate"] = "2026-11-01");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("endDate", ErrorCodes.Rule), (error.Field, error.Code));
        Assert.Equal("The last day cannot be before the first day.", error.Message);
    }

    [Fact]
    public void Cross_field_rule_is_skipped_when_a_side_is_already_invalid()
    {
        var result = ValidateLeave(d => d["endDate"] = "yesterday");
        AssertSingleError(result, "endDate", ErrorCodes.Type);
    }

    [Fact]
    public void Hidden_field_reads_as_empty_in_other_conditions()
    {
        // b is visible only when a is "yes"; c is visible only when b is not empty.
        var schema = FormOf(
            Field("a", FieldType.Text),
            Field("b", FieldType.Text) with { VisibleWhen = Condition.When("a", ConditionOperator.Equals, "yes") },
            Field("c", FieldType.Text, required: true) with { VisibleWhen = Condition.When("b", ConditionOperator.IsNotEmpty) });

        // b has a value but is hidden, so c is hidden too and its requirement does not apply.
        var result = FormValidator.Validate(schema, Json("""{ "a": "no", "b": "something" }"""));

        Assert.True(result.IsValid);
        Assert.Equal(["a"], result.Data.Select(p => p.Key));
    }

    [Fact]
    public void Composite_conditions_combine_with_all_and_any()
    {
        var schema = FormOf(
            Field("country", FieldType.Text),
            Field("age", FieldType.Integer),
            Field("consent", FieldType.Boolean) with
            {
                RequiredWhen = new Condition
                {
                    All =
                    [
                        Condition.When("country", ConditionOperator.In, new[] { "DE", "AT" }),
                        new Condition { Any = [Condition.When("age", ConditionOperator.LessThan, 16), Condition.When("age", ConditionOperator.IsEmpty)] },
                    ],
                },
            });

        Assert.False(FormValidator.Validate(schema, Json("""{ "country": "DE", "age": 15 }""")).IsValid);
        Assert.False(FormValidator.Validate(schema, Json("""{ "country": "AT" }""")).IsValid);
        Assert.True(FormValidator.Validate(schema, Json("""{ "country": "DE", "age": 16 }""")).IsValid);
        Assert.True(FormValidator.Validate(schema, Json("""{ "country": "FR", "age": 10 }""")).IsValid);
        Assert.True(FormValidator.Validate(schema, Json("""{ "country": "DE", "age": 15, "consent": true }""")).IsValid);
    }

    [Fact]
    public void Equals_on_a_multi_select_means_contains()
    {
        var schema = FormOf(
            Field("channels", FieldType.MultiSelect) with { Options = Options("email", "phone") },
            Field("phone", FieldType.Text) with { RequiredWhen = Condition.When("channels", ConditionOperator.Equals, "phone") });

        Assert.False(FormValidator.Validate(schema, Json("""{ "channels": ["email", "phone"] }""")).IsValid);
        Assert.True(FormValidator.Validate(schema, Json("""{ "channels": ["email"] }""")).IsValid);
    }

    [Fact]
    public void Date_bounds_are_checked()
    {
        var schema = FormOf(Field("day", FieldType.Date) with
        {
            Constraints = new() { MinDate = new DateOnly(2026, 1, 1), MaxDate = new DateOnly(2026, 12, 31) },
        });

        Assert.Equal(ErrorCodes.MinDate, Assert.Single(FormValidator.Validate(schema, Json("""{ "day": "2025-12-31" }""")).Errors).Code);
        Assert.Equal(ErrorCodes.MaxDate, Assert.Single(FormValidator.Validate(schema, Json("""{ "day": "2027-01-01" }""")).Errors).Code);
        Assert.True(FormValidator.Validate(schema, Json("""{ "day": "2026-06-15" }""")).IsValid);
    }

    [Fact]
    public void Number_field_accepts_decimals_and_keeps_precision()
    {
        var schema = FormOf(Field("amount", FieldType.Number));

        var result = FormValidator.Validate(schema, Json("""{ "amount": 1234.5678 }"""));

        Assert.True(result.IsValid);
        Assert.Equal(1234.5678m, result.Data["amount"]!.GetValue<decimal>());
    }

    [Fact]
    public void Pathological_input_for_a_pattern_finishes_quickly()
    {
        // (a+)+$ takes exponential time on a backtracking engine for input like "aaaa...!".
        var schema = FormOf(Field("code", FieldType.Text) with { Constraints = new() { Pattern = "(a+)+" } });
        var input = new string('a', 50_000) + "!";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = FormValidator.Validate(schema, Json($$"""{ "code": "{{input}}" }"""));

        Assert.Equal(ErrorCodes.Pattern, Assert.Single(result.Errors).Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void Readme_example_reports_these_errors_in_schema_order()
    {
        var result = FormValidator.Validate(LeaveRequest, Json("""{ "employeeName": "J", "leaveType": "sick", "workingDays": 5 }"""));

        Assert.Equal(
            [
                ("employeeName", ErrorCodes.MinLength, "Full name must be at least 2 characters long."),
                ("email", ErrorCodes.Required, "Work email is required."),
                ("startDate", ErrorCodes.Required, "First day is required."),
                ("endDate", ErrorCodes.Required, "Last day is required."),
                ("certificateNumber", ErrorCodes.Required, "Medical certificate number is required."),
            ],
            result.Errors.Select(e => (e.Field, e.Code, e.Message)));
    }

    [Fact]
    public void Unsafe_json_is_reported_instead_of_crashing()
    {
        var result = ValidateLeave(d => d["employeeName"] = "Jane\u0000Doe");
        AssertSingleError(result, "$", ErrorCodes.InvalidJson);
    }

    [Fact]
    public void Integer_with_trailing_zeros_is_stored_normalised()
    {
        var result = ValidateLeave(d => d["workingDays"] = JsonNode.Parse("5.000"));

        Assert.True(result.IsValid);
        Assert.Equal("5", result.Data["workingDays"]!.ToJsonString());
    }

    [Theory]
    [InlineData("AB-123456\n")]
    [InlineData("AB-123456\r\n")]
    public void Compiled_pattern_does_not_match_before_a_trailing_line_break(string value)
    {
        // "$" would match before a final newline; the anchor must be "\z". (Submitted values are trimmed
        // before validation, so this matters for Patterns used outside FormValidator too.)
        var regex = Patterns.TryCompile("[A-Z]{2}-[0-9]{6}", out _)!;

        Assert.DoesNotMatch(regex, value);
        Assert.Matches(regex, "AB-123456");
    }

    [Fact]
    public void Pattern_cannot_escape_its_anchors()
    {
        // On its own "x)|(?:.*" is not a valid expression, so it must be rejected rather than wrapped into
        // "^(?:x)|(?:.*)\z", which would match anything.
        Assert.Null(Patterns.TryCompile("x)|(?:.*", out var error));
        Assert.NotNull(error);
    }
}
using Forms.Core.Compatibility;
using Forms.Core.Schema;
using static Forms.Core.Tests.TestData;

namespace Forms.Core.Tests;

public sealed class SchemaCompatibilityTests
{
    private static FormSchema WithField(FieldDefinition field) =>
        LeaveRequest with { Fields = [.. LeaveRequest.Fields.Select(f => f.Key == field.Key ? field : f)] };

    private static FieldDefinition Get(string key) => LeaveRequest.FindField(key)!;

    private static SchemaChange SingleChange(FormSchema to) => Assert.Single(SchemaCompatibility.Compare(LeaveRequest, to).Changes);

    [Fact]
    public void Identical_schemas_have_no_changes() =>
        Assert.Empty(SchemaCompatibility.Compare(LeaveRequest, FormSchemaJson.Deserialize(FormSchemaJson.Serialize(LeaveRequest))).Changes);

    [Fact]
    public void Adding_an_optional_field_is_not_breaking()
    {
        var change = SingleChange(LeaveRequest with { Fields = [.. LeaveRequest.Fields, Field("comment", FieldType.LongText)] });
        Assert.Equal((ChangeKind.FieldAdded, false), (change.Kind, change.Breaking));
    }

    [Fact]
    public void Adding_a_required_field_is_breaking()
    {
        var change = SingleChange(LeaveRequest with { Fields = [.. LeaveRequest.Fields, Field("costCentre", FieldType.Text, required: true)] });
        Assert.Equal((ChangeKind.FieldAdded, true), (change.Kind, change.Breaking));
    }

    [Fact]
    public void Removing_a_field_is_breaking()
    {
        var change = SingleChange(LeaveRequest with { Fields = [.. LeaveRequest.Fields.Where(f => f.Key != "notify")] });
        Assert.Equal((ChangeKind.FieldRemoved, "notify", true), (change.Kind, change.Target, change.Breaking));
    }

    [Theory]
    [InlineData(FieldType.Number, false)]
    [InlineData(FieldType.Text, true)]
    public void Widening_a_type_is_not_breaking_but_other_type_changes_are(FieldType newType, bool breaking)
    {
        var field = Get("workingDays") with { Type = newType, Constraints = newType == FieldType.Number ? Get("workingDays").Constraints : null };
        var changes = SchemaCompatibility.Compare(LeaveRequest, WithField(field)).Changes;

        var typeChange = Assert.Single(changes, c => c.Kind == ChangeKind.TypeChanged);
        Assert.Equal(breaking, typeChange.Breaking);
    }

    [Fact]
    public void Making_a_field_required_is_breaking_and_optional_is_not()
    {
        Assert.True(SingleChange(WithField(Get("notify") with { Required = true })).Breaking);
        Assert.False(SingleChange(WithField(Get("email") with { Required = false })).Breaking);
    }

    [Theory]
    [InlineData(2, 100, false)]   // unchanged
    [InlineData(3, 100, true)]    // minLength raised
    [InlineData(1, 100, false)]   // minLength lowered
    [InlineData(2, 50, true)]     // maxLength lowered
    [InlineData(2, 200, false)]   // maxLength raised
    public void Length_constraint_changes_are_classified(int minLength, int maxLength, bool breaking)
    {
        var field = Get("employeeName") with { Constraints = new() { MinLength = minLength, MaxLength = maxLength } };
        var report = SchemaCompatibility.Compare(LeaveRequest, WithField(field));

        Assert.Equal(breaking, report.HasBreakingChanges);
        Assert.Equal(minLength != 2 || maxLength != 100, report.Changes.Count > 0);
    }

    [Fact]
    public void Removing_a_constraint_loosens_it()
    {
        var change = SingleChange(WithField(Get("certificateNumber") with { Constraints = null }));
        Assert.Equal((ChangeKind.ConstraintLoosened, false), (change.Kind, change.Breaking));
    }

    [Fact]
    public void Removing_an_option_is_breaking_and_adding_one_is_not()
    {
        var options = Get("leaveType").Options!;

        var removed = SingleChange(WithField(Get("leaveType") with { Options = [.. options.Where(o => o.Value != "unpaid")] }));
        Assert.Equal((ChangeKind.OptionRemoved, true), (removed.Kind, removed.Breaking));

        var added = SingleChange(WithField(Get("leaveType") with { Options = [.. options, new FieldOption { Value = "parental", Label = "Parental leave" }] }));
        Assert.Equal((ChangeKind.OptionAdded, false), (added.Kind, added.Breaking));
    }

    [Fact]
    public void Label_changes_are_not_breaking()
    {
        var change = SingleChange(WithField(Get("email") with { Label = "Business email" }));
        Assert.Equal((ChangeKind.LabelChanged, false), (change.Kind, change.Breaking));
    }

    [Fact]
    public void New_rule_is_breaking_and_removed_rule_is_not()
    {
        var removed = SingleChange(LeaveRequest with { Rules = [] });
        Assert.Equal((ChangeKind.RuleRemoved, false), (removed.Kind, removed.Breaking));

        var rule = new CrossFieldRule { Id = "max-days", Left = "workingDays", Operator = ConditionOperator.LessThanOrEqual, Right = "workingDays", Message = "m" };
        var added = SingleChange(LeaveRequest with { Rules = [.. LeaveRequest.Rules, rule] });
        Assert.Equal((ChangeKind.RuleAdded, true), (added.Kind, added.Breaking));
    }

    [Fact]
    public void Changing_a_required_condition_is_breaking_and_removing_it_is_not()
    {
        var stricter = WithField(Get("certificateNumber") with { RequiredWhen = Condition.When("workingDays", ConditionOperator.GreaterThan, 1) });
        Assert.True(SingleChange(stricter).Breaking);

        var removed = WithField(Get("certificateNumber") with { RequiredWhen = null });
        Assert.False(SingleChange(removed).Breaking);
    }

    [Fact]
    public void Changing_visibility_is_breaking()
    {
        // otherReason is required. Showing it always would reject every submission that was valid before.
        var change = SingleChange(WithField(Get("otherReason") with { VisibleWhen = null }));
        Assert.Equal((ChangeKind.VisibilityChanged, true), (change.Kind, change.Breaking));
    }
}
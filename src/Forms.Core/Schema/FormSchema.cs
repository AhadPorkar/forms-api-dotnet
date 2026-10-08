using System.Text.Json;

namespace Forms.Core.Schema;

/// <summary>
/// The definition of a form: its fields and the rules that span more than one field.
/// A schema is a value object. Once a form version is published, its schema never changes.
/// </summary>
public sealed record FormSchema
{
    public required IReadOnlyList<FieldDefinition> Fields { get; init; }

    public IReadOnlyList<CrossFieldRule> Rules { get; init; } = [];

    public FieldDefinition? FindField(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

public sealed record FieldDefinition
{
    /// <summary>Stable machine name of the field, used as the property name in submitted data.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required FieldType Type { get; init; }

    public string? HelpText { get; init; }

    public bool Required { get; init; }

    public FieldConstraints? Constraints { get; init; }

    /// <summary>Allowed values for <see cref="FieldType.Select"/> and <see cref="FieldType.MultiSelect"/>.</summary>
    public IReadOnlyList<FieldOption>? Options { get; init; }

    /// <summary>The field is shown (and validated) only when this condition holds.</summary>
    public Condition? VisibleWhen { get; init; }

    /// <summary>The field becomes required when this condition holds, in addition to <see cref="Required"/>.</summary>
    public Condition? RequiredWhen { get; init; }
}

public enum FieldType
{
    Text,
    LongText,
    Email,
    Number,
    Integer,
    Boolean,
    Date,
    Select,
    MultiSelect,
}

public sealed record FieldOption
{
    public required string Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>
/// Constraints that apply on top of the field type. Which members are allowed depends on the type;
/// <see cref="SchemaValidator"/> rejects combinations that make no sense, such as a pattern on a number.
/// </summary>
public sealed record FieldConstraints
{
    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>Regular expression the whole value must match. Evaluated with a non-backtracking engine.</summary>
    public string? Pattern { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public DateOnly? MinDate { get; init; }

    public DateOnly? MaxDate { get; init; }

    public int? MinItems { get; init; }

    public int? MaxItems { get; init; }
}

/// <summary>
/// A condition over the values of other fields. Either a leaf (<see cref="Field"/> + <see cref="Operator"/>)
/// or a composite (<see cref="All"/> or <see cref="Any"/>).
/// </summary>
public sealed record Condition
{
    public string? Field { get; init; }

    public ConditionOperator? Operator { get; init; }

    /// <summary>Comparison value. For <see cref="ConditionOperator.In"/> it is an array.</summary>
    public JsonElement? Value { get; init; }

    public IReadOnlyList<Condition>? All { get; init; }

    public IReadOnlyList<Condition>? Any { get; init; }

    public static Condition When(string field, ConditionOperator op, object? value = null) => new()
    {
        Field = field,
        Operator = op,
        Value = value is null ? null : JsonSerializer.SerializeToElement(value),
    };

    /// <summary>Every field key this condition reads, including nested conditions.</summary>
    public IEnumerable<string> ReferencedFields()
    {
        if (Field is not null)
        {
            yield return Field;
        }

        // OfType skips null entries that JSON input may contain; SchemaValidator reports them separately.
        foreach (var child in (All ?? []).Concat(Any ?? []).OfType<Condition>())
        {
            foreach (var key in child.ReferencedFields())
            {
                yield return key;
            }
        }
    }
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    In,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>
/// A comparison between two fields, for example "endDate must be on or after startDate".
/// The rule is checked only when both fields are visible and have a value.
/// </summary>
public sealed record CrossFieldRule
{
    public required string Id { get; init; }

    public required string Left { get; init; }

    public required ConditionOperator Operator { get; init; }

    public required string Right { get; init; }

    public required string Message { get; init; }
}
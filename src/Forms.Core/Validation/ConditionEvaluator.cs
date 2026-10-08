using System.Text.Json;
using Forms.Core.Schema;

namespace Forms.Core.Validation;

/// <summary>
/// Evaluates <see cref="Condition"/> trees against field values.
/// A field that is hidden or has no value counts as empty.
/// </summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(Condition condition, Func<string, (FieldType Type, FieldValue Value)?> lookup)
    {
        if (condition.All is { Count: > 0 } all)
        {
            return all.All(c => Evaluate(c, lookup));
        }

        if (condition.Any is { Count: > 0 } any)
        {
            return any.Any(c => Evaluate(c, lookup));
        }

        if (condition.Field is null || condition.Operator is null || lookup(condition.Field) is not var (type, value))
        {
            return false;
        }

        return condition.Operator.Value switch
        {
            ConditionOperator.IsEmpty => value.IsEmpty,
            ConditionOperator.IsNotEmpty => !value.IsEmpty,
            ConditionOperator.Equals => !value.IsEmpty && Matches(type, value, condition.Value),
            ConditionOperator.NotEquals => value.IsEmpty || !Matches(type, value, condition.Value),
            ConditionOperator.In => !value.IsEmpty && condition.Value is { ValueKind: JsonValueKind.Array } list
                && list.EnumerateArray().Any(candidate => Matches(type, value, candidate)),
            _ => !value.IsEmpty && Compare(type, value, condition.Value) is { } order && IsSatisfied(condition.Operator.Value, order),
        };
    }

    /// <summary>Compares two values of the same field type. Returns null when they are not comparable.</summary>
    public static int? Compare(FieldValue left, FieldValue right)
    {
        if (left.Number is { } ln && right.Number is { } rn)
        {
            return ln.CompareTo(rn);
        }

        if (left.Date is { } ld && right.Date is { } rd)
        {
            return ld.CompareTo(rd);
        }

        if (left.Text is { } lt && right.Text is { } rt)
        {
            return string.CompareOrdinal(lt, rt);
        }

        if (left.Boolean is { } lb && right.Boolean is { } rb)
        {
            return lb.CompareTo(rb);
        }

        return null;
    }

    public static bool IsSatisfied(ConditionOperator op, int order) => op switch
    {
        ConditionOperator.Equals => order == 0,
        ConditionOperator.NotEquals => order != 0,
        ConditionOperator.GreaterThan => order > 0,
        ConditionOperator.GreaterThanOrEqual => order >= 0,
        ConditionOperator.LessThan => order < 0,
        ConditionOperator.LessThanOrEqual => order <= 0,
        _ => false,
    };

    private static bool Matches(FieldType type, FieldValue value, JsonElement? expected)
    {
        if (expected is not { } element)
        {
            return false;
        }

        // For a multi-select field, "equals x" means "x is one of the selected options".
        if (value.Items is { } items)
        {
            return element.ValueKind == JsonValueKind.String && JsonInput.TryGetString(element, out var expectedItem)
                && items.Contains(expectedItem, StringComparer.Ordinal);
        }

        return Compare(type, value, element) == 0;
    }

    private static int? Compare(FieldType type, FieldValue value, JsonElement? expected)
    {
        if (expected is not { } element)
        {
            return null;
        }

        var readAs = type switch
        {
            FieldType.MultiSelect => FieldType.Text,
            FieldType.Integer => FieldType.Number,
            _ => type,
        };

        return FieldValue.TryRead(readAs, element, out var other) && !other.IsEmpty
            ? Compare(value, other)
            : null;
    }
}
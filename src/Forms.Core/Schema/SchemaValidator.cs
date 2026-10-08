using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forms.Core.Validation;

namespace Forms.Core.Schema;

/// <param name="Path">Where the problem is, for example <c>fields[2].constraints.pattern</c>.</param>
public sealed record SchemaError(string Path, string Message);

/// <summary>
/// Checks that a schema is internally consistent before it is stored, so that
/// <see cref="FormValidator"/> never has to deal with a broken schema at submission time.
/// </summary>
public static partial class SchemaValidator
{
    public const int MaxFields = 200;
    public const int MaxOptions = 500;
    public const int MaxRules = 100;
    public const int MaxConditionValues = 100;
    public const int MaxConditionDepth = 5;

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]{0,63}\z")]
    private static partial Regex FieldKey();

    public static IReadOnlyList<SchemaError> Validate(FormSchema schema)
    {
        var errors = new List<SchemaError>();
        var fields = schema.Fields ?? [];

        if (fields.Count == 0)
        {
            errors.Add(new("fields", "A form needs at least one field."));
        }

        if (fields.Count > MaxFields)
        {
            errors.Add(new("fields", $"A form can have at most {MaxFields} fields."));
        }

        if (fields.Any(f => f is null))
        {
            errors.Add(new("fields", "Fields cannot be null."));
            return errors;
        }

        var byKey = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var path = $"fields[{i}]";

            if (string.IsNullOrEmpty(field.Key) || !FieldKey().IsMatch(field.Key))
            {
                errors.Add(new($"{path}.key", "Key must start with a lowercase letter and contain only letters, digits and '_' (at most 64 characters)."));
            }
            else if (!byKey.TryAdd(field.Key, field))
            {
                errors.Add(new($"{path}.key", $"Key '{field.Key}' is used by more than one field."));
            }

            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 200)
            {
                errors.Add(new($"{path}.label", "Label is required and can be at most 200 characters."));
            }

            if (!Enum.IsDefined(field.Type))
            {
                errors.Add(new($"{path}.type", "Unknown field type."));
            }

            CheckOptions(field, path, errors);
            CheckConstraints(field, path, errors);
        }

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.VisibleWhen is { } visibleWhen)
            {
                CheckCondition(visibleWhen, field.Key, byKey, $"fields[{i}].visibleWhen", 1, errors);
            }

            if (field.RequiredWhen is { } requiredWhen)
            {
                CheckCondition(requiredWhen, field.Key, byKey, $"fields[{i}].requiredWhen", 1, errors);
            }
        }

        CheckVisibilityCycles(fields, byKey, errors);
        CheckRules(schema.Rules ?? [], byKey, errors);
        return errors;
    }

    private static void CheckOptions(FieldDefinition field, string path, List<SchemaError> errors)
    {
        var isChoice = field.Type is FieldType.Select or FieldType.MultiSelect;
        if (!isChoice)
        {
            if (field.Options is { Count: > 0 })
            {
                errors.Add(new($"{path}.options", "Only select and multiSelect fields can have options."));
            }

            return;
        }

        if (field.Options is not { Count: > 0 } options)
        {
            errors.Add(new($"{path}.options", "A select field needs at least one option."));
            return;
        }

        if (options.Count > MaxOptions)
        {
            errors.Add(new($"{path}.options", $"A field can have at most {MaxOptions} options."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < options.Count; j++)
        {
            if (options[j] is null)
            {
                errors.Add(new($"{path}.options[{j}]", "Options cannot be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(options[j].Value) || options[j].Value != options[j].Value.Trim())
            {
                errors.Add(new($"{path}.options[{j}].value", "Option value must be non-empty and have no surrounding spaces."));
            }
            else if (!seen.Add(options[j].Value))
            {
                errors.Add(new($"{path}.options[{j}].value", $"Option value '{options[j].Value}' is used more than once."));
            }

            if (string.IsNullOrWhiteSpace(options[j].Label))
            {
                errors.Add(new($"{path}.options[{j}].label", "Option label is required."));
            }
        }
    }

    private static void CheckConstraints(FieldDefinition field, string path, List<SchemaError> errors)
    {
        if (field.Constraints is not { } c)
        {
            return;
        }

        path += ".constraints";
        var isText = field.Type is FieldType.Text or FieldType.LongText or FieldType.Email;
        var isNumeric = field.Type is FieldType.Number or FieldType.Integer;

        void NotFor(bool present, string name, bool allowed, string types)
        {
            if (present && !allowed)
            {
                errors.Add(new($"{path}.{name}", $"{name} applies only to {types} fields."));
            }
        }

        NotFor(c.MinLength is not null, "minLength", isText, "text");
        NotFor(c.MaxLength is not null, "maxLength", isText, "text");
        NotFor(c.Pattern is not null, "pattern", isText, "text");
        NotFor(c.Min is not null, "min", isNumeric, "number");
        NotFor(c.Max is not null, "max", isNumeric, "number");
        NotFor(c.MinDate is not null, "minDate", field.Type == FieldType.Date, "date");
        NotFor(c.MaxDate is not null, "maxDate", field.Type == FieldType.Date, "date");
        NotFor(c.MinItems is not null, "minItems", field.Type == FieldType.MultiSelect, "multiSelect");
        NotFor(c.MaxItems is not null, "maxItems", field.Type == FieldType.MultiSelect, "multiSelect");

        if (c.MinLength < 0 || c.MaxLength < 0 || c.MinItems < 0 || c.MaxItems < 0)
        {
            errors.Add(new(path, "Lengths and item counts cannot be negative."));
        }

        if (c.MinLength > c.MaxLength || c.Min > c.Max || c.MinDate > c.MaxDate || c.MinItems > c.MaxItems)
        {
            errors.Add(new(path, "A minimum is greater than its maximum."));
        }

        if (c.Pattern is { } pattern && Patterns.TryCompile(pattern, out var patternError) is null)
        {
            errors.Add(new($"{path}.pattern", patternError!));
        }
    }

    private static void CheckCondition(
        Condition condition, string owner, Dictionary<string, FieldDefinition> byKey, string path, int depth, List<SchemaError> errors)
    {
        if (depth > MaxConditionDepth)
        {
            errors.Add(new(path, $"Conditions can be nested at most {MaxConditionDepth} levels deep."));
            return;
        }

        var hasAll = condition.All is { Count: > 0 };
        var hasAny = condition.Any is { Count: > 0 };
        var isLeaf = condition.Field is not null || condition.Operator is not null;

        if ((hasAll ? 1 : 0) + (hasAny ? 1 : 0) + (isLeaf ? 1 : 0) != 1)
        {
            errors.Add(new(path, "A condition must have exactly one of: field and operator, 'all', or 'any'."));
            return;
        }

        if (hasAll || hasAny)
        {
            var children = (hasAll ? condition.All : condition.Any)!;
            var name = hasAll ? "all" : "any";
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is null)
                {
                    errors.Add(new($"{path}.{name}[{i}]", "Conditions cannot be null."));
                    continue;
                }

                CheckCondition(children[i], owner, byKey, $"{path}.{name}[{i}]", depth + 1, errors);
            }

            return;
        }

        if (condition.Field is null || condition.Operator is not { } op || !Enum.IsDefined(op))
        {
            errors.Add(new(path, "A condition needs both 'field' and a known 'operator'."));
            return;
        }

        if (condition.Field == owner)
        {
            errors.Add(new($"{path}.field", "A field's condition cannot refer to the field itself."));
            return;
        }

        if (!byKey.TryGetValue(condition.Field, out var target))
        {
            errors.Add(new($"{path}.field", $"Unknown field '{condition.Field}'."));
            return;
        }

        var value = condition.Value;
        switch (op)
        {
            case ConditionOperator.IsEmpty or ConditionOperator.IsNotEmpty:
                if (value is { ValueKind: not JsonValueKind.Null })
                {
                    errors.Add(new($"{path}.value", $"'{op}' takes no value."));
                }

                break;

            case ConditionOperator.In:
                if (value is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() is 0 or > MaxConditionValues)
                {
                    errors.Add(new($"{path}.value", $"'in' needs an array of 1 to {MaxConditionValues} values."));
                }
                else
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        CheckComparisonValue(target, item, $"{path}.value", errors);
                    }
                }

                break;

            case ConditionOperator.Equals or ConditionOperator.NotEquals:
                if (value is not { } equalsValue)
                {
                    errors.Add(new($"{path}.value", $"'{op}' needs a value."));
                }
                else
                {
                    CheckComparisonValue(target, equalsValue, $"{path}.value", errors);
                }

                break;

            default:
                if (target.Type is not (FieldType.Number or FieldType.Integer or FieldType.Date))
                {
                    errors.Add(new($"{path}.operator", $"'{op}' works only on number and date fields."));
                }
                else if (value is not { } orderValue)
                {
                    errors.Add(new($"{path}.value", $"'{op}' needs a value."));
                }
                else
                {
                    CheckComparisonValue(target, orderValue, $"{path}.value", errors);
                }

                break;
        }
    }

    private static void CheckComparisonValue(FieldDefinition target, JsonElement value, string path, List<SchemaError> errors)
    {
        var readAs = target.Type == FieldType.MultiSelect ? FieldType.Select : target.Type;
        if (!FieldValue.TryRead(readAs, value, out var read) || read.IsEmpty)
        {
            errors.Add(new(path, $"Value does not match the type of field '{target.Key}' ({target.Type})."));
            return;
        }

        if (read.Text is { } text && target.Options is { } options && !options.Any(o => o.Value == text))
        {
            errors.Add(new(path, $"'{text}' is not an option of field '{target.Key}'."));
        }
    }

    private static void CheckVisibilityCycles(
        IReadOnlyList<FieldDefinition> fields, Dictionary<string, FieldDefinition> byKey, List<SchemaError> errors)
    {
        // Depth-first search over "is shown depending on" edges. 0 = unvisited, 1 = on the stack, 2 = done.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        bool Visit(string key)
        {
            state[key] = 1;
            stack.Add(key);
            var dependencies = byKey.TryGetValue(key, out var field) && field.VisibleWhen is { } when
                ? when.ReferencedFields().Where(k => k != key && byKey.ContainsKey(k)).Distinct()
                : [];

            foreach (var dependency in dependencies)
            {
                var s = state.GetValueOrDefault(dependency);
                if (s == 1)
                {
                    var cycle = stack.Skip(stack.IndexOf(dependency)).Append(dependency);
                    errors.Add(new("fields", $"visibleWhen conditions form a cycle: {string.Join(" -> ", cycle)}."));
                    return false;
                }

                if (s == 0 && !Visit(dependency))
                {
                    return false;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[key] = 2;
            return true;
        }

        foreach (var field in fields.Where(f => f.Key is not null && byKey.ContainsKey(f.Key)))
        {
            if (state.GetValueOrDefault(field.Key) == 0 && !Visit(field.Key))
            {
                return;
            }
        }
    }

    private static void CheckRules(
        IReadOnlyList<CrossFieldRule> rules, Dictionary<string, FieldDefinition> byKey, List<SchemaError> errors)
    {
        if (rules.Count > MaxRules)
        {
            errors.Add(new("rules", $"A form can have at most {MaxRules} rules."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = string.Create(CultureInfo.InvariantCulture, $"rules[{i}]");
            if (rule is null)
            {
                errors.Add(new(path, "Rules cannot be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                errors.Add(new($"{path}.id", "Rule id is required and must be unique."));
            }

            if (string.IsNullOrWhiteSpace(rule.Message))
            {
                errors.Add(new($"{path}.message", "Rule message is required."));
            }

            if (!byKey.TryGetValue(rule.Left ?? "", out var left))
            {
                errors.Add(new($"{path}.left", $"Unknown field '{rule.Left}'."));
            }

            if (!byKey.TryGetValue(rule.Right ?? "", out var right))
            {
                errors.Add(new($"{path}.right", $"Unknown field '{rule.Right}'."));
            }

            if (left is null || right is null)
            {
                continue;
            }

            if (left.Key == right.Key)
            {
                errors.Add(new(path, "A rule must compare two different fields."));
                continue;
            }

            var ordering = rule.Operator is ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual
                or ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual;
            var equality = rule.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals;
            var family = Family(left.Type);

            if (!Enum.IsDefined(rule.Operator) || (!ordering && !equality))
            {
                errors.Add(new($"{path}.operator", "Rules support only comparison operators."));
            }
            else if (family != Family(right.Type) || family == "list" || (ordering && family is not ("number" or "date")))
            {
                errors.Add(new(path, $"Fields '{left.Key}' ({left.Type}) and '{right.Key}' ({right.Type}) cannot be compared with '{rule.Operator}'."));
            }
        }
    }

    private static string Family(FieldType type) => type switch
    {
        FieldType.Number or FieldType.Integer => "number",
        FieldType.Date => "date",
        FieldType.Boolean => "boolean",
        FieldType.MultiSelect => "list",
        _ => "text",
    };
}
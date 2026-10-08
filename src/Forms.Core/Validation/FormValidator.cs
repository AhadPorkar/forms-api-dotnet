using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forms.Core.Schema;

namespace Forms.Core.Validation;

/// <summary>
/// Validates submitted data against a <see cref="FormSchema"/>.
/// The schema is assumed to have passed <see cref="SchemaValidator"/>; an invalid schema gives undefined results.
/// </summary>
public static class FormValidator
{
    public static ValidationResult Validate(FormSchema schema, JsonElement data)
    {
        var errors = new List<ValidationError>();
        if (data.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("$", ErrorCodes.NotAnObject, "The submission must be a JSON object."));
            return new(errors, []);
        }

        if (JsonInput.FindProblem(data) is { } problem)
        {
            errors.Add(new("$", ErrorCodes.InvalidJson, problem));
            return new(errors, []);
        }

        foreach (var property in data.EnumerateObject())
        {
            if (schema.FindField(property.Name) is null)
            {
                errors.Add(new(property.Name, ErrorCodes.UnknownField, $"'{property.Name}' is not a field of this form."));
            }
        }

        // 1. Read every value as its field type.
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            var raw = data.TryGetProperty(field.Key, out var element) ? element : default;
            if (FieldValue.TryRead(field.Type, raw, out var value))
            {
                values[field.Key] = value;
            }
            else
            {
                values[field.Key] = FieldValue.Empty;
                unreadable.Add(field.Key);
            }
        }

        // 2. Decide which fields are visible. A hidden field reads as empty in every condition.
        var visibility = new VisibilityResolver(schema, values);

        (FieldType, FieldValue)? Lookup(string key) =>
            schema.FindField(key) is { } f ? (f.Type, visibility.IsVisible(key) ? values[key] : FieldValue.Empty) : null;

        // 3. Check each visible field.
        var output = new JsonObject();
        foreach (var field in schema.Fields)
        {
            if (!visibility.IsVisible(field.Key))
            {
                continue;
            }

            if (unreadable.Contains(field.Key))
            {
                errors.Add(new(field.Key, ErrorCodes.Type, $"{field.Label} must be {Describe(field.Type)}."));
                continue;
            }

            var value = values[field.Key];
            if (value.IsEmpty)
            {
                var required = field.Required || (field.RequiredWhen is { } when && ConditionEvaluator.Evaluate(when, Lookup));
                if (required)
                {
                    errors.Add(new(field.Key, ErrorCodes.Required, $"{field.Label} is required."));
                }

                continue;
            }

            var before = errors.Count;
            CheckValue(field, value, errors);
            if (errors.Count == before)
            {
                output[field.Key] = value.ToJsonNode();
            }
        }

        // 4. Rules that compare two fields. Skipped when either side is hidden, empty or already invalid.
        var invalidFields = errors.Select(e => e.Field).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in schema.Rules)
        {
            if (Lookup(rule.Left) is not (_, var left) || Lookup(rule.Right) is not (_, var right)
                || left.IsEmpty || right.IsEmpty || invalidFields.Contains(rule.Left) || invalidFields.Contains(rule.Right))
            {
                continue;
            }

            if (ConditionEvaluator.Compare(left, right) is not { } order || !ConditionEvaluator.IsSatisfied(rule.Operator, order))
            {
                errors.Add(new(rule.Left, ErrorCodes.Rule, rule.Message));
            }
        }

        return new(errors, output);
    }

    private static void CheckValue(FieldDefinition field, FieldValue value, List<ValidationError> errors)
    {
        var c = field.Constraints;
        var key = field.Key;
        var label = field.Label;

        if (value.Text is { } text)
        {
            if (field.Type == FieldType.Email && !Patterns.Email.IsMatch(text))
            {
                errors.Add(new(key, ErrorCodes.Email, $"{label} must be a valid email address."));
            }

            if (field.Type == FieldType.Select && !HasOption(field, text))
            {
                errors.Add(new(key, ErrorCodes.Option, $"'{text}' is not an option of {label}."));
            }

            if (c?.MinLength is { } minLength && text.Length < minLength)
            {
                errors.Add(new(key, ErrorCodes.MinLength, $"{label} must be at least {minLength} characters long."));
            }

            if (c?.MaxLength is { } maxLength && text.Length > maxLength)
            {
                errors.Add(new(key, ErrorCodes.MaxLength, $"{label} must be at most {maxLength} characters long."));
            }

            if (c?.Pattern is { } pattern && Patterns.TryCompile(pattern, out _) is { } regex && !regex.IsMatch(text))
            {
                errors.Add(new(key, ErrorCodes.Pattern, $"{label} has an invalid format."));
            }
        }

        if (value.Number is { } number)
        {
            if (c?.Min is { } min && number < min)
            {
                errors.Add(new(key, ErrorCodes.Min, $"{label} must be at least {Format(min)}."));
            }

            if (c?.Max is { } max && number > max)
            {
                errors.Add(new(key, ErrorCodes.Max, $"{label} must be at most {Format(max)}."));
            }
        }

        if (value.Date is { } date)
        {
            if (c?.MinDate is { } minDate && date < minDate)
            {
                errors.Add(new(key, ErrorCodes.MinDate, $"{label} must be on or after {minDate:yyyy-MM-dd}."));
            }

            if (c?.MaxDate is { } maxDate && date > maxDate)
            {
                errors.Add(new(key, ErrorCodes.MaxDate, $"{label} must be on or before {maxDate:yyyy-MM-dd}."));
            }
        }

        if (value.Items is { } items)
        {
            var options = field.Options?.Select(o => o.Value).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var item in items.Where(i => !options.Contains(i)))
            {
                errors.Add(new(key, ErrorCodes.Option, $"'{item}' is not an option of {label}."));
            }

            if (items.Distinct(StringComparer.Ordinal).Count() != items.Count)
            {
                errors.Add(new(key, ErrorCodes.DuplicateItem, $"{label} contains the same option more than once."));
            }

            if (c?.MinItems is { } minItems && items.Count < minItems)
            {
                errors.Add(new(key, ErrorCodes.MinItems, $"Select at least {minItems} options for {label}."));
            }

            if (c?.MaxItems is { } maxItems && items.Count > maxItems)
            {
                errors.Add(new(key, ErrorCodes.MaxItems, $"Select at most {maxItems} options for {label}."));
            }
        }
    }

    private static bool HasOption(FieldDefinition field, string value) =>
        field.Options?.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal)) == true;

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Describe(FieldType type) => type switch
    {
        FieldType.Number => "a number",
        FieldType.Integer => "a whole number",
        FieldType.Boolean => "true or false",
        FieldType.Date => "a date in the format YYYY-MM-DD",
        FieldType.MultiSelect => "a list of options",
        _ => "text",
    };

    /// <summary>Works out visibility lazily, following visibleWhen references. The schema guarantees there are no cycles.</summary>
    private sealed class VisibilityResolver(FormSchema schema, Dictionary<string, FieldValue> values)
    {
        private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);

        public bool IsVisible(string key)
        {
            if (_cache.TryGetValue(key, out var known))
            {
                return known;
            }

            var field = schema.FindField(key);
            var visible = field is not null
                && (field.VisibleWhen is null || ConditionEvaluator.Evaluate(field.VisibleWhen, Lookup));
            _cache[key] = visible;
            return visible;
        }

        private (FieldType, FieldValue)? Lookup(string key) =>
            schema.FindField(key) is { } f ? (f.Type, IsVisible(key) ? values[key] : FieldValue.Empty) : null;
    }
}
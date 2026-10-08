using System.Text.Json;
using Forms.Core.Schema;

namespace Forms.Core.Compatibility;

public enum ChangeKind
{
    FieldAdded,
    FieldRemoved,
    TypeChanged,
    LabelChanged,
    BecameRequired,
    BecameOptional,
    RequiredConditionChanged,
    VisibilityChanged,
    ConstraintTightened,
    ConstraintLoosened,
    OptionAdded,
    OptionRemoved,
    RuleAdded,
    RuleRemoved,
    RuleChanged,
}

/// <param name="Target">The field key or rule id the change is about.</param>
/// <param name="Breaking">True when data that was valid for the old version can be invalid, or mean something else, in the new one.</param>
public sealed record SchemaChange(ChangeKind Kind, string Target, bool Breaking, string Description);

public sealed record CompatibilityReport(IReadOnlyList<SchemaChange> Changes)
{
    public bool HasBreakingChanges => Changes.Any(c => c.Breaking);
}

/// <summary>
/// Compares two schema versions and classifies every difference as breaking or non-breaking.
/// "Breaking" is defined from the point of view of data: a submission that passed the old version
/// might fail the new one, or a consumer reading submissions might find a field gone or changed in shape.
/// </summary>
public static class SchemaCompatibility
{
    public static CompatibilityReport Compare(FormSchema from, FormSchema to)
    {
        var changes = new List<SchemaChange>();
        var oldFields = from.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var newFields = to.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);

        foreach (var removed in from.Fields.Where(f => !newFields.ContainsKey(f.Key)))
        {
            changes.Add(new(ChangeKind.FieldRemoved, removed.Key, true, $"Field '{removed.Key}' was removed."));
        }

        foreach (var field in to.Fields)
        {
            if (!oldFields.TryGetValue(field.Key, out var old))
            {
                var required = field.Required || field.RequiredWhen is not null;
                changes.Add(new(ChangeKind.FieldAdded, field.Key, required,
                    required ? $"Required field '{field.Key}' was added." : $"Optional field '{field.Key}' was added."));
                continue;
            }

            CompareField(old, field, changes);
        }

        CompareRules(from.Rules, to.Rules, changes);
        return new(changes);
    }

    private static void CompareField(FieldDefinition old, FieldDefinition @new, List<SchemaChange> changes)
    {
        var key = @new.Key;

        if (old.Type != @new.Type)
        {
            var widening = (old.Type, @new.Type) is (FieldType.Integer, FieldType.Number) or (FieldType.Text, FieldType.LongText);
            changes.Add(new(ChangeKind.TypeChanged, key, !widening, $"Type of '{key}' changed from {old.Type} to {@new.Type}."));
        }

        if (old.Label != @new.Label || old.HelpText != @new.HelpText)
        {
            changes.Add(new(ChangeKind.LabelChanged, key, false, $"Label or help text of '{key}' changed."));
        }

        if (!old.Required && @new.Required)
        {
            changes.Add(new(ChangeKind.BecameRequired, key, true, $"'{key}' is now required."));
        }
        else if (old.Required && !@new.Required)
        {
            changes.Add(new(ChangeKind.BecameOptional, key, false, $"'{key}' is now optional."));
        }

        if (!Same(old.RequiredWhen, @new.RequiredWhen))
        {
            // Removing the condition can only accept more data; adding or changing it may reject data.
            var breaking = @new.RequiredWhen is not null;
            changes.Add(new(ChangeKind.RequiredConditionChanged, key, breaking, $"The condition that makes '{key}' required changed."));
        }

        if (!Same(old.VisibleWhen, @new.VisibleWhen))
        {
            // Showing a field in more cases subjects more data to its checks; hiding it in more cases drops data
            // that used to be stored. Which of the two a changed condition does is not decidable in general.
            changes.Add(new(ChangeKind.VisibilityChanged, key, true, $"The condition that shows '{key}' changed."));
        }

        CompareConstraints(key, old.Constraints ?? new(), @new.Constraints ?? new(), changes);
        CompareOptions(key, old.Options ?? [], @new.Options ?? [], changes);
    }

    private static void CompareConstraints(string key, FieldConstraints old, FieldConstraints @new, List<SchemaChange> changes)
    {
        void Lower<T>(string name, T? before, T? after) where T : struct, IComparable<T>
        {
            // A lower bound tightens when it appears or rises.
            if (Nullable.Equals(before, after))
            {
                return;
            }

            var tightened = after is { } a && (before is not { } b || a.CompareTo(b) > 0);
            Add(name, tightened, before, after);
        }

        void Upper<T>(string name, T? before, T? after) where T : struct, IComparable<T>
        {
            // An upper bound tightens when it appears or falls.
            if (Nullable.Equals(before, after))
            {
                return;
            }

            var tightened = after is { } a && (before is not { } b || a.CompareTo(b) < 0);
            Add(name, tightened, before, after);
        }

        void Add(string name, bool tightened, object? before, object? after) => changes.Add(new(
            tightened ? ChangeKind.ConstraintTightened : ChangeKind.ConstraintLoosened,
            key,
            tightened,
            $"{name} of '{key}' changed from {before ?? "none"} to {after ?? "none"}."));

        Lower("minLength", old.MinLength, @new.MinLength);
        Upper("maxLength", old.MaxLength, @new.MaxLength);
        Lower("min", old.Min, @new.Min);
        Upper("max", old.Max, @new.Max);
        Lower("minDate", old.MinDate, @new.MinDate);
        Upper("maxDate", old.MaxDate, @new.MaxDate);
        Lower("minItems", old.MinItems, @new.MinItems);
        Upper("maxItems", old.MaxItems, @new.MaxItems);

        if (old.Pattern != @new.Pattern)
        {
            // Whether one regular expression accepts a superset of another is not decidable in general,
            // so any new or changed pattern counts as tightening.
            Add("pattern", @new.Pattern is not null, old.Pattern, @new.Pattern);
        }
    }

    private static void CompareOptions(string key, IReadOnlyList<FieldOption> old, IReadOnlyList<FieldOption> @new, List<SchemaChange> changes)
    {
        var newValues = @new.ToDictionary(o => o.Value, StringComparer.Ordinal);
        var oldValues = old.ToDictionary(o => o.Value, StringComparer.Ordinal);

        foreach (var option in old.Where(o => !newValues.ContainsKey(o.Value)))
        {
            changes.Add(new(ChangeKind.OptionRemoved, key, true, $"Option '{option.Value}' was removed from '{key}'."));
        }

        foreach (var option in @new.Where(o => !oldValues.ContainsKey(o.Value)))
        {
            changes.Add(new(ChangeKind.OptionAdded, key, false, $"Option '{option.Value}' was added to '{key}'."));
        }

        foreach (var option in @new.Where(o => oldValues.TryGetValue(o.Value, out var before) && before.Label != o.Label))
        {
            changes.Add(new(ChangeKind.LabelChanged, key, false, $"Label of option '{option.Value}' in '{key}' changed."));
        }
    }

    private static void CompareRules(IReadOnlyList<CrossFieldRule> old, IReadOnlyList<CrossFieldRule> @new, List<SchemaChange> changes)
    {
        var oldRules = old.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newRules = @new.ToDictionary(r => r.Id, StringComparer.Ordinal);

        foreach (var rule in old.Where(r => !newRules.ContainsKey(r.Id)))
        {
            changes.Add(new(ChangeKind.RuleRemoved, rule.Id, false, $"Rule '{rule.Id}' was removed."));
        }

        foreach (var rule in @new)
        {
            if (!oldRules.TryGetValue(rule.Id, out var before))
            {
                changes.Add(new(ChangeKind.RuleAdded, rule.Id, true, $"Rule '{rule.Id}' was added."));
            }
            else if ((before.Left, before.Operator, before.Right) != (rule.Left, rule.Operator, rule.Right))
            {
                changes.Add(new(ChangeKind.RuleChanged, rule.Id, true, $"Rule '{rule.Id}' now compares differently."));
            }
        }
    }

    private static bool Same(Condition? a, Condition? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null
            && JsonSerializer.Serialize(a, FormSchemaJson.Options) == JsonSerializer.Serialize(b, FormSchemaJson.Options));
}
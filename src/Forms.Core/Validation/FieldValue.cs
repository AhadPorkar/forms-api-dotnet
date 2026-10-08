using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forms.Core.Schema;

namespace Forms.Core.Validation;

/// <summary>
/// A submitted value after it has been read according to its field type.
/// Exactly one of the typed members is set, or none when the value is empty.
/// </summary>
public readonly record struct FieldValue
{
    public static readonly FieldValue Empty;

    private FieldValue(string? text, decimal? number, bool? boolean, DateOnly? date, IReadOnlyList<string>? items)
    {
        Text = text;
        Number = number;
        Boolean = boolean;
        Date = date;
        Items = items;
    }

    public string? Text { get; }

    public decimal? Number { get; }

    public bool? Boolean { get; }

    public DateOnly? Date { get; }

    public IReadOnlyList<string>? Items { get; }

    public bool IsEmpty => Text is null && Number is null && Boolean is null && Date is null && Items is null;

    public static FieldValue OfText(string value) => new(value, null, null, null, null);

    /// <summary>Trailing zeros are removed, so 5, 5.0 and 5.000 are stored the same way.</summary>
    public static FieldValue OfNumber(decimal value) => new(null, value / 1.0000000000000000000000000000m, null, null, null);

    public static FieldValue OfBoolean(bool value) => new(null, null, value, null, null);

    public static FieldValue OfDate(DateOnly value) => new(null, null, null, value, null);

    public static FieldValue OfItems(IReadOnlyList<string> value) => new(null, null, null, null, value);

    public JsonNode? ToJsonNode()
    {
        if (Text is not null)
        {
            return JsonValue.Create(Text);
        }

        if (Number is { } number)
        {
            return JsonValue.Create(number);
        }

        if (Boolean is { } boolean)
        {
            return JsonValue.Create(boolean);
        }

        if (Date is { } date)
        {
            return JsonValue.Create(date.ToString(DateFormat, CultureInfo.InvariantCulture));
        }

        return Items is null ? null : new JsonArray([.. Items.Select(i => (JsonNode?)JsonValue.Create(i))]);
    }

    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Reads a raw JSON value as the given field type. Strings are trimmed and an empty string counts as no value.
    /// Returns false when the JSON kind does not fit the type (a string for a number, a fraction for an integer, and so on).
    /// </summary>
    public static bool TryRead(FieldType type, JsonElement raw, out FieldValue value)
    {
        value = Empty;
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (type)
        {
            case FieldType.Text or FieldType.LongText or FieldType.Email or FieldType.Select:
                if (raw.ValueKind != JsonValueKind.String || !JsonInput.TryGetString(raw, out var rawText))
                {
                    return false;
                }

                var text = rawText.Trim();
                value = text.Length == 0 ? Empty : OfText(text);
                return true;

            case FieldType.Number:
                if (!JsonInput.TryGetExactDecimal(raw, out var number))
                {
                    return false;
                }

                value = OfNumber(number);
                return true;

            case FieldType.Integer:
                if (!JsonInput.TryGetExactDecimal(raw, out var integer) || integer != decimal.Truncate(integer))
                {
                    return false;
                }

                value = OfNumber(integer);
                return true;

            case FieldType.Boolean:
                if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                value = OfBoolean(raw.GetBoolean());
                return true;

            case FieldType.Date:
                if (raw.ValueKind != JsonValueKind.String || !JsonInput.TryGetString(raw, out var rawDate))
                {
                    return false;
                }

                var dateText = rawDate.Trim();
                if (dateText.Length == 0)
                {
                    return true;
                }

                if (!DateOnly.TryParseExact(dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return false;
                }

                value = OfDate(date);
                return true;

            case FieldType.MultiSelect:
                if (raw.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                var items = new List<string>();
                foreach (var item in raw.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !JsonInput.TryGetString(item, out var itemText))
                    {
                        return false;
                    }

                    items.Add(itemText.Trim());
                }

                value = items.Count == 0 ? Empty : OfItems(items);
                return true;

            default:
                return false;
        }
    }
}
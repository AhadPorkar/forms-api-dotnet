using System.Numerics;
using System.Text.Json;

namespace Forms.Core.Validation;

/// <summary>
/// Checks raw JSON input before anything else reads it. JSON allows values that .NET strings or PostgreSQL
/// cannot hold (lone UTF-16 surrogates, the NUL character, numbers such as <c>1e1000000</c>). Rejecting them
/// up front keeps such input from turning into a server error deeper down.
/// </summary>
public static class JsonInput
{
    /// <summary>Returns a description of the first problem, or null when the input is safe to process.</summary>
    public static string? FindProblem(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!TryGetName(property, out var name) || name.Contains('\0', StringComparison.Ordinal))
                    {
                        return "A property name is not valid Unicode text or contains the NUL character.";
                    }

                    if (FindProblem(property.Value) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindProblem(item) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;

            case JsonValueKind.String:
                return TryGetString(element, out var text) && !text.Contains('\0', StringComparison.Ordinal)
                    ? null
                    : "A string is not valid Unicode text or contains the NUL character.";

            case JsonValueKind.Number:
                return TryGetExactDecimal(element, out _)
                    ? null
                    : "A number is out of range or has more than 28 significant digits.";

            default:
                return null;
        }
    }

    public static bool TryGetString(JsonElement element, out string value)
    {
        try
        {
            value = element.GetString() ?? "";
            return true;
        }
        catch (InvalidOperationException)
        {
            value = "";
            return false;
        }
    }

    /// <summary>
    /// Reads a JSON number as a <see cref="decimal"/> only if the decimal holds exactly the same value.
    /// <see cref="JsonElement.TryGetDecimal"/> alone silently rounds, for example <c>1.00000000000000000000000000001</c> to <c>1</c>.
    /// </summary>
    public static bool TryGetExactDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out value))
        {
            return false;
        }

        var raw = element.GetRawText();
        var exponentAt = raw.AsSpan().IndexOfAny('e', 'E');
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        if (!int.TryParse(exponentAt < 0 ? "0" : raw[(exponentAt + 1)..], out var exponent))
        {
            return false;
        }

        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        exponent -= point < 0 ? 0 : mantissa.Length - point - 1;
        var m = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (m.IsZero)
        {
            return true;
        }

        // value is (n / 10^scale); raw is (m * 10^exponent). Compare as integers.
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0xFF;
        var n = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | (uint)bits[0];
        if (value < 0)
        {
            n = -n;
        }

        var shift = exponent + scale;
        if (Math.Abs(shift) > 60)
        {
            return false;
        }

        return shift >= 0 ? m * BigInteger.Pow(10, shift) == n : m == n * BigInteger.Pow(10, -shift);
    }

    private static bool TryGetName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = "";
            return false;
        }
    }
}
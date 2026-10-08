using System.Text.Json;
using Forms.Core.Validation;
using static Forms.Core.Tests.TestData;

namespace Forms.Core.Tests;

/// <summary>Input that JSON allows but .NET strings or PostgreSQL cannot hold must be rejected, never crash.</summary>
public sealed class JsonInputTests
{
    [Theory]
    [InlineData("""{ "name": "Jane\u0000Doe" }""")]      // NUL: PostgreSQL jsonb rejects it (22P05)
    [InlineData("""{ "name": "J\ud800" }""")]            // lone surrogate: GetString() throws
    [InlineData("""{ "\ud800": 1 }""")]                  // lone surrogate in a property name
    [InlineData("""{ "n": 1e1000000 }""")]               // out of range for decimal and for PostgreSQL numeric
    [InlineData("""{ "n": [ { "deep": "\u0000" } ] }""")]
    public void Unsafe_values_are_reported(string json)
    {
        Assert.NotNull(JsonInput.FindProblem(Json(json)));
    }

    [Theory]
    [InlineData("""{ "name": "Jane Doe", "n": 1.5, "ok": true, "list": ["a", null] }""")]
    [InlineData("""{ "emoji": "😀" }""")]       // a valid surrogate pair
    [InlineData("""{ "n": 79228162514264337593543950335 }""")] // decimal.MaxValue
    public void Safe_values_pass(string json)
    {
        Assert.Null(JsonInput.FindProblem(Json(json)));
    }

    [Theory]
    [InlineData("5", "5")]
    [InlineData("5.000", "5")]
    [InlineData("1e2", "100")]
    [InlineData("0.1", "0.1")]
    [InlineData("-12.50", "-12.5")]
    [InlineData("1234.5678", "1234.5678")]
    public void Numbers_are_read_exactly_and_normalised(string raw, string expected)
    {
        Assert.True(JsonInput.TryGetExactDecimal(Json(raw), out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.Equal(expected, FieldValue.OfNumber(value).Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1.00000000000000000000000000001")]   // would be rounded to 1
    [InlineData("1e-30")]
    [InlineData("1e30")]
    public void Numbers_that_decimal_cannot_hold_exactly_are_rejected(string raw)
    {
        Assert.False(JsonInput.TryGetExactDecimal(Json(raw), out _));
    }
}
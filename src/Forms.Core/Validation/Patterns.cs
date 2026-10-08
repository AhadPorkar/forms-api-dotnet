using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Forms.Core.Validation;

/// <summary>
/// Compiles schema patterns with <see cref="RegexOptions.NonBacktracking"/>, which runs in linear time
/// and so cannot be used for a ReDoS attack. Patterns that need backtracking features
/// (backreferences, lookarounds, atomic groups) are rejected when the schema is saved.
/// </summary>
public static class Patterns
{
    public const int MaxPatternLength = 500;
    private const int MaxCacheSize = 1_000;

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static readonly Regex Email = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+\z",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Returns null and an error message when the pattern is not allowed.</summary>
    public static Regex? TryCompile(string pattern, out string? error)
    {
        error = null;
        if (pattern.Length > MaxPatternLength)
        {
            error = $"Pattern is longer than {MaxPatternLength} characters.";
            return null;
        }

        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        try
        {
            // The pattern must compile on its own first. Otherwise an unbalanced pattern such as "x)|(?:.*"
            // could close the group added below and escape the anchors.
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

            // Anchored so that the pattern describes the whole value, as in HTML's pattern attribute.
            // \z rather than $, because $ also matches before a final line break.
            var regex = new Regex($"^(?:{pattern})\\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            if (Cache.Count >= MaxCacheSize)
            {
                Cache.Clear();
            }

            Cache[pattern] = regex;
            return regex;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Pattern is not a supported regular expression: {ex.Message}";
            return null;
        }
    }
}
using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Forms.Api.Infrastructure;

public enum IfMatchState
{
    Missing,
    Invalid,
    Present,
}

public static class ETags
{
    public static string For(uint rowVersion) => $"\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Reads a single strong ETag from If-Match. A wildcard, a weak tag, a list or anything else
    /// that is not one of our ETags counts as invalid.
    /// </summary>
    public static IfMatchState ReadIfMatch(HttpRequest request, out uint rowVersion)
    {
        rowVersion = 0;
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return IfMatchState.Missing;
        }

        return EntityTagHeaderValue.TryParse(header, out var tag)
            && !tag.IsWeak
            && uint.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion)
                ? IfMatchState.Present
                : IfMatchState.Invalid;
    }
}
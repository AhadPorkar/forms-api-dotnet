using System.Globalization;
using Forms.Api.Infrastructure;
using Forms.Api.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Forms.Api.Endpoints;

public static class VersionLookup
{
    public const string Latest = "latest";
    public const string Draft = "draft";

    /// <summary>Resolves a version selector: a version number, <c>latest</c> (published) or <c>draft</c>.</summary>
    public static async Task<(FormVersion? Version, ProblemHttpResult? Problem)> FindVersionAsync(
        this FormsDbContext db, string formKey, string? selector, CancellationToken ct)
    {
        selector = string.IsNullOrEmpty(selector) ? Latest : selector;
        // Read-only: callers use the version's id and schema but never change it.
        var query = db.FormVersions.AsNoTracking().Include(v => v.Form).Where(v => v.Form.Key == formKey);

        FormVersion? version;
        if (selector == Latest)
        {
            version = await query.Where(v => v.Status == VersionStatus.Published).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);
        }
        else if (selector == Draft)
        {
            version = await query.FirstOrDefaultAsync(v => v.Status == VersionStatus.Draft, ct);
        }
        else if (int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            version = await query.FirstOrDefaultAsync(v => v.Number == number, ct);
        }
        else
        {
            return (null, Problems.BadRequest($"'{selector}' is not a version. Use a number, '{Latest}' or '{Draft}'."));
        }

        if (version is not null)
        {
            return (version, null);
        }

        var formExists = await db.Forms.AnyAsync(f => f.Key == formKey, ct);
        return (null, Problems.NotFound(formExists
            ? $"Form '{formKey}' has no version '{selector}'."
            : $"Form '{formKey}' does not exist."));
    }
}
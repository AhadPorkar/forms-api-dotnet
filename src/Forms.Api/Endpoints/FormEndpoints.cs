using System.Text.RegularExpressions;
using Forms.Api.Contracts;
using Forms.Api.Infrastructure;
using Forms.Api.Persistence;
using Forms.Core.Compatibility;
using Forms.Core.Schema;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Forms.Api.Endpoints;

public static partial class FormEndpoints
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,63}\z")]
    private static partial Regex FormKey();

    public static RouteGroupBuilder MapFormEndpoints(this RouteGroupBuilder api)
    {
        var forms = api.MapGroup("/forms").WithTags("Forms");
        forms.MapPost("/", CreateForm).WithSummary("Create a form with its first schema as a draft version.");
        forms.MapGet("/", ListForms).WithSummary("List forms.");
        forms.MapGet("/{key}", GetForm).WithSummary("Get a form and its version history.");

        var versions = api.MapGroup("/forms/{key}/versions").WithTags("Versions");
        versions.MapGet("/{version}", GetVersion)
            .WithSummary("Get one version's schema.")
            .WithDescription("`version` is a number, `latest` (the latest published version) or `draft`.");
        versions.MapPut("/draft", SaveDraftVersion)
            .WithSummary("Create or replace the draft version.")
            .WithDescription("The response includes the changes compared with the latest published version.");
        versions.MapDelete("/draft", DiscardDraftVersion).WithSummary("Discard the draft version.");
        versions.MapPost("/draft/publish", PublishDraftVersion)
            .WithSummary("Publish the draft version.")
            .WithDescription("Fails with 409 and the list of changes when the draft has breaking changes, unless `acceptBreakingChanges=true`.");
        versions.MapGet("/compare", CompareVersions).WithSummary("Compare two versions and classify each change as breaking or not.");
        return api;
    }

    private static async Task<Results<Created<FormDetails>, ValidationProblem, ProblemHttpResult>> CreateForm(
        CreateFormRequest request, FormsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key) || !FormKey().IsMatch(request.Key))
        {
            return Problems.InvalidRequest("key", "Key must be 2 to 64 lowercase letters, digits or '-', starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
        {
            return Problems.InvalidRequest("title", $"Title is required and can be at most {MaxTitleLength} characters.");
        }

        if (request.Description?.Length > MaxDescriptionLength)
        {
            return Problems.InvalidRequest("description", $"Description can be at most {MaxDescriptionLength} characters.");
        }

        if (SchemaValidator.Validate(request.Schema) is { Count: > 0 } schemaErrors)
        {
            return Problems.InvalidSchema(schemaErrors);
        }

        var now = clock.GetUtcNow();
        var form = new Form { Key = request.Key, Title = request.Title.Trim(), Description = request.Description, CreatedAt = now, UpdatedAt = now };
        form.Versions.Add(new FormVersion { Number = 1, Status = VersionStatus.Draft, Schema = request.Schema, CreatedAt = now, UpdatedAt = now });
        db.Forms.Add(form);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict($"A form with key '{request.Key}' already exists.");
        }

        return TypedResults.Created($"/api/forms/{form.Key}", ToDetails(form));
    }

    private static async Task<Ok<List<FormSummary>>> ListForms(FormsDbContext db, CancellationToken ct)
    {
        var forms = await db.Forms
            .OrderBy(f => f.Key)
            .Select(f => new FormSummary(
                f.Key,
                f.Title,
                f.Description,
                f.Versions.Where(v => v.Status == VersionStatus.Published).Max(v => (int?)v.Number),
                f.Versions.Where(v => v.Status == VersionStatus.Draft).Select(v => (int?)v.Number).FirstOrDefault(),
                f.UpdatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(forms);
    }

    private static async Task<Results<Ok<FormDetails>, ProblemHttpResult>> GetForm(string key, FormsDbContext db, CancellationToken ct)
    {
        var form = await db.Forms.AsNoTracking().Include(f => f.Versions).FirstOrDefaultAsync(f => f.Key == key, ct);
        return form is null ? Problems.NotFound($"Form '{key}' does not exist.") : TypedResults.Ok(ToDetails(form));
    }

    private static async Task<Results<Ok<VersionDetails>, ProblemHttpResult>> GetVersion(
        string key, string version, FormsDbContext db, CancellationToken ct)
    {
        var (found, problem) = await db.FindVersionAsync(key, version, ct);
        return found is null ? problem! : TypedResults.Ok(VersionDetails.From(key, found));
    }

    private static async Task<Results<Ok<DraftVersionSaved>, Created<DraftVersionSaved>, ValidationProblem, ProblemHttpResult>> SaveDraftVersion(
        string key, SaveDraftVersionRequest request, FormsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (SchemaValidator.Validate(request.Schema) is { Count: > 0 } schemaErrors)
        {
            return Problems.InvalidSchema(schemaErrors);
        }

        if (request.Title is { } title && (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength))
        {
            return Problems.InvalidRequest("title", $"Title can be at most {MaxTitleLength} characters and cannot be blank.");
        }

        if (request.Description?.Length > MaxDescriptionLength)
        {
            return Problems.InvalidRequest("description", $"Description can be at most {MaxDescriptionLength} characters.");
        }

        var form = await db.Forms.Include(f => f.Versions).FirstOrDefaultAsync(f => f.Key == key, ct);
        if (form is null)
        {
            return Problems.NotFound($"Form '{key}' does not exist.");
        }

        var now = clock.GetUtcNow();
        var draft = form.Versions.SingleOrDefault(v => v.Status == VersionStatus.Draft);
        var created = draft is null;
        if (draft is null)
        {
            draft = new FormVersion
            {
                FormId = form.Id,
                Number = form.Versions.Count == 0 ? 1 : form.Versions.Max(v => v.Number) + 1,
                Status = VersionStatus.Draft,
                Schema = request.Schema,
                CreatedAt = now,
            };

            // Added explicitly: the id is generated client-side, so EF Core would otherwise
            // take an entity reached through the navigation for an existing row.
            db.FormVersions.Add(draft);
        }

        draft.Schema = request.Schema;
        draft.UpdatedAt = now;
        form.Title = request.Title?.Trim() ?? form.Title;
        form.Description = request.Description ?? form.Description;
        form.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.Conflict("The draft was published or discarded by another request. Reload the form and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict("Another request created a draft version at the same time. Retry the request.");
        }

        var result = new DraftVersionSaved(VersionDetails.From(key, draft), CompareWithLatestPublished(form, draft));
        return created ? TypedResults.Created($"/api/forms/{key}/versions/draft", result) : TypedResults.Ok(result);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DiscardDraftVersion(string key, FormsDbContext db, CancellationToken ct)
    {
        var deleted = await db.FormVersions
            .Where(v => v.Form.Key == key && v.Status == VersionStatus.Draft)
            .ExecuteDeleteAsync(ct);
        return deleted == 0 ? Problems.NotFound($"Form '{key}' has no draft version.") : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PublishResult>, ProblemHttpResult>> PublishDraftVersion(
        string key, FormsDbContext db, TimeProvider clock, CancellationToken ct, bool acceptBreakingChanges = false)
    {
        var form = await db.Forms.Include(f => f.Versions).FirstOrDefaultAsync(f => f.Key == key, ct);
        var draft = form?.Versions.SingleOrDefault(v => v.Status == VersionStatus.Draft);
        if (form is null || draft is null)
        {
            return Problems.NotFound($"Form '{key}' has no draft version to publish.");
        }

        var report = CompareWithLatestPublished(form, draft);
        if (report is { HasBreakingChanges: true } && !acceptBreakingChanges)
        {
            return Problems.BreakingChanges(report);
        }

        var now = clock.GetUtcNow();
        draft.Status = VersionStatus.Published;
        draft.PublishedAt = now;
        draft.UpdatedAt = now;
        form.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The draft changed (or was discarded) after it was compared; the report above no longer applies.
            return Problems.Conflict("The draft was changed by another request while it was being published. Review it and publish again.");
        }

        return TypedResults.Ok(new PublishResult(VersionDetails.From(key, draft), report));
    }

    private static async Task<Results<Ok<CompatibilityReport>, ProblemHttpResult>> CompareVersions(
        string key, string from, string to, FormsDbContext db, CancellationToken ct)
    {
        var (fromVersion, fromProblem) = await db.FindVersionAsync(key, from, ct);
        if (fromVersion is null)
        {
            return fromProblem!;
        }

        var (toVersion, toProblem) = await db.FindVersionAsync(key, to, ct);
        return toVersion is null ? toProblem! : TypedResults.Ok(SchemaCompatibility.Compare(fromVersion.Schema, toVersion.Schema));
    }

    private static CompatibilityReport? CompareWithLatestPublished(Form form, FormVersion draft) =>
        form.Versions.Where(v => v.Status == VersionStatus.Published).MaxBy(v => v.Number) is { } latest
            ? SchemaCompatibility.Compare(latest.Schema, draft.Schema)
            : null;

    private static FormDetails ToDetails(Form form) => new(
        form.Key,
        form.Title,
        form.Description,
        form.CreatedAt,
        form.UpdatedAt,
        [.. form.Versions.OrderBy(v => v.Number).Select(v => new VersionSummary(v.Number, v.Status, v.CreatedAt, v.PublishedAt))]);
}
using System.Text.Json;
using Forms.Api.Contracts;
using Forms.Api.Infrastructure;
using Forms.Api.Persistence;
using Forms.Core.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forms.Api.Endpoints;

public static class SubmissionEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static RouteGroupBuilder MapSubmissionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/").WithTags("Submissions");
        group.MapPost("/forms/{key}/validate", Validate)
            .WithSummary("Validate data without storing it.")
            .WithDescription("`version` defaults to `latest`; `draft` lets a form designer try out an unpublished schema.");
        group.MapPost("/forms/{key}/submissions", Submit)
            .WithSummary("Validate and store a submission against a published version (default: latest).");
        group.MapGet("/forms/{key}/submissions", List)
            .WithSummary("List submissions, newest first.")
            .WithDescription("`filter` is a JSON object matched by containment, for example `{\"country\":\"DE\"}`. " +
                "It runs as `data @> filter` on a GIN index. Page with `after` set to the previous page's `nextCursor`.");
        group.MapGet("/submissions/{id:guid}", Get).WithSummary("Get one submission.");
        return api;
    }

    private static async Task<Results<Ok<ValidateResponse>, ValidationProblem, ProblemHttpResult>> Validate(
        string key, SubmitRequest request, FormsDbContext db, IOptions<FormsOptions> options, CancellationToken ct, string? version = null)
    {
        if (CheckInput(request.Data, options.Value) is { } invalid)
        {
            return invalid;
        }

        var (found, problem) = await db.FindVersionAsync(key, version, ct);
        return found is null ? problem! : TypedResults.Ok(ValidateResponse.From(FormValidator.Validate(found.Schema, request.Data)));
    }

    private static async Task<Results<Created<SubmissionResponse>, ValidationProblem, ProblemHttpResult>> Submit(
        string key, SubmitRequest request, FormsDbContext db, TimeProvider clock, IOptions<FormsOptions> options,
        CancellationToken ct, string? version = null)
    {
        if (CheckInput(request.Data, options.Value) is { } invalid)
        {
            return invalid;
        }

        var (found, problem) = await db.FindVersionAsync(key, version, ct);
        if (found is null)
        {
            return problem!;
        }

        if (found.Status != VersionStatus.Published)
        {
            return Problems.Conflict("Submissions are accepted only for published versions.");
        }

        var result = FormValidator.Validate(found.Schema, request.Data);
        if (!result.IsValid)
        {
            return Problems.InvalidSubmission(result);
        }

        var submission = new Submission
        {
            FormId = found.FormId,
            FormVersionId = found.Id,
            Data = JsonSerializer.SerializeToDocument(result.Data),
            SubmittedAt = clock.GetUtcNow(),
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/submissions/{submission.Id}", ToResponse(submission, key, found.Number));
    }

    private static async Task<Results<Ok<SubmissionPage>, ValidationProblem, ProblemHttpResult>> List(
        string key, FormsDbContext db, CancellationToken ct,
        string? version = null, string? filter = null, Guid? after = null, int limit = DefaultPageSize)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return Problems.InvalidRequest("limit", $"limit must be between 1 and {MaxPageSize}.");
        }

        var form = await db.Forms.AsNoTracking().Select(f => new { f.Id, f.Key }).FirstOrDefaultAsync(f => f.Key == key, ct);
        if (form is null)
        {
            return Problems.NotFound($"Form '{key}' does not exist.");
        }

        var query = db.Submissions.AsNoTracking().Where(s => s.FormId == form.Id);
        if (!string.IsNullOrEmpty(version))
        {
            var (found, problem) = await db.FindVersionAsync(key, version, ct);
            if (found is null)
            {
                return problem!;
            }

            query = query.Where(s => s.FormVersionId == found.Id);
        }

        if (!string.IsNullOrEmpty(filter))
        {
            if (!IsSafeJsonObject(filter))
            {
                return Problems.InvalidRequest("filter", "filter must be a JSON object, for example {\"country\":\"DE\"}.");
            }

            query = query.Where(s => EF.Functions.JsonContains(s.Data, filter));
        }

        if (after is { } cursor)
        {
            // Ids are version 7 GUIDs, so ordering by id is ordering by creation time.
            query = query.Where(s => s.Id.CompareTo(cursor) < 0);
        }

        var page = await query
            .OrderByDescending(s => s.Id)
            .Take(limit + 1)
            .Select(s => new { s.Id, s.Data, s.SubmittedAt, Version = s.FormVersion.Number })
            .ToListAsync(ct);

        var items = page.Take(limit).Select(s => new SubmissionResponse(s.Id, key, s.Version, s.Data.RootElement.Clone(), s.SubmittedAt)).ToList();
        return TypedResults.Ok(new SubmissionPage(items, page.Count > limit ? items[^1].Id : null));
    }

    private static async Task<Results<Ok<SubmissionResponse>, ProblemHttpResult>> Get(Guid id, FormsDbContext db, CancellationToken ct)
    {
        var found = await db.Submissions.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { Submission = s, s.FormVersion.Number, s.FormVersion.Form.Key })
            .FirstOrDefaultAsync(ct);
        return found is null
            ? Problems.NotFound($"Submission '{id}' does not exist.")
            : TypedResults.Ok(ToResponse(found.Submission, found.Key, found.Number));
    }

    internal static SubmissionResponse ToResponse(Submission s, string formKey, int version) =>
        new(s.Id, formKey, version, s.Data.RootElement.Clone(), s.SubmittedAt);

    /// <summary>Rejects data that is too large or that PostgreSQL could not store (see <see cref="JsonInput"/>).</summary>
    internal static ValidationProblem? CheckInput(JsonElement data, FormsOptions options)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > options.MaxDataBytes)
        {
            return Problems.InvalidRequest("data", $"data is larger than {options.MaxDataBytes} bytes.");
        }

        return JsonInput.FindProblem(data) is { } problem ? Problems.InvalidRequest("data", problem) : null;
    }

    private static bool IsSafeJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && JsonInput.FindProblem(document.RootElement) is null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
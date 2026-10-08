using System.Text.Json;
using Forms.Api.Contracts;
using Forms.Api.Infrastructure;
using Forms.Api.Persistence;
using Forms.Core.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forms.Api.Endpoints;

/// <summary>
/// Autosave. A client creates a draft when the user starts filling in a form and saves it every few seconds.
/// Saving never fails because of validation, so no typing is lost; the response carries the current
/// validation result instead. Updates use optimistic concurrency (ETag / If-Match) so that two browser tabs
/// cannot silently overwrite each other.
/// </summary>
public static class DraftEndpoints
{
    public static RouteGroupBuilder MapDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/").WithTags("Autosave drafts");
        group.MapPost("/forms/{key}/drafts", Create).WithSummary("Start an autosave draft on the latest published version.");
        group.MapGet("/drafts/{id:guid}", Get).WithSummary("Get a draft. The ETag header is needed for updates.");
        group.MapPut("/drafts/{id:guid}", Update).WithSummary("Save the draft's data. Requires If-Match.");
        group.MapPost("/drafts/{id:guid}/submit", Submit)
            .WithSummary("Validate the draft and turn it into a submission.")
            .WithDescription("The draft is deleted in the same transaction. If-Match is optional here.");
        group.MapDelete("/drafts/{id:guid}", Delete).WithSummary("Delete a draft.");
        return api;
    }

    private static async Task<Results<Created<DraftResponse>, ValidationProblem, ProblemHttpResult>> Create(
        string key, SaveDraftRequest request, HttpContext http, FormsDbContext db, TimeProvider clock,
        IOptions<FormsOptions> options, CancellationToken ct)
    {
        if (CheckData(request.Data, options.Value) is { } invalid)
        {
            return invalid;
        }

        var (version, problem) = await db.FindVersionAsync(key, VersionLookup.Latest, ct);
        if (version is null)
        {
            return problem!;
        }

        var now = clock.GetUtcNow();
        var draft = new SubmissionDraft
        {
            FormVersionId = version.Id,
            Data = JsonSerializer.SerializeToDocument(request.Data),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + options.Value.DraftRetention,
        };
        db.SubmissionDrafts.Add(draft);
        await db.SaveChangesAsync(ct);

        http.Response.Headers.ETag = ETags.For(draft.RowVersion);
        return TypedResults.Created($"/api/drafts/{draft.Id}", ToResponse(draft, version));
    }

    private static async Task<Results<Ok<DraftResponse>, ProblemHttpResult>> Get(
        Guid id, HttpContext http, FormsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var draft = await FindAsync(db, id, clock, ct);
        if (draft is null)
        {
            return Problems.NotFound($"Draft '{id}' does not exist or has expired.");
        }

        http.Response.Headers.ETag = ETags.For(draft.RowVersion);
        return TypedResults.Ok(ToResponse(draft, draft.FormVersion));
    }

    private static async Task<Results<Ok<DraftResponse>, ValidationProblem, ProblemHttpResult>> Update(
        Guid id, SaveDraftRequest request, HttpContext http, FormsDbContext db, TimeProvider clock,
        IOptions<FormsOptions> options, CancellationToken ct)
    {
        switch (ETags.ReadIfMatch(http.Request, out var expected))
        {
            case IfMatchState.Missing:
                return Problems.PreconditionRequired();
            case IfMatchState.Invalid:
                return Problems.InvalidIfMatch();
        }

        if (CheckData(request.Data, options.Value) is { } invalid)
        {
            return invalid;
        }

        var draft = await FindAsync(db, id, clock, ct);
        if (draft is null)
        {
            return Problems.NotFound($"Draft '{id}' does not exist or has expired.");
        }

        // Tell EF Core which version the client saw; the UPDATE then includes "WHERE xmin = expected".
        db.Entry(draft).Property(d => d.RowVersion).OriginalValue = expected;
        var now = clock.GetUtcNow();
        draft.Data = JsonSerializer.SerializeToDocument(request.Data);
        draft.UpdatedAt = now;
        draft.ExpiresAt = now + options.Value.DraftRetention;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.PreconditionFailed();
        }

        http.Response.Headers.ETag = ETags.For(draft.RowVersion);
        return TypedResults.Ok(ToResponse(draft, draft.FormVersion));
    }

    private static async Task<Results<Created<SubmissionResponse>, ValidationProblem, ProblemHttpResult>> Submit(
        Guid id, HttpContext http, FormsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var draft = await FindAsync(db, id, clock, ct);
        if (draft is null)
        {
            return Problems.NotFound($"Draft '{id}' does not exist or has expired.");
        }

        switch (ETags.ReadIfMatch(http.Request, out var expected))
        {
            case IfMatchState.Invalid:
                return Problems.InvalidIfMatch();
            case IfMatchState.Present:
                db.Entry(draft).Property(d => d.RowVersion).OriginalValue = expected;
                break;
        }

        var result = FormValidator.Validate(draft.FormVersion.Schema, draft.Data.RootElement);
        if (!result.IsValid)
        {
            return Problems.InvalidSubmission(result);
        }

        var submission = new Submission
        {
            FormId = draft.FormVersion.FormId,
            FormVersionId = draft.FormVersionId,
            Data = JsonSerializer.SerializeToDocument(result.Data),
            SubmittedAt = clock.GetUtcNow(),
        };
        db.Submissions.Add(submission);
        db.SubmissionDrafts.Remove(draft);

        try
        {
            // One SaveChanges call is one transaction: the submission exists exactly when the draft is gone.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.PreconditionFailed();
        }

        return TypedResults.Created(
            $"/api/submissions/{submission.Id}",
            SubmissionEndpoints.ToResponse(submission, draft.FormVersion.Form.Key, draft.FormVersion.Number));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Delete(Guid id, FormsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // An expired draft is treated as gone everywhere; the cleanup service removes the row later.
        var now = clock.GetUtcNow();
        var deleted = await db.SubmissionDrafts.Where(d => d.Id == id && d.ExpiresAt > now).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Problems.NotFound($"Draft '{id}' does not exist or has expired.") : TypedResults.NoContent();
    }

    private static Task<SubmissionDraft?> FindAsync(FormsDbContext db, Guid id, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.SubmissionDrafts
            .Include(d => d.FormVersion).ThenInclude(v => v.Form)
            .FirstOrDefaultAsync(d => d.Id == id && d.ExpiresAt > now, ct);
    }

    private static ValidationProblem? CheckData(JsonElement data, FormsOptions options) =>
        data.ValueKind != JsonValueKind.Object
            ? Problems.InvalidRequest("data", "data must be a JSON object.")
            : SubmissionEndpoints.CheckInput(data, options);

    private static DraftResponse ToResponse(SubmissionDraft draft, FormVersion version) => new(
        draft.Id,
        version.Form.Key,
        version.Number,
        draft.Data.RootElement.Clone(),
        draft.UpdatedAt,
        draft.ExpiresAt,
        ValidateResponse.From(FormValidator.Validate(version.Schema, draft.Data.RootElement)));
}
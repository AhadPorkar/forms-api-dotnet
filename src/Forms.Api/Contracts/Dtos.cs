using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forms.Api.Persistence;
using Forms.Core.Compatibility;
using Forms.Core.Schema;
using Forms.Core.Validation;

namespace Forms.Api.Contracts;

public sealed record CreateFormRequest(
    [property: Description("URL-friendly unique key: lowercase letters, digits and '-', 2 to 64 characters.")] string Key,
    string Title,
    FormSchema Schema,
    string? Description = null);

public sealed record SaveDraftVersionRequest(FormSchema Schema, string? Title = null, string? Description = null);

public sealed record FormSummary(string Key, string Title, string? Description, int? LatestPublishedVersion, int? DraftVersion, DateTimeOffset UpdatedAt);

public sealed record VersionSummary(int Number, VersionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record FormDetails(
    string Key, string Title, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<VersionSummary> Versions);

public sealed record VersionDetails(string FormKey, int Number, VersionStatus Status, FormSchema Schema, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt)
{
    public static VersionDetails From(string formKey, FormVersion v) => new(formKey, v.Number, v.Status, v.Schema, v.CreatedAt, v.PublishedAt);
}

/// <param name="Compatibility">Differences from the latest published version, or null when nothing has been published yet.</param>
public sealed record DraftVersionSaved(VersionDetails Version, CompatibilityReport? Compatibility);

public sealed record PublishResult(VersionDetails Version, CompatibilityReport? Compatibility);

public sealed record ValidateResponse(bool Valid, IReadOnlyList<ValidationError> Errors, JsonObject Data)
{
    public static ValidateResponse From(ValidationResult result) => new(result.IsValid, result.Errors, result.Data);
}

public sealed record SubmitRequest(JsonElement Data);

public sealed record SubmissionResponse(Guid Id, string FormKey, int Version, JsonElement Data, DateTimeOffset SubmittedAt);

public sealed record SubmissionPage(IReadOnlyList<SubmissionResponse> Items, Guid? NextCursor);

public sealed record SaveDraftRequest(JsonElement Data);

/// <param name="Validation">The result of validating the saved data as if it were submitted now, so a client can show progress.</param>
public sealed record DraftResponse(
    Guid Id, string FormKey, int Version, JsonElement Data, DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt, ValidateResponse Validation);
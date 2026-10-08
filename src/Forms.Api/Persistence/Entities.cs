using System.Text.Json;
using Forms.Core.Schema;

namespace Forms.Api.Persistence;

public sealed class Form
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>URL-friendly unique name, for example <c>job-application</c>.</summary>
    public required string Key { get; init; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<FormVersion> Versions { get; init; } = [];
}

public enum VersionStatus
{
    Draft,
    Published,
}

/// <summary>
/// One numbered version of a form's schema. At most one version per form is a draft;
/// published versions are immutable, and every submission points at the version it was validated against.
/// </summary>
public sealed class FormVersion
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid FormId { get; init; }

    public Form Form { get; init; } = null!;

    public int Number { get; init; }

    public VersionStatus Status { get; set; }

    public required FormSchema Schema { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Mapped to <c>xmin</c>. Publishing and editing the draft both check it, so a concurrent request
    /// cannot overwrite a version that was published in the meantime.
    /// </summary>
    public uint RowVersion { get; set; }
}

/// <summary>
/// Partially filled-in form data saved by the client while the user types (autosave).
/// Never validated on save; validated in full when it is submitted.
/// </summary>
public sealed class SubmissionDraft : IDisposable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid FormVersionId { get; init; }

    public FormVersion FormVersion { get; init; } = null!;

    public required JsonDocument Data { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Mapped to PostgreSQL's <c>xmin</c> system column for optimistic concurrency; exposed as the ETag.</summary>
    public uint RowVersion { get; set; }

    public void Dispose() => Data.Dispose();
}

public sealed class Submission : IDisposable
{
    /// <summary>A version 7 (time-ordered) GUID, which also serves as the paging cursor.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Copied from the version so that listing a form's submissions needs no join.</summary>
    public Guid FormId { get; init; }

    public Guid FormVersionId { get; init; }

    public FormVersion FormVersion { get; init; } = null!;

    public required JsonDocument Data { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    public void Dispose() => Data.Dispose();
}
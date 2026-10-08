namespace Forms.Api.Infrastructure;

public sealed class FormsOptions
{
    public const string Section = "Forms";

    /// <summary>How long an untouched autosave draft is kept.</summary>
    public TimeSpan DraftRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often expired drafts are deleted.</summary>
    public TimeSpan DraftCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Largest submission or draft payload accepted, in bytes of JSON.</summary>
    public int MaxDataBytes { get; set; } = 256 * 1024;

    /// <summary>Apply EF Core migrations when the app starts. Convenient for local runs and Docker Compose.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
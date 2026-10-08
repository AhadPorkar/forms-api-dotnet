using Forms.Core.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Forms.Api.Persistence;

public sealed class FormsDbContext(DbContextOptions<FormsDbContext> options) : DbContext(options)
{
    public DbSet<Form> Forms => Set<Form>();

    public DbSet<FormVersion> FormVersions => Set<FormVersion>();

    public DbSet<SubmissionDraft> SubmissionDrafts => Set<SubmissionDraft>();

    public DbSet<Submission> Submissions => Set<Submission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Form>(form =>
        {
            form.HasIndex(f => f.Key).IsUnique();
            form.Property(f => f.Key).HasMaxLength(64);
            form.Property(f => f.Title).HasMaxLength(200);
            form.Property(f => f.Description).HasMaxLength(2000);
        });

        modelBuilder.Entity<FormVersion>(version =>
        {
            version.HasOne(v => v.Form).WithMany(f => f.Versions).HasForeignKey(v => v.FormId).OnDelete(DeleteBehavior.Cascade);
            version.HasIndex(v => new { v.FormId, v.Number }).IsUnique();

            // At most one draft per form, enforced by the database rather than by application code.
            version.HasIndex(v => v.FormId).IsUnique().HasFilter("status = 'Draft'").HasDatabaseName("ix_form_versions_one_draft_per_form");

            version.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
            version.Property(v => v.RowVersion).IsRowVersion();
            version.Property(v => v.Schema)
                .HasColumnType("jsonb")
                .HasConversion(
                    schema => FormSchemaJson.Serialize(schema),
                    json => FormSchemaJson.Deserialize(json),
                    new ValueComparer<FormSchema>(
                        (a, b) => FormSchemaJson.Serialize(a!) == FormSchemaJson.Serialize(b!),
                        s => FormSchemaJson.Serialize(s).GetHashCode(StringComparison.Ordinal),
                        s => FormSchemaJson.Deserialize(FormSchemaJson.Serialize(s))));
        });

        modelBuilder.Entity<SubmissionDraft>(draft =>
        {
            draft.HasOne(d => d.FormVersion).WithMany().HasForeignKey(d => d.FormVersionId).OnDelete(DeleteBehavior.Cascade);
            draft.Property(d => d.Data).HasColumnType("jsonb");
            draft.Property(d => d.RowVersion).IsRowVersion();
            draft.HasIndex(d => d.ExpiresAt);
        });

        modelBuilder.Entity<Submission>(submission =>
        {
            submission.HasOne(s => s.FormVersion).WithMany().HasForeignKey(s => s.FormVersionId).OnDelete(DeleteBehavior.Restrict);
            submission.Property(s => s.Data).HasColumnType("jsonb");

            // jsonb_path_ops makes containment queries (data @> '{"country":"DE"}') use the index.
            submission.HasIndex(s => s.Data).HasMethod("gin").HasOperators("jsonb_path_ops");
            submission.HasOne<Form>().WithMany().HasForeignKey(s => s.FormId).OnDelete(DeleteBehavior.Restrict);
            submission.HasIndex(s => new { s.FormId, s.Id });
        });
    }
}
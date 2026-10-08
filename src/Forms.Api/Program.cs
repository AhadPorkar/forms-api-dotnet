using System.Text.Json;
using System.Text.Json.Serialization;
using Forms.Api.Endpoints;
using Forms.Api.Infrastructure;
using Forms.Api.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<FormsOptions>().BindConfiguration(FormsOptions.Section);

// Bodies larger than this are refused by Kestrel with 413 before they are read. Submission data has its own,
// smaller limit (Forms:MaxDataBytes); this one also covers schemas.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Forms:MaxRequestBodyBytes") ?? 1024 * 1024);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<FormsDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Forms"))
    .UseSnakeCaseNamingConvention());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Forms API";
    document.Info.Description = "Dynamic form definitions with versioned schemas, server-side validation, autosave drafts and submissions stored as PostgreSQL JSONB.";
    return Task.CompletedTask;
}));

builder.Services.AddHealthChecks().AddDbContextCheck<FormsDbContext>("database", tags: ["ready"]);

builder.Services.AddSingleton<DraftCleanupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DraftCleanupService>());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Forms API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

var api = app.MapGroup("/api");
api.MapFormEndpoints();
api.MapSubmissionEndpoints();
api.MapDraftEndpoints();

if (app.Services.GetRequiredService<IOptions<FormsOptions>>().Value.ApplyMigrationsOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FormsDbContext>().Database.MigrateAsync();
}

await app.RunAsync();

/// <summary>Entry point, public so that integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
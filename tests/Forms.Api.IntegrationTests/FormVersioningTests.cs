using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Forms.Api.Persistence;
using Forms.Core.Schema;
using Microsoft.EntityFrameworkCore;
using static Forms.Api.IntegrationTests.ApiClient;

namespace Forms.Api.IntegrationTests;

public sealed class FormVersioningTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Creating_a_form_stores_version_1_as_a_draft()
    {
        var key = UniqueKey("create");

        var created = await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));

        Assert.Equal(key, created.Get<string>("key"));
        Assert.Equal(1, created.Get<int>("versions.0.number"));
        Assert.Equal("draft", created.Get<string>("versions.0.status"));

        var draft = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/versions/draft"));
        Assert.Equal("employeeName", draft.Get<string>("schema.fields.0.key"));
    }

    [Fact]
    public async Task Form_keys_are_unique()
    {
        var key = UniqueKey("dup");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));
        await _client.ExpectAsync(HttpStatusCode.Conflict, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));
    }

    [Fact]
    public async Task Invalid_schema_is_rejected_with_the_paths_of_the_problems()
    {
        var form = LeaveRequestForm(UniqueKey("invalid"));
        form["schema"]!["fields"]![0]!["key"] = "Employee Name";
        form["schema"]!["fields"]![3]!["visibleWhen"]!["field"] = "nope";

        var problem = await _client.ExpectAsync(HttpStatusCode.UnprocessableEntity, c => c.PostAsJsonAsync("/api/forms", form));

        var errors = problem["errors"]!.AsObject().Select(e => e.Key).ToList();
        Assert.Contains("schema.fields[0].key", errors);
        Assert.Contains("schema.fields[3].visibleWhen.field", errors);
    }

    [Fact]
    public async Task Invalid_form_key_is_rejected()
    {
        var problem = await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm("Not A Key")));
        Assert.NotNull(problem["errors"]!["key"]);
    }

    [Fact]
    public async Task Breaking_draft_needs_explicit_confirmation_to_publish()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("breaking");
        var schema = LeaveRequestForm(key)["schema"]!.DeepClone();
        schema["fields"]!.AsArray().RemoveAt(8); // drop "notify"

        var saved = await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", new JsonObject { ["schema"] = schema }));
        Assert.Equal(2, saved.Get<int>("version.number"));
        Assert.True(saved.Get<bool>("compatibility.hasBreakingChanges"));
        Assert.Equal("fieldRemoved", saved.Get<string>("compatibility.changes.0.kind"));

        var refused = await _client.ExpectAsync(HttpStatusCode.Conflict, c => c.PostAsync($"/api/forms/{key}/versions/draft/publish", null));
        Assert.True(refused.Get<bool>("compatibility.hasBreakingChanges"));

        var published = await _client.ExpectAsync(HttpStatusCode.OK,
            c => c.PostAsync($"/api/forms/{key}/versions/draft/publish?acceptBreakingChanges=true", null));
        Assert.Equal("published", published.Get<string>("version.status"));

        var form = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}"));
        Assert.Equal(["published", "published"], form["versions"]!.AsArray().Select(v => v!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Non_breaking_draft_publishes_without_confirmation()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("compatible");
        var schema = LeaveRequestForm(key)["schema"]!.DeepClone();
        schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = "comment", ["label"] = "Comment", ["type"] = "longText" });

        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", new JsonObject { ["schema"] = schema }));
        var published = await _client.ExpectAsync(HttpStatusCode.OK, c => c.PostAsync($"/api/forms/{key}/versions/draft/publish", null));

        Assert.False(published.Get<bool>("compatibility.hasBreakingChanges"));
        var latest = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/versions/latest"));
        Assert.Equal(2, latest.Get<int>("number"));
    }

    [Fact]
    public async Task Saving_the_draft_twice_updates_the_same_version()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("resave");
        var body = new JsonObject { ["schema"] = LeaveRequestForm(key)["schema"]!.DeepClone() };

        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", body));
        var again = await _client.ExpectAsync(HttpStatusCode.OK, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", body));

        Assert.Equal(2, again.Get<int>("version.number"));
        Assert.Empty(again["compatibility"]!["changes"]!.AsArray());
    }

    [Fact]
    public async Task Compare_endpoint_reports_changes_between_versions()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("compare");
        var schema = LeaveRequestForm(key)["schema"]!.DeepClone();
        schema["fields"]![6]!["constraints"]!["max"] = 20;
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", new JsonObject { ["schema"] = schema }));

        var report = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/versions/compare?from=1&to=draft"));

        Assert.Equal("constraintTightened", report.Get<string>("changes.0.kind"));
        Assert.Equal("workingDays", report.Get<string>("changes.0.target"));
    }

    [Fact]
    public async Task Discarding_the_draft_keeps_published_versions()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("discard");
        var body = new JsonObject { ["schema"] = LeaveRequestForm(key)["schema"]!.DeepClone() };
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", body));

        await _client.ExpectAsync(HttpStatusCode.NoContent, c => c.DeleteAsync($"/api/forms/{key}/versions/draft"));

        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.GetAsync($"/api/forms/{key}/versions/draft"));
        await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/versions/1"));
    }

    [Fact]
    public async Task Unknown_version_selector_is_a_bad_request() =>
        await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.GetAsync($"/api/forms/{UniqueKey("x")}/versions/newest"));

    [Fact]
    public async Task Database_allows_only_one_draft_per_form()
    {
        var key = UniqueKey("onedraft");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));

        await using var db = factory.CreateDbContext();
        var form = await db.Forms.SingleAsync(f => f.Key == key, TestContext.Current.CancellationToken);
        db.FormVersions.Add(new FormVersion
        {
            FormId = form.Id,
            Number = 2,
            Status = VersionStatus.Draft,
            Schema = new FormSchema { Fields = [new FieldDefinition { Key = "a", Label = "A", Type = FieldType.Text }] },
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("ix_form_versions_one_draft_per_form", error.InnerException!.Message, StringComparison.Ordinal);
    }
}
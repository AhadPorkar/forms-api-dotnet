using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Forms.Api.Persistence;
using Forms.Core.Schema;
using Microsoft.EntityFrameworkCore;
using static Forms.Api.IntegrationTests.ApiClient;

namespace Forms.Api.IntegrationTests;

/// <summary>
/// Input that is syntactically valid JSON or HTTP but that the database, the runtime or the HTTP stack
/// cannot handle must get a 4xx response, never a 500.
/// </summary>
public sealed class InputHardeningTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static StringContent RawJson(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("""{ "data": { "employeeName": "Jane\u0000Doe" } }""", "NUL character")]
    [InlineData("""{ "data": { "employeeName": "J\ud800" } }""", "lone surrogate")]
    [InlineData("""{ "data": { "\ud800": 1 } }""", "lone surrogate in a name")]
    [InlineData("""{ "data": { "workingDays": 1e1000000 } }""", "number out of range")]
    public async Task Unsafe_submission_data_is_a_bad_request(string body, string because)
    {
        var key = await _client.CreatePublishedLeaveFormAsync("unsafe");

        using var submit = await _client.PostAsync($"/api/forms/{key}/submissions", RawJson(body), TestContext.Current.CancellationToken);
        using var validate = await _client.PostAsync($"/api/forms/{key}/validate", RawJson(body), TestContext.Current.CancellationToken);
        using var draft = await _client.PostAsync($"/api/forms/{key}/drafts", RawJson(body), TestContext.Current.CancellationToken);

        Assert.True(submit.StatusCode == HttpStatusCode.BadRequest, $"submit, {because}: {(int)submit.StatusCode}");
        Assert.True(validate.StatusCode == HttpStatusCode.BadRequest, $"validate, {because}: {(int)validate.StatusCode}");
        Assert.True(draft.StatusCode == HttpStatusCode.BadRequest, $"draft, {because}: {(int)draft.StatusCode}");
    }

    [Fact]
    public async Task Unsafe_filter_is_a_bad_request()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("unsafefilter");
        await _client.ExpectAsync(HttpStatusCode.BadRequest,
            c => c.GetAsync($"/api/forms/{key}/submissions?filter={Uri.EscapeDataString("""{"a":"\u0000"}""")}"));
    }

    [Theory]
    [InlineData("nlkey\\n", "trailing newline would break the Location header")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\n", "65 characters would overflow varchar(64)")]
    public async Task Form_key_with_control_characters_is_a_bad_request(string key, string because)
    {
        var body = $$"""{ "key": "{{key}}", "title": "T", "schema": { "fields": [ { "key": "a", "label": "A", "type": "text" } ] } }""";

        using var response = await _client.PostAsync("/api/forms", RawJson(body), TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{because}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Description_is_optional_but_limited()
    {
        var form = LeaveRequestForm(UniqueKey("nodesc"));
        form.Remove("description");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", form));

        var tooLong = LeaveRequestForm(UniqueKey("longdesc"));
        tooLong["description"] = new string('x', 2001);
        var problem = await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.PostAsJsonAsync("/api/forms", tooLong));
        Assert.NotNull(problem["errors"]!["description"]);
    }

    [Theory]
    [InlineData("""{ "fields": [ null ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "A", "type": "select", "options": [ null ] } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "A", "type": "text" } ], "rules": [ null ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "A", "type": "text" }, { "key": "b", "label": "B", "type": "text", "visibleWhen": { "all": [ null ] } } ] }""")]
    public async Task Null_elements_in_a_schema_are_unprocessable(string schema)
    {
        var body = $$"""{ "key": "{{UniqueKey("nulls")}}", "title": "T", "schema": {{schema}} }""";

        using var response = await _client.PostAsync("/api/forms", RawJson(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Numeric_operator_values_are_rejected()
    {
        var body = $$"""
            { "key": "{{UniqueKey("intop")}}", "title": "T", "schema": { "fields": [
                { "key": "a", "label": "A", "type": "text" },
                { "key": "b", "label": "B", "type": "text", "visibleWhen": { "field": "a", "operator": 42, "value": "x" } } ] } }
            """;

        using var response = await _client.PostAsync("/api/forms", RawJson(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stale_draft_version_cannot_overwrite_a_published_one()
    {
        var key = UniqueKey("race");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));

        // A second request loaded the draft before it was published ...
        await using var stale = factory.CreateDbContext();
        var draft = await stale.FormVersions.SingleAsync(v => v.Form.Key == key, TestContext.Current.CancellationToken);
        await _client.ExpectAsync(HttpStatusCode.OK, c => c.PostAsync($"/api/forms/{key}/versions/draft/publish", null));

        // ... and now tries to write its schema. xmin has changed, so the write is refused.
        draft.Schema = new FormSchema { Fields = [new FieldDefinition { Key = "a", Label = "A", Type = FieldType.Text }] };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(TestContext.Current.CancellationToken));

        var published = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/versions/1"));
        Assert.Equal("employeeName", published.Get<string>("schema.fields.0.key"));
    }

    [Fact]
    public async Task Submission_list_accepts_the_same_version_selectors_as_other_endpoints()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("selectors");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave()));

        var latest = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/submissions?version=latest"));
        Assert.Single(latest["items"]!.AsArray());

        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.GetAsync($"/api/forms/{key}/submissions?version=draft"));
        await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.GetAsync($"/api/forms/{key}/submissions?version=newest"));
    }

    [Fact]
    public async Task Expired_draft_cannot_be_deleted_through_the_api()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("expireddelete");
        var created = await _client.ExpectAsync(HttpStatusCode.Created,
            c => c.PostAsJsonAsync($"/api/forms/{key}/drafts", new JsonObject { ["data"] = new JsonObject() }));

        factory.Clock.Advance(TimeSpan.FromDays(31));
        try
        {
            await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.DeleteAsync($"/api/drafts/{created.Get<Guid>("id")}"));
        }
        finally
        {
            factory.Clock.Advance(TimeSpan.FromDays(-31));
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"5\"")]
    [InlineData("\"abc\"")]
    public async Task Malformed_if_match_is_a_bad_request_on_update_and_submit(string ifMatch)
    {
        var key = await _client.CreatePublishedLeaveFormAsync("ifmatch");
        var created = await _client.ExpectAsync(HttpStatusCode.Created,
            c => c.PostAsJsonAsync($"/api/forms/{key}/drafts", new JsonObject { ["data"] = ValidLeave() }));
        var id = created.Get<Guid>("id");

        foreach (var (method, path) in new[] { (HttpMethod.Put, $"/api/drafts/{id}"), (HttpMethod.Post, $"/api/drafts/{id}/submit") })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (method == HttpMethod.Put)
            {
                request.Content = Json(new JsonObject { ["data"] = ValidLeave() });
            }

            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{method} with If-Match {ifMatch}: {(int)response.StatusCode}");
        }
    }
}
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Forms.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Forms.Api.IntegrationTests.ApiClient;

namespace Forms.Api.IntegrationTests;

public sealed class AutosaveDraftTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Guid Id, EntityTagHeaderValue ETag, JsonNode Body)> StartDraftAsync(string key, JsonObject data)
    {
        using var response = await _client.PostAsJsonAsync($"/api/forms/{key}/drafts", new JsonObject { ["data"] = data });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.BodyAsync();
        return (body.Get<Guid>("id"), response.Headers.ETag!, body);
    }

    private Task<HttpResponseMessage> SaveAsync(Guid id, JsonObject data, EntityTagHeaderValue? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/drafts/{id}") { Content = Json(new JsonObject { ["data"] = data }) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Incomplete_data_is_saved_and_the_response_says_what_is_missing()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("autosave");

        var (_, etag, body) = await StartDraftAsync(key, new JsonObject { ["employeeName"] = "Jane" });

        Assert.NotNull(etag);
        Assert.False(body.Get<bool>("validation.valid"));
        Assert.Contains("email", body["validation"]!["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()));
        Assert.Equal("Jane", body.Get<string>("data.employeeName"));
    }

    [Fact]
    public async Task Update_requires_if_match_and_rejects_a_stale_etag()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("etag");
        var (id, firstETag, _) = await StartDraftAsync(key, new JsonObject { ["employeeName"] = "Jane" });

        using (var missing = await SaveAsync(id, new JsonObject { ["employeeName"] = "Jane D" }, etag: null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        EntityTagHeaderValue secondETag;
        using (var saved = await SaveAsync(id, new JsonObject { ["employeeName"] = "Jane D" }, firstETag))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            secondETag = saved.Headers.ETag!;
            Assert.NotEqual(firstETag, secondETag);
        }

        // A second browser tab still holding the first ETag must not overwrite the newer data.
        using (var stale = await SaveAsync(id, new JsonObject { ["employeeName"] = "Old tab" }, firstETag))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }

        var current = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/drafts/{id}"));
        Assert.Equal("Jane D", current.Get<string>("data.employeeName"));
    }

    [Fact]
    public async Task Submitting_a_complete_draft_creates_a_submission_and_removes_the_draft()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("draftsubmit");
        var (id, etag, _) = await StartDraftAsync(key, new JsonObject { ["employeeName"] = "Jane" });

        // Incomplete: refused, and the draft is kept.
        await _client.ExpectAsync(HttpStatusCode.UnprocessableEntity, c => c.PostAsync($"/api/drafts/{id}/submit", null));
        await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/drafts/{id}"));

        using (var saved = await SaveAsync(id, ValidLeave(), etag))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var submission = await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsync($"/api/drafts/{id}/submit", null));

        Assert.Equal("Jane Doe", submission.Get<string>("data.employeeName"));
        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.GetAsync($"/api/drafts/{id}"));
        await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/submissions/{submission.Get<Guid>("id")}"));
    }

    [Fact]
    public async Task Submit_with_a_stale_etag_is_refused()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("stalesubmit");
        var (id, firstETag, _) = await StartDraftAsync(key, ValidLeave());
        using (await SaveAsync(id, ValidLeave(d => d["workingDays"] = 4), firstETag))
        {
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/drafts/{id}/submit");
        request.Headers.IfMatch.Add(firstETag);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Drafts_expire_and_are_cleaned_up()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("expiry");
        var (id, _, _) = await StartDraftAsync(key, new JsonObject { ["employeeName"] = "Jane" });

        factory.Clock.Advance(TimeSpan.FromDays(31));
        try
        {
            await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.GetAsync($"/api/drafts/{id}"));

            var deleted = await factory.Services.GetRequiredService<DraftCleanupService>().RunOnceAsync(TestContext.Current.CancellationToken);

            Assert.True(deleted >= 1);
            await using var db = factory.CreateDbContext();
            Assert.False(await db.SubmissionDrafts.AnyAsync(d => d.Id == id, TestContext.Current.CancellationToken));
        }
        finally
        {
            factory.Clock.Advance(TimeSpan.FromDays(-31));
        }
    }

    [Fact]
    public async Task Draft_data_must_be_an_object()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("notobject");
        await _client.ExpectAsync(HttpStatusCode.BadRequest,
            c => c.PostAsJsonAsync($"/api/forms/{key}/drafts", new JsonObject { ["data"] = new JsonArray(1, 2) }));
    }
}
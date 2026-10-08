using System.Net;
using System.Text.Json.Nodes;

namespace Forms.Api.IntegrationTests;

public sealed class HostTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_api()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(json)!;

        Assert.Equal("Forms API", document["info"]!["title"]!.GetValue<string>());
        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/forms/{key}/versions/draft/publish", paths);
        Assert.Contains("/api/drafts/{id}", paths);
        Assert.Contains("/api/forms/{key}/submissions", paths);
    }

    [Fact]
    public async Task Errors_are_problem_details()
    {
        using var response = await _client.GetAsync("/api/forms/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("""{ "key": "no-schema", "title": "Missing schema" }""")]
    [InlineData("""{ "key": "bad-json", "title": """)]
    [InlineData("""{ "key": "null-fields", "title": "t", "schema": { "fields": null } }""")]
    public async Task Malformed_request_bodies_are_bad_requests(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/forms", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
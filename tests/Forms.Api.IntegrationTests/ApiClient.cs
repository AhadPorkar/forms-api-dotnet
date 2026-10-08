using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forms.Api.IntegrationTests;

/// <summary>Small helpers so tests read as API scenarios.</summary>
internal static class ApiClient
{
    public static JsonObject LeaveRequestForm(string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "leave-request.form.json");
        var form = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        form["key"] = key;
        return form;
    }

    public static JsonObject ValidLeave(Action<JsonObject>? change = null)
    {
        var data = new JsonObject
        {
            ["employeeName"] = " Jane Doe ",
            ["email"] = "jane.doe@example.com",
            ["leaveType"] = "annual",
            ["startDate"] = "2026-11-02",
            ["endDate"] = "2026-11-06",
            ["workingDays"] = 5,
        };
        change?.Invoke(data);
        return data;
    }

    public static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 33)];

    /// <summary>Creates the sample form and publishes version 1.</summary>
    public static async Task<string> CreatePublishedLeaveFormAsync(this HttpClient client, string prefix)
    {
        var key = UniqueKey(prefix);
        await client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));
        await client.ExpectAsync(HttpStatusCode.OK, c => c.PostAsync($"/api/forms/{key}/versions/draft/publish", null));
        return key;
    }

    public static Task<HttpResponseMessage> SubmitAsync(this HttpClient client, string key, JsonNode data, string? version = null) =>
        client.PostAsJsonAsync($"/api/forms/{key}/submissions{(version is null ? "" : $"?version={version}")}", new JsonObject { ["data"] = data });

    public static async Task<JsonNode> ExpectAsync(this HttpClient client, HttpStatusCode expected, Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        using var response = await call(client);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return string.IsNullOrEmpty(body) ? new JsonObject() : JsonNode.Parse(body)!;
    }

    public static async Task<JsonNode> BodyAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public static StringContent Json(JsonNode node) => new(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    public static T Get<T>(this JsonNode node, string path)
    {
        var current = node;
        foreach (var part in path.Split('.'))
        {
            current = int.TryParse(part, out var index) ? current![index] : current![part];
        }

        return current!.Deserialize<T>()!;
    }
}
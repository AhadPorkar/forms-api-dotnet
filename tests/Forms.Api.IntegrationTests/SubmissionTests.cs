using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static Forms.Api.IntegrationTests.ApiClient;

namespace Forms.Api.IntegrationTests;

public sealed class SubmissionTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Valid_submission_is_stored_normalised()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("submit");

        var created = await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave()));

        Assert.Equal(1, created.Get<int>("version"));
        Assert.Equal("Jane Doe", created.Get<string>("data.employeeName"));

        var stored = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/submissions/{created.Get<Guid>("id")}"));
        Assert.Equal("Jane Doe", stored.Get<string>("data.employeeName"));
        Assert.Equal(key, stored.Get<string>("formKey"));
    }

    [Fact]
    public async Task Invalid_submission_returns_every_violation_with_its_code()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("violations");
        var data = ValidLeave(d =>
        {
            d.Remove("email");
            d["workingDays"] = 40;
            d["endDate"] = "2026-11-01";
            d["unexpected"] = true;
        });

        var problem = await _client.ExpectAsync(HttpStatusCode.UnprocessableEntity, c => c.SubmitAsync(key, data));

        var violations = problem["violations"]!.AsArray().Select(v => (v!["field"]!.GetValue<string>(), v["code"]!.GetValue<string>())).ToList();
        Assert.Equal([("unexpected", "unknownField"), ("email", "required"), ("workingDays", "max"), ("endDate", "rule")], violations);
        Assert.NotNull(problem["errors"]!["email"]);
    }

    [Fact]
    public async Task Form_without_a_published_version_takes_no_submissions()
    {
        var key = UniqueKey("unpublished");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));

        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.SubmitAsync(key, ValidLeave()));
        await _client.ExpectAsync(HttpStatusCode.Conflict, c => c.SubmitAsync(key, ValidLeave(), version: "draft"));
    }

    [Fact]
    public async Task Draft_schema_can_be_tried_out_with_validate()
    {
        var key = UniqueKey("tryout");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PostAsJsonAsync("/api/forms", LeaveRequestForm(key)));

        var result = await _client.ExpectAsync(HttpStatusCode.OK,
            c => c.PostAsJsonAsync($"/api/forms/{key}/validate?version=draft", new JsonObject { ["data"] = ValidLeave(d => d["leaveType"] = "other") }));

        Assert.False(result.Get<bool>("valid"));
        Assert.Equal("otherReason", result.Get<string>("errors.0.field"));
    }

    [Fact]
    public async Task Submissions_stay_pinned_to_the_version_they_were_made_against()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("pinned");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave()));

        // Version 2 adds a required field.
        var schema = LeaveRequestForm(key)["schema"]!.DeepClone();
        schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = "costCentre", ["label"] = "Cost centre", ["type"] = "text", ["required"] = true });
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.PutAsJsonAsync($"/api/forms/{key}/versions/draft", new JsonObject { ["schema"] = schema }));
        await _client.ExpectAsync(HttpStatusCode.OK, c => c.PostAsync($"/api/forms/{key}/versions/draft/publish?acceptBreakingChanges=true", null));

        // The latest version now needs the new field; version 1 still accepts the old shape.
        await _client.ExpectAsync(HttpStatusCode.UnprocessableEntity, c => c.SubmitAsync(key, ValidLeave()));
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave(), version: "1"));
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave(d => d["costCentre"] = "CC-7")));

        var v1 = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/submissions?version=1"));
        var v2 = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/submissions?version=2"));
        Assert.Equal(2, v1["items"]!.AsArray().Count);
        Assert.Single(v2["items"]!.AsArray());
    }

    [Fact]
    public async Task Submissions_can_be_filtered_by_jsonb_containment()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("filter");
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave()));
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave(d => { d["leaveType"] = "sick"; d["workingDays"] = 2; })));
        await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave(d => { d["leaveType"] = "sick"; d["workingDays"] = 1; d["notify"] = new JsonArray("hr", "manager"); })));

        var sick = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/submissions?filter={Uri.EscapeDataString("""{"leaveType":"sick"}""")}"));
        Assert.Equal(2, sick["items"]!.AsArray().Count);

        // Containment also works inside arrays: notify contains "hr".
        var notifiesHr = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync($"/api/forms/{key}/submissions?filter={Uri.EscapeDataString("""{"notify":["hr"]}""")}"));
        Assert.Single(notifiesHr["items"]!.AsArray());

        await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.GetAsync($"/api/forms/{key}/submissions?filter=not-json"));
    }

    [Fact]
    public async Task Submissions_page_newest_first_with_a_cursor()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("paging");
        var ids = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            var created = await _client.ExpectAsync(HttpStatusCode.Created, c => c.SubmitAsync(key, ValidLeave(d => d["workingDays"] = i)));
            ids.Add(created.Get<Guid>("id"));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/forms/{key}/submissions?limit=2" + (cursor is null ? "" : $"&after={cursor}");
            var page = await _client.ExpectAsync(HttpStatusCode.OK, c => c.GetAsync(url));
            seen.AddRange(page["items"]!.AsArray().Select(item => item!.Get<Guid>("id")));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        ids.Reverse();
        Assert.Equal(ids, seen);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected()
    {
        var key = await _client.CreatePublishedLeaveFormAsync("huge");
        var data = ValidLeave(d => d["employeeName"] = new string('x', 300_000));

        await _client.ExpectAsync(HttpStatusCode.BadRequest, c => c.SubmitAsync(key, data));
    }

    [Fact]
    public async Task Unknown_form_and_submission_are_not_found()
    {
        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.SubmitAsync(UniqueKey("missing"), ValidLeave()));
        await _client.ExpectAsync(HttpStatusCode.NotFound, c => c.GetAsync($"/api/submissions/{Guid.NewGuid()}"));
    }
}
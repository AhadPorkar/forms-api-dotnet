using System.Text.Json;
using Forms.Core.Schema;

namespace Forms.Core.Tests;

internal static class TestData
{
    /// <summary>The sample form from <c>samples/leave-request.form.json</c>, so the README example is tested too.</summary>
    public static FormSchema LeaveRequest { get; } = LoadSample();

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static FormSchema FormOf(params FieldDefinition[] fields) => new() { Fields = fields };

    public static FieldDefinition Field(string key, FieldType type, bool required = false) =>
        new() { Key = key, Label = key, Type = type, Required = required };

    public static IReadOnlyList<FieldOption> Options(params string[] values) =>
        [.. values.Select(v => new FieldOption { Value = v, Label = v })];

    /// <summary>A valid leave request; tests change one thing at a time.</summary>
    public const string ValidLeave = """
        {
          "employeeName": "  Jane Doe ",
          "email": "jane.doe@example.com",
          "leaveType": "annual",
          "startDate": "2026-11-02",
          "endDate": "2026-11-06",
          "workingDays": 5,
          "notify": ["manager"]
        }
        """;

    private static FormSchema LoadSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "leave-request.form.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("schema").Deserialize<FormSchema>(FormSchemaJson.Options)!;
    }
}
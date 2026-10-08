using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forms.Core.Schema;

/// <summary>The one JSON shape used for schemas everywhere: in the API, in the database and in tests.</summary>
public static class FormSchemaJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(FormSchema schema) => JsonSerializer.Serialize(schema, Options);

    public static FormSchema Deserialize(string json) =>
        JsonSerializer.Deserialize<FormSchema>(json, Options)
        ?? throw new JsonException("Schema JSON was null.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
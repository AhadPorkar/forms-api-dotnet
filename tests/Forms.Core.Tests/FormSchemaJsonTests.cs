using System.Text.Json;
using Forms.Core.Schema;
using static Forms.Core.Tests.TestData;

namespace Forms.Core.Tests;

public sealed class FormSchemaJsonTests
{
    [Fact]
    public void Schema_round_trips_through_json()
    {
        var json = FormSchemaJson.Serialize(LeaveRequest);
        Assert.Equal(json, FormSchemaJson.Serialize(FormSchemaJson.Deserialize(json)));
    }

    [Fact]
    public void Enums_are_camel_case_and_nulls_are_omitted()
    {
        var json = FormSchemaJson.Serialize(FormOf(Field("notes", FieldType.LongText)));
        Assert.Equal("""{"fields":[{"key":"notes","label":"notes","type":"longText","required":false}],"rules":[]}""", json);
    }

    [Fact]
    public void Missing_required_property_is_rejected() =>
        Assert.Throws<JsonException>(() => FormSchemaJson.Deserialize("""{ "fields": [ { "key": "a", "type": "text" } ] }"""));
}
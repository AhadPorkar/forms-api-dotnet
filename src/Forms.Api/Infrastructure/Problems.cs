using Forms.Core.Compatibility;
using Forms.Core.Schema;
using Forms.Core.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Forms.Api.Infrastructure;

/// <summary>RFC 9457 problem responses used by the endpoints.</summary>
public static class Problems
{
    public static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static ProblemHttpResult Conflict(string detail, IDictionary<string, object?>? extensions = null) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions);

    public static ProblemHttpResult PreconditionFailed() => TypedResults.Problem(
        "The draft was changed by another request. Reload it and apply your change again.",
        statusCode: StatusCodes.Status412PreconditionFailed,
        title: "Precondition failed");

    public static ProblemHttpResult PreconditionRequired() => TypedResults.Problem(
        "Send the draft's ETag in an If-Match header.",
        statusCode: StatusCodes.Status428PreconditionRequired,
        title: "Precondition required");

    public static ProblemHttpResult InvalidIfMatch() =>
        BadRequest("If-Match must be one strong ETag as returned by this API, for example \"742\".");

    public static ProblemHttpResult BadRequest(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Bad request");

    public static ValidationProblem InvalidRequest(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, title: "The request is invalid.");

    /// <summary>422: the request is well-formed, but the schema it carries is not consistent.</summary>
    public static ProblemHttpResult InvalidSchema(IReadOnlyList<SchemaError> errors) => Unprocessable(
        "The form schema is invalid.",
        errors.GroupBy(e => $"schema.{e.Path}").ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));

    /// <summary>422 with the usual <c>errors</c> map plus a <c>violations</c> list that keeps the machine-readable codes.</summary>
    public static ProblemHttpResult InvalidSubmission(ValidationResult result) => Unprocessable(
        "The submission is invalid.",
        result.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()),
        new Dictionary<string, object?> { ["violations"] = result.Errors });

    private static ProblemHttpResult Unprocessable(
        string title, Dictionary<string, string[]> errors, Dictionary<string, object?>? extensions = null)
    {
        var problem = new HttpValidationProblemDetails(errors)
        {
            Title = title,
            Status = StatusCodes.Status422UnprocessableEntity,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        };
        foreach (var (name, value) in extensions ?? [])
        {
            problem.Extensions[name] = value;
        }

        return TypedResults.Problem(problem);
    }

    public static ProblemHttpResult BreakingChanges(CompatibilityReport report) => Conflict(
        "The draft contains breaking changes. Publish again with acceptBreakingChanges=true to confirm.",
        new Dictionary<string, object?> { ["compatibility"] = report });
}
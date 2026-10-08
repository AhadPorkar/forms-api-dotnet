using System.Text.Json.Nodes;

namespace Forms.Core.Validation;

/// <param name="Field">The field key, or <c>$</c> for the submission as a whole.</param>
/// <param name="Code">A stable machine-readable code such as <c>required</c> or <c>maxLength</c>.</param>
/// <param name="Message">An English message that a client may show or replace with its own translation.</param>
public sealed record ValidationError(string Field, string Code, string Message);

/// <param name="Errors">Every problem found. Validation does not stop at the first error.</param>
/// <param name="Data">
/// The cleaned submission: only visible fields that have a value, with strings trimmed, numbers without
/// trailing zeros and dates in ISO format. Fields are in schema order here; PostgreSQL <c>jsonb</c> stores
/// keys in its own order, so a stored submission reads back with the same content but not the same order.
/// Meaningful only when <see cref="IsValid"/> is true.
/// </param>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors, JsonObject Data)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ErrorCodes
{
    public const string NotAnObject = "notAnObject";
    public const string InvalidJson = "invalidJson";
    public const string UnknownField = "unknownField";
    public const string Type = "type";
    public const string Required = "required";
    public const string MinLength = "minLength";
    public const string MaxLength = "maxLength";
    public const string Pattern = "pattern";
    public const string Email = "email";
    public const string Min = "min";
    public const string Max = "max";
    public const string MinDate = "minDate";
    public const string MaxDate = "maxDate";
    public const string Option = "option";
    public const string DuplicateItem = "duplicateItem";
    public const string MinItems = "minItems";
    public const string MaxItems = "maxItems";
    public const string Rule = "rule";
}
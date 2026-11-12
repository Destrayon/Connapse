namespace Connapse.Core;

/// <summary>Which table tool a saved query runs as (CON-311).</summary>
public enum SavedQueryKind
{
    Read,
    Aggregate,
}

/// <summary>The type an argument of a saved query must have (CON-311). It is checked before the query runs.</summary>
public enum SavedQueryParameterType
{
    Text,
    WholeNumber,
    Number,
    Boolean,
    Date,
    Timestamp,
}

/// <summary>A value of the caller's login a saved query's parameter can be filled from, never from the request.</summary>
public enum SavedQueryLoginField
{
    /// <summary>The user the request is for; for an agent's key, the user the agent acts for.</summary>
    UserId,

    /// <summary>The signed-in user's email address.</summary>
    Email,
}

/// <summary>
/// One typed input of a saved query. <see cref="Default"/> is the raw JSON value used when the caller leaves the
/// argument out; without one the argument is required. A parameter with <see cref="FromLogin"/> is filled from the
/// caller's login and can't be sent.
/// </summary>
public sealed record SavedQueryParameter(
    string Name,
    SavedQueryParameterType Type,
    string? Description = null,
    string? Default = null,
    SavedQueryLoginField? FromLogin = null);

/// <summary>
/// What an admin saves (CON-311): a name agents call it by, a description agents pick it by, the kind of table request
/// and its JSON (as <c>table_read</c> or <c>table_aggregate</c> take it, with <c>{"param": "&lt;name&gt;"}</c> where a
/// filter value is filled from a parameter), and the parameters.
/// </summary>
public sealed record SavedQueryDefinition(
    string Name,
    string Description,
    SavedQueryKind Kind,
    string Request,
    IReadOnlyList<SavedQueryParameter> Parameters);

/// <summary>A saved query of one container, with who saved it and when.</summary>
public sealed record SavedQuery(Guid ContainerId, SavedQueryDefinition Definition, string SavedBy, DateTime SavedAt)
{
    public string Name => Definition.Name;
}

/// <summary>The caller's login values a saved query can be filled from; null where the login has none.</summary>
public sealed record SavedQueryLogin(string? UserId, string? Email)
{
    public string? Value(SavedQueryLoginField field) => field switch
    {
        SavedQueryLoginField.UserId => UserId,
        SavedQueryLoginField.Email => Email,
        _ => null,
    };
}

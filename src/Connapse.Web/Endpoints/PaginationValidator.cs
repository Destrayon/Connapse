namespace Connapse.Web.Endpoints;

public static class PaginationValidator
{
    public const int MinTake = 1;
    public const int MaxTake = 200;

    /// <summary>
    /// Validates skip and take pagination parameters.
    /// Returns an error result if invalid, or null if valid.
    /// </summary>
    public static IResult? Validate(int skip, int take)
    {
        if (skip < 0)
            return Results.BadRequest(new { error = "skip must be >= 0" });

        if (take < MinTake)
            return Results.BadRequest(new { error = $"take must be >= {MinTake}" });

        if (take > MaxTake)
            return Results.BadRequest(new { error = $"take must be <= {MaxTake}" });

        return null;
    }
}

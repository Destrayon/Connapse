namespace Connapse.CustomSource.Example;

public sealed record AuthZenSubject(string? Type, string? Id);

public sealed record AuthZenAction(string? Name);

public sealed record ResourceProperties(string? Scope);

public sealed record AuthZenResource(string? Type, string? Id, ResourceProperties? Properties);

public sealed record AuthZenPageRequest(string? Token, int? Limit);

public sealed record ResourceSearchRequest(
    AuthZenSubject? Subject,
    AuthZenAction? Action,
    AuthZenResource? Resource,
    AuthZenPageRequest? Page);

public sealed record ResourceRef(string Type, string Id);

public sealed record AuthZenPageResponse(string NextToken);

public sealed record ResourceSearchResponse(IReadOnlyList<ResourceRef> Results, AuthZenPageResponse Page);

public sealed record EvaluationItem(AuthZenResource? Resource);

public sealed record EvaluationsRequest(AuthZenSubject? Subject, AuthZenAction? Action, IReadOnlyList<EvaluationItem>? Evaluations);

public sealed record DecisionItem(bool Decision);

public sealed record EvaluationsResponse(IReadOnlyList<DecisionItem> Evaluations);

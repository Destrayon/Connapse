using System.Reflection;
using Connapse.Application;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Which operation a route runs: the [Operation] class its handler takes as a parameter. A handler
/// that takes several runs the riskiest; the others only read, to explain a failure (a delete that
/// lists the sources still using a connection). When two share the top risk, the route runs none
/// the spec can name, so it isn't linked.
/// </summary>
public static class EndpointOperations
{
    public static OperationAttribute? Find(IEnumerable<object> endpointMetadata)
    {
        if (endpointMetadata.OfType<MethodInfo>().FirstOrDefault() is not { } handler)
            return null;
        var operations = handler.GetParameters()
            .Select(p => p.ParameterType.GetCustomAttribute<OperationAttribute>())
            .OfType<OperationAttribute>()
            .DistinctBy(o => o.Id)
            .OrderByDescending(o => o.Risk)
            .ToList();
        return operations switch
        {
            [] => null,
            [var only] => only,
            [var top, var next, ..] => top.Risk == next.Risk ? null : top,
        };
    }
}

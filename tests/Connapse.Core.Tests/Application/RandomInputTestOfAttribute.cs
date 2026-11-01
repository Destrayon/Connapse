namespace Connapse.Core.Tests.Application;

/// <summary>
/// Names the operation a random-input test checks the rules of. EveryOperation_HasARandomInputTest
/// requires one for every [Operation] class, so a new operation can't ship with only hand-picked cases.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RandomInputTestOfAttribute(Type operation) : Attribute
{
    public Type Operation { get; } = operation;
}

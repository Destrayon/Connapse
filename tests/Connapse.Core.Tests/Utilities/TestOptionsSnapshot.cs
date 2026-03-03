using Microsoft.Extensions.Options;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Minimal IOptionsSnapshot implementation for unit tests.
/// Use <c>new TestOptionsSnapshot&lt;T&gt;(value)</c> as a drop-in replacement
/// for <c>Options.Create(value)</c> when the target type requires IOptionsSnapshot.
/// </summary>
internal sealed class TestOptionsSnapshot<T>(T value) : IOptionsSnapshot<T> where T : class
{
    public T Value { get; } = value;
    public T Get(string? name) => Value;
}

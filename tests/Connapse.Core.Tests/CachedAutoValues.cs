using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NSubstitute.Core;
using NSubstitute.Core.DependencyInjection;
using NSubstitute.Routing.AutoValues;

namespace Connapse.Core.Tests;

/// <summary>
/// NSubstitute's default auto-value providers, in the same order, with each provider's answer to
/// "can you provide a value for this type?" remembered per type. Unconfigured calls, and every call a
/// test makes while configuring one, ask that question again, and the substitute provider answers it by
/// reflecting over the type's methods each time — a large share of the random-input tests' CPU.
/// The answer depends only on the type, so remembering it changes no value a substitute returns.
/// </summary>
internal static class CachedAutoValues
{
    [ModuleInitializer]
    internal static void Install() =>
        SubstitutionContext.Current = NSubstituteDefaultFactory.DefaultContainer.Customize()
            .Register<IAutoValueProvidersFactory, Factory>(NSubLifetime.Singleton)
            .CreateScope()
            .Resolve<ISubstitutionContext>();

    private sealed class Factory : IAutoValueProvidersFactory
    {
        public IReadOnlyCollection<IAutoValueProvider> CreateProviders(ISubstituteFactory substituteFactory)
        {
            IAutoValueProvider[]? result = null;
            var lazyResult = new Lazy<IReadOnlyCollection<IAutoValueProvider>>(
                () => result ?? throw new InvalidOperationException("Value was not constructed yet."),
                LazyThreadSafetyMode.PublicationOnly);
            result =
            [
                new Cached(new AutoObservableProvider(lazyResult), Answers[0]),
                new Cached(new AutoQueryableProvider(), Answers[1]),
                new Cached(new AutoSubstituteProvider(substituteFactory), Answers[2]),
                new Cached(new AutoStringProvider(), Answers[3]),
                new Cached(new AutoArrayProvider(), Answers[4]),
                new Cached(new AutoTaskProvider(lazyResult), Answers[5]),
            ];
            return result;
        }
    }

    // NSubstitute builds a new list of providers for every substitute, so the answers are kept per kind of provider.
    private static readonly ConcurrentDictionary<Type, bool>[] Answers = [.. Enumerable.Range(0, 6).Select(_ => new ConcurrentDictionary<Type, bool>())];

    private sealed class Cached(IAutoValueProvider inner, ConcurrentDictionary<Type, bool> answers) : IAutoValueProvider
    {
        public bool CanProvideValueFor(Type type) => answers.GetOrAdd(type, inner.CanProvideValueFor);

        public object? GetValue(Type type) => inner.GetValue(type);
    }
}

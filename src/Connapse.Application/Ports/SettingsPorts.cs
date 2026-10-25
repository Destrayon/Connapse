namespace Connapse.Application.Ports;

/// <summary>
/// What a settings category is when nothing is stored for it: the value configuration (appsettings,
/// environment variables) gives it. Web answers it from the options it registered.
/// </summary>
public interface IConfiguredSettings
{
    T Current<T>() where T : class, new();
}

/// <summary>
/// Rebuilds the vector indexes for the configured embedding model, in the background: changing the model
/// needs its index, and building one must not hold up the change.
/// </summary>
public interface IVectorIndexReconciler
{
    void Schedule();
}

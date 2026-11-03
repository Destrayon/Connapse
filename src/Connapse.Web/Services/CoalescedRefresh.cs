namespace Connapse.Web.Services;

/// <summary>
/// Runs a read at most one at a time. Calls that arrive while a read is in flight collapse into one more
/// read after it, so a burst of triggers costs two reads and the last snapshot taken is the one kept.
/// Not thread-safe: call it from one synchronization context, such as a component's renderer.
/// </summary>
public sealed class CoalescedRefresh(Func<Task> read)
{
    private bool _running;
    private bool _stale;

    public async Task RunAsync()
    {
        _stale = true;
        if (_running)
            return;
        _running = true;
        try
        {
            while (_stale)
            {
                _stale = false;
                await read();
            }
        }
        finally
        {
            _running = false;
        }
    }
}

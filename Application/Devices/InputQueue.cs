using System.Collections.Concurrent;
using System.Diagnostics;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Hands input that arrives on other threads (Companion's HTTP listener) to the input loop, so it
/// runs sequentially with SDL input and a camera never gets commands from two threads at once.
/// </summary>
public sealed class InputQueue
{
    private readonly ConcurrentQueue<Action> pending = new();

    /// <summary>
    /// Guards every controller source's collections (<c>Gamepads</c>, <c>ActiveGamepads</c>) and the
    /// list that merges them. The input loop changes them, and so does activation from the UI thread;
    /// one lock shared by every source lets the merged list read all of them at once without a
    /// lock-order deadlock.
    /// </summary>
    public object ControllersLock { get; } = new();

    /// <summary>Queues <paramref name="input"/> to run on the input loop. Safe from any thread; never blocks.</summary>
    public void Enqueue(Action input) => pending.Enqueue(input);

    /// <summary>
    /// Runs every queued input in arrival order. Called by the input loop only. An input that
    /// throws is logged and skipped; the rest still run and nothing is thrown.
    /// </summary>
    public void RunPending()
    {
        while (pending.TryDequeue(out var input))
        {
            try
            {
                input();
            }
            catch (Exception e)
            {
                Trace.TraceError($"Queued input failed and was skipped: {e}");
            }
        }
    }
}

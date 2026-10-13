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

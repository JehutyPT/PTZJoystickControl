using Avalonia.Threading;
using System;
using System.Diagnostics;

namespace PtzJoystickControl.Gui;

/// <summary>
/// Raises UI-bound changes made on other threads (the input loop) on the UI thread (live-safety §4).
/// Until Avalonia is set up (<see cref="MarkReady"/>), work runs on the calling thread: no window or
/// binding exists yet, and touching <see cref="Dispatcher.UIThread"/> that early is not safe.
/// </summary>
internal static class UiThread
{
    private static volatile bool s_ready;

    /// <summary>Called on the UI thread once Avalonia is set up, before any window is created.</summary>
    internal static void MarkReady() => s_ready = true;

    /// <summary>Runs <paramref name="action"/> on the UI thread: at once if already on it, else queued. Never throws.</summary>
    internal static void Post(Action action)
    {
        if (!s_ready || Dispatcher.UIThread.CheckAccess())
            RunLogged(action);
        else
            Dispatcher.UIThread.Post(() => RunLogged(action));
    }

    // A failing subscriber is logged: on the UI thread it would otherwise take down the app.
    private static void RunLogged(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Trace.TraceError($"A UI update failed: {e}");
        }
    }
}

namespace PtzJoystickControl.Application;

/// <summary>
/// The named mutex that keeps a second copy of the app from starting while one is running.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// No '/' (.NET on macOS and Linux rejects it with an IOException) and no '\' (a namespace
    /// separator on Windows), so the same name works on every OS. Without a <c>Global\</c> prefix
    /// the mutex is per user session, as before.
    /// </summary>
    /// <remarks>
    /// Builds before #29 used <c>PTZJoystickControlMutex/BFD0A32E-...</c>, so an old build and a
    /// new build do not see each other.
    /// </remarks>
    public const string MutexName = "PTZJoystickControlMutex_BFD0A32E-F433-49E7-AB74-B49FC95012D0";

    /// <summary>
    /// Opens (or creates) the named mutex without acquiring it; the caller tries
    /// <see cref="WaitHandle.WaitOne(int, bool)"/> with a zero timeout to claim the single instance.
    /// </summary>
    public static Mutex CreateMutex(string name = MutexName) => new(false, name);
}

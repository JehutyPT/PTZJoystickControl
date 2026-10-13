using System.Diagnostics;

namespace PtzJoystickControl.Application.Services;

/// <summary>
/// Outbound HTTP calls from the app to Bitfocus Companion.
/// Every call is bounded by <see cref="Timeout"/> and never throws: failures are
/// logged through <see cref="Trace"/> and dropped, so Companion being down, slow or
/// returning errors cannot affect joystick → camera control (see docs/live-safety.md).
/// </summary>
public class BitfocusCompanionClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _client;

    public BitfocusCompanionClient(Uri baseAddress)
        : this(new HttpClientHandler(), baseAddress, DefaultTimeout) { }

    public BitfocusCompanionClient(HttpMessageHandler handler, Uri baseAddress)
        : this(handler, baseAddress, DefaultTimeout) { }

    public BitfocusCompanionClient(HttpMessageHandler handler, Uri baseAddress, TimeSpan timeout)
    {
        _client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout };
    }

    public TimeSpan Timeout => _client.Timeout;

    /// <summary>
    /// Presses a Companion button in the background. Returns immediately, does no
    /// network I/O on the caller's thread and never throws.
    /// </summary>
    public void PressButton(int? pageNumber, int? buttonNumber) =>
        RunInBackground(() => PressButtonAsync(pageNumber, buttonNumber));

    /// <summary>
    /// Sets a Companion custom variable in the background. Returns immediately, does
    /// no network I/O on the caller's thread and never throws.
    /// </summary>
    public void SetCustomVariable(string variableName, string value) =>
        RunInBackground(() => SetCustomVariableAsync(variableName, value));

    /// <summary>
    /// Presses a Companion button. Sends nothing when either number is null.
    /// The returned task always completes successfully.
    /// </summary>
    public async Task PressButtonAsync(int? pageNumber, int? buttonNumber)
    {
        if (pageNumber == null || buttonNumber == null)
            return;

        await SendAsync($"press/bank/{pageNumber}/{buttonNumber}");
    }

    /// <summary>
    /// Sets a Companion custom variable. The returned task always completes successfully.
    /// </summary>
    public Task SetCustomVariableAsync(string variableName, string value) =>
        SendAsync($"set/custom-variable/{variableName}?value={value}");

    private static void RunInBackground(Func<Task> send)
    {
        // Task.Run keeps a transport that blocks synchronously off the caller's thread;
        // HttpClient.Timeout alone cannot interrupt that.
        _ = Task.Run(send);
    }

    private async Task SendAsync(string path)
    {
        try
        {
            using var response = await _client.GetAsync(path);
            if (!response.IsSuccessStatusCode)
                TraceFailure(path, $"Companion returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            TraceFailure(path, $"timed out after {_client.Timeout.TotalMilliseconds} ms");
        }
        catch (Exception e)
        {
            TraceFailure(path, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private void TraceFailure(string path, string reason)
    {
        Trace.TraceWarning($"Companion request {_client.BaseAddress}{path} failed: {reason}");
    }
}

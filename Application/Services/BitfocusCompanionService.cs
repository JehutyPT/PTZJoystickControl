using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Services
{
    public class BitfocusCompanionService : IBitfocusCompanionService
    {
        
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand;

        private readonly ICamerasService _cameras;
        private readonly ISpeedProfilesService _speedProfiles;

        /// <summary>
        /// Starts the HTTP listener that receives Companion's camera selection and speed profile
        /// requests. The app makes no calls to Companion: Companion owns camera selection.
        /// </summary>
        /// <param name="settings">The localhost port the app listens on for Companion's requests</param>
        /// <param name="cameras">The cameras an inbound request selects by Companion number</param>
        /// <param name="speedProfiles">The speed profiles an inbound request steps or reads back</param>
        public BitfocusCompanionService(CompanionSettings settings, ICamerasService cameras, ISpeedProfilesService speedProfiles)
            : this(settings, cameras, speedProfiles, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
        {
        }

        internal BitfocusCompanionService(
            CompanionSettings settings, ICamerasService cameras, ISpeedProfilesService speedProfiles,
            TimeSpan retryInitialDelay, TimeSpan retryMaxDelay)
        {
            _cameras = cameras;
            _speedProfiles = speedProfiles;
            _prefix = $"http://localhost:{settings.ListenerPort}/";
            _retryInitialDelay = retryInitialDelay;
            _retryMaxDelay = retryMaxDelay;

            // A taken port or a denied URL ACL must not stop the app starting: joystick control
            // keeps working, only Companion → app requests are lost until a
            // background retry gets the listener started.
            if (!TryStartListener(out var error))
            {
                InboundListenerError = $"Could not listen on {_prefix}: {error!.Message}";
                Trace.TraceWarning($"Companion listener: failed to start on {_prefix}, Companion → app control is off until it starts; retrying in the background: {error}");
                _ = Task.Run(() => RetryUntilListening(_stopping.Token));
            }
        }
        #region Inbound
        private readonly string _prefix;
        private readonly TimeSpan _retryInitialDelay;
        private readonly TimeSpan _retryMaxDelay;
        private readonly CancellationTokenSource _stopping = new();
        // Guards _listener and _stopped, so a retry that starts a listener can never race StopListening.
        private readonly object _listenerLock = new();
        private HttpListener? _listener;
        private bool _stopped;
        private volatile string? _inboundListenerError;

        public string? InboundListenerError
        {
            get => _inboundListenerError;
            private set => _inboundListenerError = value;
        }

        public event EventHandler? InboundListenerErrorChanged;

        /// <summary>
        /// The wait before retry <paramref name="attempt"/> (1-based): <paramref name="initial"/>, doubling per attempt, capped at <paramref name="max"/>.
        /// </summary>
        internal static TimeSpan RetryDelay(int attempt, TimeSpan initial, TimeSpan max)
        {
            var doublings = Math.Clamp(attempt - 1, 0, 30);
            var ticks = initial.Ticks * Math.Pow(2, doublings);
            return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
        }

        /// <summary>
        /// Failed retries are logged at attempts 1, 2, 4, 8, ... so a port held for hours writes a handful of lines, not one per attempt.
        /// </summary>
        internal static bool ShouldLogRetryFailure(int attempt) => attempt > 0 && (attempt & (attempt - 1)) == 0;

        /// <summary>
        /// Stops accepting requests from Companion and cancels any pending start retry. A pending accept ends silently.
        /// </summary>
        public void StopListening()
        {
            HttpListener? listener;
            lock (_listenerLock)
            {
                _stopped = true;
                listener = _listener;
                _listener = null;
            }

            try
            {
                _stopping.Cancel();
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Companion listener: failed to cancel the start retry: {e}");
            }

            try
            {
                listener?.Close();
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Companion listener: failed to stop: {e}");
            }
        }

        private bool IsStopped
        {
            get
            {
                lock (_listenerLock)
                    return _stopped;
            }
        }

        // Starts a fresh listener (a listener whose Start failed cannot be reused). Never throws.
        private bool TryStartListener(out Exception? error)
        {
            var listener = new HttpListener();
            try
            {
                lock (_listenerLock)
                {
                    if (_stopped)
                    {
                        error = new OperationCanceledException("The Companion listener was stopped.");
                        listener.Close();
                        return false;
                    }

                    listener.Prefixes.Add(_prefix);
                    listener.Start();
                    _listener = listener;
                }
                listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                try
                {
                    listener.Close();
                }
                catch (Exception closeError)
                {
                    Trace.TraceWarning($"Companion listener: failed to close the listener that did not start: {closeError.Message}");
                }
                error = e;
                return false;
            }
        }

        // Runs on a thread-pool thread until the listener starts or StopListening is called; nothing may escape it.
        private async Task RetryUntilListening(CancellationToken stopping)
        {
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    var delay = RetryDelay(attempt, _retryInitialDelay, _retryMaxDelay);
                    await Task.Delay(delay, stopping).ConfigureAwait(false);

                    if (TryStartListener(out var error))
                    {
                        // StopListening may have closed the new listener already; the app is leaving, so stay quiet.
                        if (stopping.IsCancellationRequested)
                            return;

                        InboundListenerError = null;
                        Trace.TraceInformation($"Companion listener: listening on {_prefix} on retry {attempt}; Companion → app control is on.");
                        RaiseInboundListenerErrorChanged();
                        return;
                    }

                    if (stopping.IsCancellationRequested)
                        return;

                    if (ShouldLogRetryFailure(attempt))
                        Trace.TraceWarning($"Companion listener: still cannot start on {_prefix} on retry {attempt}, next retry in {RetryDelay(attempt + 1, _retryInitialDelay, _retryMaxDelay).TotalSeconds:0.#} s: {error!.Message}");
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion listener: start retry failed, Companion → app control stays off: {e}");
            }
        }

        private void RaiseInboundListenerErrorChanged()
        {
            try
            {
                InboundListenerErrorChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion listener: a listener-state subscriber failed: {e}");
            }
        }

        // Runs on a thread-pool thread: nothing may escape it, or the process terminates.
        private void ListenerCallback(IAsyncResult ar)
        {
            var listener = (HttpListener)ar.AsyncState!;

            try
            {
                HttpListenerContext context;
                try
                {
                    context = listener.EndGetContext(ar);
                }
                catch (Exception) when (IsStopped)
                {
                    // StopListening closed the listener under the pending accept: a deliberate stop, not a failure.
                    return;
                }
                catch (Exception e)
                {
                    Trace.TraceWarning($"Companion listener: failed to accept request: {e.Message}");
                    return;
                }

                HandleRequest(context);
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion listener: unexpected failure: {e}");
            }
            finally
            {
                ListenForNextRequest(listener);
            }
        }

        private void ListenForNextRequest(HttpListener listener)
        {
            try
            {
                if (listener.IsListening)
                    listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
            }
            catch (Exception) when (IsStopped)
            {
                // StopListening closed the listener between the check and the call.
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion listener: failed to listen for the next request: {e.Message}");
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var response = context.Response;
            string? rawQuery = null;

            try
            {
                var query = context.Request.QueryString;
                rawQuery = context.Request.Url?.Query;

                // The indexer joins duplicate parameters ("1,2"), so they are rejected rather than both run.
                var parameters = query.AllKeys
                    .Select(name => new KeyValuePair<string?, string?>(name, query[name]))
                    .ToList();
                // A snapshot: the camera list is changed on the UI thread. If a copy races such a
                // change and throws, the request gets 500 and Companion can simply press again.
                var cameras = _cameras.Cameras.ToArray();
                var parsed = BitfocusCompanionEvent.ParseRequest(parameters, cameras);

                if (!parsed.IsAccepted)
                {
                    Trace.TraceWarning($"Companion listener: rejected request '{rawQuery}': {parsed.RejectionReason}");
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                // Camera selections first: they are only queued, so a slow save below never delays them.
                foreach (var companionEvent in parsed.Events)
                    RaiseCompanionEvent(companionEvent);

                // A step is applied here, on the listener thread, never queued on the input loop: it saves
                // SpeedProfiles.json before it returns, and a slow disk must never delay joystick control.
                switch (parsed.SpeedProfile)
                {
                    case CompanionSpeedProfileRequest.Faster:
                        _speedProfiles.StepFaster();
                        break;
                    case CompanionSpeedProfileRequest.Finer:
                        _speedProfiles.StepFiner();
                        break;
                }

                response.StatusCode = (int)HttpStatusCode.OK;

                if (parsed.SpeedProfile == CompanionSpeedProfileRequest.Current)
                    WriteActiveSpeedProfile(response);
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion listener: failed to handle request '{rawQuery}': {e}");
                TrySetStatus(response, HttpStatusCode.InternalServerError);
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch (Exception e)
                {
                    Trace.TraceWarning($"Companion listener: failed to close response for '{rawQuery}': {e.Message}");
                }
            }
        }

        // The body Companion reads into a variable: {"position":2,"count":4,"name":"Medium"}.
        private void WriteActiveSpeedProfile(HttpListenerResponse response)
        {
            var profiles = _speedProfiles.Profiles;
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                position = profiles.ActivePosition,
                count = profiles.Count,
                name = profiles.Active.Name,
            });

            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }

        private static void TrySetStatus(HttpListenerResponse response, HttpStatusCode status)
        {
            try
            {
                response.StatusCode = (int)status;
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Companion listener: failed to set response status {(int)status}: {e.Message}");
            }
        }

        private void RaiseCompanionEvent(BitfocusCompanionEvent bitfocusCompanionEvent)
        {
            ReceivedBitfocusCommand?.Invoke(this, bitfocusCompanionEvent);
        }
        #endregion
    }
}

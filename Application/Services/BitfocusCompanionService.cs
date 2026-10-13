using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Services
{
    public class BitfocusCompanionService : IBitfocusCompanionService
    {
        
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand;

        private readonly HttpClient _client;
        
        /// <summary>
        /// Initializes http clients to communicate to and from a bitfocus companion instance
        /// </summary>
        /// <param name="outboundPort">The port that the target bitfocus companion is set to listen to for HTTP requests</param>
        /// <param name="inboundPort">The port that the HTTP connection module in companion is pointed to for sending HTTP requests into the PTZ Joystick app</param>
        public BitfocusCompanionService(int outboundPort, int inboundPort)
        {
            _client = new HttpClient();
            _client.BaseAddress = new Uri($"http://localhost:{outboundPort}/");

            // Create a listener.
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{inboundPort}/");
            _listener = listener;

            listener.Start();

            listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
        }
        #region Outbound

        public async void PressButton(int? pageNumber, int? buttonNumber)
        {
            if (pageNumber == null || buttonNumber == null)
                return;

            var reply = await _client.GetAsync($"press/bank/{pageNumber}/{buttonNumber}");
        }

        public async void SetCustomVariable(string variableName, string value)
        {
            var _ = _client.GetAsync($"set/custom-variable/{variableName}?value={value}");
        }

        public List<BitfocusCompanionEvent> SupportedEvents => BitfocusCompanionEvent.SupportedEvents;

        #endregion

        #region Inbound
        private readonly HttpListener _listener;

        /// <summary>
        /// Stops accepting requests from Companion. A pending accept completes and is logged.
        /// </summary>
        public void StopListening()
        {
            try
            {
                _listener.Close();
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Companion listener: failed to stop: {e}");
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
                catch (Exception e)
                {
                    Trace.WriteLine($"Companion listener: failed to accept request: {e.Message}");
                    return;
                }

                HandleRequest(context);
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Companion listener: unexpected failure: {e}");
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
            catch (Exception e)
            {
                Trace.WriteLine($"Companion listener: failed to listen for the next request: {e.Message}");
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
                var parsed = BitfocusCompanionEvent.ParseRequest(parameters);

                if (!parsed.IsAccepted)
                {
                    Trace.WriteLine($"Companion listener: rejected request '{rawQuery}': {parsed.RejectionReason}");
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                foreach (var companionEvent in parsed.Events)
                    RaiseCompanionEvent(companionEvent);

                response.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Companion listener: failed to handle request '{rawQuery}': {e}");
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
                    Trace.WriteLine($"Companion listener: failed to close response for '{rawQuery}': {e.Message}");
                }
            }
        }

        private static void TrySetStatus(HttpListenerResponse response, HttpStatusCode status)
        {
            try
            {
                response.StatusCode = (int)status;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Companion listener: failed to set response status {(int)status}: {e.Message}");
            }
        }

        private void RaiseCompanionEvent(BitfocusCompanionEvent bitfocusCompanionEvent)
        {
            ReceivedBitfocusCommand?.Invoke(this, bitfocusCompanionEvent);
        }
        #endregion
    }
}

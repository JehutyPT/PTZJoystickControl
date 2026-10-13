using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private void ListenerCallback(IAsyncResult ar)
        {
            var listener = (HttpListener)ar.AsyncState!;

            var context = listener.EndGetContext(ar);
            var request = context.Request;

            var response = context.Response;

            try
            {
                var parameters = request.QueryString.AllKeys
                    .Select(name => new KeyValuePair<string?, string?>(name, request.QueryString[name]));
                foreach (var companionEvent in BitfocusCompanionEvent.ParseRequest(parameters).Events)
                    RaiseCompanionEvent(companionEvent);

                response.StatusCode = 200;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                throw;
            }
            finally
            {
                listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
            }
        }

        private void RaiseCompanionEvent(BitfocusCompanionEvent bitfocusCompanionEvent)
        {
            ReceivedBitfocusCommand?.Invoke(this, bitfocusCompanionEvent);
        }
        #endregion
    }
}

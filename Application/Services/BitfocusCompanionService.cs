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

        private readonly BitfocusCompanionClient _outbound;
        
        /// <summary>
        /// Initializes http clients to communicate to and from a bitfocus companion instance
        /// </summary>
        /// <param name="outboundPort">The port that the target bitfocus companion is set to listen to for HTTP requests</param>
        /// <param name="inboundPort">The port that the HTTP connection module in companion is pointed to for sending HTTP requests into the PTZ Joystick app</param>
        public BitfocusCompanionService(int outboundPort, int inboundPort)
        {
            _outbound = new BitfocusCompanionClient(new Uri($"http://localhost:{outboundPort}/"));

            // Create a listener.
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{inboundPort}/");

            listener.Start();

            listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
        }
        #region Outbound

        public void PressButton(int? pageNumber, int? buttonNumber) =>
            _outbound.PressButton(pageNumber, buttonNumber);

        public void SetCustomVariable(string variableName, string value) =>
            _outbound.SetCustomVariable(variableName, value);

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
                foreach (var queryParam in request.QueryString.AllKeys)
                {
                    var companionEvent =
                        BitfocusCompanionEvent.FindMatchingEvent(queryParam!, request.QueryString[queryParam]);
                    if(companionEvent != null)
                    {
                        RaiseCompanionEvent(companionEvent);
                    }
                        
                }

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

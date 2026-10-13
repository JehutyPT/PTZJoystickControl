using System;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Core.Services
{
    public interface IBitfocusCompanionService
    {
        /// <summary>
        /// Raised on the HTTP listener thread for each validated command of a Companion request.
        /// Subscribers must queue the work for the input loop, never run controller input here.
        /// </summary>
        event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand;

        /// <summary>
        /// Why Companion → app requests are not being received, or null while the inbound listener is running.
        /// </summary>
        public string? InboundListenerError { get; }

        /// <summary>
        /// Raised, on a background thread, when <see cref="InboundListenerError"/> changes (for example, once a retry starts the listener).
        /// </summary>
        event EventHandler? InboundListenerErrorChanged;
    }
}

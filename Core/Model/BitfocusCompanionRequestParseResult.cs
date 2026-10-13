using System;
using System.Collections.Generic;

namespace PtzJoystickControl.Core.Model
{
    /// <summary>
    /// Outcome of validating an inbound Companion request: either every event it asks for,
    /// or the reason it was rejected (in which case nothing may be executed).
    /// </summary>
    public sealed class BitfocusCompanionRequestParseResult
    {
        private BitfocusCompanionRequestParseResult(IReadOnlyList<BitfocusCompanionEvent> events, string? rejectionReason)
        {
            Events = events;
            RejectionReason = rejectionReason;
        }

        public bool IsAccepted => RejectionReason == null;

        public IReadOnlyList<BitfocusCompanionEvent> Events { get; }

        public string? RejectionReason { get; }

        public static BitfocusCompanionRequestParseResult Accepted(IReadOnlyList<BitfocusCompanionEvent> events) =>
            new(events, null);

        public static BitfocusCompanionRequestParseResult Rejected(string reason) =>
            new(Array.Empty<BitfocusCompanionEvent>(), reason);
    }
}

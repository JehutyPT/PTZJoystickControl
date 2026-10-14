using System;
using System.Collections.Generic;

namespace PtzJoystickControl.Core.Model
{
    /// <summary>
    /// Outcome of validating an inbound Companion request: either every event and the speed profile
    /// request it asks for, or the reason it was rejected (in which case nothing may be executed).
    /// </summary>
    public sealed class BitfocusCompanionRequestParseResult
    {
        private BitfocusCompanionRequestParseResult(
            IReadOnlyList<BitfocusCompanionEvent> events, CompanionSpeedProfileRequest? speedProfileRequest, string? rejectionReason)
        {
            Events = events;
            SpeedProfileRequest = speedProfileRequest;
            RejectionReason = rejectionReason;
        }

        public bool IsAccepted => RejectionReason == null;

        public IReadOnlyList<BitfocusCompanionEvent> Events { get; }

        /// <summary>The request's <c>SpeedProfile</c> command, or null when it has none.</summary>
        public CompanionSpeedProfileRequest? SpeedProfileRequest { get; }

        public string? RejectionReason { get; }

        public static BitfocusCompanionRequestParseResult Accepted(
            IReadOnlyList<BitfocusCompanionEvent> events, CompanionSpeedProfileRequest? speedProfileRequest) =>
            new(events, speedProfileRequest, null);

        public static BitfocusCompanionRequestParseResult Rejected(string reason) =>
            new(Array.Empty<BitfocusCompanionEvent>(), null, reason);
    }
}

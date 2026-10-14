using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.Model
{
    public enum InboundBitfocusCompanionEventEnum
    {
        /// <summary>Legacy alias of <see cref="SelectCamera"/>, kept for existing Companion buttons.</summary>
        SetCameraPreview,
        SelectCamera,
    }

    /// <summary>
    /// One validated inbound Companion command: select <see cref="Camera"/>, the camera whose
    /// Companion number the request named, on every active controller.
    /// </summary>
    public sealed class BitfocusCompanionEvent
    {
        private static readonly Dictionary<string, InboundBitfocusCompanionEventEnum> Commands =
            Enum.GetValues<InboundBitfocusCompanionEventEnum>().ToDictionary(e => e.ToString(), StringComparer.Ordinal);

        private const string SpeedProfileCommand = "SpeedProfile";

        private static readonly Dictionary<string, CompanionSpeedProfileRequest> SpeedProfileRequests = new(StringComparer.Ordinal)
        {
            ["faster"] = CompanionSpeedProfileRequest.Faster,
            ["finer"] = CompanionSpeedProfileRequest.Finer,
            ["current"] = CompanionSpeedProfileRequest.Current,
        };

        public BitfocusCompanionEvent(InboundBitfocusCompanionEventEnum eventType, int companionNumber, ICamera camera)
        {
            EventType = eventType;
            CompanionNumber = companionNumber;
            Camera = camera;
        }

        public InboundBitfocusCompanionEventEnum EventType { get; }

        public int CompanionNumber { get; }

        public ICamera Camera { get; }

        /// <summary>
        /// Validates every parameter of an inbound request before anything is executed.
        /// A parameter matches only a defined command by exact, case-sensitive name. A camera
        /// command's value must be a plain positive decimal number that exactly one of
        /// <paramref name="cameras"/> has as its Companion number. <c>SpeedProfile</c>'s value must be
        /// exactly <c>faster</c>, <c>finer</c> or <c>current</c>; it may appear once, and
        /// <c>current</c> only on its own. Returns one event per camera command and the speed profile
        /// request, or a rejection when the request is empty or any parameter does not match.
        /// </summary>
        public static BitfocusCompanionRequestParseResult ParseRequest(
            IEnumerable<KeyValuePair<string?, string?>> parameters, IReadOnlyCollection<ICamera> cameras)
        {
            var events = new List<BitfocusCompanionEvent>();
            CompanionSpeedProfileRequest? speedProfile = null;

            foreach (var (name, value) in parameters)
            {
                if (name == null)
                    return BitfocusCompanionRequestParseResult.Rejected($"Parameter without a name (value '{value}')");

                if (string.Equals(name, SpeedProfileCommand, StringComparison.Ordinal))
                {
                    if (speedProfile != null)
                        return BitfocusCompanionRequestParseResult.Rejected($"Command '{name}' given more than once");
                    // The query string joins a repeated parameter ("faster,finer"), so it never matches.
                    if (value == null || !SpeedProfileRequests.TryGetValue(value, out var request))
                        return BitfocusCompanionRequestParseResult.Rejected($"'{value}' for command '{name}' is not faster, finer or current");

                    speedProfile = request;
                    continue;
                }

                // Ordinal name lookup, not Enum.Parse/TryParse: those accept numbers and other casings.
                if (!Commands.TryGetValue(name, out var eventType))
                    return BitfocusCompanionRequestParseResult.Rejected($"Unknown command '{name}'");

                // NumberStyles.None: digits only, no sign, spaces or separators.
                if (value == null
                    || !value.All(char.IsAsciiDigit)
                    || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number < 1)
                    return BitfocusCompanionRequestParseResult.Rejected($"'{value}' for command '{name}' is not a Companion number (1 or more)");

                var matches = cameras.Where(c => c.CompanionNumber == number).ToList();
                if (matches.Count == 0)
                    return BitfocusCompanionRequestParseResult.Rejected($"No camera has Companion number {number} (command '{name}')");
                if (matches.Count > 1)
                    return BitfocusCompanionRequestParseResult.Rejected(
                        $"Companion number {number} (command '{name}') is used by more than one camera ({string.Join(", ", matches.Select(c => c.Name))}); give each camera its own number");

                events.Add(new BitfocusCompanionEvent(eventType, number, matches[0]));
            }

            if (speedProfile == CompanionSpeedProfileRequest.Current && events.Count > 0)
                return BitfocusCompanionRequestParseResult.Rejected($"'{SpeedProfileCommand}=current' must be the only parameter of its request");

            if (events.Count == 0 && speedProfile == null)
                return BitfocusCompanionRequestParseResult.Rejected("No command in request");

            return BitfocusCompanionRequestParseResult.Accepted(events, speedProfile);
        }
    }
}

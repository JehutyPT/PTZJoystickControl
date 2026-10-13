using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static PtzJoystickControl.Core.Model.InboundBitfocusCompanionEventEnum;

namespace PtzJoystickControl.Core.Model
{
    public enum InboundBitfocusCompanionEventEnum
    {
        SetCameraPreview
    }

    public class BitfocusCompanionEvent
    {

        #region Static Members
        public static List<BitfocusCompanionEvent> SupportedEvents = new()
        {
            new BitfocusCompanionEvent(SetCameraPreview, "1"),
            new BitfocusCompanionEvent(SetCameraPreview, "2")
        };

        private static int _buttonCounter = 0;
        private static int _joystickButtonCountOffset = 0;

        /// <summary>
        /// Validates every parameter of an inbound request before anything is executed.
        /// A parameter matches only a defined command by exact, case-sensitive name and one of
        /// its supported values. Returns existing <see cref="SupportedEvents"/> entries, or a
        /// rejection when the request is empty or any parameter does not match.
        /// </summary>
        public static BitfocusCompanionRequestParseResult ParseRequest(IEnumerable<KeyValuePair<string?, string?>> parameters)
        {
            var events = new List<BitfocusCompanionEvent>();

            foreach (var (name, value) in parameters)
            {
                if (name == null)
                    return BitfocusCompanionRequestParseResult.Rejected($"Parameter without a name (value '{value}')");

                // Ordinal name comparison, not Enum.Parse/TryParse: those accept numbers and other casings.
                if (!Enum.GetNames<InboundBitfocusCompanionEventEnum>().Contains(name, StringComparer.Ordinal))
                    return BitfocusCompanionRequestParseResult.Rejected($"Unknown command '{name}'");

                var match = SupportedEvents.FindAll(e => e.EventType.ToString() == name && e.Value == value);
                if (match.Count != 1)
                    return BitfocusCompanionRequestParseResult.Rejected($"Unsupported value '{value}' for command '{name}'");

                events.Add(match[0]);
            }

            if (events.Count == 0)
                return BitfocusCompanionRequestParseResult.Rejected("No command in request");

            return BitfocusCompanionRequestParseResult.Accepted(events);
        }

        public static void SetButtonCountOffset(int offset) => _joystickButtonCountOffset = offset;

#endregion


private int _button;
        public InboundBitfocusCompanionEventEnum EventType { get; set; }
        public string Value { get; set; }

        public int Button
        {
            get => _button + _joystickButtonCountOffset;
            private set => _button = value;
        }

        public string EventNameFormatString
        {
            get
            {
                switch (EventType)
                {
                    case SetCameraPreview:
                        return $"Vmix Cam {Value} Preview";
                    default:
                        throw new Exception($"Unsupported name format string for event: {EventType}");
                }
            }
        }

        public BitfocusCompanionEvent(InboundBitfocusCompanionEventEnum type, string value)
        {
            Button = ++_buttonCounter;
            Value = value;
            EventType = type;
        }
    }
}

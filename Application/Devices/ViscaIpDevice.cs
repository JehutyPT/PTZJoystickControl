using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Utils;
using PtzJoystickControl.Core.ViscaCommands;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PtzJoystickControl.Application.Devices;

public class ViscaIpDevice : ViscaIPDeviceBase, IDisposable
{
    private readonly object sendRecieveLock = new();
    private readonly HashSet<Func<ViscaIPHeaderType>> commandsToSend = new();

    byte[] tmpBuffer = new byte[16];
    ushort tmpBuffIndex = 0;
    private uint packetId = 0;

    // VISCA over UDP has no delivery guarantee and commands are only sent on change, so a single
    // lost stop would leave the camera drifting. Each stop is sent once immediately and then
    // StopRepeatCount more times, StopRepeatInterval after the previous send of it went out
    // (3 sends over roughly 100–200 ms for a single axis: timer resolution, ~15.6 ms on Windows,
    // and the send throttle stretch each interval).
    internal const int StopRepeatCount = 2;
    internal static readonly TimeSpan StopRepeatInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PendingPollInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxPendingWait = TimeSpan.FromSeconds(1);

    // Guarded by commandsToSend. Once disposed, the camera ignores commands and never opens a
    // socket. busy counts the send loops (AddCommand) and stop repeats still running: the last of
    // them to end on a disposed camera releases it, or Dispose does if none is running. Released,
    // the camera has closed its socket and unregistered its listener, and sends and logs nothing.
    private bool disposed;
    private bool released;
    private int busy;

    // The endpoint this camera receives UDP replies from, as registered: ViscaIpEndpont changes in
    // place when the address or port is set, before EndSocket runs. Null when not registered.
    private IPEndPoint? listenerEndpoint;

    public ViscaIpDevice(string name) : this(name, null) { }

    public ViscaIpDevice(string name, IPEndPoint? viscaEndpint) : base(name, viscaEndpint) { }

    /// <summary>
    /// True once the disposed camera is released: from then on it sends and logs nothing more, its
    /// socket closes and its UDP reply listener is unregistered. For tests, as
    /// <see cref="UdpSocket.ListenerFor"/> exposes the listener.
    /// </summary>
    internal bool Released
    {
        get { lock (commandsToSend) return released; }
    }

    private bool IsDisposed
    {
        get { lock (commandsToSend) return disposed; }
    }

    public override void BeginSocket()
    {
        // A disposed camera never opens a socket, so none of its stops can re-register the listener.
        if (IsDisposed)
            return;

        if (socket == null)
        {
            if (protocol == Protocol.Udp)
            {
                socket = UdpSocket.GetInstance();
                var endpoint = new IPEndPoint(ViscaIpEndpont.Address, ViscaIpEndpont.Port);
                if (UdpSocket.AddListenerCallback(endpoint, ParseViscaIPReply))
                    listenerEndpoint = endpoint;
                else
                    Trace.TraceWarning($"Camera {name}: another camera already receives the replies from {endpoint}; this one still sends its commands but ignores replies");
                return;
            }
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                SendTimeout = 500,
                NoDelay = true,
                SendBufferSize = sendBuffer.Length,
                ReceiveBufferSize = receiveBuffer.Length,
            };
        }

        if (protocol == Protocol.Tcp && !socket.Connected)
        {
            try
            {
                SocketAsyncEventArgs socketAsyncEvArgs = new SocketAsyncEventArgs() { RemoteEndPoint = ViscaIpEndpont };
                socketAsyncEvArgs.Completed += OnConnected;

                if (!socket.ConnectAsync(socketAsyncEvArgs))
                    OnConnected(null, socketAsyncEvArgs);
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"TCP BeginSocket {name}: {e.Message}");
                EndSocket();
            }
        }
    }

    private void OnConnected(object? _, SocketAsyncEventArgs eventArgs)
    {
        NotifyPropertyChanged(nameof(Connected));
        try
        {
            if (eventArgs.SocketError == SocketError.Success)
                socket!.BeginReceive(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, new AsyncCallback(OnRecieve), null);
        }
        catch (Exception e)
        {
            // Released: the camera closed this socket itself and logs nothing more.
            if (Released)
                return;
            Trace.TraceWarning($"TCP OnConnected {name}: {eventArgs?.SocketError} {e.Message}");
            EndSocket();
        }
    }

    private void OnRecieve(IAsyncResult res)
    {
        IPEndPoint remoteIPEndPoint = new IPEndPoint(System.Net.IPAddress.Any, 0);
        EndPoint remoteEndPoint = remoteIPEndPoint;

        int length = 0;
        try
        {
            length = socket?.EndReceive(res, out var error) ?? 0;
            Debug.WriteLine($"TCP OnReceive: {ViscaIpEndpont} - {BitConverter.ToString(receiveBuffer, 0, length)}");
            ParseViscaIPReply(receiveBuffer, length);
            socket!.BeginReceive(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, new AsyncCallback(OnRecieve), null);
        }
        catch (Exception e)
        {
            // Released: closing the socket ended the receive, which is no connection loss.
            if (Released)
                return;
            Trace.TraceWarning($"TCP OnReceive {name}: connection lost: {e.Message}");
            NotifyPropertyChanged(nameof(Connected));
            EndSocket();
        }
    }

    public override void EndSocket()
    {
        if (socket != null)
        {
            EventHandler<SocketAsyncEventArgs> onComplete = (sender, e) =>
            {
                NotifyPropertyChanged(nameof(Connected));
                if (socket != UdpSocket.GetInstance())
                    socket.Dispose();
                socket = null;
            };

            SocketAsyncEventArgs socketAsyncEvArgs = new SocketAsyncEventArgs();
            socketAsyncEvArgs.Completed += onComplete;

            //Manually invoke on complete if DisconnectAsync completed synchronusly.
            if (!socket.Connected || !socket.DisconnectAsync(socketAsyncEvArgs))
                onComplete.Invoke(null, null!);
        }
        if (listenerEndpoint != null)
        {
            UdpSocket.RemoveListenerCallback(listenerEndpoint, ParseViscaIPReply);
            listenerEndpoint = null;
        }
    }

    public void ParseViscaIPReply(byte[] buffer, int length)
    {
        ViscaIPCommandParser.ParseReply(this, buffer, length);
    }

    public override void Power(Power power)
    {
        if (IsDisposed)
            return;
        powerCmd = (byte)power;
        AddCommand(AddPowerCommand);
    }

    // VISCA speed ranges. A speed fraction f becomes Min + floor(f · (Max − Min) + 1e-6), so
    // fraction 0 still moves at Min and 1 is the top speed.
    internal const byte PanSpeedMin = 1, PanSpeedMax = 24;
    internal const byte TiltSpeedMin = 1, TiltSpeedMax = 20;
    internal const byte ZoomSpeedMin = 0, ZoomSpeedMax = 7;
    internal const byte FocusSpeedMin = 0, FocusSpeedMax = 7;

    /// <summary>
    /// Maps a speed fraction onto a VISCA speed in <paramref name="min"/>..<paramref name="max"/>.
    /// A fraction outside 0..1 (or NaN) is clamped; the 1e-6 absorbs floating-point error so a
    /// fraction meant to land exactly on a step does not fall one below it.
    /// </summary>
    internal static byte ToViscaSpeed(double fraction, byte min, byte max)
    {
        if (double.IsNaN(fraction)) fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);
        var speed = min + Math.Floor(fraction * (max - min) + 1e-6);
        return (byte)Math.Clamp(speed, min, max);
    }

    // A stopped axis sends speed 0, as every command passed with a stop before speeds were fractions.
    private static byte PanSpeedByte(double fraction, PanDir dir) =>
        dir == PanDir.Stop ? (byte)0 : ToViscaSpeed(fraction, PanSpeedMin, PanSpeedMax);

    private static byte TiltSpeedByte(double fraction, TiltDir dir) =>
        dir == TiltDir.Stop ? (byte)0 : ToViscaSpeed(fraction, TiltSpeedMin, TiltSpeedMax);

    public override void Pan(double panSpeed, PanDir panDir)
    {
        PanTilt(PanSpeedByte(panSpeed, panDir), tiltSpeed, panDir, tiltDir);
    }

    public override void Tilt(double tiltSpeed, TiltDir tiltDir)
    {
        PanTilt(panSpeed, TiltSpeedByte(tiltSpeed, tiltDir), panDir, tiltDir);
    }

    public override void PanTilt(double panSpeed, double tiltSpeed, PanDir panDir, TiltDir tiltDir)
    {
        PanTilt(PanSpeedByte(panSpeed, panDir), TiltSpeedByte(tiltSpeed, tiltDir), panDir, tiltDir);
    }

    // Change detection compares VISCA bytes, so fractions that quantize to the same speed send nothing.
    private void PanTilt(byte panSpeed, byte tiltSpeed, PanDir panDir, TiltDir tiltDir)
    {
        if (IsDisposed)
            return;

        bool enteredStop = (this.panDir != PanDir.Stop && panDir == PanDir.Stop)
            || (this.tiltDir != TiltDir.Stop && tiltDir == TiltDir.Stop);
        bool panTiltChanged = false;
        if (this.panDir != panDir || this.tiltDir != tiltDir)
        {
            panTiltChanged = true;
        }
        else if ((this.panDir != PanDir.Stop || this.tiltDir != TiltDir.Stop) && (this.panSpeed != panSpeed || this.tiltSpeed != tiltSpeed))
        {
            panTiltChanged = true;
        }

        this.panDir = panDir;
        this.tiltDir = tiltDir;
        this.panSpeed = panSpeed;
        this.tiltSpeed = tiltSpeed;

        if (panTiltChanged)
        {
            if (enteredStop)
                SendStop(AddPanTiltCommand);
            else
                AddCommand(AddPanTiltCommand);
        }
    }

    public override void Zoom(double zoomSpeedFraction, ZoomDir zoomDir)
    {
        if (IsDisposed)
            return;

        byte zoomSpeed = zoomDir == ZoomDir.Stop ? (byte)0 : ToViscaSpeed(zoomSpeedFraction, ZoomSpeedMin, ZoomSpeedMax);

        //zoomCmd byte is split in direction and speed. 1 is dir and 2 is speed in 0x12
        bool zoomChanged = false;
        if ((zoomCmd & 0xf0) != (byte)zoomDir)
        { //Direction changed
            zoomChanged = true;
        }
        else if (zoomDir != (byte)ZoomDir.Stop && (zoomCmd & 0x0f) != zoomSpeed)
        { //Speed changed
            zoomChanged = true;
        }

        zoomCmd = zoomDir == (byte)ZoomDir.Stop ? (byte)0x00 : (byte)((byte)zoomDir | zoomSpeed & 0x0f);

        if (zoomChanged)
        {
            if (zoomDir == ZoomDir.Stop)
                SendStop(AddZoomCommand);
            else
                AddCommand(AddZoomCommand);
        }
    }

    public override void Focus(double focusSpeedFraction, FocusDir focusDir)
    {
        if (IsDisposed)
            return;

        byte focusSpeed = focusDir == FocusDir.Stop ? (byte)0 : ToViscaSpeed(focusSpeedFraction, FocusSpeedMin, FocusSpeedMax);

        bool focusChanged = false;
        if ((focusCmd & 0xf0) != (byte)focusDir)
        {
            focusChanged = true;
        }
        else if (focusDir != (byte)FocusDir.Stop && (focusCmd & 0x0f) != focusSpeed)
        {
            focusChanged = true;
        }

        focusCmd = focusDir == (byte)FocusDir.Stop ? (byte)0x00 : (byte)((byte)focusDir | focusSpeed & 0x0f);

        if (focusChanged)
        {
            if (focusDir == FocusDir.Stop)
                SendStop(AddFocusCommand);
            else
                AddCommand(AddFocusCommand);
        }
    }

    public override void FocusMode(FocusMode focusMode)
    {
        if (IsDisposed)
            return;
        focusModeCmd = (byte)focusMode;
        AddCommand(AddFocusModeCommand);
    }

    public override void FocusLock(FocusLock focusLock)
    {
        if (IsDisposed)
            return;
        focusLockCmd = (byte)focusLock;
        AddCommand(AddFocusLockCommand);
    }

    public override void Preset(Preset preset, byte presetNumber)
    {
        if (IsDisposed)
            return;
        presetCmd = (byte)preset;
        presetCmdNumber = presetNumber;
        AddCommand(AddPresetCommand);
    }

    public override void PresetRecallSpeed(byte value)
    {
        if (IsDisposed)
            return;
        presetRecallSpeed = value;
        AddCommand(AddPresetRecallSpeedCommand);
    }

    public override void Stop()
    {
        if (IsDisposed)
            return;

        // Bypass change detection: the camera may still be moving even if the last command
        // we sent was a stop (it may have been lost).
        panDir = PanDir.Stop;
        tiltDir = TiltDir.Stop;
        zoomCmd = (byte)ZoomDir.Stop;
        focusCmd = (byte)FocusDir.Stop;

        SendStop(AddPanTiltCommand);
        SendStop(AddZoomCommand);
        SendStop(AddFocusCommand);
    }

    /// <summary>
    /// Returns at once. From here on the camera ignores commands: they change no state and send
    /// nothing; the address, port and protocol setters still record their values and open nothing.
    /// The stop repeats already running finish, so each pending stop still goes out 3 times. Once
    /// none is running and nothing is queued (at once if the camera is idle), the camera is released:
    /// it closes its socket and unregisters its UDP reply listener, and sends and logs nothing more.
    /// It never opens or reconnects a socket, so a stop that cannot go out on the socket it has is
    /// dropped, and a repeat cannot re-register the listener.
    /// </summary>
    public void Dispose()
    {
        lock (commandsToSend)
        {
            if (disposed)
                return;
            disposed = true;
            if (busy > 0)
                return;
            released = true;
        }
        EndSocket();
    }

    // Ends a send loop or stop repeat. The last one to end on a disposed camera releases it.
    private void EndBusy()
    {
        lock (commandsToSend)
        {
            busy--;
            if (!disposed || released || busy > 0)
                return;
            released = true;
        }
        EndSocket();
    }

    private void SendStop(Func<ViscaIPHeaderType> commandFunction)
    {
        AddCommand(commandFunction);
        RepeatCommand(commandFunction);
    }

    /// <summary>
    /// Re-sends a command StopRepeatCount more times, StopRepeatInterval apart. Each repeat waits
    /// until the previous send of the same command has left the queue: pending commands are a
    /// set, so adding one that is still queued would merge into a single packet. The packet is
    /// built from the device's state at send time, so if the operator moves the camera again
    /// within the window the repeat carries the new motion rather than a stale stop. Disposing the
    /// camera lets the repeats finish: it ignores commands from then on, so they carry the stop.
    /// </summary>
    private void RepeatCommand(Func<ViscaIPHeaderType> commandFunction)
    {
        lock (commandsToSend)
            busy++;
        _ = RepeatCommandAsync(commandFunction);
    }

    private async Task RepeatCommandAsync(Func<ViscaIPHeaderType> commandFunction)
    {
        try
        {
            try
            {
                for (int i = 0; i < StopRepeatCount; i++)
                {
                    var waitUntil = DateTime.UtcNow + MaxPendingWait;
                    while (IsPending(commandFunction) && DateTime.UtcNow < waitUntil)
                        await Task.Delay(PendingPollInterval).ConfigureAwait(false);

                    await Task.Delay(StopRepeatInterval).ConfigureAwait(false);
                    AddCommand(commandFunction);
                }
            }
            finally
            {
                EndBusy();
            }
        }
        catch (Exception e)
        {
            Trace.TraceError($"Repeat command {name}: {e}");
        }
    }

    private bool IsPending(Func<ViscaIPHeaderType> commandFunction)
    {
        lock (commandsToSend)
        {
            return commandsToSend.Contains(commandFunction);
        }
    }

    public ViscaIPHeaderType AddPowerCommand()
    {
        // 81 01 04 00 0X FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.Power;
        tmpBuffer[tmpBuffIndex++] = powerCmd;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    public ViscaIPHeaderType AddPowerInquiry()
    {
        // 81 09 04 00 FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Inquiry;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)InquiryType.Power;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        InquiryReplyParser = ViscaCommandParser.ParsePowerInquiryReply;
        return ViscaIPHeaderType.Inquery;
    }

    private ViscaIPHeaderType AddPanTiltCommand()
    {
        // 81 01 06 01 VV WW XX YY FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.PanTilt;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.PanTilt;
        tmpBuffer[tmpBuffIndex++] = panSpeed;
        tmpBuffer[tmpBuffIndex++] = tiltSpeed;
        tmpBuffer[tmpBuffIndex++] = (byte)panDir;
        tmpBuffer[tmpBuffIndex++] = (byte)tiltDir;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddZoomCommand()
    {
        // 81 01 04 07 xp FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.PanTiltZoomAndLimit;
        tmpBuffer[tmpBuffIndex++] = zoomCmd;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddFocusCommand()
    {
        // 81 01 04 08 2p FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.Focus;
        tmpBuffer[tmpBuffIndex++] = focusCmd;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddFocusModeCommand()
    {
        // 81 01 04 08 2p FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.FocusMode;
        tmpBuffer[tmpBuffIndex++] = focusModeCmd;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddFocusLockCommand()
    {
        // 81 0a 04 68 0x FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.FocusLock;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.FocusLock;
        tmpBuffer[tmpBuffIndex++] = focusLockCmd;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddPresetCommand()
    {
        // 81 01 04 3F 0x pp FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.Camera;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandType.Preset;
        tmpBuffer[tmpBuffIndex++] = presetCmd;
        tmpBuffer[tmpBuffIndex++] = presetCmdNumber;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    private ViscaIPHeaderType AddPresetRecallSpeedCommand()
    {
        // 81 01 06 01 pp FF
        tmpBuffer[tmpBuffIndex++] = 0x81;
        tmpBuffer[tmpBuffIndex++] = (byte)PacketType.Command;
        tmpBuffer[tmpBuffIndex++] = (byte)CommandCategory.PanTilt;
        tmpBuffer[tmpBuffIndex++] = 0x01;
        tmpBuffer[tmpBuffIndex++] = presetRecallSpeed;
        tmpBuffer[tmpBuffIndex++] = (byte)Terminator.Terminate;
        return ViscaIPHeaderType.Command;
    }

    public void AddHeader(ViscaIPHeaderType commandType, ushort payloadLength)
    {
        sendBuffer[sendBuffIndex++] = (byte)((ushort)commandType >> 8);
        sendBuffer[sendBuffIndex++] = (byte)commandType;
        sendBuffer[sendBuffIndex++] = (byte)(payloadLength >> 8);
        sendBuffer[sendBuffIndex++] = (byte)payloadLength;
        sendBuffer[sendBuffIndex++] = (byte)(packetId >> 24);
        sendBuffer[sendBuffIndex++] = (byte)(packetId >> 16);
        sendBuffer[sendBuffIndex++] = (byte)(packetId >> 8);
        sendBuffer[sendBuffIndex++] = (byte)packetId;
        packetId++;
    }

    private async void AddCommand(Func<ViscaIPHeaderType> commandFunction)
    {
        // async void: an exception escaping here would crash the process.
        try
        {
            lock (commandsToSend)
            {
                busy++;
                commandsToSend.Add(commandFunction);
            }

            try
            {
                while (await Task.Run(() => trySendCommandsWithLock()))
                {
                    lock (commandsToSend)
                    {
                        if (commandsToSend.Count == 0)
                        {
                            break;
                        }
                    }
                };
            }
            finally
            {
                EndBusy();
            }
        }
        catch (Exception e)
        {
            Trace.TraceError($"AddCommand {name}: {e}");
        }
    }

    private bool trySendCommandsWithLock()
    {
        bool hadLock = false;
        try
        {
            if (Monitor.TryEnter(sendRecieveLock))
            {
                if ((DateTime.UtcNow - lastSendTime).TotalMilliseconds >= SendWaitTime)
                {
                    buildCommand();
                    SendCommand();
                    lastSendTime = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            if (Monitor.IsEntered(sendRecieveLock))
            {
                Monitor.Exit(sendRecieveLock);
                hadLock = true;
            }
        }
        return hadLock;
    }

    // Each packet carries exactly one VISCA command (the first pending one); the rest go out on later sends.
    private void buildCommand()
    {
        sendBuffIndex = 0;
        lock (commandsToSend)
        {
            if (commandsToSend.Count == 0)
                return;

            var command = commandsToSend.First();
            tmpBuffIndex = 0;
            var headerType = command();

            ushort cmdLen = tmpBuffIndex;
            if (useHeader)
                cmdLen += 8;

            if (cmdLen > sendBuffer.Length)
                return;

            if (useHeader)
                AddHeader(headerType, tmpBuffIndex);

            for (int i = 0; i < tmpBuffIndex; i++)
                sendBuffer[sendBuffIndex++] = tmpBuffer[i];

            commandsToSend.Remove(command);
        }
    }

    public void SendCommand()
    {
        // A disposed camera never opens or reconnects a socket: the command is dropped.
        if (socket == null)
        {
            if (IsDisposed)
                return;
            Trace.TraceInformation($"Camera {name}: no socket, connecting to {ViscaIpEndpont}");
            BeginSocket();
            return;
        }


        if (protocol == Protocol.Tcp && !socket.Connected)
        {
            if (IsDisposed)
                return;
            Trace.TraceWarning($"Camera {name}: TCP not connected, reconnecting to {ViscaIpEndpont}");
            BeginSocket();
            return;
        }

        try
        {
            socket.SendTo(sendBuffer, sendBuffIndex, SocketFlags.None, ViscaIpEndpont);
        }
        catch (Exception e)
        {
            Trace.TraceError($"Camera {name}: send to {ViscaIpEndpont} failed, closing socket: {e}");
            EndSocket();
        }
    }

}

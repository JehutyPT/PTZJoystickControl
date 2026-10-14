using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PtzJoystickControl.Core.Utils;

public class UdpSocket
{
    // Cameras add and remove listeners from the UI thread and the send path while OnRecieve reads them.
    private static readonly object receiveCallbacksLock = new();
    private static readonly Dictionary<EndPoint, Action<byte[], int>> receiveCallbacks = new();
    private static Socket? socket;
    private static int port = 0;
    private readonly static EndPoint remoteReceiveFromEndPoint = new IPEndPoint(IPAddress.Any, 0);
    private readonly static EndPoint currentRemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

    public int Port
    {
        get => port;
        set => port = value;
    }

    private static byte[] _buffer = new byte[128];

    public static Socket GetInstance()
    {
        if (socket == null)
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 128,
                SendBufferSize = 128
            };

        if (!socket.IsBound)
        {
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                socket.BeginReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref remoteEndPoint, new AsyncCallback(OnRecieve), null);
            }
            catch (Exception e)
            {
                Trace.TraceError($"UDP GetInstance: failed to bind the VISCA UDP socket: {e.Message}");
            }
        }
        return socket;
    }

    private static void OnRecieve(IAsyncResult res)
    {
        try
        {
            EndPoint remoteEndPoint = currentRemoteEndPoint;
            var length = socket?.EndReceiveFrom(res, ref remoteEndPoint) ?? 0;

            Debug.WriteLine($"UDP OnReceive: {remoteEndPoint} - {BitConverter.ToString(_buffer, 0, length)}");
            Action<byte[], int>? callback = null;
            if (length > 0)
                lock (receiveCallbacksLock)
                    receiveCallbacks.TryGetValue(remoteEndPoint, out callback);
            callback?.Invoke(_buffer, length);

            remoteEndPoint = remoteReceiveFromEndPoint;
            socket?.BeginReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref remoteEndPoint, new AsyncCallback(OnRecieve), null);
        }
        catch (SocketException e)
        {
            Trace.TraceWarning($"UDP OnReceive: socket closed: {e.Message}");
            socket?.Dispose();
            socket = null;
        }
        catch (Exception e)
        {
            Trace.TraceError($"UDP OnReceive: {e.Message}");
        }
    }

    /// <summary>
    /// Add callback to receive UDP packets from remote endpint. The registry keeps a copy of the
    /// endpoint, so a caller changing its own endpoint afterwards cannot move or orphan the entry.
    /// </summary>
    /// <param name="remoteEndpint">Remote endpoint to receive packets from</param>
    /// <param name="callback">Callback wich recived buffer and content length as parameters</param>
    /// <returns>
    /// False, adding nothing, when another callback already receives from this endpoint: replies
    /// from one endpoint cannot be told apart, so the first callback keeps them.
    /// </returns>
    public static bool AddListenerCallback(EndPoint remoteEndpint, Action<byte[], int> callback)
    {
        lock (receiveCallbacksLock)
        {
            if (receiveCallbacks.TryGetValue(remoteEndpint, out var existing))
                return existing.Equals(callback);
            receiveCallbacks.Add(Copy(remoteEndpint), callback);
            return true;
        }
    }

    /// <summary>
    /// Removes the callback for remote endpoint, only if it is <paramref name="callback"/>, so
    /// closing one camera never stops another camera on the same endpoint receiving.
    /// </summary>
    public static void RemoveListenerCallback(EndPoint remoteEndpint, Action<byte[], int> callback)
    {
        lock (receiveCallbacksLock)
        {
            if (receiveCallbacks.TryGetValue(remoteEndpint, out var existing) && existing.Equals(callback))
                receiveCallbacks.Remove(remoteEndpint);
        }
    }

    internal static EndPoint[] ListenerEndpoints()
    {
        lock (receiveCallbacksLock)
            return receiveCallbacks.Keys.ToArray();
    }

    internal static Action<byte[], int>? ListenerFor(EndPoint remoteEndpoint)
    {
        lock (receiveCallbacksLock)
            return receiveCallbacks.TryGetValue(remoteEndpoint, out var callback) ? callback : null;
    }

    private static EndPoint Copy(EndPoint endPoint) =>
        endPoint is IPEndPoint ip ? new IPEndPoint(ip.Address, ip.Port) : endPoint;
}

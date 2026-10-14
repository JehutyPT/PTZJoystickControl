using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PtzJoystickControl.Tests.Devices;

/// <summary>
/// A loopback XC camera: a minimal HTTP/1.1 server that records every request (path, query, arrival
/// time, <c>Authorization</c> header) and answers as <see cref="Responder"/> decides: with an HTTP
/// status and <c>livescope-status</c>, after the test releases it, after a delay, or never. With a
/// <see cref="Login"/>, a request without valid credentials is answered with HTTP 401 and the login's
/// challenges instead, at the time the responder decides.
/// Connections are kept alive, so a client that reuses one is served on it.
/// </summary>
internal sealed class FakeXcCamera : IDisposable
{
    public const string ControlPath = "/-wvhttp-01-/control.cgi";
    public const string InfoPath = "/-wvhttp-01-/info.cgi";

    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly List<XcRequest> _requests = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _accepted;
    private int _inFlight;
    private int _maxInFlight;

    public FakeXcCamera()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    /// <summary>Decides how each request is answered. Called on the fake's own tasks. Default: HTTP 200, <c>livescope-status: 0</c>.</summary>
    public Func<XcRequest, XcReply> Responder { get; set; } = _ => XcReply.Ok;

    /// <summary>The camera's login; null (the default) for guest control, where no request is challenged.</summary>
    public XcLogin? Login { get; set; }

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>
    /// The most requests received and not yet answered at one time. A held request counts until it is
    /// released, even if the client gave up on it.
    /// </summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public IReadOnlyList<XcRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public IReadOnlyList<XcRequest> ControlRequests => Requests.Where(r => r.IsControl).ToList();

    public IReadOnlyList<XcRequest> Probes => Requests.Where(r => r.IsProbe).ToList();

    /// <summary>Answers every request held by <see cref="XcReply.Held"/> so far.</summary>
    public void Release() => Interlocked.Exchange(ref _release, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Waits until <paramref name="condition"/> holds for the requests received so far, and returns them.</summary>
    public async Task<IReadOnlyList<XcRequest>> WaitUntilAsync(Func<IReadOnlyList<XcRequest>, bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)((timeout ?? DefaultWait).TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var requests = Requests;
            if (condition(requests))
                return requests;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. {Describe(requests)}");
            await Task.Delay(5);
        }
    }

    /// <summary>Waits for the <paramref name="count"/>th control request and returns it.</summary>
    public async Task<XcRequest> WaitForControlAsync(int count, TimeSpan? timeout = null)
    {
        var requests = await WaitUntilAsync(r => r.Count(x => x.IsControl) >= count, $"{count} control request(s)", timeout);
        return requests.Where(r => r.IsControl).ElementAt(count - 1);
    }

    /// <summary>Waits for the <paramref name="count"/>th probe and returns it.</summary>
    public async Task<XcRequest> WaitForProbeAsync(int count, TimeSpan? timeout = null)
    {
        var requests = await WaitUntilAsync(r => r.Count(x => x.IsProbe) >= count, $"{count} probe(s)", timeout);
        return requests.Where(r => r.IsProbe).ElementAt(count - 1);
    }

    public static string Describe(IEnumerable<XcRequest> requests)
    {
        var list = requests.ToList();
        if (list.Count == 0)
            return "Received: nothing";
        var first = list[0].ArrivedAt;
        // Marks a request that carried credentials or was challenged, never with the credentials themselves.
        return "Received: " + string.Join(" | ", list.Select(r =>
            $"+{Stopwatch.GetElapsedTime(first, r.ArrivedAt).TotalMilliseconds:0} ms {r.Path[(r.Path.LastIndexOf('/') + 1)..]}?{r.Query}"
            + (r.Authorization != null ? " [auth]" : "") + (r.Challenged ? " [401]" : "")));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys)
            client.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            _clients[client] = 0;
            Interlocked.Increment(ref _accepted);
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new StringBuilder();
            while (true)
            {
                // A request head can arrive in pieces; GET requests carry no body.
                int end;
                while ((end = pending.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token);
                    if (read == 0)
                        return;
                    pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var arrivedAt = Stopwatch.GetTimestamp();
                var head = pending.ToString(0, end);
                pending.Remove(0, end + 4);
                var lines = head.Split("\r\n");
                var target = lines[0].Split(' ')[1];
                var authorization = lines.Skip(1)
                    .Select(line => line.Split(':', 2))
                    .Where(header => header.Length == 2 && header[0].Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    .Select(header => header[1].Trim())
                    .FirstOrDefault();
                // Checked on arrival, so the nonce counts follow the order the requests came in.
                var login = Login;
                var stale = false;
                var challenged = login != null && !login.Accepts(authorization, target, out stale);
                var question = target.IndexOf('?');
                var request = new XcRequest(
                    question < 0 ? target : target[..question],
                    question < 0 ? "" : target[(question + 1)..],
                    arrivedAt)
                {
                    Authorization = authorization,
                    Challenged = challenged,
                };
                lock (_requests)
                    _requests.Add(request);

                var inFlight = Interlocked.Increment(ref _inFlight);
                try
                {
                    int max;
                    while (inFlight > (max = Volatile.Read(ref _maxInFlight))
                        && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
                    {
                    }

                    var reply = Responder(request);
                    switch (reply.Kind)
                    {
                        case XcReplyKind.Never:
                            // Until the client gives up and closes the connection.
                            while (await stream.ReadAsync(buffer, _stopping.Token) > 0)
                            {
                            }
                            return;
                        case XcReplyKind.Held:
                            await Volatile.Read(ref _release).Task.WaitAsync(_stopping.Token);
                            break;
                        case XcReplyKind.Delayed:
                            await Task.Delay(reply.Delay, _stopping.Token);
                            break;
                    }

                    // A challenged request gets the login's 401 whatever the responder answers.
                    var status = challenged ? 401 : reply.Status;
                    var response = new StringBuilder($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n");
                    if (!challenged && reply.Livescope != null)
                        response.Append($"livescope-status: {reply.Livescope}\r\n");
                    foreach (var challenge in challenged ? login!.Challenges(stale && login.Stale) : reply.Challenges)
                        response.Append($"WWW-Authenticate: {challenge}\r\n");
                    response.Append("Content-Length: 0\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stopping.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        catch (Exception)
        {
            // The client aborted the connection (a timed-out request) or the fake is stopping.
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }
}

internal sealed record XcRequest(string Path, string Query, long ArrivedAt)
{
    /// <summary>The request's <c>Authorization</c> header value; null when it had none.</summary>
    public string? Authorization { get; init; }

    /// <summary>Whether the fake's <see cref="FakeXcCamera.Login"/> challenged it: it is answered with HTTP 401.</summary>
    public bool Challenged { get; init; }

    /// <summary>The request target as on the request line: the path and, when there is one, the query.</summary>
    public string Target => Query.Length == 0 ? Path : $"{Path}?{Query}";

    public bool IsControl => Path == FakeXcCamera.ControlPath;

    public bool IsProbe => Path == FakeXcCamera.InfoPath;

    public string? Parameter(string name) =>
        Query.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name) is { Length: 2 } pair ? pair[1] : null;

    public string? Pan => Parameter("c.1.pan");

    public string? Tilt => Parameter("c.1.tilt");

    public string? Zoom => Parameter("c.1.zoom");

    public string? Focus => Parameter("c.1.focus.action");

    public string? Preset => Parameter("p");

    // A request stopping pan and tilt, whatever else it carries.
    public bool IsStop => IsControl && Pan == "stop" && Tilt == "stop";
}

internal enum XcReplyKind { Answer, Held, Delayed, Never }

internal sealed record XcReply(XcReplyKind Kind, int Status = 200, string? Livescope = "0")
{
    public static readonly XcReply Ok = new(XcReplyKind.Answer);

    /// <summary>Answered with HTTP 200 and <c>livescope-status: 0</c> once the test calls <see cref="FakeXcCamera.Release"/>.</summary>
    public static readonly XcReply Held = new(XcReplyKind.Held);

    /// <summary>Read and never answered; the connection stays open until the client closes it.</summary>
    public static readonly XcReply Never = new(XcReplyKind.Never);

    /// <summary>HTTP 200 with <c>livescope-status: 301</c>: another client holds the camera control right.</summary>
    public static readonly XcReply Refused = With(200, "301");

    /// <summary>The <c>WWW-Authenticate</c> challenges the answer carries, one header each.</summary>
    public IReadOnlyList<string> Challenges { get; init; } = Array.Empty<string>();

    /// <summary>How long a <see cref="XcReplyKind.Delayed"/> reply waits before it is answered.</summary>
    public TimeSpan Delay { get; init; }

    public static XcReply With(int status, string? livescope) => new(XcReplyKind.Answer, status, livescope);

    /// <summary>Answered with HTTP 200 and <c>livescope-status: 0</c> no sooner than <paramref name="delay"/> after it arrived.</summary>
    public static XcReply After(TimeSpan delay) => new(XcReplyKind.Delayed) { Delay = delay };

    /// <summary>HTTP 401 with these <c>WWW-Authenticate</c> challenges, whatever the login.</summary>
    public static XcReply Unauthorized(params string[] challenges) => new(XcReplyKind.Answer, 401, null) { Challenges = challenges };
}

/// <summary>
/// A login for <see cref="FakeXcCamera"/>: Basic (RFC 7617) and Digest with MD5 (RFC 7616), offered
/// in the challenges of its HTTP 401 answers. Checks each <c>Authorization</c> header strictly: the
/// scheme, every Digest parameter and how it is quoted, the <c>uri</c> against the request target,
/// <c>nc</c> counting up per nonce, and the MD5 <c>response</c>. Safe to use from several connections.
/// </summary>
internal sealed class XcLogin
{
    // auth-param (RFC 9110 section 11.2): a name, "=" and a token or a quoted string.
    private static readonly Regex AuthParam = new(@"([^\s=,]+)\s*=\s*(?:""((?:[^""\\]|\\.)*)""|([^\s"",]+))", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedPair = new(@"\\(.)", RegexOptions.CultureInvariant);
    private static readonly Regex NonceCount = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex Md5Hex = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    // Nonces issued before the current one: an answer to one is challenged again, as expired.
    private readonly HashSet<string> _superseded = new();
    // The highest nc accepted per nonce.
    private readonly Dictionary<string, long> _lastNonceCount = new();
    private string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    public required string Username { get; init; }
    public required string Password { get; init; }

    /// <summary>Offers Basic.</summary>
    public bool Basic { get; init; }

    /// <summary>Offers Digest, after Basic in the 401's challenges.</summary>
    public bool Digest { get; init; }

    /// <summary>Also offers a Digest challenge with SHA-256, before the MD5 one, as RFC 7616 servers do.</summary>
    public bool Sha256 { get; init; }

    /// <summary>Offers qop <c>auth</c> (after <c>auth-int</c>); without it, Digest in the RFC 2069 form, with no qop, nc or cnonce.</summary>
    public bool Qop { get; init; } = true;

    /// <summary>The algorithm the Digest challenge names; null names none, which means MD5.</summary>
    public string? Algorithm { get; init; } = "MD5";

    /// <summary>The Digest challenge's opaque value; null for none.</summary>
    public string? Opaque { get; init; } = "fake/opaque=";

    /// <summary>The realm. The default holds a double quote and a backslash, which must be escaped when quoted.</summary>
    public string Realm { get; init; } = "XC \"fake\" camera \\ 1";

    /// <summary>Whether the 401 to an answer with a superseded nonce says <c>stale=true</c>.</summary>
    public bool Stale { get; init; }

    /// <summary>The current Digest nonce.</summary>
    public string Nonce
    {
        get { lock (_lock) return _nonce; }
        init => _nonce = value;
    }

    /// <summary>Issues a new nonce: answers with the current one are challenged from now on, as expired.</summary>
    public void RenewNonce()
    {
        lock (_lock)
        {
            _superseded.Add(_nonce);
            _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        }
    }

    /// <summary>The <c>WWW-Authenticate</c> challenges of a 401: Basic first, then Digest.</summary>
    public IReadOnlyList<string> Challenges(bool stale = false)
    {
        lock (_lock)
        {
            var challenges = new List<string>();
            if (Basic)
                challenges.Add($"Basic realm={Quoted(Realm)}");
            if (Digest && Sha256)
                challenges.Add(DigestChallenge("SHA-256", stale));
            if (Digest)
                challenges.Add(DigestChallenge(Algorithm, stale));
            return challenges;
        }
    }

    private string DigestChallenge(string? algorithm, bool stale)
    {
        var challenge = new StringBuilder($"Digest realm={Quoted(Realm)}, nonce={Quoted(_nonce)}");
        if (Qop)
            challenge.Append(", qop=\"auth-int, auth\"");
        if (Opaque != null)
            challenge.Append($", opaque={Quoted(Opaque)}");
        if (algorithm != null)
            challenge.Append($", algorithm={algorithm}");
        if (stale)
            challenge.Append(", stale=true");
        return challenge.ToString();
    }

    /// <summary>
    /// Whether <paramref name="authorization"/> holds valid credentials for a GET of
    /// <paramref name="target"/>. When it does not, <paramref name="stale"/> says whether it answered
    /// a superseded nonce (the 401 says <c>stale=true</c> only when <see cref="Stale"/> is set).
    /// </summary>
    public bool Accepts(string? authorization, string target, out bool stale)
    {
        stale = false;
        var space = authorization?.IndexOf(' ') ?? -1;
        if (space < 0)
            return false;

        var scheme = authorization![..space];
        var credentials = authorization[(space + 1)..].Trim();
        if (Basic && scheme == "Basic")
            return AcceptsBasic(credentials);
        if (Digest && scheme == "Digest")
            return AcceptsDigest(credentials, target, out stale);
        return false;
    }

    private bool AcceptsBasic(string credentials)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(credentials)) == $"{Username}:{Password}";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // RFC 7616 section 3.4: username, realm, nonce, uri, response, cnonce and opaque are quoted
    // strings; qop, nc and algorithm are tokens.
    private bool AcceptsDigest(string credentials, string target, out bool stale)
    {
        stale = false;
        var parameters = Parameters(credentials);
        if (parameters == null)
            return false;

        string? QuotedValue(string name) => parameters.TryGetValue(name, out var p) && p.Quoted ? p.Value : null;
        string? TokenValue(string name) => parameters.TryGetValue(name, out var p) && !p.Quoted ? p.Value : null;

        var nonce = QuotedValue("nonce");
        var response = QuotedValue("response");
        if (QuotedValue("username") != Username || QuotedValue("realm") != Realm || QuotedValue("uri") != target
            || nonce == null || response == null || !Md5Hex.IsMatch(response)
            || (Opaque != null ? QuotedValue("opaque") != Opaque : parameters.ContainsKey("opaque"))
            || (Algorithm != null ? TokenValue("algorithm") != Algorithm : parameters.ContainsKey("algorithm")))
            return false;

        lock (_lock)
        {
            if (nonce != _nonce)
            {
                stale = _superseded.Contains(nonce);
                return false;
            }

            if (!Qop)
                return !parameters.ContainsKey("qop") && !parameters.ContainsKey("nc") && !parameters.ContainsKey("cnonce")
                    && response == DigestResponse(Username, Realm, Password, "GET", target, nonce, null, null, null);

            var nc = TokenValue("nc");
            var cnonce = QuotedValue("cnonce");
            if (TokenValue("qop") != "auth" || nc == null || !NonceCount.IsMatch(nc) || string.IsNullOrEmpty(cnonce))
                return false;
            var count = long.Parse(nc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (_lastNonceCount.TryGetValue(nonce, out var last) && count <= last)
                return false;
            if (response != DigestResponse(Username, Realm, Password, "GET", target, nonce, nc, cnonce, "auth"))
                return false;
            _lastNonceCount[nonce] = count;
            return true;
        }
    }

    /// <summary>
    /// The Digest response (RFC 7616 section 3.4.1, MD5): MD5(HA1:nonce:nc:cnonce:qop:HA2) with a qop,
    /// MD5(HA1:nonce:HA2) without, where HA1 = MD5(username:realm:password) and HA2 = MD5(method:uri).
    /// </summary>
    internal static string DigestResponse(string username, string realm, string password, string method, string uri, string nonce, string? nc, string? cnonce, string? qop)
    {
        var ha1 = Md5($"{username}:{realm}:{password}");
        var ha2 = Md5($"{method}:{uri}");
        return Md5(qop == null ? $"{ha1}:{nonce}:{ha2}" : $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    // The parameters by name, with each value unescaped and whether it was quoted; null when the text
    // is not a comma-separated list of parameters or names one twice.
    private static Dictionary<string, (string Value, bool Quoted)>? Parameters(string text)
    {
        var parameters = new Dictionary<string, (string Value, bool Quoted)>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (Match match in AuthParam.Matches(text))
        {
            if (text[position..match.Index].Trim() != (parameters.Count == 0 ? "" : ","))
                return null;
            var quoted = match.Groups[2].Success;
            var value = quoted ? QuotedPair.Replace(match.Groups[2].Value, "$1") : match.Groups[3].Value;
            if (!parameters.TryAdd(match.Groups[1].Value, (value, quoted)))
                return null;
            position = match.Index + match.Length;
        }
        return text[position..].Trim().Length == 0 ? parameters : null;
    }

    private static string Quoted(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}

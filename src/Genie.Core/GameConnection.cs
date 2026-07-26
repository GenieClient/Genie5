using System.Diagnostics;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using Genie.Core.Events;
using Microsoft.Extensions.Logging;

namespace Genie.Core.Connection;

/// <summary>
/// Manages the live TCP connection to DragonRealms (or Lich proxy / dev replay).
///
/// Responsibilities:
///   - Perform SGE auth (DirectSGE mode) or connect straight to the proxy/replay
///   - Read the raw XML byte stream, accumulate into lines/tags
///   - Publish every raw XML chunk to <see cref="RawXmlStream"/> for the parser
///   - Publish every raw XML chunk to <see cref="AiRawStream"/> for the AI analyzer
///   - Handle reconnection on drop
///   - Send commands back to the game server
///
/// The connection class intentionally knows nothing about game semantics — it only
/// moves bytes.  All interpretation happens downstream in DrXmlParser / GameState.
/// </summary>
public sealed class GameConnection : IAsyncDisposable
{
    private readonly ConnectionConfig       _cfg;
    private readonly SgeAuthClient          _sge;
    private readonly ILogger<GameConnection> _log;

    // ── Observable streams (hot, multicast) ─────────────────────────────────

    /// <summary>
    /// Raw XML text chunks exactly as received from the game server.
    /// Subscribers: DrXmlParser (game state), AiContextBuffer (AI), Logger.
    /// </summary>
    private readonly Subject<string> _rawXmlSubject  = new();
    public IObservable<string> RawXmlStream => _rawXmlSubject;

    /// <summary>
    /// Mirror of RawXmlStream specifically for the AI pipeline.
    /// Exists as a separate subject so the AI pipe can be suspended/resumed
    /// without affecting the parser pipeline.
    /// </summary>
    private readonly Subject<string> _aiRawSubject   = new();
    public IObservable<string> AiRawStream  => _aiRawSubject;

    /// <summary>
    /// Connection lifecycle events (Connected, Disconnected, Reconnecting, Error).
    /// </summary>
    private readonly Subject<ConnectionEvent> _stateSubject = new();
    public IObservable<ConnectionEvent> StateStream => _stateSubject;

    /// <summary>Transport the most recent SGE login used — "TLS" or "plaintext",
    /// or null for non-SGE modes (Lich/DevReplay). Carried in the Connected event
    /// so the UI can show a security indicator.</summary>
    private string? _authTransport;

    private Action<string>? _diag;
    /// <summary>Human-readable, timed connect-progress sink surfaced to the game
    /// window. Forwarded to the SGE client so its per-step timings show too.</summary>
    public Action<string>? Diag
    {
        get => _diag;
        set { _diag = value; _sge.Diag = value; }
    }

    /// <summary>Gates the granular per-step SGE marks (off by default; the
    /// high-level connect status lines always emit). Forwarded to the SGE
    /// client. Driven by <c>#config conndebug</c>.</summary>
    public bool VerboseDiag
    {
        get => _sge.VerboseDiag;
        set => _sge.VerboseDiag = value;
    }

    // ── Internal state ───────────────────────────────────────────────────────
    private TcpClient?    _tcp;
    private NetworkStream? _networkStream;
    private StreamWriter?  _writer;
    // Serializes outbound writes. The .cmd engine sends on the UI thread, but
    // .js scripts run on their own threads and can call SendCommandAsync
    // concurrently — with each other and with .cmd sends. StreamWriter is not
    // thread-safe for concurrent writes, so gate every write+flush.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private Task?          _readLoop;
    private Task?          _watchdogLoop;

    /// <summary>Monotonic timestamp (<see cref="Environment.TickCount64"/>) of the
    /// last byte received from the server. Stamped on connect and on every read,
    /// read by the server-activity watchdog. <see cref="Volatile"/>-accessed
    /// because the read loop writes it and the watchdog loop reads it.</summary>
    private long _lastServerActivityTicks;

    public bool IsConnected => _tcp?.Connected ?? false;

    // ── Resolved game endpoint ───────────────────────────────────────────────
    /// <summary>
    /// The host the client actually dialled for the game stream, set on every
    /// (re)connect. DirectSGE → the GAMEHOST the login response returned;
    /// LichProxy / DevReplay → the proxy/replay endpoint. Surfaced as the
    /// <c>$gamehost</c> script global (Genie 4 parity). Empty until first connect.
    /// </summary>
    public string ResolvedGameHost { get; private set; } = string.Empty;

    /// <summary>
    /// True when the last DirectSGE login reported a PREMIUM account. Used to
    /// seed the DR type-ahead limit (premium accounts get an extra type-ahead
    /// line). Stays false for Lich / DevReplay (no SGE handshake), where the
    /// limit instead self-calibrates from the server's cap message.
    /// </summary>
    public bool AccountPremium { get; private set; }

    /// <summary>The port paired with <see cref="ResolvedGameHost"/>; surfaced as
    /// <c>$gameport</c>. 0 until first connect.</summary>
    public int ResolvedGamePort { get; private set; }

    // ── AI pipe toggle ───────────────────────────────────────────────────────
    /// <summary>
    /// When false, raw XML is NOT forwarded to AiRawStream.
    /// Lets users disable AI processing without a reconnection.
    /// </summary>
    public bool AiPipeEnabled { get; set; } = true;

    public GameConnection(
        ConnectionConfig cfg,
        SgeAuthClient sge,
        ILogger<GameConnection> log)
    {
        _cfg = cfg;
        _sge = sge;
        _log = log;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        for (int attempt = 0; attempt <= _cfg.MaxReconnectAttempts; attempt++)
        {
            if (attempt > 0)
            {
                _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Reconnecting, attempt));
                _log.LogInformation("Reconnect attempt {Attempt}/{Max}",
                    attempt, _cfg.MaxReconnectAttempts);
                await Task.Delay(_cfg.ReconnectDelayMs, ct);
            }

            try
            {
                // Bound the connect+auth phase with an explicit deadline — async
                // socket connect/read don't time out on their own, so a server
                // that accepts the TCP connection but never answers would hang
                // the whole connect forever (no event, greyed UI). On expiry we
                // raise it as a non-retryable failure with a clear reason.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(_cfg.ConnectTimeoutMs);
                try
                {
                    await EstablishConnectionAsync(attemptCts.Token);
                }
                catch (OperationCanceledException)
                    when (attemptCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new SgeAuthException(
                        $"connection timed out after {_cfg.ConnectTimeoutMs / 1000}s — couldn't reach the " +
                        $"{_cfg.Mode} login server. Check your network/VPN and that the server is reachable. " +
                        "(Repeated bad-password attempts can briefly rate-limit the login server.)");
                }
                _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Connected, 0, _authTransport));
                _readLoop = ReadLoopAsync(ct);
                // Server-activity watchdog (off unless ServerActivityTimeoutMs > 0).
                // TCP keepalive (configured in EstablishConnectionAsync) is the
                // primary dead-link detector — it can tell a dead peer from a
                // merely-idle one. This app-level timer is an optional backstop
                // for the pathological "peer ACKs keepalive but the game app has
                // wedged" case; it CANNOT distinguish idle from dead, so it stays
                // opt-in to avoid dropping a healthy but quiet session.
                if (_cfg.ServerActivityTimeoutMs > 0)
                    _watchdogLoop = WatchdogLoopAsync(ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (SgeAuthException ex)
            {
                // Non-transient: bad credentials, a server PROBLEM (already
                // logged in / character in game / billing / unavailable), or an
                // unparseable login reply. Retrying won't change the outcome, so
                // surface the real reason and stop immediately instead of burning
                // MaxReconnectAttempts on a futile loop (the symptom that turned
                // a "character is currently in game" into ~50s of silent retries
                // ending in a generic failure).
                _log.LogWarning("SGE login refused (non-retryable): {Reason}", ex.Message);
                _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Error, 0, ex.Message));
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Connection attempt {Attempt} failed", attempt + 1);
                _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Error, 0, ex.Message));
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect after {_cfg.MaxReconnectAttempts} attempts.");
    }

    /// <summary>
    /// Sends a game command (e.g. "attack kobold", "get sword").
    /// Automatically appends the newline the game expects.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (_writer is null)
            throw new InvalidOperationException("Not connected.");

        _log.LogDebug("→ {Command}", command);
        await _sendGate.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(command.AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        // Only emit Disconnected here if there was no read loop to unwind —
        // when one exists, its own finally block (below, in ReadLoopAsync)
        // already emits it after being cancelled, so emitting again here
        // would double-fire the event for every normal disconnect.
        bool hadReadLoop = _readLoop is not null;

        await _cts.CancelAsync();
        if (_readLoop is not null)
            await _readLoop.ConfigureAwait(false);
        if (_watchdogLoop is not null)
            await _watchdogLoop.ConfigureAwait(false);
        Cleanup();
        if (!hadReadLoop)
            _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Disconnected));
    }

    // ── Connection establishment ─────────────────────────────────────────────

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        Cleanup();
        _authTransport = null;   // set by the SGE path; stays null for Lich/DevReplay
        _tcp = new TcpClient { ReceiveTimeout = _cfg.ReadTimeoutMs };

        switch (_cfg.Mode)
        {
            case ConnectionMode.DirectSGE:
                await ConnectViaSgeAsync(ct);
                break;

            case ConnectionMode.LichProxy:
                _log.LogInformation("Connecting to Lich proxy {Host}:{Port}",
                    _cfg.LichProxyHost, _cfg.LichProxyPort);
                await _tcp.ConnectAsync(_cfg.LichProxyHost, _cfg.LichProxyPort, ct);
                _networkStream = _tcp.GetStream();
                ResolvedGameHost = _cfg.LichProxyHost;
                ResolvedGamePort = _cfg.LichProxyPort;
                // Lich proxy is already authenticated; no key handshake needed.
                break;

            case ConnectionMode.DevReplay:
                // DevReplay uses a local server that replays a recorded session.
                // The replay server listens on LichProxyHost:LichProxyPort by convention.
                _log.LogInformation("Connecting to dev-replay server at {Host}:{Port}",
                    _cfg.LichProxyHost, _cfg.LichProxyPort);
                await _tcp.ConnectAsync(_cfg.LichProxyHost, _cfg.LichProxyPort, ct);
                _networkStream = _tcp.GetStream();
                ResolvedGameHost = _cfg.LichProxyHost;
                ResolvedGamePort = _cfg.LichProxyPort;
                break;

            default:
                throw new NotSupportedException($"Unknown connection mode: {_cfg.Mode}");
        }

        // Enable TCP keepalive so a half-open / silently-dropped link (idle NAT
        // timeout, a server that vanishes without a FIN) is detected instead of
        // hanging ReadAsync forever — the core of issue #87. Keepalive probes
        // ride below the application layer, so a healthy-but-idle peer's TCP
        // stack still ACKs them and the link stays up; only a truly dead peer
        // stops answering and trips the failure.
        ConfigureKeepAlive(_tcp!.Client);

        // Seed the activity stamp so the watchdog measures silence from connect,
        // not from process start.
        _lastServerActivityTicks = Environment.TickCount64;

        // UTF-8: DR's XML stream is ASCII-safe but UTF-8 compatible for any
        // extended characters in player names / room descriptions.
        _writer = new StreamWriter(_networkStream!, Encoding.UTF8, leaveOpen: true)
            { AutoFlush = false, NewLine = "\r\n" };
    }

    /// <summary>
    /// Turn on TCP keepalive and tune the probe cadence so a dead link surfaces
    /// in roughly <c>Time + Interval × RetryCount</c> (≈110s here) instead of the
    /// OS default of two hours. The tuning knobs are best-effort: not every
    /// platform exposes all three, so a failure there leaves plain keepalive on
    /// (the essential part) with OS-default timing.
    /// </summary>
    private void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TCP keepalive could not be enabled on this socket");
            return;
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime,       60); // idle seconds before first probe
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval,   10); // seconds between probes
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount,  5); // unanswered probes before dead
        }
        catch (Exception ex)
        {
            // Keepalive is on; only the cadence tuning is unavailable here.
            _log.LogDebug(ex, "TCP keepalive cadence tuning unsupported on this platform — using OS defaults");
        }
    }

    private async Task ConnectViaSgeAsync(CancellationToken ct)
    {
        _log.LogInformation("Authenticating via SGE for {Account}", _cfg.AccountName);
        var sgeResult = await _sge.AuthenticateAsync(_cfg, ct);

        // Remember which transport the login actually used so the UI can show a
        // TLS / plaintext indicator (the TLS attempt auto-falls-back to plain).
        _authTransport = sgeResult.UsedTls ? "TLS" : "plaintext";
        AccountPremium = sgeResult.IsPremium;

        _log.LogInformation("SGE OK → connecting to game {Host}:{Port}",
            sgeResult.GameHost, sgeResult.GamePort);
        _diag?.Invoke($"[conn] SGE OK ({_authTransport}) → game {sgeResult.GameHost}:{sgeResult.GamePort}");

        ResolvedGameHost = sgeResult.GameHost;
        ResolvedGamePort = sgeResult.GamePort;

        var swG = Stopwatch.StartNew();
        await _tcp!.ConnectAsync(sgeResult.GameHost, sgeResult.GamePort, ct);
        _diag?.Invoke($"[conn]   game server connected (+{swG.ElapsedMilliseconds}ms)");
        _networkStream = _tcp.GetStream();

        // Send the key (plain ASCII, terminated with '\n') to prove identity.
        var keyBytes = Encoding.ASCII.GetBytes(sgeResult.GameKey + "\n");
        await _networkStream.WriteAsync(keyBytes, ct);
        await _networkStream.FlushAsync(ct);
        _log.LogDebug("Game key sent ({Len} bytes)", keyBytes.Length);

        // StormFront mode: declare frontend to activate XML event stream.
        // Wizard mode: no FE announcement — the server starts sending plain text
        // immediately after the key. Sending any FE string causes the WIZ server
        // to close the connection.
        if (_cfg.ClientMode != GameClientMode.Wizard)
        {
            // FE identifier comes from ConnectionConfig so users can toggle
            // between FE:GENIE (legacy Genie behavior) and FE:STORM (Wrayth
            // identification, which DR appears to ungate richer click markup
            // for) without recompiling. Default is GENIE for Genie 4 parity.
            var feLine  = $"FE:{_cfg.FrontEndId} /VERSION:5.0.0.1 /P:WIN_UNKNOWN /XML\n";
            var feBytes = Encoding.ASCII.GetBytes(feLine);
            await _networkStream.WriteAsync(feBytes, ct);
            await _networkStream.FlushAsync(ct);
            _log.LogDebug("FE identification sent (StormFront)");
        }
        else
        {
            _log.LogDebug("Wizard mode — skipping FE announcement");
        }

        // "look" is sent by GenieCore when <settingsInfo/> arrives, not here.
        // This keeps the connection layer free of game-semantic knowledge.
    }

    // ── Read loop ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the raw XML stream continuously.  The DR stream is not line-delimited
    /// in the traditional sense — it mixes bare text with XML tags in a single flow.
    /// We buffer into logical chunks between tag boundaries and emit each chunk.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer  = new byte[8192];
        var pending = new StringBuilder(4096);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await _networkStream!.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                {
                    _log.LogWarning("Server closed connection (0 bytes read).");
                    break;
                }

                // Stamp activity for the server-activity watchdog.
                Volatile.Write(ref _lastServerActivityTicks, Environment.TickCount64);

                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                pending.Append(text);

                // Emit complete logical chunks: each complete XML element or
                // bare-text segment between elements.  We flush whenever we see
                // a '>' (end of tag) or '\n' (end of text line), keeping partial
                // tags buffered until complete.
                EmitChunks(pending);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Read loop error — will trigger reconnect");
            _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Error, 0, ex.Message));
        }
        finally
        {
            _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Disconnected));
        }
    }

    /// <summary>
    /// Optional backstop watchdog (enabled only when
    /// <see cref="ConnectionConfig.ServerActivityTimeoutMs"/> &gt; 0). If no byte
    /// arrives for the configured window, declares the link dead: raises an Error
    /// event with the reason, then cancels the connection so the blocked
    /// <c>ReadAsync</c> unwinds and the read loop's <c>finally</c> publishes
    /// Disconnected. TCP keepalive normally beats this to the punch; this only
    /// matters when the peer keeps ACKing keepalive probes while the game stream
    /// itself has gone silent.
    /// </summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        var timeoutMs = _cfg.ServerActivityTimeoutMs;
        // Check a few times per window so detection latency is a fraction of it.
        var pollMs = Math.Clamp(timeoutMs / 4, 1_000, 15_000);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pollMs, ct);
                var idleMs = Environment.TickCount64 - Volatile.Read(ref _lastServerActivityTicks);
                if (idleMs < timeoutMs) continue;

                _log.LogWarning(
                    "Server-activity watchdog tripped: no data for {Idle}ms (>= {Timeout}ms) — declaring link dead.",
                    idleMs, timeoutMs);
                _stateSubject.OnNext(new ConnectionEvent(ConnectionEventKind.Error, 0,
                    $"no data from the server for {idleMs / 1000}s — the connection appears to have dropped."));
                await _cts.CancelAsync();   // unblocks ReadAsync → finally → Disconnected
                break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    /// <summary>
    /// Scans the pending buffer for complete XML tags or newline-terminated text lines
    /// and emits each as a discrete chunk to both the parser and AI streams.
    /// Incomplete tags are left in the buffer for the next read.
    /// </summary>
    private void EmitChunks(StringBuilder pending)
    {
        if (pending.Length == 0) return;

        // Materialize the buffer ONCE per read and scan it with an advancing
        // cursor. The previous version called pending.ToString() AND
        // pending.Remove(0, n) on every chunk — both O(buffer length) — making a
        // burst (login settings block, full inventory dump) O(n²). Here the
        // whole pass is O(n): one ToString, IndexOf calls that advance with the
        // cursor, and a single Clear+Append of the trailing remainder.
        var raw = pending.ToString();
        int pos = 0;

        while (pos < raw.Length)
        {
            // Find the next natural split point at or after the cursor: end of an
            // XML tag or a newline.
            int tagClose  = raw.IndexOf('>',  pos);
            int lineBreak = raw.IndexOf('\n', pos);

            int splitAt;
            if (tagClose >= 0 && (lineBreak < 0 || tagClose < lineBreak))
                splitAt = tagClose + 1;          // emit up to and including '>'
            else if (lineBreak >= 0)
                splitAt = lineBreak + 1;         // emit up to and including '\n'
            else
                break;                           // no complete chunk yet — wait

            var chunk = raw.Substring(pos, splitAt - pos);
            pos = splitAt;

            // Skip empty / whitespace-only chunks
            if (string.IsNullOrWhiteSpace(chunk)) continue;

            Publish(chunk);
        }

        // Retain only the unconsumed remainder (a partial tag / line) for the
        // next read to complete.
        pending.Clear();
        if (pos < raw.Length) pending.Append(raw, pos, raw.Length - pos);
    }

    private void Publish(string chunk)
    {
        _log.LogTrace("← {Chunk}", chunk.TrimEnd());

        // Always publish to the parser stream
        _rawXmlSubject.OnNext(chunk);

        // Conditionally publish to the AI stream
        if (AiPipeEnabled)
            _aiRawSubject.OnNext(chunk);
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void Cleanup()
    {
        _writer?.Dispose();
        _networkStream?.Dispose();
        _tcp?.Dispose();
        _writer = null;
        _networkStream = null;
        _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _rawXmlSubject.Dispose();
        _aiRawSubject.Dispose();
        _stateSubject.Dispose();
        _cts.Dispose();
        _sendGate.Dispose();
    }
}

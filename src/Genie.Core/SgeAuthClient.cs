using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Genie.Core.Connection;

/// <summary>
/// Handles the Simutronics SGE (Game Entry) authentication protocol.
///
/// Protocol (matches Genie 4 Connection.cs). The handshake is identical over
/// plaintext (port 7900) and TLS (port 7910); only the transport differs —
/// see <see cref="ConnectionConfig.UseTls"/>. TLS is the default because on
/// 7900 the password is only XOR-obfuscated with a key the server sends in
/// the clear, leaving it recoverable by a network observer.
///   1. TCP/TLS to eaccess.play.net — 7910 (TLS, default) or 7900 (plaintext)
///   2. Client sends "K\n" → server returns 32 raw key bytes + trailing \n
///   3. Client sends "A\t{ACCOUNT}\t{raw-encrypted-password}\n"
///      Password encoding: for each byte i: ((password[i] - 32) ^ keyByte[i]) + 32
///      Result is raw bytes in the stream, NOT hex-encoded
///   4. Client sends "M\n" → server returns game list
///   5. Client sends "G\t{GAMECODE}\n" → server returns account/game info
///   6. Client sends "C\n" → server returns character list
///   7. Client sends "L\t{CHARCODE}\tSTORM\n" → server returns KEY/GAMEHOST/GAMEPORT
///   8. Caller opens new TCP to GAMEHOST:GAMEPORT and sends KEY\n
/// </summary>
public sealed class SgeAuthClient(ILogger<SgeAuthClient> logger)
{
    /// <summary>Optional sink for human-readable connect-progress lines (timed),
    /// surfaced to the game window so a stall can be isolated to an exact step.</summary>
    public Action<string>? Diag { get; set; }

    /// <summary>When <c>true</c>, the granular per-step protocol marks
    /// (<c>→K sent</c>, <c>←32-byte key</c>, <c>→auth</c>, TCP/TLS handshake
    /// timings…) are emitted to <see cref="Diag"/>. The high-level status lines
    /// (trying TLS, login OK, fallback, connected + padlock) emit regardless.
    /// Off by default — toggled via <c>#config conndebug true</c> so a user
    /// hitting a connection stall can capture a full trace on demand without
    /// every normal login spamming the game window.</summary>
    public bool VerboseDiag { get; set; }
    public sealed record SgeResult(string GameHost, int GamePort, string GameKey, bool UsedTls = false, bool IsPremium = false);
    public sealed record SgeCharacter(string Code, string Name);

    /// <summary>
    /// Authenticates through SGE steps 1–6 and returns the character list for the account.
    /// Does NOT proceed to login — use this to discover available characters.
    /// </summary>
    public Task<List<SgeCharacter>> ListCharactersAsync(
        ConnectionConfig cfg,
        CancellationToken ct = default)
        => WithTlsFallback(cfg, ct, ListCharactersCoreAsync);

    private async Task<List<SgeCharacter>> ListCharactersCoreAsync(
        ConnectionConfig cfg,
        bool useTls,
        CancellationToken ct)
    {
        logger.LogInformation("Listing characters for {Account} on {Game}",
            cfg.AccountName, cfg.GameCode);

        var (tcp, stream) = await OpenTransportAsync(cfg, useTls, ct);
        using var ownedTcp    = tcp;
        using var ownedStream = stream;

        await StreamWriteAsync(stream, "K\n", ct);
        var keyBuf = new byte[32];
        await ReadExactAsync(stream, keyBuf, ct);
        // Plaintext appends a trailing '\n' after the key; TLS does not — only
        // consume it on the transport that sends it (see ReadLineAsync).
        if (!useTls)
        {
            var nlBuf = new byte[1];
            _ = await stream.ReadAsync(nlBuf, ct);
        }

        var prefix  = Encoding.ASCII.GetBytes($"A\t{cfg.AccountName.ToUpper()}\t");
        var encPw   = EncryptPassword(cfg.AccountPassword, keyBuf);
        var authMsg = new byte[prefix.Length + encPw.Length + 1];
        Buffer.BlockCopy(prefix, 0, authMsg, 0,             prefix.Length);
        Buffer.BlockCopy(encPw,  0, authMsg, prefix.Length, encPw.Length);
        authMsg[^1] = (byte)'\n';
        await stream.WriteAsync(authMsg, ct);
        await stream.FlushAsync(ct);

        var authResponse = await ReadLineAsync(stream, useTls, ct);
        var authParts = authResponse.Split('\t');
        if (!authResponse.StartsWith("A\t") || authParts.Length < 3 ||
            authParts[2].Equals("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
            authParts[2].StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            var reason = authParts.Length >= 3 ? authParts[2] : authResponse;
            throw new SgeAuthException($"authentication failed — {FriendlyAuthFailure(reason)}");
        }

        await StreamWriteAsync(stream, "M\n", ct);
        await ReadLineAsync(stream, useTls, ct); // game list — discard

        await StreamWriteAsync(stream, $"G\t{cfg.GameCode}\n", ct);
        await ReadLineAsync(stream, useTls, ct); // game select — discard

        await StreamWriteAsync(stream, "C\n", ct);
        var charList = await ReadLineAsync(stream, useTls, ct);
        logger.LogDebug("Character list: {CharList}", charList);

        return ParseCharacterList(charList);
    }

    public Task<SgeResult> AuthenticateAsync(
        ConnectionConfig cfg,
        CancellationToken ct = default)
        => WithTlsFallback(cfg, ct, AuthenticateCoreAsync);

    private async Task<SgeResult> AuthenticateCoreAsync(
        ConnectionConfig cfg,
        bool useTls,
        CancellationToken ct)
    {
        logger.LogInformation("Starting SGE authentication for {Account} on {Game}",
            cfg.AccountName, cfg.GameCode);

        var (tcp, stream) = await OpenTransportAsync(cfg, useTls, ct);
        using var ownedTcp    = tcp;
        using var ownedStream = stream;

        // Per-step timing → game window, so a stall shows exactly which round-trip hung.
        var sw = Stopwatch.StartNew();
        long lastMs = 0;
        void mark(string s) { var n = sw.ElapsedMilliseconds; if (VerboseDiag) Diag?.Invoke($"[conn]   {s} (+{n - lastMs}ms)"); lastMs = n; }
        mark($"{(useTls ? "TLS" : "plain")} transport ready");

        // ── Step 1: Request hash key ─────────────────────────────────────────
        await StreamWriteAsync(stream, "K\n", ct);
        mark("→K sent");

        // ── Step 2: Read exactly 32 raw key bytes (+ 1 newline the server appends)
        var keyBuf = new byte[32];
        await ReadExactAsync(stream, keyBuf, ct);
        mark("←32-byte key");
        // Plaintext (7900) appends a trailing '\n' after the 32-byte key — 33 bytes
        // total — but the TLS endpoint (7910) sends the 32 key bytes ONLY (verified
        // live: this 1-byte read hangs forever over TLS, instant over plain). So
        // consume the newline only on the transport that actually sends it;
        // otherwise we block on a byte that never arrives and the whole TLS login
        // stalls. (This was the TLS "intermittent stall" — a protocol-framing bug,
        // not server latency.)
        if (!useTls)
        {
            var nlBuf = new byte[1];
            _ = await stream.ReadAsync(nlBuf, ct);
        }
        mark(useTls ? "←(no trailing newline over TLS)" : "←trailing newline");
        logger.LogDebug("Received hash key (32 bytes)");

        // ── Step 3: Send encrypted password as raw bytes ─────────────────────
        // Format: "A\t{ACCOUNT}\t" + raw_encrypted_password_bytes + "\n"
        // The encrypted bytes are sent in-band; the StreamReader must not have
        // buffered anything yet (it hasn't been used), so mixing is safe here.
        var prefix    = Encoding.ASCII.GetBytes($"A\t{cfg.AccountName.ToUpper()}\t");
        var encPw     = EncryptPassword(cfg.AccountPassword, keyBuf);
        var authMsg   = new byte[prefix.Length + encPw.Length + 1];
        Buffer.BlockCopy(prefix, 0, authMsg, 0,             prefix.Length);
        Buffer.BlockCopy(encPw,  0, authMsg, prefix.Length, encPw.Length);
        authMsg[^1] = (byte)'\n';
        await stream.WriteAsync(authMsg, ct);
        await stream.FlushAsync(ct);

        // ── Step 4: Account validation ───────────────────────────────────────
        var authResponse = await ReadLineAsync(stream, useTls, ct);
        logger.LogDebug("Auth response: {AuthResponse}", authResponse);

        if (!authResponse.StartsWith("A\t"))
            throw new SgeAuthException(
                $"SGE auth failed — unexpected response: {authResponse}");

        var authParts = authResponse.Split('\t');
        // Success: response contains KEY somewhere; failure codes: PASSWORD, UNKNOWN, etc.
        if (authParts.Length < 3 ||
            authParts[2].Equals("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
            authParts[2].StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            var reason = authParts.Length >= 3 ? authParts[2] : authResponse;
            throw new SgeAuthException($"authentication failed — {FriendlyAuthFailure(reason)}");
        }
        mark("→auth  ←account validated");

        // ── Step 5: Game list → select game ─────────────────────────────────
        await StreamWriteAsync(stream, "M\n", ct);
        var gameList = await ReadLineAsync(stream, useTls, ct);
        logger.LogDebug("Game list: {GameList}", gameList);

        await StreamWriteAsync(stream, $"G\t{cfg.GameCode}\n", ct);
        var gameResponse = await ReadLineAsync(stream, useTls, ct);
        logger.LogDebug("Game select: {GameResponse}", gameResponse);
        // The game-select response carries the account's subscription type as one
        // of its tab fields (PREMIUM / NORMAL / FREE_TO_PLAY / TRIAL / …). We keep
        // only whether it's PREMIUM — used to seed the DR type-ahead limit (premium
        // accounts get an extra type-ahead line; see TypeAheadSession). Scan ALL
        // fields rather than a fixed index, same defensive approach as PROBLEM codes.
        bool isPremium = gameResponse.Split('\t')
            .Any(f => f.Equals("PREMIUM", StringComparison.OrdinalIgnoreCase));
        mark("→M/G  ←game selected");

        // ── Step 6: Character list ───────────────────────────────────────────
        await StreamWriteAsync(stream, "C\n", ct);
        var charList = await ReadLineAsync(stream, useTls, ct);
        logger.LogDebug("Character list: {CharList}", charList);
        mark("→C  ←character list");

        var characters = ParseCharacterList(charList);
        var charCode   = characters.FirstOrDefault(c =>
            string.Equals(c.Name, cfg.CharacterName, StringComparison.OrdinalIgnoreCase))?.Code;
        if (charCode is null)
        {
            var names = string.Join(", ", characters.Select(c => c.Name));
            throw new InvalidOperationException(
                $"Character '{cfg.CharacterName}' not found. " +
                $"Available: {(names.Length > 0 ? names : "(none)")}");
        }

        // ── Step 7: Select character → receive game entry token ──────────────
        // Always use STORM here — "WIZ" is deprecated on DR and returns PROBLEM 2.
        // Plain-text output is achieved by NOT sending the FE:/XML announcement
        // after the game server connection, which GameConnection handles via ClientMode.
        await StreamWriteAsync(stream, $"L\t{charCode}\tSTORM\n", ct);
        var loginResponse = await ReadLineAsync(stream, useTls, ct);
        // Log at Warning so this is always visible — needed to diagnose WIZ vs STORM differences.
        logger.LogWarning("SGE login response: {LoginResponse}", loginResponse);
        mark("→L  ←login token");

        return ParseLoginResponse(loginResponse) with { UsedTls = useTls, IsPremium = isPremium };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// SHA-256 fingerprint of Simutronics' self-signed eAccess TLS certificate
    /// (Subject/Issuer <c>O=Simutronics Corp.</c>; valid 2018→3017; live-verified
    /// 2026-05-31). The cert carries no CN/SAN and is not chained to a public
    /// root, so ordinary hostname/chain validation can never authenticate the
    /// server — we PIN this exact certificate instead. If Simutronics rotates
    /// it, re-probe port 7910, verify the new fingerprint out-of-band, and
    /// update this constant (or set <see cref="ConnectionConfig.UseTls"/> = false
    /// to use plaintext 7900 meanwhile).
    /// </summary>
    private const string SgeCertSha256Pin =
        "10B737E661987D15BC5C8245E3F8B78291D41ED8ABC76672ECB02FE78ED0218A";

    /// <summary>
    /// Upper bound on the whole TLS LOGIN attempt (transport + SGE handshake)
    /// before we give up and fall back to plaintext. A healthy 7910 login is
    /// sub-second to a couple of seconds; a longer wait is the intermittent
    /// server-side stall (the handshake succeeds but the SGE byte protocol
    /// stalls), and we'd rather drop to the rock-solid 7900.
    /// </summary>
    private const int TlsAttemptTimeoutSeconds = 8;

    /// <summary>
    /// Runs an SGE operation preferring TLS but never letting it block login.
    /// The cert pin is correct and the TLS *handshake* works, but 7910
    /// intermittently stalls the SGE byte protocol AFTER the handshake — so the
    /// whole TLS attempt (transport + handshake + the SGE reads/writes) is bounded
    /// by <see cref="TlsAttemptTimeoutSeconds"/>, and on ANY failure or stall —
    /// other than bad credentials or user cancellation — the ENTIRE operation is
    /// retried over plaintext 7900 (the transport Genie 4 uses; byte protocol
    /// identical). Bad credentials (<see cref="SgeAuthException"/>) do NOT fall
    /// back — they'd fail the same way over plain.
    /// </summary>
    private async Task<T> WithTlsFallback<T>(
        ConnectionConfig cfg, CancellationToken ct,
        Func<ConnectionConfig, bool, CancellationToken, Task<T>> op)
    {
        if (cfg.UseTls)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Diag?.Invoke($"[conn] trying TLS → {cfg.SgeHost}:{cfg.SgeTlsPort} (≤{TlsAttemptTimeoutSeconds}s)…");
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(TimeSpan.FromSeconds(TlsAttemptTimeoutSeconds));
                var r = await op(cfg, /* useTls: */ true, tlsCts.Token);
                Diag?.Invoke($"[conn] TLS login OK in {sw.ElapsedMilliseconds}ms");
                return r;
            }
            catch (SgeAuthException) { throw; }   // wrong creds — no point retrying over plain
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                var why = ex is OperationCanceledException ? $"stalled past {TlsAttemptTimeoutSeconds}s"
                                                           : $"failed ({ex.GetType().Name}: {ex.Message})";
                Diag?.Invoke($"[conn] TLS {why} after {sw.ElapsedMilliseconds}ms → falling back to plaintext {cfg.SgePort}…");
                logger.LogWarning("SGE login over TLS ({Host}:{TlsPort}) {What}; falling back to plaintext {PlainPort}.",
                    cfg.SgeHost, cfg.SgeTlsPort, why, cfg.SgePort);
            }
        }

        Diag?.Invoke($"[conn] connecting plaintext → {cfg.SgeHost}:{cfg.SgePort}…");
        return await op(cfg, /* useTls: */ false, ct);
    }

    /// <summary>
    /// Connects a fresh socket to the SGE host on the appropriate port and, for
    /// TLS, completes the pinned handshake. Disposes its socket on any failure so
    /// a fallback attempt starts from a clean slate.
    /// </summary>
    private async Task<(TcpClient Tcp, Stream Stream)> OpenTransportAsync(
        ConnectionConfig cfg, bool useTls, CancellationToken ct)
    {
        var port = useTls ? cfg.SgeTlsPort : cfg.SgePort;
        logger.LogDebug("Connecting to SGE {Host}:{Port} (TLS={Tls})", cfg.SgeHost, port, useTls);

        var tcp = new TcpClient();
        try
        {
            if (useTls)
            {
                // The whole TLS attempt is already bounded by the caller's token.
                var swT = Stopwatch.StartNew();
                await tcp.ConnectAsync(cfg.SgeHost, port, ct);
                if (VerboseDiag) Diag?.Invoke($"[conn]   TCP {port} connected (+{swT.ElapsedMilliseconds}ms)");

                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, ValidateSgeCertificate);
                var hsStart = swT.ElapsedMilliseconds;
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost          = cfg.SgeHost,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, ct);
                }
                catch { ssl.Dispose(); throw; }
                if (VerboseDiag) Diag?.Invoke($"[conn]   TLS handshake done (+{swT.ElapsedMilliseconds - hsStart}ms)");

                logger.LogDebug("SGE TLS established: {Protocol} / {Cipher}",
                    ssl.SslProtocol, ssl.NegotiatedCipherSuite);
                return (tcp, ssl);
            }

            // Plaintext: bound the bare TCP connect so a dead port fails with a
            // clear timeout rather than hanging.
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await tcp.ConnectAsync(cfg.SgeHost, port, connectCts.Token);
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out connecting to SGE server {cfg.SgeHost}:{port} (15 s). " +
                    $"Check that outbound TCP port {port} is not blocked by a firewall.");
            }
            return (tcp, tcp.GetStream());
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Certificate-pinning validation for the eAccess TLS endpoint. Simutronics
    /// presents a self-signed certificate with no CN/SAN, so the standard
    /// <see cref="SslPolicyErrors"/> are always non-<c>None</c> and are
    /// deliberately ignored. Instead we require the certificate's SHA-256
    /// fingerprint to equal <see cref="SgeCertSha256Pin"/> — which still
    /// defeats man-in-the-middle attacks, since an attacker cannot reproduce
    /// this exact certificate without its private key.
    /// </summary>
    private bool ValidateSgeCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            logger.LogError("SGE TLS: server presented no certificate — refusing connection.");
            return false;
        }

        var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        if (string.Equals(actual, SgeCertSha256Pin, StringComparison.OrdinalIgnoreCase))
            return true;

        logger.LogError(
            "SGE TLS certificate pin MISMATCH (policy errors: {Errors}). Expected {Expected}, got {Actual}. " +
            "Refusing the connection. If Simutronics legitimately rotated their certificate, verify the new " +
            "fingerprint out-of-band and update SgeCertSha256Pin.",
            sslPolicyErrors, SgeCertSha256Pin, actual);
        return false;
    }

    /// <summary>
    /// Genie 4 formula: ((passwordByte - 32) XOR keyByte) + 32.
    /// Key bytes are used raw — NOT offset by 32 — matching Utility.EncryptText(byte[], string).
    /// </summary>
    private static byte[] EncryptPassword(string password, byte[] keyBuf)
    {
        var result = new byte[password.Length];
        for (int i = 0; i < password.Length; i++)
            result[i] = (byte)(((password[i] - 32) ^ keyBuf[i % keyBuf.Length]) + 32);
        return result;
    }

    private static async Task StreamWriteAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n == 0) throw new EndOfStreamException("SGE connection closed during key read.");
            total += n;
        }
    }

    /// <summary>
    /// Read one SGE response line directly off the stream (no StreamReader, which
    /// would buffer across the raw key read and — worse — block forever over TLS).
    /// Plaintext (7900) terminates each response with '\n', so we read until it.
    /// The TLS endpoint (7910) sends each response as a record with NO trailing
    /// '\n', so over TLS we take the bytes and, once the server briefly goes idle,
    /// treat the line as complete. SGE is strict request/response, so exactly one
    /// response is in flight per call. A clean EOF mid-line (server closing on a
    /// failed login) returns what arrived; an EOF with nothing throws.
    /// </summary>
    private static async Task<string> ReadLineAsync(Stream stream, bool useTls, CancellationToken ct)
    {
        var buf = new byte[2048];
        var sb  = new StringBuilder();
        while (true)
        {
            int n;
            if (useTls && sb.Length > 0)
            {
                // Already have the record's bytes; TLS won't send a '\n', so only
                // wait briefly for a continuation, then treat the line as done.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromMilliseconds(200));
                try { n = await stream.ReadAsync(buf, idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            }
            else
            {
                n = await stream.ReadAsync(buf, ct);
            }
            if (n == 0) break;   // EOF (e.g. server closes on a failed login)
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().IndexOf('\n') >= 0) break;   // plaintext terminator
        }
        if (sb.Length == 0)
            throw new EndOfStreamException("SGE connection closed unexpectedly.");
        var s  = sb.ToString();
        var nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).TrimEnd('\r', '\n');
    }

    private static List<SgeCharacter> ParseCharacterList(string charListLine)
    {
        // Format: C\t{count}\t{?}\t{?}\t{?}\t{CODE}\t{NAME}\t{CODE}\t{NAME}\t...
        // Character data starts at tab-field index 5, alternating code/name pairs.
        var result = new List<SgeCharacter>();
        var parts  = charListLine.Split('\t');
        for (int i = 5; i + 1 < parts.Length; i += 2)
        {
            var code = parts[i].Trim();
            var name = parts[i + 1].Trim();
            if (code.Length > 0 && name.Length > 0)
                result.Add(new SgeCharacter(code, name));
        }
        return result;
    }

    /// <summary>
    /// Turn the raw SGE auth-failure code (PASSWORD / UNKNOWN / other) into a
    /// specific, actionable message so the user knows exactly what to fix.
    /// </summary>
    private static string FriendlyAuthFailure(string reason)
    {
        var r = reason.Trim();
        if (r.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            return "the password was incorrect. Passwords are case-sensitive — check it and try again.";
        if (r.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return "the account name was not recognized. Use your play.net ACCOUNT name (not your character name).";
        return $"the server rejected the credentials (replied '{r}'). Check your account name and password.";
    }

    private static SgeResult ParseLoginResponse(string response)
    {
        // Error responses: L\t[optional fields]\tPROBLEM N
        // The PROBLEM field can appear at any tab position depending on the login mode.
        var parts       = response.Split('\t');
        var problemField = parts.FirstOrDefault(p =>
            p.TrimStart().StartsWith("PROBLEM", StringComparison.OrdinalIgnoreCase));
        if (problemField is not null)
        {
            var code   = problemField.Trim();
            var detail = code switch
            {
                "PROBLEM 1" => "your account can't access this game — usually a billing or subscription issue. Verify your subscription at play.net.",
                "PROBLEM 2" => "this character may already be logged in (if you were just disconnected, wait ~1 minute and retry), or this login mode isn't supported.",
                "PROBLEM 3" => "this character is currently in game — disconnect the other session first.",
                "PROBLEM 4" => "the game is unavailable — DragonRealms may be down for maintenance. Try again shortly.",
                _           => $"the server refused the login ({code})."
            };
            throw new SgeAuthException($"login refused — {detail} [{code}]");
        }

        // Success: L\tOK\tKEY=xxxx\tGAMEHOST=...\tGAMEPORT=...\t...
        string? key = null, host = null;
        int port = 0;

        foreach (var part in parts)
        {
            if (part.StartsWith("KEY="))           key  = part[4..];
            else if (part.StartsWith("GAMEHOST=")) host = part[9..];
            else if (part.StartsWith("GAMEPORT=")) port = int.Parse(part[9..]);
        }

        if (key is null || host is null || port == 0)
            throw new SgeAuthException(
                $"Could not parse SGE login response: {response}");

        return new SgeResult(host, port, key);
    }
}

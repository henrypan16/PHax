using System.Text.RegularExpressions;
using PHax.Core.Interfaces;
using PHax.Core.Models;
using PHax.Modem.Profiles;

namespace PHax.Modem.Class1;

public sealed class UnsupportedModemException(string message, Class1Capabilities? capabilities = null) : Exception(message)
{
    public Class1Capabilities? Capabilities { get; } = capabilities;
}

/// <summary>
/// T.31 / EIA-578 Class 1 modem driver. Implements only the Class 1 command set; Class 2 commands are never issued.
/// </summary>
public sealed partial class Class1Modem : IFaxModem
{
    private readonly ModemLink _link;
    private readonly IProtocolLog _log;
    private readonly IReadOnlyList<string> _extraInit;
    private readonly bool _hardwareFlow;
    private readonly bool _preferClass10;

    // After ATD the DCE is already in +FRH=3 and has returned CONNECT; after ATA it is in +FTH=3.
    private bool _pendingReceiveConnect;
    private bool _pendingTransmitConnect;

    public ModemProfile Profile { get; }
    public ModemIdentity? Identity { get; private set; }
    public Class1Capabilities? Capabilities { get; private set; }
    public bool IsSimulated => _link.Transport.IsSimulated;
    public string PortName => _link.Transport.Name;
    /// <summary>Status of the most recent +FTM transmission.</summary>
    public DataTransmitStatus LastTransmitStatus { get; private set; }

    public event EventHandler<ModemUnsolicitedEvent>? Unsolicited;
    public event EventHandler<Exception>? Disconnected;

    public Class1Modem(ISerialTransport transport, ModemProfile profile, IProtocolLog log,
        bool hardwareFlowControl = true, IEnumerable<string>? extraInitCommands = null, bool? preferClass10 = null)
    {
        Profile = profile;
        _log = log;
        _hardwareFlow = hardwareFlowControl;
        _preferClass10 = preferClass10 ?? profile.PreferClass10;
        _extraInit = (extraInitCommands ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        _link = new ModemLink(transport);
        _link.Traffic += (dir, text) => _log.Log(dir == ">" ? "AT>" : dir == "<" ? "AT<" : "DATA", text);
        _link.UnsolicitedLine += line => { var e = ParseUnsolicited(line); if (e is not null) Unsolicited?.Invoke(this, e); };
        _link.Disconnected += ex => Disconnected?.Invoke(this, ex);
    }

    private TimeSpan CommandTimeout => TimeSpan.FromMilliseconds(Profile.CommandTimeoutMs);

    private async Task<AtResponse> Cmd(string command, CancellationToken ct, TimeSpan? timeout = null) =>
        await _link.CommandAsync(command, timeout ?? CommandTimeout, ct).ConfigureAwait(false);

    public async Task<Class1Capabilities> InitializeAsync(CancellationToken ct)
    {
        _pendingReceiveConnect = _pendingTransmitConnect = false;
        _link.Flush();

        // Synchronise: escape any data mode, then expect OK to a bare AT.
        bool alive = false;
        for (int i = 0; i < 3 && !alive; i++)
        {
            var r = await Cmd("AT", ct, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            alive = r.IsOk;
            if (!alive) { await _link.WriteAsync(new byte[] { Profile.AbortByte }, ct); await Task.Delay(300, ct); _link.Flush(); }
        }
        if (!alive) throw new ModemNotRespondingException($"No response to AT on {PortName}.");

        await Cmd(Profile.ResetCommand, ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        _link.Flush();

        var init = new List<string>(Profile.InitCommands)
        {
            _hardwareFlow ? Profile.HardwareFlowControlCommand : Profile.NoFlowControlCommand,
        };
        if (Profile.DialTimeoutCommand is { } dt) init.Add(dt);
        if (Profile.CallerIdCommand is { } cid) init.Add(cid);
        init.AddRange(_extraInit);
        foreach (var command in init)
        {
            var r = await Cmd(command, ct).ConfigureAwait(false);
            if (!r.IsOk) _log.Log("MODEM", $"Init command '{command}' returned {(r.TimedOut ? "no response" : r.Result)} (continuing).");
        }

        Identity = await IdentifyAsync(ct).ConfigureAwait(false);

        var classes = ParseList((await Cmd("AT+FCLASS=?", ct).ConfigureAwait(false)).Info);
        if (!classes.Contains("1") && !classes.Contains("1.0"))
            throw new UnsupportedModemException(
                $"Modem does not report Fax Class 1 or 1.0 (reported classes: {(classes.Count == 0 ? "none" : string.Join(",", classes))}). " +
                "PHax does not use Class 2.", new Class1Capabilities(classes, [], [], ""));

        string selected = _preferClass10 && classes.Contains("1.0") ? "1.0" : classes.Contains("1") ? "1" : "1.0";
        var sel = await Cmd($"AT+FCLASS={selected}", ct).ConfigureAwait(false);
        if (!sel.IsOk && selected == "1.0" && classes.Contains("1"))
        {
            selected = "1";
            sel = await Cmd("AT+FCLASS=1", ct).ConfigureAwait(false);
        }
        if (!sel.IsOk) throw new UnsupportedModemException($"Modem refused AT+FCLASS={selected}.");

        var ftm = ParseInts((await Cmd("AT+FTM=?", ct).ConfigureAwait(false)).Info);
        var frm = ParseInts((await Cmd("AT+FRM=?", ct).ConfigureAwait(false)).Info);
        var fth = ParseInts((await Cmd("AT+FTH=?", ct).ConfigureAwait(false)).Info);
        Capabilities = new Class1Capabilities(classes, ftm, frm, selected);
        if (ftm.Count == 0 || frm.Count == 0 || !fth.Contains(3))
            throw new UnsupportedModemException(
                $"Modem accepted +FCLASS={selected} but did not report usable Class 1 modulations " +
                $"(+FTM: {string.Join(",", ftm)}; +FRM: {string.Join(",", frm)}; +FTH: {string.Join(",", fth)}).", Capabilities);

        _log.Log("MODEM", $"Class {selected} ready. Identity: {Identity}. TX modulations: {string.Join(",", ftm)}; RX: {string.Join(",", frm)}.");
        return Capabilities;
    }

    public async Task<ModemIdentity> IdentifyAsync(CancellationToken ct)
    {
        async Task<string> Query(params string[] commands)
        {
            foreach (var c in commands)
            {
                var r = await Cmd(c, ct).ConfigureAwait(false);
                // Some modems echo the command name ("+GMI: CONEXANT"); keep only the value.
                if (r.IsOk && r.Lines.Count > 0)
                    return string.Join(" ", r.Lines.Select(l => IdentityPrefix().Replace(l, ""))).Trim();
            }
            return "";
        }
        var manufacturer = await Query("AT+GMI", "AT+FMI?", "ATI3").ConfigureAwait(false);
        var model = await Query("AT+GMM", "AT+FMM?", "ATI0").ConfigureAwait(false);
        var revision = await Query("AT+GMR", "AT+FMR?", "ATI1").ConfigureAwait(false);
        var raw = await Query("ATI3", "ATI4").ConfigureAwait(false);
        Identity = new ModemIdentity(manufacturer, model, revision, raw);
        return Identity;
    }

    public async Task<DialResult> DialAsync(string number, bool tone, TimeSpan timeout, CancellationToken ct)
    {
        _pendingReceiveConnect = false;
        var r = await Cmd($"ATD{(tone ? "T" : "P")}{number}", ct, timeout).ConfigureAwait(false);
        if (r.TimedOut)
        {
            await AbortAsync(ct).ConfigureAwait(false);
            return DialResult.NoAnswer;
        }
        switch (r.Result)
        {
            case "CONNECT":
                _pendingReceiveConnect = true;
                return DialResult.Connected;
            case "BUSY": return DialResult.Busy;
            case "NO DIALTONE": return DialResult.NoDialTone;
            case "NO ANSWER": return DialResult.NoAnswer;
            case "NO CARRIER": return DialResult.NoCarrier;
            default: return DialResult.Error;
        }
    }

    public async Task<bool> AnswerAsync(TimeSpan timeout, CancellationToken ct)
    {
        _pendingTransmitConnect = false;
        var r = await Cmd("ATA", ct, timeout).ConfigureAwait(false);
        if (r.TimedOut) await AbortAsync(ct).ConfigureAwait(false);
        if (r.Result != "CONNECT") return false;
        _pendingTransmitConnect = true;
        return true;
    }

    public async Task HangupAsync(CancellationToken ct)
    {
        _pendingReceiveConnect = _pendingTransmitConnect = false;
        if (!_link.IsConnected) return;
        var r = await Cmd("ATH0", ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!r.IsOk)
        {
            _log.Log("MODEM", "ATH0 not acknowledged; dropping DTR.");
            await _link.Transport.PulseDtrAsync(ct).ConfigureAwait(false);
            _link.Flush();
            await Cmd("ATH0", ct, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }

    /// <summary>Aborts a pending +FRH/+FRM/ATD by sending a character, then waits for the modem to return to command state.</summary>
    private async Task AbortAsync(CancellationToken ct)
    {
        await _link.WriteAsync(new[] { Profile.AbortByte }, ct).ConfigureAwait(false);
        var r = await _link.ReadResultAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (r.TimedOut) _log.Log("MODEM", "Modem did not confirm abort.");
    }

    public async Task<bool> TransmitHdlcAsync(IReadOnlyList<byte[]> frames, CancellationToken ct)
    {
        if (frames.Count == 0) return true;
        if (!_pendingTransmitConnect)
        {
            var r = await Cmd("AT+FTH=3", ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (r.Result != "CONNECT") return false;
        }
        _pendingTransmitConnect = false;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            bool last = i == frames.Count - 1;
            var payload = Dle.Escape(frame);
            var buffer = new byte[payload.Length + 2];
            payload.CopyTo(buffer, 0);
            Dle.Terminator.CopyTo(buffer, payload.Length);
            _link.ReportData(">", $"HDLC {Convert.ToHexString(frame)}");
            await _link.WriteAsync(buffer, ct).ConfigureAwait(false);

            // V.21 is 300 bit/s; allow for the 1 s preamble and the frame itself.
            var timeout = TimeSpan.FromSeconds(4 + frame.Length * 8 / 300.0 * 1.5);
            var r = await _link.ReadResultAsync(timeout, ct).ConfigureAwait(false);
            string expected = last ? "OK" : "CONNECT";
            if (r.Result != expected)
            {
                _log.Log("MODEM", $"HDLC transmit: expected {expected}, got {(r.TimedOut ? "timeout" : r.Result)}.");
                return false;
            }
        }
        return true;
    }

    public async Task<HdlcReceiveResult> ReceiveHdlcAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!_pendingReceiveConnect)
        {
            await _link.WriteCommandAsync("AT+FRH=3", ct).ConfigureAwait(false);
            var r = await _link.ReadResultAsync(timeout, ct).ConfigureAwait(false);
            if (r.TimedOut)
            {
                await AbortAsync(ct).ConfigureAwait(false);
                return HdlcReceiveResult.Failed(HdlcStatus.Timeout);
            }
            switch (r.Result)
            {
                case "CONNECT": break;
                case "NO CARRIER": return HdlcReceiveResult.Failed(HdlcStatus.NoCarrier);
                case "+FCERROR": return HdlcReceiveResult.Failed(HdlcStatus.WrongCarrier);
                default: return HdlcReceiveResult.Failed(HdlcStatus.Error);
            }
        }
        _pendingReceiveConnect = false;

        var decoder = new DleDecoder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (!decoder.Complete)
        {
            var remaining = deadline - DateTime.UtcNow;
            int b = remaining > TimeSpan.Zero ? await _link.ReadByteAsync(remaining, ct).ConfigureAwait(false) : -1;
            if (b < 0)
            {
                await AbortAsync(ct).ConfigureAwait(false);
                return HdlcReceiveResult.Failed(HdlcStatus.Timeout);
            }
            decoder.Feed((byte)b);
            if (decoder.Length > 2048)
            {
                await AbortAsync(ct).ConfigureAwait(false);
                return HdlcReceiveResult.Failed(HdlcStatus.Error);
            }
        }

        var data = decoder.ToArray();
        var result = await _link.ReadResultAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        var frame = Profile.HdlcReceiveIncludesFcs && data.Length >= 2 ? data[..^2] : data;
        _link.ReportData("<", $"HDLC {Convert.ToHexString(frame)} ({(result.IsOk ? "FCS ok" : "FCS error")})");
        return result.Result switch
        {
            "OK" => new HdlcReceiveResult(HdlcStatus.Ok, frame),
            "ERROR" => new HdlcReceiveResult(HdlcStatus.FcsError, frame),
            "NO CARRIER" => new HdlcReceiveResult(HdlcStatus.NoCarrier, frame),
            _ => new HdlcReceiveResult(HdlcStatus.Error, frame),
        };
    }

    public async Task<DataTransmitStatus> TransmitDataAsync(int modulation, ReadOnlyMemory<byte> data, IProgress<long>? progress, CancellationToken ct)
    {
        LastTransmitStatus = DataTransmitStatus.NotStarted;
        var r = await Cmd($"AT+FTM={modulation}", ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (r.Result != "CONNECT")
        {
            _log.Log("MODEM", $"+FTM={modulation} refused: {(r.TimedOut ? "timeout" : r.Result)}.");
            return LastTransmitStatus;
        }
        LastTransmitStatus = DataTransmitStatus.Aborted;
        _link.ReportData(">", $"[{data.Length} bytes at +FTM={modulation}]");

        const int chunk = 1024;
        long sent = 0;
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            int length = Math.Min(chunk, data.Length - offset);
            var escaped = Dle.Escape(data.Span.Slice(offset, length));
            await _link.WriteAsync(escaped, ct).ConfigureAwait(false);
            sent += length;
            progress?.Report(sent);
        }
        await _link.WriteAsync(Dle.Terminator, ct).ConfigureAwait(false);

        // The DCE returns OK once its buffer has drained onto the line.
        var bitRate = Math.Max(2400, ModulationBitRate(modulation));
        var drain = TimeSpan.FromSeconds(30 + data.Length * 8.0 / bitRate);
        var done = await _link.ReadResultAsync(drain, ct).ConfigureAwait(false);
        if (!done.IsOk)
        {
            _log.Log("MODEM", $"+FTM data not acknowledged: {(done.TimedOut ? "timeout" : done.Result)}.");
            return LastTransmitStatus;
        }
        LastTransmitStatus = DataTransmitStatus.Completed;
        return LastTransmitStatus;
    }

    public async Task<DataReceiveResult> ReceiveDataAsync(int modulation, TimeSpan carrierTimeout, TimeSpan maxDuration, CancellationToken ct)
    {
        await _link.WriteCommandAsync($"AT+FRM={modulation}", ct).ConfigureAwait(false);
        var r = await _link.ReadResultAsync(carrierTimeout, ct).ConfigureAwait(false);
        if (r.TimedOut)
        {
            await AbortAsync(ct).ConfigureAwait(false);
            return new DataReceiveResult(DataStatus.Timeout, []);
        }
        switch (r.Result)
        {
            case "CONNECT": break;
            case "+FCERROR": return new DataReceiveResult(DataStatus.WrongCarrier, []);
            case "NO CARRIER": return new DataReceiveResult(DataStatus.NoCarrier, []);
            default: return new DataReceiveResult(DataStatus.Error, []);
        }

        var decoder = new DleDecoder();
        var deadline = DateTime.UtcNow + maxDuration;
        var inactivity = TimeSpan.FromSeconds(10);
        while (!decoder.Complete)
        {
            var remaining = deadline - DateTime.UtcNow;
            int b = remaining > TimeSpan.Zero
                ? await _link.ReadByteAsync(remaining < inactivity ? remaining : inactivity, ct).ConfigureAwait(false)
                : -1;
            if (b < 0)
            {
                await AbortAsync(ct).ConfigureAwait(false);
                _link.ReportData("<", $"[{decoder.Length} bytes at +FRM={modulation}, incomplete]");
                return new DataReceiveResult(DataStatus.Timeout, decoder.ToArray());
            }
            decoder.Feed((byte)b);
        }
        // After DLE ETX the DCE reports NO CARRIER (or OK on some modems).
        await _link.ReadResultAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        _link.ReportData("<", $"[{decoder.Length} bytes at +FRM={modulation}]");
        return new DataReceiveResult(DataStatus.Ok, decoder.ToArray());
    }

    public async Task<bool> TransmitSilenceAsync(int tenMs, CancellationToken ct) =>
        (await Cmd($"AT+FTS={tenMs}", ct, TimeSpan.FromSeconds(3 + tenMs / 100.0)).ConfigureAwait(false)).IsOk;

    public async Task<bool> WaitForSilenceAsync(int tenMs, CancellationToken ct)
    {
        await _link.WriteCommandAsync($"AT+FRS={tenMs}", ct).ConfigureAwait(false);
        var r = await _link.ReadResultAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (r.TimedOut) { await AbortAsync(ct).ConfigureAwait(false); return false; }
        return r.IsOk;
    }

    public async Task<IReadOnlyList<string>> RawCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var r = await Cmd(command, ct, timeout).ConfigureAwait(false);
        var lines = new List<string>(r.Lines) { r.TimedOut ? "(timeout)" : r.Result };
        return lines;
    }

    public async Task<ModemUnsolicitedEvent?> WaitForUnsolicitedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var line = await _link.ReadLineAsync(timeout, ct).ConfigureAwait(false);
        return line is null ? null : ParseUnsolicited(line) ?? new ModemUnsolicitedEvent("OTHER", line);
    }

    private static ModemUnsolicitedEvent? ParseUnsolicited(string line)
    {
        var upper = line.ToUpperInvariant();
        if (upper == "RING" || upper.StartsWith("DRING", StringComparison.Ordinal)) return new ModemUnsolicitedEvent("RING", line);
        var m = CallerIdRegex().Match(line);
        if (m.Success)
        {
            var key = m.Groups[1].Value.ToUpperInvariant();
            var value = m.Groups[2].Value.Trim();
            return key.StartsWith("NMBR", StringComparison.Ordinal) || key.StartsWith("CALLER", StringComparison.Ordinal)
                ? new ModemUnsolicitedEvent("CID", line, CallerNumber: value)
                : key == "NAME" ? new ModemUnsolicitedEvent("CID", line, CallerName: value)
                : new ModemUnsolicitedEvent("CIDINFO", line);
        }
        return null;
    }

    [GeneratedRegex(@"^\s*(NMBR|NAME|DATE|TIME|CALLER NUMBER)\s*[=:]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CallerIdRegex();

    [GeneratedRegex(@"^\s*\+(GM[IMR]|FM[IMR])\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityPrefix();

    /// <summary>Parses "+FCLASS=?" style lists: "0,1,1.0,2,8" or "(0-2,8)".</summary>
    internal static List<string> ParseList(string info)
    {
        var cleaned = info.Replace("(", "").Replace(")", "").Replace("+FCLASS:", "", StringComparison.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var token in cleaned.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            var dash = t.IndexOf('-');
            if (dash > 0 && int.TryParse(t[..dash], out var a) && int.TryParse(t[(dash + 1)..], out var b))
                for (int i = a; i <= b; i++) result.Add(i.ToString());
            else result.Add(t);
        }
        return result.Distinct().ToList();
    }

    internal static List<int> ParseInts(string info) =>
        ParseList(info).Select(s => int.TryParse(s, out var v) ? v : -1).Where(v => v >= 0).ToList();

    internal static int ModulationBitRate(int code) => code switch
    {
        24 => 2400, 48 => 4800, 72 or 73 or 74 => 7200, 96 or 97 or 98 => 9600,
        121 or 122 => 12000, 145 or 146 => 14400, _ => 2400,
    };

    public async ValueTask DisposeAsync()
    {
        // Close the port first: that ends any pending read, so the link's pump can finish.
        _link.BeginShutdown();
        _link.Transport.Close();
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}

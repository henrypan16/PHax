using System.Threading.Channels;
using PHax.Core.Interfaces;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Decoding;
using PHax.Imaging.Encoding;
using PHax.Modem.Class1;
using PHax.Modem.Profiles;
using PHax.Modem.Serial;
using PHax.Modem.T30;

namespace PHax.Service;

public abstract record SupervisorRequest;
public sealed record ReloadRequest : SupervisorRequest;
public sealed record ResetRequest : SupervisorRequest;
public sealed record AnswerRequest : SupervisorRequest;
public sealed record NudgeRequest : SupervisorRequest;
public sealed record SimulateIncomingRequest : SupervisorRequest;
public sealed record ProbeRequest(ModemProbeRequest Request, TaskCompletionSource<ModemProbeResult> Result) : SupervisorRequest;
/// <summary>Releases the modem until <paramref name="Resume"/> completes (used by restore).</summary>
public sealed record SuspendRequest(TaskCompletionSource Suspended, Task Resume) : SupervisorRequest;

/// <summary>
/// The single owner of the modem. All modem I/O — idle monitoring, answering, sending, probing — happens on this loop,
/// so the COM port is never used concurrently.
/// </summary>
public sealed class ModemSupervisor(
    SettingsStore settingsStore,
    IFaxJobQueue queue,
    AppPaths paths,
    StatusHolder status,
    EventBus bus,
    Notifier notifier,
    ServiceProtocolLog protocol,
    OutboundProcessor outbound,
    InboundProcessor inbound,
    ILogger<ModemSupervisor> log) : BackgroundService
{
    private readonly Channel<SupervisorRequest> _requests = Channel.CreateUnbounded<SupervisorRequest>();
    private SupervisorRequest? _deferred;
    private bool _disconnectNotified;
    private SimulatedEnvironment? _simulator;

    public void Enqueue(SupervisorRequest request) => _requests.Writer.TryWrite(request);

    public Task<ModemProbeResult> ProbeAsync(ModemProbeRequest request)
    {
        var tcs = new TaskCompletionSource<ModemProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new ProbeRequest(request, tcs));
        return tcs.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await queue.RecoverAsync(stopping);
        int backoffSeconds = 5;
        while (!stopping.IsCancellationRequested)
        {
            var settings = await settingsStore.LoadAsync(stopping);
            paths.SetStorageRoot(settings.General.StorageDirectory);
            paths.EnsureCreated();

            if (_deferred is not null)
            {
                var deferred = _deferred;
                _deferred = null;
                await HandleWhileClosedAsync(deferred, stopping);
                continue;
            }

            if (!settings.Modem.UseSimulator && string.IsNullOrWhiteSpace(settings.Modem.PortName))
            {
                status.Update(s => s with { ModemState = ModemState.NotConfigured, PortName = null, IsSimulated = false, LastError = "No modem selected. Choose a COM port in Settings > Modem." });
                await WaitWhileClosedAsync(TimeSpan.FromSeconds(30), stopping);
                continue;
            }

            Class1Modem? modem = null;
            try
            {
                modem = await OpenAsync(settings, stopping);
                backoffSeconds = 5;
                _disconnectNotified = false;
                await IdleLoopAsync(modem, settings, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (UnsupportedModemException ex)
            {
                log.LogWarning("Unsupported modem: {Message}", ex.Message);
                status.Update(s => s with { ModemState = ModemState.Unsupported, LastError = ex.Message, FaxClass = null });
                await CloseAsync(modem);
                modem = null;
                await WaitWhileClosedAsync(TimeSpan.FromMinutes(2), stopping);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            {
                var message = PortConflict.IsInUse(ex) ? $"{ex.Message} {PortConflict.Describe(settings.Modem.PortName)}" : ex.Message;
                log.LogWarning("Modem unavailable on {Port}: {Message}", settings.Modem.PortName, message);
                status.Update(s => s with { ModemState = ModemState.Disconnected, LastError = message, Phase = T30Phase.Idle, Call = null });
                if (!_disconnectNotified)
                {
                    _disconnectNotified = true;
                    await notifier.NotifyAsync(NotificationKind.ModemDisconnected, "Modem disconnected", ex.Message, null, settings);
                }
                await CloseAsync(modem);
                modem = null;
                await WaitWhileClosedAsync(TimeSpan.FromSeconds(backoffSeconds), stopping);
                backoffSeconds = Math.Min(60, backoffSeconds * 2);
            }
            finally
            {
                await CloseAsync(modem);
            }
        }
        if (_simulator is not null) await _simulator.DisposeAsync();
    }

    private async Task<Class1Modem> OpenAsync(AppSettings settings, CancellationToken ct)
    {
        var profile = ModemProfiles.Get(settings.Modem.ProfileId);
        ISerialTransport transport;
        if (settings.Modem.UseSimulator)
        {
            if (_simulator is null)
            {
                _simulator = new SimulatedEnvironment();
                await _simulator.StartAsync();
            }
            transport = _simulator.LocalTransport;
            profile = ModemProfiles.GenericClass1;
        }
        else
        {
            if (_simulator is not null) { await _simulator.DisposeAsync(); _simulator = null; }
            transport = new SerialPortTransport(settings.Modem.PortName, settings.Modem.BaudRate, settings.Modem.HardwareFlowControl);
        }

        status.Update(s => s with
        {
            ModemState = ModemState.Opening,
            PortName = transport.Name,
            IsSimulated = transport.IsSimulated,
            LastError = null,
        });
        await transport.OpenAsync(ct);
        var extra = settings.Modem.ExtraInitCommands.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var modem = new Class1Modem(transport, profile, protocol.Log, settings.Modem.HardwareFlowControl, extra, settings.Modem.PreferClass10);
        status.Update(s => s with { ModemState = ModemState.Initializing });
        Class1Capabilities caps;
        try
        {
            caps = await modem.InitializeAsync(ct);
        }
        catch
        {
            // The caller never receives this modem, so it must release the port here; otherwise the handle leaks
            // and every later open fails with "COM3 is in use" (held by this very service).
            await CloseAsync(modem);
            throw;
        }
        status.Update(s => s with
        {
            ModemState = ModemState.Ready,
            ModemIdentity = modem.Identity?.ToString(),
            FaxClass = caps.SelectedClass,
            Phase = T30Phase.Idle,
            LastError = null,
        });
        log.LogInformation("Modem ready on {Port}: {Identity}, Class {Class}{Sim}", transport.Name, modem.Identity, caps.SelectedClass,
            transport.IsSimulated ? " (SIMULATOR)" : "");
        return modem;
    }

    private static async Task CloseAsync(Class1Modem? modem)
    {
        if (modem is null) return;
        try { await modem.DisposeAsync(); } catch { /* already gone */ }
    }

    private Class1FaxEngine CreateEngine(Class1Modem modem) =>
        new(new T4Encoder(), new T4Decoder(), protocol.Log, T30Options.FromProfile(modem.Profile));

    private async Task IdleLoopAsync(Class1Modem modem, AppSettings settings, CancellationToken ct)
    {
        var engine = CreateEngine(modem);
        bool disconnected = false;
        modem.Disconnected += (_, _) => disconnected = true;

        int rings = 0;
        DateTime lastRing = DateTime.MinValue, lastHealth = DateTime.UtcNow;
        string? callerNumber = null, callerName = null;
        bool waitingForManualAnswer = false;

        CallInfoDto CallInfo(string state) => new()
        {
            State = state, RingCount = rings, CallerNumber = callerNumber, CallerName = callerName,
            StartedUtc = DateTime.UtcNow, AwaitingManualAnswer = waitingForManualAnswer,
        };

        void ResetCall()
        {
            rings = 0;
            callerNumber = callerName = null;
            waitingForManualAnswer = false;
            status.Update(s => s with { Call = null });
        }

        async Task AnswerAsync()
        {
            var call = CallInfo("Answering");
            ResetCall();
            await inbound.ReceiveAsync(modem, engine, settings, call, ct);
            lastHealth = DateTime.UtcNow;
        }

        while (!ct.IsCancellationRequested)
        {
            if (disconnected) throw new ModemDisconnectedException("Modem disconnected.");

            while (_requests.Reader.TryRead(out var request))
            {
                switch (request)
                {
                    case ReloadRequest:
                    case ResetRequest:
                        log.LogInformation("Re-initialising modem ({Reason}).", request.GetType().Name);
                        return;
                    case ProbeRequest p when !string.Equals(p.Request.PortName, modem.PortName, StringComparison.OrdinalIgnoreCase):
                        p.Result.TrySetResult(await ModemProbe.RunAsync(p.Request, ct));
                        break;
                    case ProbeRequest or SuspendRequest:
                        // The port must be released first.
                        _deferred = request;
                        return;
                    case AnswerRequest when rings > 0:
                        await AnswerAsync();
                        break;
                    case SimulateIncomingRequest:
                        if (_simulator is not null) _simulator.RequestIncomingFax();
                        break;
                }
            }

            if (settings.Outgoing.EnableSending && rings == 0)
            {
                var job = await queue.TakeNextDueAsync(DateTime.UtcNow, ct);
                if (job is not null)
                {
                    await outbound.SendAsync(modem, engine, job, settings, ct);
                    lastHealth = DateTime.UtcNow;
                    continue;
                }
            }

            var e = await modem.WaitForUnsolicitedAsync(TimeSpan.FromMilliseconds(750), ct);
            if (e is { Kind: "RING" })
            {
                rings = DateTime.UtcNow - lastRing > TimeSpan.FromSeconds(10) ? 1 : rings + 1;
                lastRing = DateTime.UtcNow;
                waitingForManualAnswer = !settings.Incoming.AutoReceive;
                status.Update(s => s with { ModemState = ModemState.Ringing, Call = CallInfo("Ringing") });
                bus.Publish(PipeEvents.IncomingCall, CallInfo("Ringing"));
                log.LogInformation("RING {Count}{Caller}", rings, callerNumber is null ? "" : $" from {DialString.Redact(callerNumber)}");
                if (settings.Incoming.AutoReceive && rings >= Math.Clamp(settings.Incoming.AnswerAfterRings, 1, 10))
                    await AnswerAsync();
            }
            else if (e is { Kind: "CID" })
            {
                callerNumber = e.CallerNumber ?? callerNumber;
                callerName = e.CallerName ?? callerName;
                if (rings > 0) status.Update(s => s with { Call = CallInfo("Ringing") });
            }

            if (rings > 0 && DateTime.UtcNow - lastRing > TimeSpan.FromSeconds(10))
            {
                log.LogInformation("Caller stopped ringing after {Rings} ring(s).", rings);
                ResetCall();
                status.Update(s => s with { ModemState = ModemState.Ready });
            }

            if (rings == 0 && DateTime.UtcNow - lastHealth > TimeSpan.FromSeconds(30))
            {
                lastHealth = DateTime.UtcNow;
                var r = await modem.RawCommandAsync("AT", TimeSpan.FromSeconds(3), ct);
                if (r.LastOrDefault() != "OK") throw new IOException("Modem stopped responding to AT.");
            }
        }
    }

    /// <summary>Waits while no modem is open, servicing requests that do not need one.</summary>
    private async Task WaitWhileClosedAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (await _requests.Reader.WaitToReadAsync(cts.Token))
            {
                if (!_requests.Reader.TryRead(out var request)) continue;
                if (request is ReloadRequest or ResetRequest) return;
                await HandleWhileClosedAsync(request, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task HandleWhileClosedAsync(SupervisorRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case ProbeRequest p:
                status.Update(s => s with { ModemState = ModemState.Opening, LastError = "Testing modem…" });
                p.Result.TrySetResult(await ModemProbe.RunAsync(p.Request, ct));
                break;
            case SuspendRequest s:
                status.Update(st => st with { ModemState = ModemState.Disconnected, LastError = "Suspended for restore." });
                s.Suspended.TrySetResult();
                await s.Resume.WaitAsync(ct);
                break;
            case AnswerRequest:
                break;
        }
    }
}

/// <summary>Opens a port, runs the Class 1 initialisation sequence and reports what the modem supports.</summary>
public static class ModemProbe
{
    public static async Task<ModemProbeResult> RunAsync(ModemProbeRequest request, CancellationToken ct)
    {
        var log = new ProtocolLog(500);
        var transcript = new List<string>();
        log.EntryAdded += e => { if (e.Category.StartsWith("AT", StringComparison.Ordinal) || e.Category == "MODEM") transcript.Add($"{e.Category} {e.Message}"); };
        var transport = new SerialPortTransport(request.PortName, request.BaudRate, request.HardwareFlowControl);
        try
        {
            // Some USB serial drivers block in Open when the device is wedged; never wait forever.
            await Task.Run(() => transport.OpenAsync(ct), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return new ModemProbeResult(false, false, false, null, [], [], [],
                ex is TimeoutException ? $"Opening {request.PortName} did not complete within 10 seconds. Unplug and reconnect the modem."
                : PortConflict.IsInUse(ex) ? $"{ex.Message} {PortConflict.Describe(request.PortName)}"
                : ex.Message,
                transcript);
        }
        await using var modem = new Class1Modem(transport, ModemProfiles.Get(request.ProfileId), log, request.HardwareFlowControl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var caps = await modem.InitializeAsync(timeout.Token);
            await modem.HangupAsync(timeout.Token);
            return new ModemProbeResult(true, true, true, modem.Identity?.ToString(), caps.FaxClasses, caps.TransmitModulations, caps.ReceiveModulations, null, transcript);
        }
        catch (UnsupportedModemException ex)
        {
            return new ModemProbeResult(true, true, false, modem.Identity?.ToString(), ex.Capabilities?.FaxClasses ?? [],
                ex.Capabilities?.TransmitModulations ?? [], ex.Capabilities?.ReceiveModulations ?? [], ex.Message, transcript);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            return new ModemProbeResult(true, false, false, null, [], [], [], ex is OperationCanceledException ? "Modem did not respond in time." : ex.Message, transcript);
        }
    }
}

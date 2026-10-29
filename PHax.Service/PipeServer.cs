using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Data;
using PHax.Modem.Serial;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service;

/// <summary>Named-pipe endpoint for the desktop app. Authenticated local users may connect; network access is denied.</summary>
public sealed class PipeServer(EventBus bus, CommandHandler handler, ILogger<PipeServer> log) : BackgroundService
{
    private readonly List<ClientConnection> _clients = [];

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        bus.Published += Broadcast;
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stopping);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Pipe listen failed");
                await Task.Delay(1000, stopping);
                continue;
            }
            var client = new ClientConnection(server);
            lock (_clients) _clients.Add(client);
            _ = Task.Run(() => ServeAsync(client, stopping), stopping);
        }
        bus.Published -= Broadcast;
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private async Task ServeAsync(ClientConnection client, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var line = await client.Reader.ReadLineAsync(stopping);
                if (line is null) break;
                PipeEnvelope? request;
                try { request = PipeProtocol.Deserialize(line); }
                catch (System.Text.Json.JsonException) { continue; }
                if (request is not { Kind: EnvelopeKind.Request }) continue;
                _ = Task.Run(async () =>
                {
                    var response = new PipeEnvelope { Kind = EnvelopeKind.Response, Id = request.Id, Type = request.Type };
                    try
                    {
                        response.Payload = await handler.HandleAsync(request.Type, request.Payload, stopping);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Command {Type} failed", request.Type);
                        response.Error = ex.Message;
                    }
                    await client.SendAsync(response);
                }, stopping);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_clients) _clients.Remove(client);
            client.Dispose();
        }
    }

    private void Broadcast(PipeEnvelope envelope)
    {
        ClientConnection[] clients;
        lock (_clients) clients = [.. _clients];
        foreach (var c in clients) _ = c.SendAsync(envelope);
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientConnection(NamedPipeServerStream stream)
        {
            _stream = stream;
            Reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        }

        public StreamReader Reader { get; }

        public async Task SendAsync(PipeEnvelope envelope)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_stream.IsConnected) await _writer.WriteLineAsync(PipeProtocol.Serialize(envelope));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch (IOException) { }
        }
    }
}

/// <summary>Dispatches UI commands.</summary>
public sealed class CommandHandler(
    ModemSupervisor supervisor,
    OutboundProcessor outbound,
    StatusHolder status,
    ServiceProtocolLog protocol,
    BackupService backup,
    SettingsStore settingsStore,
    IDbContextFactory<FaxDbContext> dbFactory,
    EventBus bus)
{
    public async Task<System.Text.Json.JsonElement?> HandleAsync(string type, System.Text.Json.JsonElement? payload, CancellationToken ct)
    {
        switch (type)
        {
            case PipeCommands.Ping:
                return PipeProtocol.ToElement("pong");
            case PipeCommands.GetStatus:
                return PipeProtocol.ToElement(status.Current);
            case PipeCommands.JobSubmitted:
                supervisor.Enqueue(new NudgeRequest());
                return null;
            case PipeCommands.CancelJob:
                return PipeProtocol.ToElement(await ChangeJobAsync(Ref(payload), job =>
                {
                    if (outbound.TryCancelActive(job.Id)) return "Cancelling the active transmission.";
                    if (job.Status is OutboundStatus.Queued or OutboundStatus.RetryWaiting or OutboundStatus.Paused)
                    {
                        job.Status = OutboundStatus.Cancelled;
                        job.NextAttemptUtc = null;
                        return "Cancelled.";
                    }
                    return $"A job that is {job.Status} cannot be cancelled.";
                }, ct));
            case PipeCommands.PauseJob:
                return PipeProtocol.ToElement(await ChangeJobAsync(Ref(payload), job =>
                {
                    if (job.Status is not (OutboundStatus.Queued or OutboundStatus.RetryWaiting)) return $"A job that is {job.Status} cannot be paused.";
                    job.Status = OutboundStatus.Paused;
                    return "Paused.";
                }, ct));
            case PipeCommands.ResumeJob:
                return PipeProtocol.ToElement(await ChangeJobAsync(Ref(payload), job =>
                {
                    if (job.Status != OutboundStatus.Paused) return "Only paused jobs can be resumed.";
                    job.Status = job.NextAttemptUtc is null ? OutboundStatus.Queued : OutboundStatus.RetryWaiting;
                    return "Resumed.";
                }, ct));
            case PipeCommands.RetryJob:
            {
                var settings = await settingsStore.LoadAsync(ct);
                return PipeProtocol.ToElement(await ChangeJobAsync(Ref(payload), job =>
                {
                    if (job.Status is not (OutboundStatus.Failed or OutboundStatus.Cancelled or OutboundStatus.Uncertain or OutboundStatus.RetryWaiting))
                        return $"A job that is {job.Status} cannot be retried.";
                    // A manual retry resends the whole document: the user explicitly accepted possible duplicates.
                    job.Status = OutboundStatus.Queued;
                    job.NextAttemptUtc = null;
                    job.PagesConfirmed = 0;
                    job.MaxAttempts = job.Attempts + Math.Max(1, settings.Redial.MaxAttempts);
                    job.QueuedUtc = DateTime.UtcNow;
                    return "Queued for retry.";
                }, ct));
            }
            case PipeCommands.AnswerCall:
                supervisor.Enqueue(new AnswerRequest());
                return null;
            case PipeCommands.ReloadSettings:
                supervisor.Enqueue(new ReloadRequest());
                return null;
            case PipeCommands.ResetModem:
                supervisor.Enqueue(new ResetRequest());
                return null;
            case PipeCommands.ProbeModem:
                var probe = PipeProtocol.FromElement<ModemProbeRequest>(payload) ?? throw new ArgumentException("Missing probe request.");
                var result = await supervisor.ProbeAsync(probe).WaitAsync(TimeSpan.FromSeconds(60), ct);
                supervisor.Enqueue(new ReloadRequest());
                return PipeProtocol.ToElement(result);
            case PipeCommands.GetDiagnostics:
            {
                var entries = protocol.Log.Snapshot();
                var at = entries.Where(e => e.Category is "AT>" or "AT<" or "DATA").TakeLast(1000).Select(e => new DiagnosticEntryDto(e.Utc, e.Category, e.Message)).ToList();
                var events = entries.Where(e => e.Category is not ("AT>" or "AT<" or "DATA")).TakeLast(1000).Select(e => new DiagnosticEntryDto(e.Utc, e.Category, e.Message)).ToList();
                return PipeProtocol.ToElement(new DiagnosticsSnapshot(status.Current, at, events));
            }
            case PipeCommands.ListPorts:
                return PipeProtocol.ToElement(SerialPortDiscovery.List());
            case PipeCommands.ListPrinters:
                return PipeProtocol.ToElement(PHax.Imaging.Conversion.FaxPrinter.InstalledPrinters());
            case PipeCommands.RunBackup:
                return PipeProtocol.ToElement(await backup.RunAsync(scheduled: false, ct));
            case PipeCommands.RestoreBackup:
                var restore = PipeProtocol.FromElement<RestoreRequest>(payload) ?? throw new ArgumentException("Missing backup path.");
                await backup.RestoreAsync(restore.BackupPath, ct);
                return null;
            case "simulateIncoming":
                supervisor.Enqueue(new SimulateIncomingRequest());
                return null;
            default:
                throw new NotSupportedException($"Unknown command '{type}'.");
        }
    }

    private static Guid Ref(System.Text.Json.JsonElement? payload) =>
        PipeProtocol.FromElement<JobRef>(payload)?.JobId ?? throw new ArgumentException("Missing job id.");

    private async Task<string> ChangeJobAsync(Guid id, Func<OutboundFax, string> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.OutboundFaxes.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new KeyNotFoundException("Job not found.");
        var message = change(job);
        job.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        bus.Publish(PipeEvents.JobChanged, new JobRef(id));
        supervisor.Enqueue(new NudgeRequest());
        return message;
    }
}

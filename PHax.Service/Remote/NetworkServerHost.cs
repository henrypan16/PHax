using System.Security.Cryptography.X509Certificates;
using PHax.Core.Ipc;
using PHax.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace PHax.Service.Remote;

/// <summary>
/// Runs the HTTPS listener while <see cref="NetworkConfig"/> says this PC is a server that allows clients. Nothing
/// listens on the network otherwise. Runs next to the modem loop and never touches the modem directly.
/// </summary>
public sealed class NetworkServerHost(
    NetworkConfigStore configStore,
    NetworkServerState state,
    ServerCertificate certificate,
    RemoteApi api,
    ILogger<NetworkServerHost> log) : BackgroundService
{
    private WebApplication? _app;
    // Kestrel uses this for every handshake, so it lives exactly as long as the listener.
    private X509Certificate2? _certificate;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await ApplyAsync(configStore.Load(), stopping);
                await state.WaitForReloadAsync(stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // An exception leaving a hosted service stops the whole host, modem included. Faxing must outlive this.
            log.LogError(ex, "The network listener stopped");
            state.Error = $"The network listener stopped: {ex.Message}";
        }
        await StopListenerAsync();
    }

    private async Task ApplyAsync(NetworkConfig config, CancellationToken ct)
    {
        bool wanted = config.Mode == NetworkMode.Server && config.AllowClients;
        if (wanted && _app is not null && state.Port == config.Port) return;
        await StopListenerAsync();
        bool managesFirewall = WindowsServiceHelpers.IsWindowsService();
        if (!wanted)
        {
            if (managesFirewall) FirewallRule.Remove(log);
            state.Error = null;
            return;
        }
        try
        {
            _certificate = certificate.LoadOrCreate();
            var thumbprint = _certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var app = Build(config.Port, _certificate, thumbprint);
            await app.StartAsync(ct);
            _app = app;
            state.Port = config.Port;
            state.Listening = true;
            state.Error = null;
            if (managesFirewall && !FirewallRule.Allow(config.Port, log))
                state.Error = "Windows Firewall could not be updated, so other PCs may not be able to connect.";
            log.LogInformation("Listening for paired PCs on port {Port}", config.Port);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A port in use or an unreadable certificate is reported in Settings; it must not take the modem down.
            log.LogError(ex, "Could not listen on port {Port}", config.Port);
            state.Error = $"Could not listen on port {config.Port}: {ex.Message}";
            ReleaseCertificate();
        }
    }

    private WebApplication Build(int port, X509Certificate2 cert, string thumbprint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024 * 1024; // raised only for fax uploads, after authentication
            options.Limits.MaxConcurrentConnections = 200;
            // ListenAnyIP, not an explicit address: it falls back to IPv4 on PCs with IPv6 turned off.
            options.ListenAnyIP(port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = cert,
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }));
        });
        builder.Host.UseSerilog(Log.Logger, dispose: false);
        // Event streams end on ApplicationStopping, so nothing needs a long grace period.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            foreach (var converter in PipeProtocol.Json.Converters) o.SerializerOptions.Converters.Add(converter);
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        var app = builder.Build();
        api.Map(app, thumbprint);
        return app;
    }

    private async Task StopListenerAsync()
    {
        state.Listening = false;
        if (_app is not { } app) return;
        _app = null;
        try { await app.StopAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { log.LogWarning(ex, "Stopping the network listener failed"); }
        await app.DisposeAsync();
        ReleaseCertificate();
    }

    private void ReleaseCertificate()
    {
        _certificate?.Dispose();
        _certificate = null;
    }
}

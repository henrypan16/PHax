using PHax.Core.Interfaces;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using PHax.Service.Remote;

namespace PHax.Service;

public static class ServiceRegistration
{
    /// <summary>Registers the fax service components. <paramref name="enablePipe"/> is false in in-process tests.</summary>
    public static IServiceCollection AddPHaxService(this IServiceCollection services, AppPaths paths, bool enablePipe = true)
    {
        services.AddSingleton(paths);
        services.AddDbContextFactory<FaxDbContext>(o => FaxDbContext.Configure(o, paths.DatabasePath));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IFaxJobQueue, DbFaxJobQueue>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<StatusHolder>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<ServiceProtocolLog>();
        services.AddSingleton<OutboundProcessor>();
        services.AddSingleton<InboundProcessor>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<ModemSupervisor>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<NetworkConfigStore>();
        services.AddSingleton<NetworkServerState>();
        services.AddSingleton<ServerCertificate>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<FaxRecords>();
        services.AddSingleton<RemoteApi>();
        services.AddHostedService(sp => sp.GetRequiredService<ModemSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
        services.AddHostedService<NetworkServerHost>();
        if (enablePipe) services.AddHostedService<PipeServer>();
        return services;
    }
}

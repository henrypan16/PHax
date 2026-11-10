using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.App.Views;
using PHax.Core.Integrations;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;

namespace PHax.App;

public partial class App : Application
{
    private const string RestartArgument = "--restart";
    private static IServiceProvider? _services;

    public static MainWindow? Shell { get; private set; }
    public static T GetService<T>() where T : notnull => _services!.GetRequiredService<T>();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstance.TryAcquire(waitForExit: Environment.GetCommandLineArgs().Contains(RestartArgument)))
        {
            // PHax is already running in this session: bring it to the front instead of starting a second copy.
            SingleInstance.ActivateExisting();
            Exit();
            return;
        }

        var paths = new AppPaths();
        string logDir;
        try { paths.EnsureCreated(); logDir = paths.LogsDirectory; }
        catch (UnauthorizedAccessException) { logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PHax", "Logs"); }
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(logDir, $"app-{Environment.UserName}-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        // Read once: the network role decides what the whole app talks to, so changing it restarts the app.
        var networkStore = new NetworkConfigStore(paths);
        var network = networkStore.Load();
        bool isClient = network.Mode == NetworkMode.Client;

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(networkStore);
        services.AddSingleton(network);
        services.AddDbContextFactory<FaxDbContext>(o => FaxDbContext.Configure(o, paths.DatabasePath));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<FaxRecords>();
        services.AddSingleton<ServiceClient>();
        services.AddSingleton<RemoteConnection>();
        services.AddSingleton<IServiceConnection>(sp => isClient ? sp.GetRequiredService<RemoteConnection>() : sp.GetRequiredService<ServiceClient>());
        if (isClient) services.AddSingleton<IFaxData, RemoteFaxData>();
        else services.AddSingleton<IFaxData, LocalFaxData>();
        services.AddSingleton<AppNotifications>();
        services.AddSingleton<FaxDocumentService>();
        services.AddSingleton<ScannerService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<TransmissionPopupController>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<InboxViewModel>();
        services.AddSingleton<DraftsViewModel>();
        services.AddSingleton<OutboxViewModel>();
        services.AddSingleton<SentViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ComposeViewModel>();
        services.AddSingleton<IntegrationHost>();
        services.AddSingleton<IFaxSubmitter>(sp => sp.GetRequiredService<IntegrationHost>());
        services.AddSingleton<IUserNotifier>(sp => sp.GetRequiredService<IntegrationHost>());
        var integrations = IntegrationLoader.Load(services, AppContext.BaseDirectory, IntegrationLoader.AddonsDirectoryFor(AppContext.BaseDirectory));
        foreach (var message in integrations.Messages) Log.Information("{Message}", message);
        services.AddSingleton(integrations);
        _services = services.BuildServiceProvider();

        string? startupError = null;
        try
        {
            await FaxDbContext.InitializeAsync(GetService<IDbContextFactory<FaxDbContext>>());
            var settings = await GetService<SettingsStore>().LoadAsync();
            paths.SetStorageRoot(settings.General.StorageDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Database initialisation failed");
            startupError = $"The fax database could not be opened ({paths.DatabasePath}): {ex.Message}";
        }

        GetService<AppNotifications>().Register();
        Shell = new MainWindow();
        bool minimized = Environment.GetCommandLineArgs().Contains("--minimized");
        if (!minimized) Shell.Activate();
        if (startupError is not null) Shell.ShowStartupError(startupError);
        SingleInstance.Listen(() => UiThread.Post(() => Shell.BringToFront()));
        GetService<TransmissionPopupController>().Start();
        // The local service is still needed on a client PC, to hear about a change of role.
        GetService<ServiceClient>().Start();
        if (isClient) GetService<RemoteConnection>().Start();
        if (startupError is null) StartIntegrations();
    }

    private static void StartIntegrations()
    {
        foreach (var integration in _services!.GetServices<IAppIntegration>())
        {
            try { integration.Start(); }
            catch (Exception ex) { Log.Error(ex, "Integration {Integration} failed to start", integration.Title); }
        }
    }

    /// <summary>Starts a new copy of PHax and closes this one.</summary>
    public static async Task RestartAsync()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, RestartArgument) { UseShellExecute = false });
        if (Shell is not null) await Shell.ExitAsync();
    }

    public static async Task ShutdownAsync()
    {
        GetService<AppNotifications>().Unregister();
        foreach (var integration in _services!.GetServices<IAppIntegration>()) integration.Dispose();
        await GetService<RemoteConnection>().DisposeAsync();
        await GetService<ServiceClient>().DisposeAsync();
        await Log.CloseAndFlushAsync();
        Current.Exit();
    }
}

using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.App.Views;
using PHax.Core.Integrations;
using PHax.Core.Services;
using PHax.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;

namespace PHax.App;

public partial class App : Application
{
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
        if (!SingleInstance.TryAcquire())
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

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddDbContextFactory<FaxDbContext>(o => FaxDbContext.Configure(o, paths.DatabasePath));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ServiceClient>();
        services.AddSingleton<AppNotifications>();
        services.AddSingleton<FaxDocumentService>();
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
        foreach (var message in IntegrationLoader.Load(services, AppContext.BaseDirectory)) Log.Information("{Message}", message);
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
        GetService<ServiceClient>().Start();
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

    public static async Task ShutdownAsync()
    {
        GetService<AppNotifications>().Unregister();
        foreach (var integration in _services!.GetServices<IAppIntegration>()) integration.Dispose();
        await GetService<ServiceClient>().DisposeAsync();
        await Log.CloseAndFlushAsync();
        Current.Exit();
    }
}

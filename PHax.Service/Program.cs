using PHax.Core.Services;
using PHax.Data;
using PHax.Service;
using Microsoft.EntityFrameworkCore;
using Serilog;

// PHax Windows Service: sole owner of the fax modem. Runs as a console app when started interactively.
var paths = new AppPaths();
string? migrationError = null;
try { paths.MigrateLegacyData(); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { migrationError = ex.Message; }
paths.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(Path.Combine(paths.LogsDirectory, "service-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = "PHax");
    builder.Services.AddSerilog();

    builder.Services.AddPHaxService(paths);

    var host = builder.Build();
    await FaxDbContext.InitializeAsync(host.Services.GetRequiredService<IDbContextFactory<FaxDbContext>>());
    Log.Information("PHax service starting. Data root: {Root}", paths.DataRoot);
    if (migrationError is not null) Log.Error("Could not move data from the old FTIFax folder: {Error}", migrationError);
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PHax service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
return 0;

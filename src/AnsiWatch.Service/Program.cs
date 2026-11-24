using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Text;
using AnsiWatch.Service;
using AnsiWatch.Service.Configuration;
using AnsiWatch.Service.Logging;
using AnsiWatch.Service.Status;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

// Zonder deze registratie is codepage 1252 niet beschikbaar in .NET 8.
CodePageSupport.EnsureRegistered();

// Hulpcommando's (gebruikt door de MSI en voor beheer): AnsiWatch.Service.exe <command> ...
if (args.Length > 0 && Cli.IsCommand(args[0]))
{
    return await Cli.RunAsync(args).ConfigureAwait(false);
}

var runningAsService = WindowsServiceHelpers.IsWindowsService();
Log.Logger = LoggingSetup.CreateLogger(console: !runningAsService);
LoggingSetup.ApplyLevel("Information");

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ServiceName);
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(20);
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    });

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger, dispose: false);

    builder.Services.AddSingleton(sp => new ConfigMonitor(sp.GetRequiredService<ILogger<ConfigMonitor>>()));
    builder.Services.AddSingleton<StatusTracker>();
    builder.Services.AddHostedService<AnsiWatchWorker>();

    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "AnsiWatch kon niet starten: {Error}", ex.Message);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

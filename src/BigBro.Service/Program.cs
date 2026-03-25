using BigBro.Common.Config;
using BigBro.Service;
using Microsoft.Extensions.Options;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "BigBroAgent";
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<AgentConfig>(context.Configuration.GetSection("BigBro"));
        services.AddSingleton<CollectorLauncher>();
        services.AddSingleton<CollectorWatchdog>();
        services.AddSingleton<ExportScheduler>();
        services.AddHostedService<ServiceWorker>();
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        // Load from ProgramData config directory
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BigBro", "config");
        config.AddJsonFile(Path.Combine(configPath, "appsettings.json"), optional: true, reloadOnChange: true);
    });

var host = builder.Build();
host.Run();

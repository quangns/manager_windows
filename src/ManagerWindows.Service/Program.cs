using ManagerWindows.Service;
using ManagerWindows.Storage;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServicePaths.ServiceName);

var dataDir = ServicePaths.DataDir;
Directory.CreateDirectory(dataDir);
builder.Services.AddSingleton(new Database(Path.Combine(dataDir, "service.db")));
builder.Services.AddSingleton<UsageRepository>();
builder.Services.AddSingleton<CleanupRunRepository>();
builder.Services.AddSingleton<SystemCleanupService>();
builder.Services.AddHostedService<PipeServerWorker>();
builder.Services.AddHostedService<SystemCleanupWorker>();
builder.Services.AddHostedService<ProcessStartWatcher>();

builder.Build().Run();

using FlyWatcher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "FlyWatcher" });

builder.Logging.AddConsole();
builder.Services.Configure<EventLogSettings>(settings => settings.SourceName = "Flyleaf");
builder.Services.AddWindowsService(options => options.ServiceName = "FlyWatcher");

builder.Services.AddSingleton(WatcherOptions.Parse(args));
builder.Services.AddSingleton<NotesIndex>();
builder.Services.AddHostedService<WatcherService>();
builder.Services.AddHostedService<PipeServer>();

await builder.Build().RunAsync();

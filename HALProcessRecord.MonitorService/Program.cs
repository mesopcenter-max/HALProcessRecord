using HALProcessRecord.Data;
using HALProcessRecord.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "HAL Process Record OPC UA Monitor";
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<OpcUaService>();
builder.Services.AddSingleton<TimingMonitorService>();
builder.Services.AddHostedService<OpcUaSubscriptionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TimingMonitorService>());

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    TimingDatabaseInitializer.Initialize(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

await host.RunAsync();

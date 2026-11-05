using HALProcessRecord.Data;
using HALProcessRecord.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IExcelGeneratorService, ExcelGeneratorService>();
builder.Services.AddSingleton<OpcUaService>();
builder.Services.AddSingleton<TimingMonitorService>();
builder.Services.AddHostedService<OpcUaSubscriptionService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();

    var httpsPort = builder.Configuration["HTTPS_PORT"] ?? builder.Configuration["ASPNETCORE_HTTPS_PORT"];
    if (!string.IsNullOrWhiteSpace(httpsPort))
    {
        app.UseHttpsRedirection();
    }
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    TimingDatabaseInitializer.Initialize(db);
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=ProcessRecord}/{action=Create}/{id?}");

app.Run();

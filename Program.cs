using HALProcessRecord.Data;
using HALProcessRecord.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IExcelGeneratorService, ExcelGeneratorService>();
builder.Services.AddSingleton<OpcUaService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("""
        IF COL_LENGTH('dbo.ProcessRecords', 'DoorOpenValue') IS NULL
            ALTER TABLE [dbo].[ProcessRecords] ADD [DoorOpenValue] bit NOT NULL CONSTRAINT [DF_ProcessRecords_DoorOpenValue] DEFAULT 0;
        IF COL_LENGTH('dbo.ProcessRecords', 'DoorOpenSourceTimestamp') IS NULL
            ALTER TABLE [dbo].[ProcessRecords] ADD [DoorOpenSourceTimestamp] datetime2 NULL;
        IF COL_LENGTH('dbo.ProcessRecords', 'DoorClosedValue') IS NULL
            ALTER TABLE [dbo].[ProcessRecords] ADD [DoorClosedValue] bit NOT NULL CONSTRAINT [DF_ProcessRecords_DoorClosedValue] DEFAULT 0;
        IF COL_LENGTH('dbo.ProcessRecords', 'DoorClosedSourceTimestamp') IS NULL
            ALTER TABLE [dbo].[ProcessRecords] ADD [DoorClosedSourceTimestamp] datetime2 NULL;
        """);
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=ProcessRecord}/{action=Create}/{id?}");

app.Run();

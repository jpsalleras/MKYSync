using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using DbSync.Worker;

var builder = Host.CreateDefaultBuilder(args);

builder.UseWindowsService(options =>
{
    options.ServiceName = "DbSync Worker";
});

builder.ConfigureServices((context, services) =>
{
    // Configuración
    services.Configure<WorkerSettings>(
        context.Configuration.GetSection(WorkerSettings.SectionName));
    services.Configure<SmtpSettings>(
        context.Configuration.GetSection(SmtpSettings.SectionName));

    // SQL Server — base unificada (AppDbContext + CentralRepository en la misma DB)
    var centralCs = context.Configuration.GetConnectionString("CentralRepository")
        ?? throw new InvalidOperationException("CentralRepository connection string es requerido");
    services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(centralCs));
    services.AddSingleton(new CentralRepository(centralCs));

    // Servicios core
    var encryptionKey = context.Configuration["Encryption:Key"]
        ?? throw new InvalidOperationException("Encryption:Key es requerido en appsettings.json");
    services.AddSingleton(new CredentialEncryptor(encryptionKey));
    services.AddScoped<DbObjectExtractor>();
    services.AddScoped<NotificationService>();
    services.AddScoped<SnapshotScanner>();

    // Worker hosted service
    services.AddHostedService<ScanWorker>();
});

var host = builder.Build();

// Crear base y tablas al iniciar
var centralRepo = host.Services.GetRequiredService<CentralRepository>();
await centralRepo.EnsureDatabaseAsync();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

await host.RunAsync();

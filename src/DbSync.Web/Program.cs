using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using DbSync.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// SQL Server — base unificada (AppDbContext + CentralRepository en la misma DB)
var centralCs = builder.Configuration.GetConnectionString("CentralRepository")
    ?? throw new InvalidOperationException("ConnectionStrings:CentralRepository es requerido");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(centralCs));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.Name = "DbSync.Auth";
    options.Cookie.HttpOnly = true;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("DBAOrAdmin", policy => policy.RequireRole("DBA", "Admin"));
    options.AddPolicy("CanExecuteSync", policy => policy.RequireRole("Admin", "Ejecutar"));
});

// Servicios del Core
var encryptionKey = builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key es requerido en appsettings.json");
builder.Services.AddSingleton(new CredentialEncryptor(encryptionKey));
builder.Services.AddScoped<DbObjectExtractor>();
builder.Services.AddScoped<DbComparer>();
builder.Services.AddScoped<ScriptGenerator>();
builder.Services.AddScoped<SyncExecutor>();
builder.Services.AddScoped<VersionHistoryReader>();
builder.Services.AddScoped<VersionControlProvisioner>();
builder.Services.AddScoped<UserClientService>();

// Repositorio central SQL Server (misma base que AppDbContext)
builder.Services.AddSingleton(new CentralRepository(centralCs));
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection(SmtpSettings.SectionName));
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<SnapshotScanner>();
builder.Services.AddScoped<CrossClientComparer>();
builder.Services.AddSingleton<ScanQueue>();
builder.Services.AddHostedService<WebScanBackgroundService>();

// Claude API para sugerencias de merge
builder.Services.Configure<ClaudeSettings>(builder.Configuration.GetSection(ClaudeSettings.SectionName));
builder.Services.AddHttpClient<MergeSuggestionService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

var mvcBuilder = builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
});

if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}

var app = builder.Build();

// Crear base y tablas al iniciar
using (var scope = app.Services.CreateScope())
{
    // 1. CentralRepository crea la DB y sus tablas (raw SQL, IF NOT EXISTS)
    var centralRepo = scope.ServiceProvider.GetRequiredService<CentralRepository>();
    await centralRepo.EnsureDatabaseAsync();

    // 2. EF Core migra las tablas de AppDbContext (Clientes, Identity, etc.)
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Migrar passwords a formato AES (desde texto plano o DPAPI legacy)
    var enc = scope.ServiceProvider.GetRequiredService<CredentialEncryptor>();
    var ambientes = db.Set<ClienteAmbiente>()
        .Where(a => a.PasswordEncrypted != null && a.PasswordEncrypted != "")
        .ToList();
    var migrated = 0;
    var failed = 0;
    foreach (var amb in ambientes)
    {
        var pwd = amb.PasswordEncrypted!;

        // Ya encriptado con AES — no hacer nada
        if (pwd.StartsWith("AES:")) continue;

        // Texto plano (sin prefijo) — encriptar directamente con AES
        if (!enc.IsEncrypted(pwd))
        {
            amb.PasswordEncrypted = enc.Encrypt(pwd);
            migrated++;
            continue;
        }

        // DPAPI legacy (prefijo ENC:) — intentar desencriptar y re-encriptar con AES
        if (enc.NeedsMigration(pwd))
        {
            try
            {
                var plain = enc.Decrypt(pwd);
                amb.PasswordEncrypted = enc.Encrypt(plain);
                migrated++;
            }
            catch
            {
                // DPAPI no puede desencriptar (otro usuario de Windows)
                // Dejar como está; el usuario deberá re-ingresar la password
                failed++;
            }
        }
    }
    if (migrated > 0)
    {
        db.SaveChanges();
        app.Logger.LogInformation("Migradas {Count} contraseñas a formato AES", migrated);
    }
    if (failed > 0)
    {
        app.Logger.LogWarning("{Count} contraseñas DPAPI no pudieron migrarse (encriptadas por otro usuario). Deben re-ingresarse manualmente.", failed);
    }

    // Seed roles y usuario admin
    await SeedIdentityAsync(scope.ServiceProvider);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

static async Task SeedIdentityAsync(IServiceProvider services)
{
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    string[] roles = ["Admin", "DBA", "Reader", "Ejecutar"];
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    var adminEmail = "admin@dbsync.local";
    var adminUser = await userManager.FindByEmailAsync(adminEmail);
    if (adminUser == null)
    {
        adminUser = new ApplicationUser
        {
            UserName = "admin",
            Email = adminEmail,
            NombreCompleto = "Administrador",
            Activo = true,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(adminUser, "Admin123!");
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(adminUser, "Admin");
        }
    }
}

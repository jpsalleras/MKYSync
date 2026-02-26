using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;

// Connection string desde variable de entorno o hardcoded para CLI
var connectionString = Environment.GetEnvironmentVariable("DBSYNC_CONNECTION_STRING")
    ?? "Server=10.159.0.114;Database=MarkeySyncSQL;User Id=jjaramillo;Password=jjaramillo;TrustServerCertificate=true;";
var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseSqlServer(connectionString);
var encryptionKey = Environment.GetEnvironmentVariable("DBSYNC_ENCRYPTION_KEY")
    ?? "MKYSync-DbSync-2024-SecretKey!@#";
var encryptor = new CredentialEncryptor(encryptionKey);

var rootCommand = new RootCommand("DbSync - Sincronización de objetos SQL Server");

// ---- COMPARE ----
var compareCommand = new Command("compare", "Compara objetos entre dos ambientes");
var clienteOpt = new Option<string>("--cliente", "Código del cliente") { IsRequired = true };
var origenOpt = new Option<string>("--origen", () => "DEV", "Ambiente origen");
var destinoOpt = new Option<string>("--destino", () => "QA", "Ambiente destino");
var tipoOpt = new Option<string?>("--tipo", "Filtrar tipo (SP/VIEW/FN)");

compareCommand.AddOption(clienteOpt);
compareCommand.AddOption(origenOpt);
compareCommand.AddOption(destinoOpt);
compareCommand.AddOption(tipoOpt);

compareCommand.SetHandler(async (string codigo, string origen, string destino, string? tipo) =>
{
    await using var db = new AppDbContext(optionsBuilder.Options);
    var extractor = new DbObjectExtractor();
    var comparer = new DbComparer(extractor);

    var cliente = await db.Clientes
        .Include(c => c.Ambientes).Include(c => c.ObjetosCustom)
        .FirstOrDefaultAsync(c => c.Codigo == codigo);

    if (cliente == null)
    {
        WriteColor($"Cliente '{codigo}' no encontrado.", ConsoleColor.Red);
        return;
    }

    Console.WriteLine($"Comparando {cliente.Nombre}: {origen} -> {destino}...\n");

    var summary = await comparer.CompareAsync(
        cliente, Enum.Parse<Ambiente>(origen, true), Enum.Parse<Ambiente>(destino, true), encryptor.Decrypt);

    if (!string.IsNullOrEmpty(tipo))
        summary.Results = summary.Results.Where(r => r.ObjectType.ToShortCode().Equals(tipo, StringComparison.OrdinalIgnoreCase)).ToList();

    WriteColor($"  Iguales:       {summary.TotalEqual}", ConsoleColor.Green);
    WriteColor($"  Modificados:   {summary.TotalModified}", ConsoleColor.Yellow);
    WriteColor($"  Solo {origen}:    {summary.TotalOnlyInSource}", ConsoleColor.Cyan);
    WriteColor($"  Solo {destino}:    {summary.TotalOnlyInTarget}", ConsoleColor.Red);
    WriteColor($"  Custom:        {summary.TotalCustom}", ConsoleColor.Magenta);
    Console.WriteLine($"\n  Total: {summary.TotalObjects} en {summary.Duracion.TotalSeconds:F1}s\n");

    foreach (var r in summary.Results.Where(r => r.Status != CompareStatus.Equal))
    {
        var icon = r.Status switch
        {
            CompareStatus.Modified => "~",
            CompareStatus.OnlyInSource => "+",
            CompareStatus.OnlyInTarget => "-",
            _ => " "
        };
        var color = r.Status switch
        {
            CompareStatus.Modified => ConsoleColor.Yellow,
            CompareStatus.OnlyInSource => ConsoleColor.Cyan,
            CompareStatus.OnlyInTarget => ConsoleColor.Red,
            _ => ConsoleColor.Gray
        };
        var custom = r.IsCustom ? " [CUSTOM]" : "";
        WriteColor($"  [{icon}] {r.ObjectType.ToShortCode(),-5} {r.ObjectFullName}{custom}", color);
    }

}, clienteOpt, origenOpt, destinoOpt, tipoOpt);

// ---- EXPORT ----
var exportCommand = new Command("export", "Exporta objetos a archivos .sql");
var exportClienteOpt = new Option<string>("--cliente", "Código del cliente") { IsRequired = true };
var exportAmbOpt = new Option<string>("--ambiente", () => "PR", "Ambiente a exportar");
var exportPathOpt = new Option<string>("--output", () => "./export", "Carpeta de salida");

exportCommand.AddOption(exportClienteOpt);
exportCommand.AddOption(exportAmbOpt);
exportCommand.AddOption(exportPathOpt);

exportCommand.SetHandler(async (string codigo, string ambiente, string outputPath) =>
{
    await using var db = new AppDbContext(optionsBuilder.Options);
    var extractor = new DbObjectExtractor();

    var cliente = await db.Clientes
        .Include(c => c.Ambientes)
        .FirstOrDefaultAsync(c => c.Codigo == codigo);

    if (cliente == null)
    {
        WriteColor($"Cliente '{codigo}' no encontrado.", ConsoleColor.Red);
        return;
    }

    var amb = Enum.Parse<Ambiente>(ambiente, true);
    var ambConfig = cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
    if (ambConfig == null)
    {
        WriteColor($"Ambiente {ambiente} no configurado.", ConsoleColor.Red);
        return;
    }

    Console.WriteLine($"Exportando {cliente.Nombre} ({ambiente})...");

    var objects = await extractor.ExtractAllAsync(ambConfig.GetConnectionString(encryptor.Decrypt));
    var baseDir = Path.Combine(outputPath, cliente.Codigo, ambiente);
    Directory.CreateDirectory(baseDir);

    foreach (var obj in objects)
    {
        var subDir = obj.ObjectType switch
        {
            DbObjectType.StoredProcedure => "StoredProcedures",
            DbObjectType.View => "Views",
            _ => "Functions"
        };
        var dir = Path.Combine(baseDir, subDir);
        Directory.CreateDirectory(dir);

        var fileName = $"{obj.SchemaName}.{obj.ObjectName}.sql";
        await File.WriteAllTextAsync(Path.Combine(dir, fileName), obj.Definition);
    }

    WriteColor($"Exportados {objects.Count} objetos a {baseDir}", ConsoleColor.Green);

}, exportClienteOpt, exportAmbOpt, exportPathOpt);

// ---- TEST ----
var testCommand = new Command("test", "Prueba conexión a un cliente");
var testClienteOpt = new Option<string>("--cliente", "Código del cliente") { IsRequired = true };
testCommand.AddOption(testClienteOpt);

testCommand.SetHandler(async (string codigo) =>
{
    await using var db = new AppDbContext(optionsBuilder.Options);
    var extractor = new DbObjectExtractor();

    var cliente = await db.Clientes.Include(c => c.Ambientes).FirstOrDefaultAsync(c => c.Codigo == codigo);
    if (cliente == null)
    {
        WriteColor($"Cliente '{codigo}' no encontrado.", ConsoleColor.Red);
        return;
    }

    foreach (var amb in cliente.Ambientes.OrderBy(a => a.Ambiente))
    {
        Console.Write($"  {amb.Ambiente,-4} {amb.Server}/{amb.Database} ... ");
        var (ok, msg) = await extractor.TestConnectionAsync(amb.GetConnectionString(encryptor.Decrypt));
        WriteColor(ok ? "OK" : $"FALLO: {msg}", ok ? ConsoleColor.Green : ConsoleColor.Red);
    }

}, testClienteOpt);

rootCommand.AddCommand(compareCommand);
rootCommand.AddCommand(exportCommand);
rootCommand.AddCommand(testCommand);

// ---- HISTORY ----
var historyCommand = new Command("history", "Consulta historial de versiones de stored procedures");
var histClienteOpt = new Option<string>("--cliente", "Código del cliente") { IsRequired = true };
var histAmbOpt = new Option<string>("--ambiente", () => "PR", "Ambiente");
var histSpOpt = new Option<string?>("--sp", "Nombre del SP (schema.nombre)");
var histTopOpt = new Option<int>("--top", () => 20, "Cantidad de resultados");
var histCompareOpt = new Option<bool>("--compare-last", "Comparar las últimas 2 versiones");

historyCommand.AddOption(histClienteOpt);
historyCommand.AddOption(histAmbOpt);
historyCommand.AddOption(histSpOpt);
historyCommand.AddOption(histTopOpt);
historyCommand.AddOption(histCompareOpt);

historyCommand.SetHandler(async (string codigo, string ambiente, string? spName, int top, bool compareLast) =>
{
    await using var db = new AppDbContext(optionsBuilder.Options);
    var reader = new VersionHistoryReader();

    var cliente = await db.Clientes.Include(c => c.Ambientes).FirstOrDefaultAsync(c => c.Codigo == codigo);
    if (cliente == null) { WriteColor($"Cliente '{codigo}' no encontrado.", ConsoleColor.Red); return; }

    var amb = Enum.Parse<Ambiente>(ambiente, true);
    var ambConfig = cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
    if (ambConfig == null) { WriteColor($"Ambiente {ambiente} no configurado.", ConsoleColor.Red); return; }

    var connStr = ambConfig.GetConnectionString(encryptor.Decrypt);

    // Verificar tabla ObjectChangeHistory
    var (exists, msg) = await reader.CheckVersionControlDbAsync(connStr);
    if (!exists) { WriteColor($"Version Control: {msg}", ConsoleColor.Red); return; }

    if (string.IsNullOrEmpty(spName))
    {
        // Mostrar stats generales
        Console.WriteLine($"Historial de versiones - {cliente.Nombre} ({ambiente})\n");
        var stats = await reader.GetStatsAsync(connStr);

        Console.WriteLine($"{"Objeto",-45} {"Ver",-5} {"Total",-6} {"Última Modificación",-20} {"Contrib.",-5}");
        Console.WriteLine(new string('-', 90));

        foreach (var s in stats.Take(top))
        {
            Console.WriteLine($"{s.FullName,-45} v{s.CurrentVersion,-4} {s.TotalVersions,-6} {s.LastModified:dd/MM/yy HH:mm,-20} {s.TotalContributors,-5}");
        }
        Console.WriteLine($"\nTotal: {stats.Count} objetos versionados");
    }
    else
    {
        // Historial de un SP específico
        var parts = spName.Split('.', 2);
        var schema = parts.Length > 1 ? parts[0] : "dbo";
        var name = parts.Length > 1 ? parts[1] : parts[0];

        var history = await reader.GetHistoryAsync(connStr, schemaName: schema, objectName: name, maxResults: top);

        if (!history.Any())
        {
            WriteColor($"No se encontraron versiones para {spName}", ConsoleColor.Yellow);
            return;
        }

        Console.WriteLine($"Historial de {spName} - {cliente.Nombre} ({ambiente})\n");
        Console.WriteLine($"{"ID",-8} {"Ver",-5} {"Evento",-20} {"Fecha",-22} {"Usuario",-25} {"Host",-15}");
        Console.WriteLine(new string('-', 100));

        foreach (var v in history)
        {
            var color = v.EventType switch
            {
                var e when e.StartsWith("CREATE") => ConsoleColor.Green,
                var e when e.StartsWith("DROP") => ConsoleColor.Red,
                _ => ConsoleColor.Yellow
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"{v.HistoryID,-8} v{v.VersionNumber,-4} {v.EventType,-20} {v.ModifiedDate:dd/MM/yy HH:mm:ss,-22} {v.ModifiedBy,-25} {v.HostName,-15}");
            Console.ResetColor();
        }

        // Comparar últimas 2 versiones si se pidió
        if (compareLast && history.Count >= 2)
        {
            Console.WriteLine($"\n--- Diff: v{history[0].VersionNumber} vs v{history[1].VersionNumber} ---\n");

            var def1 = DbObject.NormalizeDefinition(history[1].ObjectDefinition ?? "");
            var def2 = DbObject.NormalizeDefinition(history[0].ObjectDefinition ?? "");

            if (def1 == def2)
            {
                WriteColor("Las versiones son idénticas.", ConsoleColor.Green);
            }
            else
            {
                // Diff simple en consola (sin HTML)
                var lines1 = def1.Split('\n');
                var lines2 = def2.Split('\n');
                var maxL = Math.Max(lines1.Length, lines2.Length);

                for (int i = 0; i < maxL; i++)
                {
                    var l1 = i < lines1.Length ? lines1[i] : "";
                    var l2 = i < lines2.Length ? lines2[i] : "";

                    if (l1 != l2)
                    {
                        if (!string.IsNullOrWhiteSpace(l1))
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"  - {l1}");
                        }
                        if (!string.IsNullOrWhiteSpace(l2))
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"  + {l2}");
                        }
                        Console.ResetColor();
                    }
                }
            }
        }
    }
}, histClienteOpt, histAmbOpt, histSpOpt, histTopOpt, histCompareOpt);

rootCommand.AddCommand(historyCommand);

return await rootCommand.InvokeAsync(args);

// Helper
static void WriteColor(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Services;
using Npgsql;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Аутентификация Windows (Negotiate/Kerberos).
// На хостинге (Render/Railway) — без AD, поэтому отключаем если нет Negotiate.
var disableAuth = Environment.GetEnvironmentVariable("DISABLE_WINDOWS_AUTH") == "true"
    || Environment.GetEnvironmentVariable("RENDER") == "true";

if (!disableAuth)
{
    builder.Services
        .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();
    builder.Services.AddAuthorization();
}

// MVC и текущие сервисы
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<DataService>();
builder.Services.AddSingleton<IDataService>(sp => sp.GetRequiredService<DataService>());
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// ── Получаем строку подключения ────────────────────────────────────
// Поддерживаем два формата:
// 1. .NET: Host=...;Port=5432;Database=...;Username=...;Password=...
// 2. URL:  postgresql://user:pass@host:port/database
var rawConnStr = builder.Configuration.GetConnectionString("PmoNavigatorDb") ?? "";
var connStr = NormalizeConnectionString(rawConnStr);

static string NormalizeConnectionString(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return raw;

    raw = raw.Trim();

    // Если уже в формате ключ=значение — оставляем как есть
    if (raw.StartsWith("Host=") || raw.StartsWith("Server="))
        return raw;

    // Если URL-формат: postgresql://user:pass@host:port/db
    if (raw.StartsWith("postgresql://") || raw.StartsWith("postgres://"))
    {
        // Удаляем схему
        var rest = raw.Substring(raw.IndexOf("//") + 2);

        // Разделяем credentials@host/db
        string creds = "", hostPart = rest;
        var atIdx = rest.IndexOf('@');
        if (atIdx >= 0)
        {
            creds = rest.Substring(0, atIdx);
            hostPart = rest.Substring(atIdx + 1);
        }

        // credentials: user:pass
        string user = "", pass = "";
        if (!string.IsNullOrEmpty(creds))
        {
            var colonIdx = creds.IndexOf(':');
            if (colonIdx >= 0)
            {
                user = Uri.UnescapeDataString(creds.Substring(0, colonIdx));
                pass = Uri.UnescapeDataString(creds.Substring(colonIdx + 1));
            }
            else
            {
                user = Uri.UnescapeDataString(creds);
            }
        }

        // hostPart: host:port/database?params
        string host = "", port = "5432", database = "";
        var slashIdx = hostPart.IndexOf('/');
        var hostPort = slashIdx >= 0 ? hostPart.Substring(0, slashIdx) : hostPart;
        database = slashIdx >= 0 ? hostPart.Substring(slashIdx + 1) : "";

        // Убираем query string из database
        var qIdx = database.IndexOf('?');
        if (qIdx >= 0) database = database.Substring(0, qIdx);

        // host:port
        var portColon = hostPort.LastIndexOf(':');
        if (portColon >= 0 && int.TryParse(hostPort.Substring(portColon + 1), out _))
        {
            host = hostPort.Substring(0, portColon);
            port = hostPort.Substring(portColon + 1);
        }
        else
        {
            host = hostPort;
        }

        var parts = new List<string> { $"Host={host}", $"Port={port}" };
        if (!string.IsNullOrEmpty(database)) parts.Add($"Database={database}");
        if (!string.IsNullOrEmpty(user)) parts.Add($"Username={user}");
        if (!string.IsNullOrEmpty(pass)) parts.Add($"Password={pass}");
        parts.Add("TrustServerCertificate=true");
        parts.Add("SSL Mode=Require");

        return string.Join(";", parts);
    }

    return raw;
}

Console.WriteLine($"[Startup] Connection string detected: {(connStr.StartsWith("Host=") ? "PostgreSQL (key=value)" : connStr.Contains("Port=5432") ? "PostgreSQL" : "MS SQL / unknown")}");

if (connStr.StartsWith("Host=") || connStr.Contains("Port=5432"))
{
    builder.Services.AddDbContext<PmoDbContext>(options =>
        options.UseNpgsql(connStr));
}
else
{
    builder.Services.AddDbContext<PmoDbContext>(options =>
        options.UseSqlServer(connStr));
}

var app = builder.Build();

// ── Авто-миграция БД при запуске ─────────────────────────────────────
try
{
    if (connStr.StartsWith("Host=") || connStr.Contains("Port=5432"))
    {
        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Database", "migrate_postgres.sql");
        if (!File.Exists(migrationPath))
            migrationPath = Path.Combine(builder.Environment.ContentRootPath, "Database", "migrate_postgres.sql");

        if (File.Exists(migrationPath))
        {
            var sql = await File.ReadAllTextAsync(migrationPath);
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine("[Startup] Database migration completed successfully.");
        }
        else
        {
            Console.WriteLine("[Startup] WARNING: migrate_postgres.sql not found at " + AppContext.BaseDirectory);
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] WARNING: Migration error: {ex.Message}");
}

app.UseStaticFiles();

if (!disableAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

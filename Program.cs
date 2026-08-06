using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Services;
using Npgsql;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Аутентификация Windows (Negotiate/Kerberos).
// На хостинге (Render/Railway) — без AD, поэтому отключаем если нет Negotiate.
// Условие: если переменная окружения DISABLE_WINDOWS_AUTH = "true", пропускаем.
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

// ── Поддержка и MS SQL (LocalDB), и PostgreSQL ──────────────────────
// Определяем провайдера по префиксу строки подключения:
// "Host=" / "Server=localhost;Port=5432" → PostgreSQL (Npgsql)
// "Server=(localdb)..." → SQL Server (старый вариант)
var connStr = builder.Configuration.GetConnectionString("PmoNavigatorDb") ?? "";

if (connStr.StartsWith("Host=") || connStr.Contains("Port=5432"))
{
    // PostgreSQL — лёгкая, безлицензионная СУБД
    builder.Services.AddDbContext<PmoDbContext>(options =>
        options.UseNpgsql(connStr));
}
else
{
    // Fallback на MS SQL LocalDB (для обратной совместимости)
    builder.Services.AddDbContext<PmoDbContext>(options =>
        options.UseSqlServer(connStr));
}

var app = builder.Build();

// ── Авто-миграция БД при запуске ─────────────────────────────────────
// Читаем migrate_postgres.sql и выполняем его. CREATE TABLE IF NOT EXISTS
// гарантирует, что при повторном запуске ничего не сломается.
try
{
    if (connStr.StartsWith("Host=") || connStr.Contains("Port=5432"))
    {
        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Database", "migrate_postgres.sql");
        if (!File.Exists(migrationPath))
        {
            // Пытаемся найти относительно ContentRoot
            migrationPath = Path.Combine(builder.Environment.ContentRootPath, "Database", "migrate_postgres.sql");
        }

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
            Console.WriteLine("[Startup] WARNING: migrate_postgres.sql not found. Skipping migration.");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] WARNING: Migration error: {ex.Message}");
    // Не падаем — приложение всё равно запустится, таблицы могут быть уже созданы
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

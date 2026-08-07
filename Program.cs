using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Services;
using Npgsql;
using System.Text.RegularExpressions;
using System.Linq;

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

    raw = raw.Trim().Trim('(', ')').Trim();

    // Если уже в формате ключ=значение — чистим и возвращаем
    if (raw.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("Server=", StringComparison.OrdinalIgnoreCase))
    {
        // Убираем Markdown-ссылки: [text](url) → text
        // Render dashboard иногда оборачивает hostname в кликабельную ссылку
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"\[([^\]]+)\]\(https?://[^)]+\)", "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Очищаем Host от // префиксов
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"Host\s*=\s*//+", "Host=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!raw.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            raw += ";TrustServerCertificate=true";
        if (!raw.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase))
            raw += ";SSL Mode=Require";
        return raw;
    }

    // Если URL-формат: postgresql://user:pass@host:port/db
    if (raw.StartsWith("postgresql://") || raw.StartsWith("postgres://"))
    {
        // Убираем Markdown-ссылки на всякий случай
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"\[([^\]]+)\]\(https?://[^)]+\)", "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Используем Uri для надёжного парсинга
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port.ToString() : "5432";
            var database = uri.AbsolutePath.TrimStart('/');
            // Убираем query string
            var qIdx = database.IndexOf('?');
            if (qIdx >= 0) database = database.Substring(0, qIdx);

            var userInfo = uri.UserInfo;
            string user = "", pass = "";
            if (!string.IsNullOrEmpty(userInfo))
            {
                var colonIdx = userInfo.IndexOf(':');
                if (colonIdx >= 0)
                {
                    user = Uri.UnescapeDataString(userInfo.Substring(0, colonIdx));
                    pass = Uri.UnescapeDataString(userInfo.Substring(colonIdx + 1));
                }
                else
                {
                    user = Uri.UnescapeDataString(userInfo);
                }
            }

            var parts = new List<string> { $"Host={host}", $"Port={port}" };
            if (!string.IsNullOrEmpty(database)) parts.Add($"Database={database}");
            if (!string.IsNullOrEmpty(user)) parts.Add($"Username={user}");
            if (!string.IsNullOrEmpty(pass)) parts.Add($"Password={pass}");
            parts.Add("TrustServerCertificate=true");
            parts.Add("SSL Mode=Require");

            return string.Join(";", parts);
        }

        // Фолбэк: ручной парсинг если Uri не справился
        var rest = raw.Substring(raw.IndexOf("//") + 2);
        string creds = "", hostPart = rest;
        var atIdx = rest.IndexOf('@');
        if (atIdx >= 0)
        {
            creds = rest.Substring(0, atIdx);
            hostPart = rest.Substring(atIdx + 1);
        }

        string user2 = "", pass2 = "";
        if (!string.IsNullOrEmpty(creds))
        {
            var colonIdx = creds.IndexOf(':');
            if (colonIdx >= 0)
            {
                user2 = Uri.UnescapeDataString(creds.Substring(0, colonIdx));
                pass2 = Uri.UnescapeDataString(creds.Substring(colonIdx + 1));
            }
            else
            {
                user2 = Uri.UnescapeDataString(creds);
            }
        }

        string host2 = "", port2 = "5432", database2 = "";
        var slashIdx = hostPart.IndexOf('/');
        var hostPort = slashIdx >= 0 ? hostPart.Substring(0, slashIdx) : hostPart;
        database2 = slashIdx >= 0 ? hostPart.Substring(slashIdx + 1) : "";
        var qIdx2 = database2.IndexOf('?');
        if (qIdx2 >= 0) database2 = database2.Substring(0, qIdx2);

        var portColon = hostPort.LastIndexOf(':');
        if (portColon >= 0 && int.TryParse(hostPort.Substring(portColon + 1), out _))
        {
            host2 = hostPort.Substring(0, portColon);
            port2 = hostPort.Substring(portColon + 1);
        }
        else
        {
            host2 = hostPort;
        }

        // Чистим host от // и )
        host2 = host2.TrimStart('/').TrimEnd(')');

        var parts2 = new List<string> { $"Host={host2}", $"Port={port2}" };
        if (!string.IsNullOrEmpty(database2)) parts2.Add($"Database={database2}");
        if (!string.IsNullOrEmpty(user2)) parts2.Add($"Username={user2}");
        if (!string.IsNullOrEmpty(pass2)) parts2.Add($"Password={pass2}");
        parts2.Add("TrustServerCertificate=true");
        parts2.Add("SSL Mode=Require");

        return string.Join(";", parts2);
    }

    // Не распознали формат — пытаемся очистить от Markdown и вернуть
    raw = System.Text.RegularExpressions.Regex.Replace(
        raw, @"\[([^\]]+)\]\(https?://[^)]+\)", "$1",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return raw;
}

// Логируем формат строки подключения (без пароля!)
var safeLog = System.Text.RegularExpressions.Regex.Replace(connStr, @"Password=([^;]+)", "Password=***");
Console.WriteLine($"[Startup] Connection string detected: {(connStr.StartsWith("Host=") ? "PostgreSQL (key=value)" : connStr.Contains("Port=5432") ? "PostgreSQL" : connStr.Contains("postgresql://") || connStr.Contains("postgres://") ? "PostgreSQL (URL)" : "MS SQL / unknown")}");
Console.WriteLine($"[Startup] Connection string (masked): {safeLog.Substring(0, Math.Min(60, safeLog.Length))}...");

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
// Выполняем КАЖДЫЙ SQL-statement отдельно (не одним батчем), чтобы ошибка
// в одном (например, в черновом view) не откатывала создание остальных таблиц.
if (connStr.StartsWith("Host=") || connStr.Contains("Port=5432"))
{
    var migrationPath = Path.Combine(AppContext.BaseDirectory, "Database", "migrate_postgres.sql");
    if (!File.Exists(migrationPath))
        migrationPath = Path.Combine(builder.Environment.ContentRootPath, "Database", "migrate_postgres.sql");

    if (File.Exists(migrationPath))
    {
        try
        {
            var rawSql = await File.ReadAllTextAsync(migrationPath);

            // Убираем строки-комментарии (начинающиеся с --), чтобы точки с запятой
            // внутри комментариев не путали разбивку на отдельные statements.
            var cleanedLines = rawSql
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--"))
                .ToArray();
            var cleanedSql = string.Join('\n', cleanedLines);

            var statements = cleanedSql
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            int okCount = 0, failCount = 0;
            foreach (var statement in statements)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(statement, conn);
                    await cmd.ExecuteNonQueryAsync();
                    okCount++;
                }
                catch (Exception stmtEx)
                {
                    failCount++;
                    Console.WriteLine($"[Startup] Migration statement failed (continuing): {stmtEx.Message}");
                }
            }

            Console.WriteLine($"[Startup] Database migration finished: {okCount} statements OK, {failCount} failed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] WARNING: Migration error: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("[Startup] WARNING: migrate_postgres.sql not found at " + AppContext.BaseDirectory);
    }
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

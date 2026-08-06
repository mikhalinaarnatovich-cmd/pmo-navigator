using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Services;

var builder = WebApplication.CreateBuilder(args);

// Аутентификация Windows
builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization();

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

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

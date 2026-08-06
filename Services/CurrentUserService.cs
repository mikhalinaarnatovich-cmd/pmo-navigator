namespace PmoNav.Services;

public interface ICurrentUserService
{
    string Login    { get; }   // TP\arnatovich_m
    string Display  { get; }   // arnatovich_m
    bool   IsKnown  { get; }

    /// <summary>
    /// УБА (управление бизнес-анализа) — единственная группа с правом
    /// редактирования документов проекта (запись в сетевую папку и т.п.).
    /// Всем остальным — только чтение/просмотр.
    ///
    /// Сейчас определяется по списку логинов в appsettings.json ("UbaLogins").
    /// Точка расширения: заменить на реальную проверку членства в AD-группе
    /// через System.DirectoryServices.Protocols (пакет уже подключён в проекте).
    /// </summary>
    bool IsUba { get; }
}

public class CurrentUserService : ICurrentUserService
{
    public CurrentUserService(IHttpContextAccessor acc, IConfiguration config)
    {
        var user = acc.HttpContext?.User;
        var name = user?.Identity?.Name ?? "";

        Login   = name;
        Display = name.Contains('\\') ? name.Split('\\').Last() : name;
        IsKnown = !string.IsNullOrEmpty(name);

        var ubaLogins = config.GetSection("UbaLogins").Get<string[]>() ?? Array.Empty<string>();
        IsUba = ubaLogins.Any(l =>
            string.Equals(l, Login, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l, Display, StringComparison.OrdinalIgnoreCase));
    }

    public string Login   { get; }
    public string Display { get; }
    public bool   IsKnown { get; }
    public bool   IsUba   { get; }
}

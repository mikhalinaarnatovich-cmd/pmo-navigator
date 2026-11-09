using System.Security.Claims;
using PmoNav.Data;
using PmoNav.Models;

namespace PmoNav.Services;

public sealed class NavigatorPermissions(IHttpContextAccessor accessor, IConfiguration config)
{
    private bool Matches(string section, string[] fallback)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return false;
        var identities = new[] { user.Identity.Name, user.FindFirstValue(ClaimTypes.Upn) }.Where(s => !string.IsNullOrEmpty(s));
        return (config.GetSection(section).Get<string[]>() ?? fallback)
            .Any(allowed => identities.Any(id => string.Equals(id, allowed, StringComparison.OrdinalIgnoreCase)));
    }
    public bool FeedbackOwner => Matches("Feedback:AdminLogins", [@"TP\arnatovich_m"]);
    public bool ManagePeriods => Matches("PeriodLocks:Managers", []);
}

public static class ResourceLockPolicy
{
    public static string? ClosedReason(Employee? employee, DateOnly month, IEnumerable<PeriodLockEntity> locks)
    {
        var rows = locks.Where(l => l.PeriodStart == month).ToList();
        var rule = rows.FirstOrDefault(l => l.GroupType == "Department" && l.GroupValue == employee?.Department)
            ?? rows.FirstOrDefault(l => l.GroupType == "Sector" && l.GroupValue == employee?.Sector)
            ?? rows.FirstOrDefault(l => l.GroupType == "All");
        return rule?.IsOpen == false ? $"Редактирование за {month:MM.yyyy} закрыто." : null;
    }
}

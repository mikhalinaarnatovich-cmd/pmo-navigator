using Microsoft.AspNetCore.Mvc;
using PmoNav.Services;

namespace PmoNav.Controllers;

/// <summary>
/// Вкладка "Портфель проектов" (ПК): агрегированные данные для дашбордов
/// "Здоровье портфеля", ранее живших в отдельном Qlik-приложении по ПК.
/// Единый источник данных — тот же IDataService, что и остальной Навигатор,
/// поэтому цифры здесь и в остальном приложении совпадают по построению.
/// Для сверки с Qlik используйте /api/portfolio/quality-check.
/// </summary>
[ApiController]
[Route("api/portfolio")]
public class PortfolioController : ControllerBase
{
    private readonly IDataService _data;

    public PortfolioController(IDataService data)
    {
        _data = data;
    }

    // GET /api/portfolio/health
    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        var all = _data.GetAll()
            .Where(p => p.ProcessType == "Проектная деятельность")
            .ToList();

        var total = all.Count;
        var inProgress = all.Count(p => p.Status == "Выполнение и контроль");
        var needsIntervention = all.Count(p => p.HealthStatus == "Требуется вмешательство");
        var hasRisks = all.Count(p => p.HealthStatus == "Есть риски");
        var onHold = all.Count(p => p.Status == "На паузе (On-Hold)");
        var hardDeadlines = all.Count(p => p.HardDeadline == "Да" || p.HardDeadline == "Yes" || p.HardDeadline == "1");

        var byProjectType = all
            .GroupBy(p => string.IsNullOrWhiteSpace(p.ProjectType) ? "Не указан" : p.ProjectType)
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var byHealth = all
            .GroupBy(p => string.IsNullOrWhiteSpace(p.HealthStatus) ? "Не указан" : p.HealthStatus)
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var byStatus = all
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Status) ? "Не указан" : p.Status)
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        // "Скоринг": Сложность и Риски (X) / Стратегическое соответствие (Y)
        decimal? ParseScore(string s) =>
            decimal.TryParse(s?.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        // Матрица скоринга — как в Qlik ПК: X = Итоговый балл (взвешенный 0-5),
        // Y = Стратегическая важность (Низкая=1/Средняя=2/Высокая=3),
        // размер пузыря = Сложность и Риски (тоже 1-3).
        decimal? ParseLevel(string s) => s?.Trim() switch
        {
            "Низкая" => 1m,
            "Средняя" => 2m,
            "Высокая" => 3m,
            _ => null,
        };

        var scoring = all
            .Select(p => new
            {
                projectId = p.ProjectId,
                name = p.Name,
                x = ParseScore(p.TotalScore),
                y = ParseLevel(p.StrategicImportance),
                size = ParseLevel(p.ComplexityRisk),
            })
            .Where(x => x.x != null && x.y != null)
            .ToList();

        var byDepartment = all
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Department) ? "Не указан" : p.Department)
            .Select(g => new
            {
                department = g.Key,
                total = g.Count(),
                onPlan = g.Count(p => p.HealthStatus == "По плану"),
                risks = g.Count(p => p.HealthStatus == "Есть риски"),
                intervention = g.Count(p => p.HealthStatus == "Требуется вмешательство"),
            })
            .OrderByDescending(x => x.total)
            .ToList();

        var redProjects = all
            .Where(p => p.HealthStatus == "Требуется вмешательство")
            .Select(p => new
            {
                projectId = p.ProjectId,
                name = p.Name,
                owner = p.ProjectManager,
                totalScore = p.TotalScore
            })
            .ToList();

        var deadlines = all
            .Where(p => p.HardDeadline == "Да" || p.HardDeadline == "Yes" || p.HardDeadline == "1")
            .Select(p => new { projectId = p.ProjectId, name = p.Name, deadlineDate = p.DeadlineDate })
            .OrderBy(x => x.deadlineDate)
            .ToList();

        return Ok(new
        {
            kpis = new
            {
                total,
                inProgress,
                needsIntervention,
                hasRisks,
                onHold,
                hardDeadlines
            },
            byProjectType,
            byHealth,
            byStatus,
            scoring,
            byDepartment,
            redProjects,
            deadlines,
            lastLoaded = _data.LastLoaded,
        });
    }

    // GET /api/portfolio/quality-check
    // Простая проверка целостности, чтобы цифры в Навигаторе и в Qlik совпадали:
    // контрольные суммы/счётчики, которые можно сверить с выгрузкой в Qlik.
    [HttpGet("quality-check")]
    public IActionResult QualityCheck()
    {
        var all = _data.GetAll();

        var checksum = new
        {
            totalProjects = all.Count,
            totalMembers = all.Sum(p => p.Members.Count),
            projectsByProcessType = all
                .GroupBy(p => string.IsNullOrWhiteSpace(p.ProcessType) ? "Не указан" : p.ProcessType)
                .Select(g => new { processType = g.Key, count = g.Count() })
                .OrderBy(x => x.processType)
                .ToList(),
            missingHealthStatus = all.Count(p => string.IsNullOrWhiteSpace(p.HealthStatus)),
            missingDepartment = all.Count(p => string.IsNullOrWhiteSpace(p.Department)),
            duplicateProjectIds = all.GroupBy(p => p.ProjectId).Where(g => g.Count() > 1).Select(g => g.Key).ToList(),
            lastLoaded = _data.LastLoaded,
            generatedAt = DateTime.UtcNow,
        };

        return Ok(checksum);
    }
}

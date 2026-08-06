using Microsoft.AspNetCore.Mvc;
using PmoNav.Models;
using PmoNav.Services;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsApiController : ControllerBase
{
    private readonly IDataService _data;
    private readonly ICurrentUserService _user;
    private readonly string _projectsBasePath;
    private readonly IWebHostEnvironment _env;

    public ProjectsApiController(IDataService data, ICurrentUserService user,
        IConfiguration config, IWebHostEnvironment env)
    {
        _data = data;
        _user = user;
        _env = env;
        _projectsBasePath = config["ProjectsBasePath"] ?? @"W:\PMO";
    }

    // GET /api/projects?status=Выполнение+и+контроль&search=авто
    [HttpGet]
    public IActionResult GetList([FromQuery] string? status, [FromQuery] string? search)
    {
        var projects = _data.GetForUser(_user.Login);

        projects = projects.Where(p => p.ProcessType == "Проектная деятельность").ToList();

        if (!string.IsNullOrEmpty(status) && status != "Все")
            projects = projects.Where(p => p.Status == status).ToList();

        if (!string.IsNullOrEmpty(search))
        {
            var q = search.ToLowerInvariant();
            projects = projects.Where(p =>
                p.Name.ToLower().Contains(q) ||
                p.Customer.ToLower().Contains(q) ||
                p.ProjectManager.ToLower().Contains(q) ||
                p.Department.ToLower().Contains(q) ||
                p.ProjectId.ToString().Contains(q)
            ).ToList();
        }

        var statusOrder = new Dictionary<string, int>
        {
            ["Выполнение и контроль"] = 0,
            ["Инициация"] = 1,
            ["Планирование"] = 2,
            ["На паузе (On-Hold)"] = 3,
            ["Завершение"] = 4,
            ["Утвержден (В бэклоге)"] = 5,
            ["Отложен"] = 6,
            ["Завершён"] = 7,
            ["Отменён"] = 8,
        };

        projects = projects
            .OrderBy(p => statusOrder.TryGetValue(p.Status, out var o) ? o : 99)
            .ThenByDescending(p => p.ProjectId)
            .ToList();

        return Ok(new
        {
            total = projects.Count,
            items = projects.Select(p => new {
                p.ProjectId,
                p.Name,
                p.Priority,
                p.Customer,
                p.Curator,
                p.ProjectManager,
                p.Status,
                p.PlanStart,
                p.PlanEnd,
                p.FactEnd,
                p.Department,
                p.InPlan,
                p.HealthStatus,
                p.StrategicImportance,
                p.TotalScore,
                p.HardDeadline,
                p.DeadlineDate,
                p.ProcessType,
            })
        });
    }

    // GET /api/projects/5 — полная карточка
    [HttpGet("{id:int}")]
    public IActionResult GetOne(int id)
    {
        var p = _data.GetById(id);
        if (p == null) return NotFound();

        var folders = GetFolderDocs(p.ProjectId, p.Name);

        return Ok(new
        {
            p.ProjectId,
            p.Name,
            p.Priority,
            p.Department,
            p.InternalDept,
            p.Customer,
            p.Curator,
            p.ProjectManager,
            p.Goal,
            p.Status,
            p.ProjectType,
            p.PlanStart,
            p.PlanEnd,
            p.FactStart,
            p.FactEnd,
            p.InPlan,
            p.ProjectUrl,
            p.ProcessType,
            p.HealthStatus,
            p.StrategicImportance,
            p.TotalScore,
            p.HardDeadline,
            p.DeadlineDate,
            members = p.Members.Select(m => new { m.Person, m.Role }),
            folders,
        });
    }

    // GET /api/projects/statuses
    [HttpGet("statuses")]
    public IActionResult GetStatuses()
    {
        var all = _data.GetForUser(_user.Login);
        var statuses = all
            .Select(p => p.Status)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        return Ok(statuses);
    }

    // GET /api/projects/me
    [HttpGet("me")]
    public IActionResult Me()
    {
        var all = _data.GetForUser(_user.Login);
        return Ok(new
        {
            login = _user.Login,
            display = _user.Display,
            isKnown = _user.IsKnown,
            projectsCount = all.Count,
            lastLoaded = _data.LastLoaded,
        });
    }

    // POST /api/projects/reload
    [HttpPost("reload")]
    public IActionResult Reload([FromServices] DataService ds)
    {
        ds.Load();
        return Ok(new { ok = true, lastLoaded = ds.LastLoaded });
    }

    // GET /api/projects/pk-comments
    [HttpGet("pk-comments")]
    public IActionResult GetPKComments()
    {
        var comments = _data.GetPKComments();
        return Ok(comments);
    }

    // POST /api/projects/pk-comments
    [HttpPost("pk-comments")]
    public IActionResult SavePKComment([FromBody] PKCommentDto dto)
    {
        _data.SavePKComment(dto.ProjectId, dto.Comment ?? "");
        return Ok(new { ok = true });
    }

    // GET /api/projects/diag — диагностика пути к папкам
    [HttpGet("diag")]
    public IActionResult Diag()
    {
        var path = _projectsBasePath;
        bool exists = Directory.Exists(path);
        string[] dirs = Array.Empty<string>();
        string error = "";

        try
        {
            if (exists)
                dirs = Directory.GetDirectories(path)
                                .Select(d => Path.GetFileName(d))
                                .ToArray();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return Ok(new
        {
            configuredPath = path,
            exists,
            dirs,
            error
        });
    }

    // ── документы из сетевой папки ──────────────────────────────────────────
    private List<object> GetFolderDocs(int projectId, string projectName)
    {
        var result = new List<object>();
        if (!Directory.Exists(_projectsBasePath)) return result;

        var folders = Directory.GetDirectories(_projectsBasePath, $"{projectId}_*");
        if (folders.Length == 0)
            folders = Directory.GetDirectories(_projectsBasePath, $"{projectId}*");
        if (folders.Length == 0) return result;

        var projectFolder = folders[0];

        // Подпапки-этапы
        var stageFolders = Directory.GetDirectories(projectFolder)
            .OrderBy(d => d)
            .ToList();

        if (stageFolders.Count > 0)
        {
            foreach (var stageFolder in stageFolders)
            {
                var stageName = Path.GetFileName(stageFolder);
                var files = Directory.GetFiles(stageFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(fi => (object)new
                    {
                        name = fi.Name,
                        path = fi.FullName.Replace('\\', '/'),
                        size = fi.Length,
                        modified = fi.LastWriteTime.ToString("dd.MM.yyyy"),
                        ext = fi.Extension.ToLower().TrimStart('.'),
                    })
                    .ToList();

                result.Add(new { name = stageName, files });
            }

            // Файлы прямо в корне папки проекта → в «Прочее»
            var rootFiles = Directory.GetFiles(projectFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(fi => (object)new
                {
                    name = fi.Name,
                    path = fi.FullName.Replace('\\', '/'),
                    size = fi.Length,
                    modified = fi.LastWriteTime.ToString("dd.MM.yyyy"),
                    ext = fi.Extension.ToLower().TrimStart('.'),
                })
                .ToList();

            if (rootFiles.Count > 0)
                result.Add(new { name = "Прочее", files = rootFiles });
        }
        else
        {
            var files = Directory.GetFiles(projectFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(fi => (object)new
                {
                    name = fi.Name,
                    path = fi.FullName.Replace('\\', '/'),
                    size = fi.Length,
                    modified = fi.LastWriteTime.ToString("dd.MM.yyyy"),
                    ext = fi.Extension.ToLower().TrimStart('.'),
                })
                .ToList();

            result.Add(new { name = "Документы", files });
        }

        return result;
    }

    // ── reads ────────────────────────────────────────────────────────────────
    [HttpPost("/api/reads")]
    public IActionResult SaveRead([FromBody] ReadDto dto)
    {
        var path = Path.Combine(_env.WebRootPath, "data", "reads.json");
        var dict = LoadJson<Dictionary<string, List<string>>>(path) ?? new();
        if (!dict.ContainsKey(dto.Login)) dict[dto.Login] = new();
        if (!dict[dto.Login].Contains(dto.Path)) dict[dto.Login].Add(dto.Path);
        SaveJson(path, dict);
        return Ok(new { ok = true });
    }

    // ── approvals ────────────────────────────────────────────────────────────
    [HttpPost("/api/approvals")]
    public IActionResult SaveApproval([FromBody] ApprovalDto dto)
    {
        var path = Path.Combine(_env.WebRootPath, "data", "approvals.json");
        var dict = LoadJson<Dictionary<string, Dictionary<string, string>>>(path) ?? new();
        if (!dict.ContainsKey(dto.Path)) dict[dto.Path] = new();
        dict[dto.Path][dto.Login] = dto.Status;
        SaveJson(path, dict);
        return Ok(new { ok = true });
    }

    // ── folder listing ───────────────────────────────────────────────────────
    [HttpGet("/api/folder")]
    public IActionResult GetFolder([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Ok(Array.Empty<object>());

        var files = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(fi => (object)new
            {
                name = fi.Name,
                path = fi.FullName.Replace('\\', '/'),
                modified = fi.LastWriteTime.ToString("dd.MM.yyyy"),
                ext = fi.Extension.ToLower().TrimStart('.')
            })
            .ToArray();

        return Ok(files);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static T? LoadJson<T>(string path)
    {
        if (!System.IO.File.Exists(path)) return default;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path)); }
        catch { return default; }
    }

    private static void SaveJson<T>(string path, T obj)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(path,
            System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

// ── DTO ──────────────────────────────────────────────────────────────────────
public record PKCommentDto(int ProjectId, string? Comment);
public record ReadDto(string Login, string Path);
public record ApprovalDto(string Path, string Login, string Status);
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using PmoNav.Models;

namespace PmoNav.Services;

public interface IDataService
{
    List<Project> GetAll();
    Project? GetById(int id);
    List<Project> GetForUser(string login);
    Dictionary<int, string> GetPKComments();
    void SavePKComment(int projectId, string comment);
    DateTime LastLoaded { get; }
}

public class DataService : IDataService
{
    private List<Project> _projects = new();
    private DateTime _lastLoaded = DateTime.MinValue;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DataService> _log;
    private readonly string _dataDir;

    public DataService(IWebHostEnvironment env, ILogger<DataService> log, IConfiguration config)
    {
        _env = env;
        _log = log;
        var cfgPath = config["ProjectsDataPath"];
        _dataDir = !string.IsNullOrWhiteSpace(cfgPath)
            ? cfgPath
            : Path.Combine(_env.WebRootPath, "data");
        Load();
    }

    public DateTime LastLoaded => _lastLoaded;

    public List<Project> GetAll() => _projects;
    public Project? GetById(int id) => _projects.FirstOrDefault(p => p.ProjectId == id);

    public List<Project> GetForUser(string login)
    {
        if (string.IsNullOrEmpty(login)) return _projects;
        var shortLogin = login.Contains('\\') ? login.Split('\\').Last() : login;

        bool IsSamePerson(string person)
        {
            if (string.IsNullOrEmpty(person)) return false;
            var p = person.Trim().ToLowerInvariant();
            return p == login.ToLowerInvariant()
                || p == shortLogin.ToLowerInvariant()
                || p.Replace(" ", "").Contains(shortLogin.ToLowerInvariant().Replace("_", ""));
        }

        var userRoles = _projects
            .SelectMany(p => p.Members)
            .Where(m => IsSamePerson(m.Person))
            .Select(m => m.Role)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (userRoles.Count == 0) return _projects;

        return _projects
            .Where(p => p.Members.Any(m => IsSamePerson(m.Person)))
            .ToList();
    }

    public Dictionary<int, string> GetPKComments()
    {
        var path = Path.Combine(_dataDir, "pk_comments.json");
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<int, string>>(json) ?? new();
    }

    public void SavePKComment(int projectId, string comment)
    {
        var path = Path.Combine(_dataDir, "pk_comments.json");
        var dict = GetPKComments();
        dict[projectId] = comment;
        File.WriteAllText(path,
            System.Text.Json.JsonSerializer.Serialize(dict,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public void Load()
    {
        try
        {
            _log.LogInformation("DataDir = {Dir}", _dataDir);
            _log.LogInformation("File exists = {Exists}", File.Exists(Path.Combine(_dataDir, "projects.csv")));

            var projectsFile = Path.Combine(_dataDir, "projects.csv");
            if (!File.Exists(projectsFile))
            {
                _log.LogWarning("projects.csv не найден по пути: {Path}", projectsFile);
                return;
            }

            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                BadDataFound = null,
                Encoding = System.Text.Encoding.UTF8,
            };

            var dict = new Dictionary<int, Project>();

            using var reader = new StreamReader(projectsFile, System.Text.Encoding.UTF8);
            using var csv = new CsvReader(reader, cfg);

            csv.Read();
            csv.ReadHeader();
            _log.LogInformation("Заголовки: {Headers}", string.Join(" | ", csv.HeaderRecord ?? Array.Empty<string>()));
            int totalMembers = 0;

            while (csv.Read())
            {
                var idStr = csv.GetField("PROJECTID") ?? "";
                if (!int.TryParse(idStr, out var id)) continue;

                if (!dict.TryGetValue(id, out var project))
                {
                    project = csv.GetRecord<Project>()!;
                    project.Members = new List<Member>();
                    dict[id] = project;
                }

                var person = csv.GetField("Сотрудник участник проекта") ?? "";
                var role = csv.GetField("Роль в проекте") ?? "";

                if (!string.IsNullOrWhiteSpace(person))
                {
                    project.Members.Add(new Member
                    {
                        ProjectId = id,
                        Person = person.Trim(),
                        Role = role.Trim(),
                    });
                    totalMembers++;
                }
            }

            _projects = dict.Values.ToList();
            _lastLoaded = DateTime.Now;
            _log.LogInformation("Загружено {Count} проектов, {Mem} участников",
                _projects.Count, totalMembers);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка загрузки данных");
        }
    }
}
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Models;
using System.Globalization;

namespace PmoNav.Controllers;

/// <summary>
/// Единый справочник сотрудников: источник правды для иерархии
/// (руководитель → отдел/сектор → подчинённые) и ставки (FTE),
/// используемой как предел загрузки в ресурсном плане.
/// </summary>
[ApiController]
[Route("api/employees")]
public class EmployeesController : ControllerBase
{
    private readonly PmoDbContext _db;

    public EmployeesController(PmoDbContext db)
    {
        _db = db;
    }

    // GET /api/employees?department=...&sector=...&managerFullName=...
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? department,
        [FromQuery] string? sector,
        [FromQuery] string? managerFullName,
        [FromQuery] bool includeInactive = false)
    {
        var q = _db.Employees.AsNoTracking().AsQueryable();

        if (!includeInactive) q = q.Where(e => e.IsActive);
        if (!string.IsNullOrWhiteSpace(department)) q = q.Where(e => e.Department == department);
        if (!string.IsNullOrWhiteSpace(sector)) q = q.Where(e => e.Sector == sector);
        if (!string.IsNullOrWhiteSpace(managerFullName)) q = q.Where(e => e.ManagerFullName == managerFullName);

        var list = await q.OrderBy(e => e.FullName).ToListAsync();
        return Ok(list);
    }

    // GET /api/employees/departments — для фильтров и группового открытия/закрытия периода
    [HttpGet("departments")]
    public async Task<IActionResult> GetDepartments()
    {
        var departments = await _db.Employees
            .Where(e => e.IsActive && e.Department != null && e.Department != "")
            .Select(e => e.Department!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        var sectors = await _db.Employees
            .Where(e => e.IsActive && e.Sector != null && e.Sector != "")
            .Select(e => e.Sector!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        return Ok(new { departments, sectors });
    }

    // GET /api/employees/team?managerFullName=Иванов+Иван — прямые подчинённые
    [HttpGet("team")]
    public async Task<IActionResult> GetTeam([FromQuery] string managerFullName)
    {
        if (string.IsNullOrWhiteSpace(managerFullName))
            return BadRequest(new { message = "Укажите ФИО руководителя." });

        var team = await _db.Employees
            .AsNoTracking()
            .Where(e => e.IsActive && e.ManagerFullName == managerFullName)
            .OrderBy(e => e.FullName)
            .ToListAsync();

        return Ok(team);
    }

    // POST /api/employees
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Employee dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FullName))
            return BadRequest(new { message = "Укажите ФИО сотрудника." });

        var exists = await _db.Employees.AnyAsync(e => e.FullName == dto.FullName.Trim());
        if (exists)
            return BadRequest(new { message = "Сотрудник с таким ФИО уже есть в справочнике." });

        var entity = new Employee
        {
            FullName = dto.FullName.Trim(),
            Login = dto.Login?.Trim(),
            Department = dto.Department?.Trim(),
            Sector = dto.Sector?.Trim(),
            ManagerFullName = dto.ManagerFullName?.Trim(),
            Rate = dto.Rate <= 0 ? 1.00m : dto.Rate,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Employees.Add(entity);
        await _db.SaveChangesAsync();

        return Ok(entity);
    }

    // PUT /api/employees/5 — правки отдела/сектора/руководителя/ставки
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Employee dto)
    {
        var entity = await _db.Employees.FindAsync(id);
        if (entity == null) return NotFound();

        entity.Department = dto.Department?.Trim();
        entity.Sector = dto.Sector?.Trim();
        entity.ManagerFullName = dto.ManagerFullName?.Trim();
        entity.Login = dto.Login?.Trim();
        entity.Rate = dto.Rate <= 0 ? entity.Rate : dto.Rate;
        entity.IsActive = dto.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    // POST /api/employees/bulk-import — CSV: FullName;Login;Department;Sector;ManagerFullName;Rate
    // Обновляет существующих (по FullName) и создаёт новых. Используется, чтобы
    // подтянуть оргструктуру из HR-выгрузки одним файлом вместо ручного ввода.
    [HttpPost("bulk-import")]
    public async Task<IActionResult> BulkImport(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "Файл не передан." });

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };

        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        using var csv = new CsvReader(reader, cfg);

        csv.Read();
        csv.ReadHeader();

        int created = 0, updated = 0;

        while (csv.Read())
        {
            var fullName = (csv.GetField("FullName") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fullName)) continue;

            var login = csv.GetField("Login")?.Trim();
            var department = csv.GetField("Department")?.Trim();
            var sector = csv.GetField("Sector")?.Trim();
            var manager = csv.GetField("ManagerFullName")?.Trim();
            var rateStr = csv.GetField("Rate")?.Trim();
            decimal.TryParse(rateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate);
            if (rate <= 0) rate = 1.00m;

            var entity = await _db.Employees.FirstOrDefaultAsync(e => e.FullName == fullName);

            if (entity == null)
            {
                _db.Employees.Add(new Employee
                {
                    FullName = fullName,
                    Login = login,
                    Department = department,
                    Sector = sector,
                    ManagerFullName = manager,
                    Rate = rate,
                    IsActive = true,
                });
                created++;
            }
            else
            {
                entity.Login = login;
                entity.Department = department;
                entity.Sector = sector;
                entity.ManagerFullName = manager;
                entity.Rate = rate;
                entity.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, created, updated });
    }
}

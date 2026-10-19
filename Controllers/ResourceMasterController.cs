using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Services;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/resource-plan/master")]
public class ResourceMasterController : ControllerBase
{
    private readonly PmoDbContext _db;
    private readonly IDataService _data;
    public ResourceMasterController(PmoDbContext db, IDataService data) { _db = db; _data = data; }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? department, [FromQuery] string? sector, [FromQuery] string? manager,
        [FromQuery] string? employeeType, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = from.HasValue ? new DateOnly(from.Value.Year, from.Value.Month, 1) : new DateOnly(today.Year, today.Month, 1);
        if (start.Year < 1900 || start.Year > 2100)
            return BadRequest(new { message = "Поддерживаются периоды с 1900 по 2100 год." });
        var end = to.HasValue ? new DateOnly(to.Value.Year, to.Value.Month, 1) : start.AddMonths(2);
        var count = (end.Year - start.Year) * 12 + end.Month - start.Month + 1;
        if (count < 1 || count > 24 || start.Year < 1900 || end.Year > 2100)
            return BadRequest(new { message = "Выберите период от 1 до 24 месяцев, с 1900 по 2100 год." });
        var periods = Enumerable.Range(0, count).Select(start.AddMonths).ToList();
        var directory = await _db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync(ct);
        var scope = directory.Where(e =>
            (string.IsNullOrEmpty(department) || e.Department == department) &&
            (string.IsNullOrEmpty(sector) || (string.IsNullOrWhiteSpace(e.Sector) ? e.Department : e.Sector) == sector) &&
            (string.IsNullOrEmpty(manager) || e.ManagerFullName == manager) &&
            (string.IsNullOrEmpty(employeeType) || e.EmployeeType == employeeType)).ToList();
        var names = scope.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allRows = await _db.ResourceAllocations.AsNoTracking()
            .Where(x => x.PeriodStart >= start && x.PeriodStart <= end).ToListAsync(ct);
        var rows = allRows.Where(x => names.Contains(x.EmployeeName)).ToList();
        var calendar = (await _db.WorkCalendars.AsNoTracking()
            .Where(x => x.Year >= start.Year && x.Year <= end.Year).ToListAsync(ct))
            .ToDictionary(x => new DateOnly(x.Year, x.Month, 1), x => x.WorkingHours > 0 ? (decimal?)x.WorkingHours : null);
        var employeeMap = scope.ToDictionary(x => x.FullName, StringComparer.OrdinalIgnoreCase);
        var projects = _data.GetAll().ToDictionary(x => x.ProjectId);
        var assignments = rows.Select(row => new
        {
            employee = row.EmployeeName, period = row.PeriodStart, kind = row.Kind,
            projectId = row.ProjectId,
            activity = row.Kind == "Project"
                ? (row.ProjectId.HasValue && projects.TryGetValue(row.ProjectId.Value, out var project) ? project.Name : row.ProjectNameSnapshot ?? $"Проект #{row.ProjectId}")
                : row.ActivityName ?? (row.Kind == "Vacation" ? "Отпуск" : "Операционная деятельность"),
            percent = ResourcePlanningMath.OwnPercent(row, employeeMap[row.EmployeeName].Rate),
            fte = ResourcePlanningMath.Fte(row),
            hours = ResourcePlanningMath.Hours(row, calendar.GetValueOrDefault(row.PeriodStart))
        }).ToList();
        var grouped = rows.GroupBy(x => (Name: x.EmployeeName.ToUpperInvariant(), x.PeriodStart))
            .ToDictionary(g => g.Key, g => g.ToList());
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var employees = scope.Select(e => new
        {
            name = e.FullName, department = e.Department ?? "",
            sector = string.IsNullOrWhiteSpace(e.Sector) ? e.Department ?? "" : e.Sector,
            employeeType = e.EmployeeType ?? "", manager = e.ManagerFullName ?? "",
            cells = periods.Select(period =>
            {
                var allocated = grouped.GetValueOrDefault((e.FullName.ToUpperInvariant(), period)) ?? new();
                var rates = allocated.Where(x => x.RateSnapshot > 0).Select(x => x.RateSnapshot!.Value).Distinct().ToList();
                var rate = period < currentMonth && rates.Count == 1 ? rates[0] : e.Rate;
                var planned = allocated.Sum(ResourcePlanningMath.Fte);
                var known = rate > 0;
                return new
                {
                    period, rate = known ? (decimal?)rate : null,
                    plannedFte = planned, freeFte = known ? (decimal?)(rate - planned) : null,
                    percent = known ? (decimal?)(planned / rate * 100m) : null,
                    rateConflict = rates.Any(r => r != rate),
                    calendarHours = calendar.GetValueOrDefault(period)
                };
            }).ToList()
        }).ToList();
        var rosterNames = directory.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ok(new
        {
            periods, employees, assignments,
            missingCalendar = periods.Where(p => !calendar.GetValueOrDefault(p).HasValue).ToList(),
            unassignedRecords = allRows.Count(x => !rosterNames.Contains(x.EmployeeName)),
            filters = new
            {
                departments = directory.Select(e => e.Department).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s),
                sectors = directory.Select(e => string.IsNullOrWhiteSpace(e.Sector) ? e.Department : e.Sector).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s),
                managers = directory.Select(e => e.ManagerFullName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s),
                employeeTypes = directory.Select(e => e.EmployeeType).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s)
            }
        });
    }
}

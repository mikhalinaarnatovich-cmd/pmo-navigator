using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Models;
using PmoNav.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/resource-plan")]
public class ResourcePlanController : ControllerBase
{
    private readonly PmoDbContext _db;
    private readonly IDataService _data;
    private readonly ICurrentUserService _user;
    private readonly DailyWorkCalendar _calendar;
    private readonly NavigatorPermissions _permissions;

    public ResourcePlanController(
        PmoDbContext db,
        IDataService data,
        ICurrentUserService user, DailyWorkCalendar calendar, NavigatorPermissions permissions)
    {
        _db = db;
        _data = data;
        _user = user;
        _calendar = calendar;
        _permissions = permissions;
    }

    // ── Фиксированные справочники "не проектной" деятельности ───────────
    // Вынесены в константы, чтобы список был единым во всём приложении.
    // При необходимости расширить/сделать редактируемым через БД —
    // достаточно завести таблицу-справочник и заменить эти массивы на запрос.
    public static readonly string[] OperationalActivities = new[]
    {
        "Операционная деятельность",
    };

    public static readonly string[] VacationActivities = new[]
    {
        "Отпуск",
    };

    // GET /api/resource-plan/reference
    // Разграничены списки: только проектная деятельность отдельно от
    // операционной деятельности и отпуска — без общего списка "всего подряд".
    [HttpGet("reference")]
    public async Task<IActionResult> GetReference()
    {
        var projects = _data.GetAll()
            .Where(p => p.ProcessType == "Проектная деятельность")
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                projectId = p.ProjectId,
                name = p.Name,
                status = p.Status,
                department = p.Department
            })
            .ToList();

        // Импортированная история не теряет проект, если выгрузка каталога ещё не обновлена.
        var catalogIds = _data.GetAll().Select(p => p.ProjectId).ToHashSet();
        var importedProjects = await _db.ResourceAllocations.AsNoTracking()
            .Where(x => x.Kind == "Project" && x.ProjectId != null && x.ProjectNameSnapshot != null)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new { x.ProjectId, x.ProjectNameSnapshot }).ToListAsync();
        foreach (var p in importedProjects.DistinctBy(x => x.ProjectId))
            if (!catalogIds.Contains(p.ProjectId!.Value))
                projects.Add(new { projectId = p.ProjectId.Value, name = p.ProjectNameSnapshot!,
                    status = "Из сохранённого плана", department = string.Empty });

        var directory = await _db.Employees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.FullName)
            .ToListAsync();

        // Состав и ставки берём только из справочника, не придумываем ставку участникам проектов.
        var employees = directory
            .Select(e => new
            {
                name = e.FullName,
                login = e.Login,
                department = e.Department,
                sector = e.Sector,
                managerFullName = e.ManagerFullName,
                employeeType = e.EmployeeType,
                rate = e.Rate,
                capPercent = 100m,
            })
            .OrderBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            employees,
            projectItems = projects,
            operationalItems = OperationalActivities.Select(a => new { code = a, name = a }),
            vacationItems = VacationActivities.Select(a => new { code = a, name = a }),
        });
    }

    // GET /api/resource-plan?period=2026-08-01&managerFullName=Иванов+Иван
    // managerFullName — режим "моя команда": руководитель видит только своих
    // подчинённых (из единого справочника сотрудников) и кто из них заполнил план.
    [HttpGet]
    public async Task<IActionResult> GetPlan(
        [FromQuery] DateOnly? period,
        [FromQuery] string? managerFullName,
        [FromQuery] string? department,
        [FromQuery] string? sector, [FromQuery] string? employeeType)
    {
        var month = GetMonthStart(period);

        var allocations = await _db.ResourceAllocations
            .AsNoTracking()
            .Where(x => x.PeriodStart == month)
            .OrderBy(x => x.EmployeeName)
            .ThenBy(x => x.ProjectId)
            .ToListAsync();

        var directory = await _db.Employees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .ToListAsync();

        IEnumerable<Models.Employee> scoped = directory;

        if (!string.IsNullOrWhiteSpace(managerFullName))
        {
            scoped = directory.Where(e =>
                string.Equals(e.ManagerFullName, managerFullName, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(department))
            scoped = scoped.Where(e => e.Department == department);
        if (!string.IsNullOrWhiteSpace(sector))
            scoped = scoped.Where(e => (string.IsNullOrWhiteSpace(e.Sector) ? e.Department : e.Sector) == sector);
        if (!string.IsNullOrWhiteSpace(employeeType))
            scoped = scoped.Where(e => e.EmployeeType == employeeType);

        var employeeNames = scoped
            .Select(e => e.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        // Фолбэк: если у руководителя пока никто не привязан в справочнике,
        // не показываем пустоту молча — но и не подменяем это полным списком.
        var directoryByName = directory.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        var calendarHours = await GetWorkingHoursForMonth(month.Year, month.Month);
        var items = allocations.Select(x => new
        {
            id = x.ResourceAllocationId, vacationPeriodId = x.VacationPeriodId, employeeName = x.EmployeeName, employeeLogin = x.EmployeeLogin,
            kind = x.Kind, projectId = x.ProjectId, activityName = x.ActivityName, periodStart = x.PeriodStart,
            allocationPercent = ResourcePlanningMath.OwnPercent(x, directoryByName.GetValueOrDefault(x.EmployeeName)?.Rate ?? 0m),
            fullTimePercent = ResourcePlanningMath.Fte(x) * 100m,
            plannedFte = ResourcePlanningMath.Fte(x),
            plannedHours = ResourcePlanningMath.Hours(x, calendarHours),
            rateSnapshot = x.RateSnapshot, calendarHoursForMonth = x.CalendarHoursForMonth,
            comment = x.Comment, updatedAt = x.UpdatedAt, updatedBy = x.UpdatedBy
        }).ToList();

        var totalByEmployee = items
            .GroupBy(x => x.employeeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Sum(i => i.allocationPercent),
                StringComparer.OrdinalIgnoreCase);

        var employeeStatus = employeeNames
            .Select(name =>
            {
                var total = totalByEmployee.TryGetValue(name, out var value) ? value : 0m;
                var rate = directoryByName.TryGetValue(name, out var emp) ? emp.Rate : 1.00m;
                var cap = 100m;

                var status = rate <= 0 ? "Нет ставки" : total == 0m
                    ? "Не заполнил"
                    : total < cap
                        ? "Частично заполнено"
                        : total > cap + 0.000001m ? "Превышение" : "Заполнено";

                return new
                {
                    employeeName = name,
                    department = directoryByName.TryGetValue(name, out var e2) ? e2.Department : null,
                    sector = directoryByName.TryGetValue(name, out var e3) ? e3.Sector : null,
                    rate,
                    capPercent = cap,
                    totalPercent = total,
                    status
                };
            })
            .ToList();

        var totals = employeeStatus
            .Where(x => x.totalPercent > 0m)
            .Select(x => new { employeeName = x.employeeName, totalPercent = x.totalPercent })
            .ToList();

        var summary = new
        {
            employeesTotal = employeeStatus.Count,
            filledCount = employeeStatus.Count(x => x.status == "Заполнено"),
            partialCount = employeeStatus.Count(x => x.status == "Частично заполнено"),
            emptyCount = employeeStatus.Count(x => x.status == "Не заполнил")
        };

        var locks = await GetLocksForPeriod(month);

        return Ok(new
        {
            period = month,
            items,
            totals,
            employees = employeeStatus,
            summary,
            locks,
            calendarHours,
            canManagePeriods = _permissions.ManagePeriods,
        });
    }

    // GET /api/resource-plan/history?employee=Иванов+Иван
    // Единая точка входа: сотрудник видит историю всех своих прошлых внесений
    // в Навигаторе, не заходя в Excel/Qlik.
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] string employee)
    {
        if (string.IsNullOrWhiteSpace(employee))
            return BadRequest(new { message = "Укажите сотрудника." });

        var rows = await _db.ResourceAllocations
            .AsNoTracking()
            .Where(x => x.EmployeeName == employee.Trim())
            .OrderByDescending(x => x.PeriodStart)
            .ThenBy(x => x.ProjectId)
            .ToListAsync();

        var projectMap = _data.GetAll().ToDictionary(p => p.ProjectId);
        var employeeRate = await _db.Employees.AsNoTracking().Where(e => e.FullName == employee.Trim())
            .Select(e => (decimal?)e.Rate).FirstOrDefaultAsync() ?? 0m;
        var historyCalendar = _calendar.Read();

        var result = rows.Select(x => new
        {
            id = x.ResourceAllocationId,
            periodStart = x.PeriodStart,
            kind = x.Kind,
            projectId = x.ProjectId,
            projectName = x.ProjectId.HasValue && projectMap.TryGetValue(x.ProjectId.Value, out var p)
                ? p.Name
                : x.ProjectNameSnapshot ?? x.ActivityName ?? $"Проект #{x.ProjectId}",
            allocationPercent = ResourcePlanningMath.OwnPercent(x, employeeRate),
            plannedFte = ResourcePlanningMath.Fte(x),
            plannedHours = ResourcePlanningMath.Hours(x, historyCalendar.MonthHours(x.PeriodStart)),
            comment = x.Comment,
            updatedAt = x.UpdatedAt,
            updatedBy = x.UpdatedBy,
            createdAt = x.CreatedAt,
        });

        return Ok(result);
    }

    // GET /api/resource-plan/audit?employee=&period=2026-08-01&projectId=
    // Журнал изменений: кто, когда, что менял, включая удаления.
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] string? employee,
        [FromQuery] DateOnly? period,
        [FromQuery] int? projectId)
    {
        var q = _db.ResourceAllocationAudits.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(employee))
            q = q.Where(x => x.EmployeeName == employee.Trim());

        if (period.HasValue)
            q = q.Where(x => x.PeriodStart == GetMonthStart(period));

        if (projectId.HasValue)
            q = q.Where(x => x.ProjectId == projectId);

        var rows = await q
            .OrderByDescending(x => x.ChangedAt)
            .Take(500)
            .ToListAsync();

        return Ok(rows);
    }

    // ── Открытие/закрытие редактирования по группам ──────────────────────

    // GET /api/resource-plan/locks?period=2026-08-01
    [HttpGet("locks")]
    public async Task<IActionResult> GetLocks([FromQuery] DateOnly? period)
    {
        var month = GetMonthStart(period);
        return Ok(await GetLocksForPeriod(month));
    }

    public record SetLockRequest(DateOnly PeriodStart, string GroupType, List<string> GroupValues, bool IsOpen);

    // POST /api/resource-plan/locks
    // Закрывает/открывает редактирование сразу для группы сотрудников
    // (весь отдел/сектор/все), а не по одному человеку.
    [HttpPost("locks")]
    public async Task<IActionResult> SetLocks([FromBody] SetLockRequest request)
    {
        if (!_permissions.ManagePeriods)
            return StatusCode(403, new { message = "Открывать и закрывать периоды могут только назначенные ответственные после личного входа." });
        if (request.GroupValues == null || request.GroupValues.Count == 0)
            return BadRequest(new { message = "Укажите хотя бы одну группу (отдел/сектор) или \"*\" для всех." });

        var groupType = request.GroupType is "Department" or "Sector" or "All" ? request.GroupType : "All";
        var month = GetMonthStart(request.PeriodStart);
        var now = DateTime.UtcNow;

        foreach (var groupValue in request.GroupValues)
        {
            var existing = await _db.PeriodLocks.FirstOrDefaultAsync(x =>
                x.PeriodStart == month && x.GroupType == groupType && x.GroupValue == groupValue);

            if (existing == null)
            {
                _db.PeriodLocks.Add(new PeriodLockEntity
                {
                    PeriodStart = month,
                    GroupType = groupType,
                    GroupValue = groupValue,
                    IsOpen = request.IsOpen,
                    UpdatedBy = _user.Login,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.IsOpen = request.IsOpen;
                existing.UpdatedBy = _user.Login;
                existing.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private async Task<List<PeriodLockEntity>> GetLocksForPeriod(DateOnly month)
    {
        return await _db.PeriodLocks
            .AsNoTracking()
            .Where(x => x.PeriodStart == month)
            .ToListAsync();
    }

    // Правило приоритета: Department > Sector > All > по умолчанию открыто.
    private async Task<(bool isOpen, string? reason)> IsPeriodOpenForEmployee(string employeeName, DateOnly month)
    {
        var emp = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.FullName == employeeName);

        var locks = await GetLocksForPeriod(month);
        if (locks.Count == 0) return (true, null);

        if (emp?.Department != null)
        {
            var deptLock = locks.FirstOrDefault(l => l.GroupType == "Department" && l.GroupValue == emp.Department);
            if (deptLock != null)
                return (deptLock.IsOpen, deptLock.IsOpen ? null : $"Редактирование закрыто для отдела «{emp.Department}» за {month:MM.yyyy}.");
        }

        if (emp?.Sector != null)
        {
            var sectorLock = locks.FirstOrDefault(l => l.GroupType == "Sector" && l.GroupValue == emp.Sector);
            if (sectorLock != null)
                return (sectorLock.IsOpen, sectorLock.IsOpen ? null : $"Редактирование закрыто для сектора «{emp.Sector}» за {month:MM.yyyy}.");
        }

        var allLock = locks.FirstOrDefault(l => l.GroupType == "All");
        if (allLock != null)
            return (allLock.IsOpen, allLock.IsOpen ? null : $"Редактирование ресурсного плана за {month:MM.yyyy} закрыто.");

        return (true, null);
    }

    // ── Производственный календарь: перевод часов в %% от ставки ────────

    private Task<decimal?> GetWorkingHoursForMonth(int year, int month) =>
        Task.FromResult(_calendar.Read().MonthHours(new DateOnly(year, month, 1)));

    // POST /api/resource-plan
    [HttpPost]
    public async Task<IActionResult> SaveAllocation([FromBody] SaveResourceAllocationRequest request)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var result = await SaveAllocationCore(request);
            if (result is OkObjectResult) await transaction.CommitAsync();
            return result;
        }
        catch (Exception ex) when (IsConcurrentWrite(ex))
        {
            return Conflict(new { message = "План одновременно изменил другой пользователь. Обновите данные и повторите сохранение." });
        }
    }

    private static bool IsConcurrentWrite(Exception ex) =>
        ex is Npgsql.PostgresException { SqlState: "40001" or "40P01" }
        || ex is Microsoft.Data.SqlClient.SqlException { Number: 1205 }
        || (ex.InnerException != null && IsConcurrentWrite(ex.InnerException));

    private async Task<IActionResult> SaveAllocationCore(SaveResourceAllocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeName))
            return BadRequest(new { message = "Укажите сотрудника." });

        var kind = request.Kind is "Project" or "Operational" or "Vacation" ? request.Kind : "Project";
        var employee = request.EmployeeName.Trim();
        var month = GetMonthStart(request.PeriodStart);

        int? projectId = null;
        string? activityName = null;
        string? projectName = null;

        if (kind == "Project")
        {
            if (request.ProjectId is null or <= 0)
                return BadRequest(new { message = "Выберите проект." });

            projectName = _data.GetById(request.ProjectId.Value)?.Name;
            projectName ??= await _db.ResourceAllocations.AsNoTracking()
                .Where(x => x.ProjectId == request.ProjectId && x.ProjectNameSnapshot != null)
                .OrderByDescending(x => x.UpdatedAt).Select(x => x.ProjectNameSnapshot).FirstOrDefaultAsync();
            if (projectName == null)
                return BadRequest(new { message = "Проект не найден в текущем списке проектов." });

            projectId = request.ProjectId;
        }
        else
        {
            var allowed = kind == "Operational" ? OperationalActivities : VacationActivities;
            activityName = string.IsNullOrWhiteSpace(request.ActivityName)
                ? allowed[0]
                : request.ActivityName.Trim();
        }

        // ── Проверка блокировки периода для группы сотрудника ────────────
        var (isOpen, reason) = await IsPeriodOpenForEmployee(employee, month);
        if (!isOpen)
            return BadRequest(new { message = reason ?? "Редактирование за этот период закрыто." });

        var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.FullName == employee);
        if (emp == null || !emp.IsActive || emp.Rate <= 0)
            return BadRequest(new { message = "Сотрудник должен быть активным и иметь заполненную ставку в справочнике." });
        var rate = emp.Rate;
        var calendarHours = await GetWorkingHoursForMonth(month.Year, month.Month);
        if (request.Hours < 0 || request.AllocationPercent < 0)
            return BadRequest(new { message = "Загрузка не может быть отрицательной." });
        if (request.Hours.HasValue && calendarHours == null)
            return BadRequest(new { message = "Для месяца не загружена норма рабочего календаря. Введите процент от ставки; часы пока не рассчитываются." });
        decimal ownPercent = request.Hours.HasValue
            ? Math.Round(request.Hours.Value / (calendarHours!.Value * rate) * 100m, 6)
            : request.AllocationPercent ?? 0m;
        if (ownPercent <= 0 || ownPercent > 100m)
            return BadRequest(new { message = "Введите загрузку больше 0 и не больше 100% от ставки сотрудника." });
        var allocationPercent = Math.Round(ownPercent * rate, 2); // совместимость старого поля
        var plannedHours = request.Hours ?? (calendarHours.HasValue ? Math.Round(calendarHours.Value * rate * ownPercent / 100m, 2) : (decimal?)null);
        var capPercent = 100m;

        var existing = await _db.ResourceAllocations.FirstOrDefaultAsync(x =>
            x.EmployeeName == employee &&
            x.Kind == kind &&
            x.ProjectId == projectId &&
            x.ActivityName == activityName &&
            x.PeriodStart == month);

        var otherRows = await _db.ResourceAllocations.AsNoTracking()
            .Where(x => x.EmployeeName == employee && x.PeriodStart == month &&
                        (existing == null || x.ResourceAllocationId != existing.ResourceAllocationId))
            .ToListAsync();
        if (existing?.VacationPeriodId != null)
            return BadRequest(new { message = "Этот отпуск рассчитан по датам. Измените весь период отпуска в разделе «Отпуск»." });
        if (kind == "Vacation" && otherRows.Any(x => x.VacationPeriodId != null))
            return BadRequest(new { message = "В этом месяце уже есть отпуск по датам. Дополнительный отпуск также укажите по датам, чтобы проверить пересечение." });
        var otherTotal = otherRows.Sum(x => ResourcePlanningMath.OwnPercent(x, rate) ?? 0m);

        var newTotal = otherTotal + ownPercent;

        if (newTotal > capPercent + 0.000001m)
        {
            return BadRequest(new
            {
                message =
                    $"Нельзя сохранить: суммарная загрузка «{employee}» за {month:MM.yyyy} " +
                    $"составит {newTotal:0.##}%. Допустимо не больше 100% от его ставки {rate:0.##}.",
                totalPercent = newTotal,
                capPercent
            });
        }

        var now = DateTime.UtcNow;
        var isCreate = existing == null;
        string? oldJson = existing != null ? JsonSerializer.Serialize(existing) : null;

        if (existing == null)
        {
            existing = new ResourceAllocationEntity
            {
                EmployeeName = employee,
                EmployeeLogin = request.EmployeeLogin?.Trim(),
                Kind = kind,
                ProjectId = projectId,
                ActivityName = activityName,
                PeriodStart = month,
                AllocationPercent = allocationPercent,
                EmployeeAllocationPercent = ownPercent,
                RateSnapshot = rate,
                ProjectNameSnapshot = projectName,
                PlannedHours = plannedHours,
                CalendarHoursForMonth = calendarHours,
                Comment = request.Comment?.Trim(),
                CreatedAt = now,
                CreatedBy = _user.Login,
                UpdatedAt = now,
                UpdatedBy = _user.Login
            };

            _db.ResourceAllocations.Add(existing);
        }
        else
        {
            existing.EmployeeLogin = request.EmployeeLogin?.Trim();
            existing.AllocationPercent = allocationPercent;
            existing.EmployeeAllocationPercent = ownPercent;
            existing.RateSnapshot = rate;
            existing.ProjectNameSnapshot = projectName;
            existing.PlannedHours = plannedHours;
            existing.CalendarHoursForMonth = calendarHours;
            existing.Comment = request.Comment?.Trim();
            existing.UpdatedAt = now;
            existing.UpdatedBy = _user.Login;
        }

        await _db.SaveChangesAsync();

        _db.ResourceAllocationAudits.Add(new ResourceAllocationAuditEntity
        {
            ResourceAllocationId = existing.ResourceAllocationId,
            Action = isCreate ? "Create" : "Update",
            EmployeeName = employee,
            ProjectId = projectId,
            ActivityName = activityName,
            PeriodStart = month,
            OldValueJson = oldJson,
            NewValueJson = JsonSerializer.Serialize(existing),
            ChangedBy = _user.Login,
            ChangedAt = now,
        });
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, id = existing.ResourceAllocationId, totalPercent = newTotal, capPercent });
    }

    // DELETE /api/resource-plan/15
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteAllocation(long id)
    {
        var item = await _db.ResourceAllocations.FindAsync(id);
        if (item == null) return NotFound();
        if (item.VacationPeriodId != null)
            return BadRequest(new { message = "Удалите весь период отпуска в разделе «Отпуск», а не отдельный месяц." });

        var (isOpen, reason) = await IsPeriodOpenForEmployee(item.EmployeeName, item.PeriodStart);
        if (!isOpen)
            return BadRequest(new { message = reason ?? "Редактирование за этот период закрыто." });

        var oldJson = JsonSerializer.Serialize(item);

        _db.ResourceAllocations.Remove(item);
        await _db.SaveChangesAsync();

        _db.ResourceAllocationAudits.Add(new ResourceAllocationAuditEntity
        {
            ResourceAllocationId = id,
            Action = "Delete",
            EmployeeName = item.EmployeeName,
            ProjectId = item.ProjectId,
            ActivityName = item.ActivityName,
            PeriodStart = item.PeriodStart,
            OldValueJson = oldJson,
            NewValueJson = null,
            ChangedBy = _user.Login,
            ChangedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    // GET /api/resource-plan/export/csv?from=2026-08-01&to=2026-10-01
    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var data = await GetExportRows(from, to);
        var csv = new StringBuilder();

        csv.AppendLine(
            "Период;Сотрудник;Логин сотрудника;Вид;ID проекта;Проект/Активность;" +
            "Статус проекта;Подразделение;Загрузка, %;Часы;Комментарий;Обновлено;Кем обновлено");

        foreach (var row in data)
        {
            csv.AppendLine(string.Join(";",
                CsvValue(row.PeriodStart.ToString("yyyy-MM-dd")),
                CsvValue(row.EmployeeName),
                CsvValue(row.EmployeeLogin),
                CsvValue(row.Kind),
                row.ProjectId?.ToString(CultureInfo.InvariantCulture) ?? "",
                CsvValue(row.ProjectName),
                CsvValue(row.ProjectStatus),
                CsvValue(row.Department),
                row.EmployeePercent?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty,
                row.PlannedHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                CsvValue(row.Comment),
                CsvValue(row.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                CsvValue(row.UpdatedBy)));
        }

        var bytes = Encoding.GetEncoding(1251).GetBytes(csv.ToString());
        var fileName = $"resource-plan_{GetMonthStart(from):yyyy-MM}_{GetMonthStart(to):yyyy-MM}.csv";
        return File(bytes, "text/csv; charset=windows-1251", fileName);
    }

    // GET /api/resource-plan/export/excel?from=2026-08-01&to=2026-10-01
    [HttpGet("export/excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var data = await GetExportRows(from, to);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Ресурсный план");

        var headers = new[]
        {
            "Период", "Сотрудник", "Логин сотрудника", "Вид", "ID проекта",
            "Проект/Активность", "Статус проекта", "Подразделение",
            "Загрузка, %", "Часы", "Комментарий", "Обновлено", "Кем обновлено"
        };

        for (var column = 0; column < headers.Length; column++)
        {
            var cell = worksheet.Cell(1, column + 1);
            cell.Value = headers[column];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        var rowIndex = 2;
        foreach (var row in data)
        {
            worksheet.Cell(rowIndex, 1).Value = row.PeriodStart.ToString("yyyy-MM");
            worksheet.Cell(rowIndex, 2).Value = row.EmployeeName;
            worksheet.Cell(rowIndex, 3).Value = row.EmployeeLogin;
            worksheet.Cell(rowIndex, 4).Value = row.Kind;
            worksheet.Cell(rowIndex, 5).Value = row.ProjectId;
            worksheet.Cell(rowIndex, 6).Value = row.ProjectName;
            worksheet.Cell(rowIndex, 7).Value = row.ProjectStatus;
            worksheet.Cell(rowIndex, 8).Value = row.Department;
            if (row.EmployeePercent.HasValue) worksheet.Cell(rowIndex, 9).Value = row.EmployeePercent.Value;
            worksheet.Cell(rowIndex, 10).Value = row.PlannedHours;
            worksheet.Cell(rowIndex, 11).Value = row.Comment;
            worksheet.Cell(rowIndex, 12).Value = row.UpdatedAt;
            worksheet.Cell(rowIndex, 13).Value = row.UpdatedBy;

            worksheet.Cell(rowIndex, 9).Style.NumberFormat.Format = "0.00";
            worksheet.Cell(rowIndex, 12).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
            rowIndex++;
        }

        var tableRange = worksheet.Range(1, 1, Math.Max(1, rowIndex - 1), headers.Length);
        tableRange.CreateTable("ResourcePlan");
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns().AdjustToContents();
        worksheet.Column(11).Width = 45;
        worksheet.Column(11).Style.Alignment.WrapText = true;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var fileName = $"resource-plan_{GetMonthStart(from):yyyy-MM}_{GetMonthStart(to):yyyy-MM}.xlsx";
        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // GET /api/resource-plan/analytics?from=2026-08-01&to=2026-10-01
    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var data = await GetExportRows(from, to);

        var projects = data
            .GroupBy(x => new { x.ProjectId, x.ProjectName, x.ProjectStatus, x.Department })
            .OrderBy(x => x.Key.ProjectName)
            .Select(x => new
            {
                projectId = x.Key.ProjectId,
                projectName = x.Key.ProjectName,
                projectStatus = x.Key.ProjectStatus,
                department = x.Key.Department,
                totalPercent = x.Sum(i => i.AllocationPercent),
                employeesCount = x.Select(i => i.EmployeeName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .ToList();

        var employees = data
            .GroupBy(x => x.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key)
            .Select(x => new
            {
                employeeName = x.Key,
                totalPercent = x.Sum(i => i.AllocationPercent),
                projectsCount = x.Select(i => i.ProjectId).Distinct().Count()
            })
            .ToList();

        var months = data
            .GroupBy(x => x.PeriodStart)
            .OrderBy(x => x.Key)
            .Select(x => new
            {
                period = x.Key,
                totalPercent = x.Sum(i => i.AllocationPercent),
                employeesCount = x.Select(i => i.EmployeeName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                projectsCount = x.Select(i => i.ProjectId).Distinct().Count()
            })
            .ToList();

        return Ok(new { from = GetMonthStart(from), to = GetMonthStart(to), items = data, projects, employees, months });
    }

    private async Task<List<ResourceExportRow>> GetExportRows(DateOnly? from, DateOnly? to)
    {
        var fromMonth = GetMonthStart(from);
        var toMonth = GetMonthStart(to);
        if (toMonth < fromMonth) (fromMonth, toMonth) = (toMonth, fromMonth);

        var projectMap = _data.GetAll().ToDictionary(p => p.ProjectId);
        var rates = await _db.Employees.AsNoTracking().ToDictionaryAsync(e => e.FullName, e => e.Rate);
        var calendar = _calendar.Read();

        var allocations = await _db.ResourceAllocations
            .AsNoTracking()
            .Where(x => x.PeriodStart >= fromMonth && x.PeriodStart <= toMonth)
            .OrderBy(x => x.PeriodStart).ThenBy(x => x.EmployeeName).ThenBy(x => x.ProjectId)
            .ToListAsync();

        return allocations.Select(x =>
        {
            Project? project = x.ProjectId.HasValue && projectMap.TryGetValue(x.ProjectId.Value, out var p) ? p : null;

            return new ResourceExportRow(
                x.PeriodStart,
                x.EmployeeName,
                x.EmployeeLogin ?? string.Empty,
                x.Kind,
                x.ProjectId,
                project?.Name ?? x.ProjectNameSnapshot ?? x.ActivityName ?? $"Проект #{x.ProjectId}",
                project?.Status ?? string.Empty,
                project?.Department ?? string.Empty,
                ResourcePlanningMath.Fte(x) * 100m,
                ResourcePlanningMath.Hours(x, calendar.MonthHours(x.PeriodStart)),
                x.Comment ?? string.Empty,
                x.UpdatedAt,
                x.UpdatedBy ?? string.Empty,
                ResourcePlanningMath.OwnPercent(x, rates.GetValueOrDefault(x.EmployeeName)),
                ResourcePlanningMath.Fte(x));
        }).ToList();
    }

    private static string CsvValue(string? value)
    {
        var text = value ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static DateOnly GetMonthStart(DateOnly? date)
    {
        var source = date ?? DateOnly.FromDateTime(DateTime.Today);
        return new DateOnly(source.Year, source.Month, 1);
    }
}

public record SaveResourceAllocationRequest(
    string? EmployeeName,
    string? EmployeeLogin,
    string? Kind,
    int? ProjectId,
    string? ActivityName,
    DateOnly? PeriodStart,
    decimal? AllocationPercent,
    decimal? Hours,
    string? Comment);

public record ResourceExportRow(
    DateOnly PeriodStart,
    string EmployeeName,
    string EmployeeLogin,
    string Kind,
    int? ProjectId,
    string ProjectName,
    string ProjectStatus,
    string Department,
    decimal AllocationPercent,
    decimal? PlannedHours,
    string Comment,
    DateTime UpdatedAt,
    string UpdatedBy,
    decimal? EmployeePercent,
    decimal PlannedFte);

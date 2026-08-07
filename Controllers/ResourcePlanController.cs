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
    private readonly ILogger<ResourcePlanController> _logger;

    public ResourcePlanController(
        PmoDbContext db,
        IDataService data,
        ICurrentUserService user,
        ILogger<ResourcePlanController> logger)
    {
        _db = db;
        _data = data;
        _user = user;
        _logger = logger;
    }

    // ── Фиксированные справочники "не проектной" деятельности ───────────
    // Вынесены в константы, чтобы список был единым во всём приложении.
    // При необходимости расширить/сделать редактируемым через БД —
    // достаточно завести таблицу-справочник и заменить эти массивы на запрос.
    public static readonly string[] OperationalActivities = new[]
    {
        "Операционная деятельность: техническая поддержка",
        "Операционная деятельность: администрирование систем",
        "Операционная деятельность: совещания и отчётность",
        "Операционная деятельность: обучение и развитие",
        "Операционная деятельность: прочая текучка",
    };

    public static readonly string[] VacationActivities = new[]
    {
        "Отпуск основной",
        "Отпуск за свой счёт",
        "Больничный",
    };

    // GET /api/resource-plan/reference
    // Разграничены списки: только проектная деятельность отдельно от
    // операционной деятельности и отпуска — без общего списка "всего подряд".
    [HttpGet("reference")]
    public async Task<IActionResult> GetReference()
    {
        try
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

        var directory = await _db.Employees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.FullName)
            .ToListAsync();

        // Если справочник ещё не заполнен для кого-то из участников проектов —
        // всё равно показываем его в списке (с дефолтной ставкой 1.0), чтобы
        // не блокировать заполнение плана, пока HR-данные подтягиваются.
        var namesFromProjects = _data.GetAll()
            .SelectMany(p => p.Members)
            .Select(m => m.Person.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var directoryNames = directory.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var employees = directory
            .Select(e => new
            {
                name = e.FullName,
                login = e.Login,
                department = e.Department,
                sector = e.Sector,
                managerFullName = e.ManagerFullName,
                rate = e.Rate,
                capPercent = e.Rate * 100m,
            })
            .Concat(namesFromProjects
                .Where(name => !directoryNames.Contains(name))
                .Select(name => new
                {
                    name,
                    login = (string?)null,
                    department = (string?)null,
                    sector = (string?)null,
                    managerFullName = (string?)null,
                    rate = 1.00m,
                    capPercent = 100m,
                }))
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetReference failed: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.InnerException?.Message, stack = ex.StackTrace?.Substring(0, Math.Min(500, ex.StackTrace.Length)) });
        }
    }

    // GET /api/resource-plan?period=2026-08-01&managerFullName=Иванов+Иван
    // managerFullName — режим "моя команда": руководитель видит только своих
    // подчинённых (из единого справочника сотрудников) и кто из них заполнил план.
    [HttpGet]
    public async Task<IActionResult> GetPlan(
        [FromQuery] DateOnly? period,
        [FromQuery] string? managerFullName)
    {
        var month = GetMonthStart(period);

        var items = await _db.ResourceAllocations
            .AsNoTracking()
            .Where(x => x.PeriodStart == month)
            .OrderBy(x => x.EmployeeName)
            .ThenBy(x => x.ProjectId)
            .Select(x => new
            {
                id = x.ResourceAllocationId,
                employeeName = x.EmployeeName,
                employeeLogin = x.EmployeeLogin,
                kind = x.Kind,
                projectId = x.ProjectId,
                activityName = x.ActivityName,
                periodStart = x.PeriodStart,
                allocationPercent = x.AllocationPercent,
                plannedHours = x.PlannedHours,
                comment = x.Comment,
                updatedAt = x.UpdatedAt,
                updatedBy = x.UpdatedBy
            })
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

        var employeeNames = scoped
            .Select(e => e.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        // Фолбэк: если у руководителя пока никто не привязан в справочнике,
        // не показываем пустоту молча — но и не подменяем это полным списком.
        var directoryByName = directory.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);

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
                var cap = rate * 100m;

                var status = total == 0m
                    ? "Не заполнил"
                    : total < cap
                        ? "Частично заполнено"
                        : "Заполнено";

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

        var result = rows.Select(x => new
        {
            id = x.ResourceAllocationId,
            periodStart = x.PeriodStart,
            kind = x.Kind,
            projectId = x.ProjectId,
            projectName = x.ProjectId.HasValue && projectMap.TryGetValue(x.ProjectId.Value, out var p)
                ? p.Name
                : x.ActivityName,
            allocationPercent = x.AllocationPercent,
            plannedHours = x.PlannedHours,
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
        if (request.GroupValues == null || request.GroupValues.Count == 0)
            return BadRequest(new { message = "Укажите хотя бы одну группу (отдел/сектор) или \"*\" для всех." });

        var groupType = request.GroupType is "Department" or "Sector" or "All" ? request.GroupType : "All";
        var month = GetMonthStart(request.PeriodStart);
        var now = DateTime.Now;

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

    private async Task<decimal> GetWorkingHoursForMonth(int year, int month)
    {
        var row = await _db.WorkCalendars.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Year == year && x.Month == month);

        if (row != null) return row.WorkingHours;

        // Фолбэк, если календарь на этот месяц ещё не заведён администратором:
        // считаем рабочие дни как будни (Пн-Пт) * 8 часов. Это приближение —
        // точные нормы (с учётом праздников/переносов) нужно занести в WorkCalendars.
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var workDays = 0;
        for (var d = 1; d <= daysInMonth; d++)
        {
            var dow = new DateOnly(year, month, d).DayOfWeek;
            if (dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday) workDays++;
        }
        return workDays * 8m;
    }

    // POST /api/resource-plan
    [HttpPost]
    public async Task<IActionResult> SaveAllocation([FromBody] SaveResourceAllocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeName))
            return BadRequest(new { message = "Укажите сотрудника." });

        var kind = request.Kind is "Project" or "Operational" or "Vacation" ? request.Kind : "Project";
        var employee = request.EmployeeName.Trim();
        var month = GetMonthStart(request.PeriodStart);

        int? projectId = null;
        string? activityName = null;

        if (kind == "Project")
        {
            if (request.ProjectId is null or <= 0)
                return BadRequest(new { message = "Выберите проект." });

            if (_data.GetById(request.ProjectId.Value) == null)
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

        // ── Определяем итоговый % загрузки: либо ввели часы, либо % напрямую ──
        decimal? calendarHours = null;
        decimal allocationPercent;

        if (request.Hours is > 0)
        {
            calendarHours = await GetWorkingHoursForMonth(month.Year, month.Month);
            allocationPercent = calendarHours > 0
                ? Math.Round(request.Hours.Value / calendarHours.Value * 100m, 2)
                : 0m;
        }
        else
        {
            allocationPercent = request.AllocationPercent ?? 0m;
        }

        if (allocationPercent < 0)
            return BadRequest(new { message = "Загрузка не может быть отрицательной." });

        // ── Предел загрузки — по ставке (FTE) сотрудника, а не жёстко 100% ──
        // Это и есть логика для многоставочников: у сотрудника с 0.25 ставки
        // предел 25%, а у совместителя с 1.25 ставки — 125%.
        var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.FullName == employee);
        var rate = emp?.Rate ?? 1.00m;
        var capPercent = rate * 100m;

        var existing = await _db.ResourceAllocations.FirstOrDefaultAsync(x =>
            x.EmployeeName == employee &&
            x.Kind == kind &&
            x.ProjectId == projectId &&
            x.ActivityName == activityName &&
            x.PeriodStart == month);

        var otherTotal = await _db.ResourceAllocations
            .Where(x => x.EmployeeName == employee && x.PeriodStart == month &&
                        (existing == null || x.ResourceAllocationId != existing.ResourceAllocationId))
            .SumAsync(x => (decimal?)x.AllocationPercent) ?? 0m;

        var newTotal = otherTotal + allocationPercent;

        if (newTotal > capPercent + 0.01m)
        {
            return BadRequest(new
            {
                message =
                    $"Нельзя сохранить: суммарная загрузка «{employee}» за {month:MM.yyyy} " +
                    $"составит {newTotal:0.##}%, это больше предела по ставке сотрудника " +
                    $"({rate:0.##} ставки = {capPercent:0.##}%).",
                totalPercent = newTotal,
                capPercent
            });
        }

        var now = DateTime.Now;
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
                PlannedHours = request.Hours,
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
            existing.PlannedHours = request.Hours;
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
            ChangedAt = DateTime.Now,
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
                row.AllocationPercent.ToString("0.##", CultureInfo.InvariantCulture),
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
            worksheet.Cell(rowIndex, 9).Value = row.AllocationPercent;
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
                project?.Name ?? x.ActivityName ?? $"Проект #{x.ProjectId}",
                project?.Status ?? string.Empty,
                project?.Department ?? string.Empty,
                x.AllocationPercent,
                x.PlannedHours,
                x.Comment ?? string.Empty,
                x.UpdatedAt,
                x.UpdatedBy ?? string.Empty);
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
    string UpdatedBy);

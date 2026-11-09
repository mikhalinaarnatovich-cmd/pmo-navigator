using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Models;
using PmoNav.Services;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/resource-plan/vacations")]
public class VacationsController(PmoDbContext db, DailyWorkCalendar calendar, ICurrentUserService user) : ControllerBase
{
    public record VacationRequest(Guid Id, int Revision, string EmployeeName, DateOnly StartDate, DateOnly EndDate, string? Comment, string? CalendarVersion);
    public record MonthPart(DateOnly Month, decimal StandardHours, decimal MonthHours, decimal Hours, decimal Percent);
    private sealed class InvalidVacation(string message) : Exception(message);

    private async Task<(Employee Employee, CalendarSnapshot Calendar, List<MonthPart> Parts)> Calculate(VacationRequest request)
    {
        if (request.Id == Guid.Empty || request.StartDate.Year < 2000 || request.EndDate.Year > 2100
            || request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber > 366)
            throw new InvalidVacation("Укажите корректные даты отпуска: начало и конец включительно, не более 367 дней.");
        if ((request.Comment?.Length ?? 0) > 2000) throw new InvalidVacation("Комментарий: не более 2000 символов.");
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.FullName == request.EmployeeName && x.IsActive);
        if (employee == null || employee.Rate <= 0) throw new InvalidVacation("Выберите действующего сотрудника с заполненной ставкой.");
        var snapshot = calendar.Read();
        if (snapshot.Error != null) throw new InvalidVacation(snapshot.Error);
        if (!string.IsNullOrEmpty(request.CalendarVersion) && request.CalendarVersion != snapshot.Version)
            throw new InvalidVacation("Календарь изменился. Повторите расчёт перед сохранением.");
        var parts = new List<MonthPart>();
        for (var month = new DateOnly(request.StartDate.Year, request.StartDate.Month, 1);
            month <= request.EndDate; month = month.AddMonths(1))
        {
            var norm = snapshot.MonthHours(month);
            if (norm == null) throw new InvalidVacation($"Нет полной положительной нормы за {month:MM.yyyy}. Нужна запись на каждый день месяца, включая выходные с 0 часов.");
            decimal selected = 0;
            for (var day = month; day < month.AddMonths(1); day = day.AddDays(1))
                if (day >= request.StartDate && day <= request.EndDate) selected += snapshot.Days[day];
            parts.Add(new(month, selected, norm.Value, Math.Round(selected * employee.Rate, 2),
                Math.Round(selected / norm.Value * 100, 6)));
        }
        return (employee, snapshot, parts);
    }

    [HttpGet("calendar")]
    public IActionResult CalendarStatus()
    {
        var snapshot = calendar.Read();
        return Ok(new { available = snapshot.Error == null, message = snapshot.Error, version = snapshot.Version,
            firstDate = snapshot.Days.Count > 0 ? (DateOnly?)snapshot.Days.Keys.Min() : null,
            lastDate = snapshot.Days.Count > 0 ? (DateOnly?)snapshot.Days.Keys.Max() : null });
    }

    [HttpPost("calculate")]
    public async Task<IActionResult> Preview(VacationRequest request)
    {
        try
        {
            var result = await Calculate(request);
            return Ok(new { calendarVersion = result.Calendar.Version, rate = result.Employee.Rate, parts = result.Parts });
        }
        catch (InvalidVacation ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string employee)
    {
        if (string.IsNullOrWhiteSpace(employee)) return BadRequest(new { message = "Выберите сотрудника." });
        var periods = await db.VacationPeriods.AsNoTracking().Where(x => x.EmployeeName == employee && !x.Cancelled)
            .OrderByDescending(x => x.StartDate).ToListAsync();
        var rows = await db.ResourceAllocations.AsNoTracking().Where(x => x.EmployeeName == employee && x.VacationPeriodId != null).ToListAsync();
        return Ok(periods.Select(x => new { x.Id, x.Revision, x.EmployeeName, x.StartDate, x.EndDate, x.Comment,
            hours = rows.Where(r => r.VacationPeriodId == x.Id).Sum(r => r.PlannedHours ?? 0) }));
    }

    private async Task CheckLocks(Employee? employee, IEnumerable<DateOnly> months)
    {
        var periodList = months.Distinct().ToList();
        var locks = await db.PeriodLocks.AsNoTracking().Where(x => periodList.Contains(x.PeriodStart)).ToListAsync();
        foreach (var month in periodList)
            if (ResourceLockPolicy.ClosedReason(employee, month, locks) is string error) throw new InvalidVacation(error);
    }

    private void Audit(ResourceAllocationEntity row, string action, VacationPeriod vacation)
    {
        var json = JsonSerializer.Serialize(new { allocation = row, vacation });
        db.ResourceAllocationAudits.Add(new ResourceAllocationAuditEntity
        {
            ResourceAllocationId = row.ResourceAllocationId, Action = action, EmployeeName = row.EmployeeName,
            ActivityName = row.ActivityName, PeriodStart = row.PeriodStart,
            OldValueJson = action == "Delete" ? json : null, NewValueJson = action == "Create" ? json : null,
            ChangedAt = DateTime.UtcNow, ChangedBy = user.Login ?? ""
        });
    }

    [HttpPost]
    public async Task<IActionResult> Save(VacationRequest request)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var existing = await db.VacationPeriods.SingleOrDefaultAsync(x => x.Id == request.Id);
            var oldPeriodJson = existing == null ? null : JsonSerializer.Serialize(existing);
            if (existing?.Cancelled == true) throw new InvalidVacation("Этот отпуск отменён. Создайте новую запись.");
            // Exact retry is a read, not a second write or a recalculation of old snapshots.
            if (existing != null && existing.EmployeeName == request.EmployeeName && existing.StartDate == request.StartDate
                && existing.EndDate == request.EndDate && existing.Comment == request.Comment)
                return Ok(new { id = existing.Id, revision = existing.Revision, unchanged = true });
            if ((existing == null && request.Revision != 0) || (existing != null && existing.Revision != request.Revision))
                return Conflict(new { message = "Отпуск уже изменён. Обновите список и повторите действие." });
            if (existing != null && existing.EmployeeName != request.EmployeeName)
                throw new InvalidVacation("При изменении отпуска нельзя менять сотрудника. Создайте отдельную запись.");
            var (employee, snapshot, parts) = await Calculate(request);
            if (await db.VacationPeriods.AnyAsync(x => x.Id != request.Id && !x.Cancelled && x.EmployeeName == employee.FullName
                && x.StartDate <= request.EndDate && x.EndDate >= request.StartDate))
                throw new InvalidVacation("Даты пересекаются с другим отпуском сотрудника.");
            var oldRows = await db.ResourceAllocations.Where(x => x.VacationPeriodId == request.Id).ToListAsync();
            var months = parts.Select(x => x.Month).ToList();
            // Check old dates as well: moving an existing vacation out of a closed month is a modification.
            if (existing != null)
                for (var m = new DateOnly(existing.StartDate.Year, existing.StartDate.Month, 1); m <= existing.EndDate; m = m.AddMonths(1))
                    months.Add(m);
            await CheckLocks(employee, months);
            var otherRows = await db.ResourceAllocations.AsNoTracking()
                .Where(x => x.EmployeeName == employee.FullName && months.Contains(x.PeriodStart) && x.VacationPeriodId != request.Id).ToListAsync();
            foreach (var part in parts)
            {
                var others = otherRows.Where(x => x.PeriodStart == part.Month).ToList();
                if (others.Any(x => x.Kind == "Vacation" && x.VacationPeriodId == null))
                    throw new InvalidVacation($"За {part.Month:MM.yyyy} уже есть отпуск, введённый вручную без дат. Сначала уточните эту запись, чтобы не посчитать её дважды.");
                if (others.Any(x => x.RateSnapshot > 0 && x.RateSnapshot != employee.Rate))
                    throw new InvalidVacation($"Ставка в сохранённом плане за {part.Month:MM.yyyy} отличается от справочника. Сначала уточните ставку.");
                var total = others.Sum(x => ResourcePlanningMath.OwnPercent(x, employee.Rate) ?? 0) + part.Percent;
                if (total > 100.000001m) throw new InvalidVacation($"За {part.Month:MM.yyyy} вместе с отпуском получится {total:0.##}%. Допустимо 100% от ставки сотрудника.");
            }
            if (existing != null)
                foreach (var row in oldRows) Audit(row, "Delete", existing);
            db.ResourceAllocations.RemoveRange(oldRows);
            await db.SaveChangesAsync(); // delete before inserting records with the same unique business key
            var vacation = existing ?? new VacationPeriod { Id = request.Id, EmployeeName = employee.FullName };
            if (existing == null) db.VacationPeriods.Add(vacation);
            vacation.StartDate = request.StartDate;
            vacation.EndDate = request.EndDate;
            vacation.Comment = request.Comment;
            vacation.CalendarVersion = snapshot.Version;
            vacation.RateSnapshot = employee.Rate;
            vacation.Revision++;
            vacation.UpdatedAt = DateTime.UtcNow;
            vacation.UpdatedBy = user.Login;
            db.ResourceAllocationAudits.Add(new ResourceAllocationAuditEntity
            {
                Action = existing == null ? "Create" : "Update", EmployeeName = vacation.EmployeeName,
                ActivityName = "Отпуск (период дат)", PeriodStart = new DateOnly(vacation.StartDate.Year, vacation.StartDate.Month, 1),
                OldValueJson = oldPeriodJson, NewValueJson = JsonSerializer.Serialize(vacation),
                ChangedAt = DateTime.UtcNow, ChangedBy = user.Login ?? ""
            });
            var added = parts.Where(x => x.StandardHours > 0).Select(part => new ResourceAllocationEntity
            {
                VacationPeriodId = vacation.Id, EmployeeName = employee.FullName, EmployeeLogin = employee.Login,
                Kind = "Vacation", ActivityName = $"Отпуск {request.StartDate:dd.MM.yyyy} - {request.EndDate:dd.MM.yyyy}",
                PeriodStart = part.Month, EmployeeAllocationPercent = part.Percent, AllocationPercent = Math.Round(part.Percent * employee.Rate, 2),
                RateSnapshot = employee.Rate, CalendarHoursForMonth = part.MonthHours, PlannedHours = part.Hours, Comment = request.Comment,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, CreatedBy = user.Login, UpdatedBy = user.Login
            }).ToList();
            db.ResourceAllocations.AddRange(added);
            await db.SaveChangesAsync();
            foreach (var row in added) Audit(row, "Create", vacation);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new { id = vacation.Id, revision = vacation.Revision, parts });
        }
        catch (InvalidVacation ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (Concurrent(ex)) { return Conflict(new { message = "Данные изменились одновременно. Обновите список и повторите действие." }); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, [FromQuery] int revision)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var vacation = await db.VacationPeriods.SingleOrDefaultAsync(x => x.Id == id);
            if (vacation == null) return NotFound();
            if (vacation.Cancelled) return Ok(new { ok = true });
            if (vacation.Revision != revision) return Conflict(new { message = "Отпуск изменён. Обновите список." });
            var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.FullName == vacation.EmployeeName);
            if (employee == null) throw new InvalidVacation("Сотрудник отсутствует в справочнике: нельзя проверить блокировки его подразделения.");
            var months = new List<DateOnly>();
            for (var m = new DateOnly(vacation.StartDate.Year, vacation.StartDate.Month, 1); m <= vacation.EndDate; m = m.AddMonths(1)) months.Add(m);
            await CheckLocks(employee, months);
            var rows = await db.ResourceAllocations.Where(x => x.VacationPeriodId == id).ToListAsync();
            foreach (var row in rows) Audit(row, "Delete", vacation);
            db.ResourceAllocationAudits.Add(new ResourceAllocationAuditEntity
            {
                Action = "Delete", EmployeeName = vacation.EmployeeName, ActivityName = "Отпуск (период дат)",
                PeriodStart = new DateOnly(vacation.StartDate.Year, vacation.StartDate.Month, 1),
                OldValueJson = JsonSerializer.Serialize(vacation), ChangedAt = DateTime.UtcNow, ChangedBy = user.Login ?? ""
            });
            db.ResourceAllocations.RemoveRange(rows);
            vacation.Cancelled = true;
            vacation.Revision++;
            vacation.UpdatedAt = DateTime.UtcNow;
            vacation.UpdatedBy = user.Login;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new { ok = true });
        }
        catch (InvalidVacation ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (Concurrent(ex)) { return Conflict(new { message = "Данные изменились одновременно. Обновите список и повторите действие." }); }
    }

    private static bool Concurrent(Exception ex) => ex is DbUpdateConcurrencyException
        || ex is Npgsql.PostgresException { SqlState: "40001" or "40P01" or "23505" }
        || ex is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 }
        || (ex.InnerException != null && Concurrent(ex.InnerException));
}

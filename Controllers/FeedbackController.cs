using ClosedXML.Excel;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PmoNav.Data;
using PmoNav.Models;
using PmoNav.Services;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/feedback")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class FeedbackController : ControllerBase
{
    private readonly PmoDbContext _db;
    private readonly ICurrentUserService _user;
    private readonly IConfiguration _config;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<FeedbackController> _log;
    private static readonly TimeZoneInfo FeedbackZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["issue"] = "Ошибка", ["idea"] = "Предложение", ["question"] = "Вопрос"
    };

    public FeedbackController(PmoDbContext db, ICurrentUserService user, IConfiguration config,
        IAntiforgery antiforgery, ILogger<FeedbackController> log)
    {
        _db = db; _user = user; _config = config; _antiforgery = antiforgery; _log = log;
    }

    // Explicit temporary opt-in, restricted to Render. Never uses a client-supplied login.
    private bool TestReview => _config.GetValue<bool>("Feedback:AllowAnonymousReviewOnRender")
        && string.Equals(Environment.GetEnvironmentVariable("RENDER"), "true", StringComparison.OrdinalIgnoreCase);
    private bool Authenticated => User.Identity?.IsAuthenticated == true;
    private bool CanReview => TestReview || (Authenticated &&
        (_config.GetSection("Feedback:AdminLogins").Get<string[]>() ?? new[] { @"TP\arnatovich_m" })
        .Any(login => string.Equals(login, _user.Login, StringComparison.OrdinalIgnoreCase)));

    [HttpGet("access")]
    public IActionResult Access()
    {
        var token = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        return Ok(new
        {
            canReview = CanReview, canSubmit = Authenticated || TestReview,
            testReview = TestReview, authenticated = Authenticated,
            author = Authenticated ? _user.Display : "", token, timeZone = FeedbackZone.Id
        });
    }

    public record SubmitRequest(Guid RequestId, string? Kind, string? Message, string? Section, string? AuthorName);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("feedback-submit")]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct)
    {
        if (!Authenticated && !TestReview) return Unauthorized(new { message = "Войдите в Навигатор под своей рабочей учётной записью." });
        var message = request.Message?.Trim() ?? "";
        if (request.RequestId == Guid.Empty || request.Kind == null || !Kinds.ContainsKey(request.Kind))
            return BadRequest(new { message = "Выберите тип обращения и повторите отправку." });
        if (message.Length < 5 || message.Length > 5000)
            return BadRequest(new { message = "Сообщение должно содержать от 5 до 5000 символов." });
        if ((request.Section?.Length ?? 0) > 100 || (request.AuthorName?.Length ?? 0) > 120)
            return BadRequest(new { message = "Слишком длинное имя автора или название раздела." });
        try
        {
            var existing = await _db.FeedbackEntries.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
            if (existing != null) return Ok(new { id = existing.FeedbackId });
            var entry = new FeedbackEntry
            {
                RequestId = request.RequestId, CreatedAt = DateTimeOffset.UtcNow,
                Kind = request.Kind, Message = message, Section = request.Section?.Trim() ?? "Портфель",
                AuthorName = Authenticated ? _user.Display : (request.AuthorName?.Trim() ?? ""),
                AuthorLogin = Authenticated ? _user.Login : null, IsAnonymous = !Authenticated
            };
            _db.FeedbackEntries.Add(entry);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // A retried request must not create a second copy.
                var duplicate = await _db.FeedbackEntries.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
                if (duplicate != null) return Ok(new { id = duplicate.FeedbackId });
                throw;
            }
            return Ok(new { id = entry.FeedbackId });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Feedback submission failed");
            return StatusCode(503, new { message = "Не удалось подтвердить сохранение. Текст оставлен в форме; повторите отправку позже." });
        }
    }

    private IQueryable<FeedbackEntry> Filter(string? kind, DateOnly? from, DateOnly? to)
    {
        var query = _db.FeedbackEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(x => x.Kind == kind);
        if (from.HasValue)
        {
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(from.Value.ToDateTime(TimeOnly.MinValue), FeedbackZone));
            query = query.Where(x => x.CreatedAt >= start);
        }
        if (to.HasValue)
        {
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), FeedbackZone));
            query = query.Where(x => x.CreatedAt < end);
        }
        return query;
    }

    private bool ValidFilters(string? kind, DateOnly? from, DateOnly? to) =>
        (string.IsNullOrEmpty(kind) || Kinds.ContainsKey(kind)) &&
        (!from.HasValue || !to.HasValue || from.Value <= to.Value) &&
        (!to.HasValue || to.Value < DateOnly.MaxValue);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? kind, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (!CanReview) return StatusCode(403, new { message = "Просмотр обращений доступен только владельцу раздела." });
        if (!ValidFilters(kind, from, to) || page < 1 || page > 100000)
            return BadRequest(new { message = "Проверьте тип обращения и период: дата «С» не должна быть позже даты «По»." });
        try
        {
            var query = Filter(kind, from, to);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.FeedbackId)
                .Skip((page - 1) * 50).Take(50)
                .Select(x => new { id = x.FeedbackId, x.CreatedAt, x.Kind, x.Message, x.Section, x.AuthorName, x.AuthorLogin, x.IsAnonymous })
                .ToListAsync(ct);
            return Ok(new { items, total, page, pageSize = 50 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Feedback list failed");
            return StatusCode(503, new { message = "Список обращений временно недоступен. Проверьте подключение к базе и создание таблицы feedback." });
        }
    }

    [HttpGet("export/excel")]
    public async Task<IActionResult> Export([FromQuery] string? kind, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!CanReview) return StatusCode(403, new { message = "Экспорт обращений доступен только владельцу раздела." });
        if (!ValidFilters(kind, from, to)) return BadRequest(new { message = "Проверьте выбранный период и тип обращения." });
        try
        {
            var rows = await Filter(kind, from, to).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.FeedbackId)
                .Take(10001).ToListAsync(ct);
            if (rows.Count > 10000) return BadRequest(new { message = "Больше 10 000 обращений. Сузьте период выгрузки." });
            using var book = new XLWorkbook();
            var sheet = book.Worksheets.Add("Обратная связь");
            sheet.Style.Font.FontName = "Calibri";
            sheet.Style.Font.FontSize = 11;
            sheet.Cell(1, 1).Value = "Обратная связь PMO Навигатора";
            sheet.Range(1, 1, 1, 8).Merge().Style.Font.SetBold().Font.FontSize = 15;
            sheet.Cell(2, 1).Value = "Дата и время: Минск. Выгружены все обращения по выбранным фильтрам.";
            sheet.Range(2, 1, 2, 8).Merge();
            sheet.Cell(3, 1).Value = $"Тип: {(string.IsNullOrEmpty(kind) ? "Все" : Kinds[kind])}. " +
                $"С: {from?.ToString("dd.MM.yyyy") ?? "без ограничения"}. По: {to?.ToString("dd.MM.yyyy") ?? "без ограничения"}. Записей: {rows.Count}.";
            sheet.Range(3, 1, 3, 8).Merge();
            var headers = new[] { "№", "Дата (Минск)", "Тип", "Автор", "Учётная запись", "Раздел", "Сообщение", "Авторизация" };
            for (int c = 0; c < headers.Length; c++) sheet.Cell(4, c + 1).Value = headers[c];
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; int r = i + 5;
                sheet.Cell(r, 1).Value = row.FeedbackId;
                sheet.Cell(r, 2).Value = TimeZoneInfo.ConvertTime(row.CreatedAt, FeedbackZone).DateTime;
                sheet.Cell(r, 2).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
                sheet.Cell(r, 3).Value = Kinds.GetValueOrDefault(row.Kind, row.Kind);
                // Values are text, not FormulaA1: spreadsheet formulas in messages are not executed.
                sheet.Cell(r, 4).Value = row.AuthorName;
                sheet.Cell(r, 5).Value = row.AuthorLogin ?? "";
                sheet.Cell(r, 6).Value = row.Section;
                sheet.Cell(r, 7).Value = row.Message;
                sheet.Cell(r, 8).Value = row.IsAnonymous ? "Тест: автор не подтверждён" : "Windows";
            }
            var end = Math.Max(5, rows.Count + 4);
            sheet.Range(4, 1, end, 8).CreateTable("Feedback").Theme = XLTableTheme.TableStyleMedium2;
            sheet.SheetView.FreezeRows(4);
            sheet.Column(1).Width = 9; sheet.Column(2).Width = 21; sheet.Column(3).Width = 18;
            sheet.Column(4).Width = 25; sheet.Column(5).Width = 26; sheet.Column(6).Width = 26;
            sheet.Column(7).Width = 85; sheet.Column(8).Width = 31;
            sheet.Range(5, 1, end, 8).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sheet.Range(5, 1, end, 8).Style.Alignment.WrapText = true;
            sheet.Rows(5, end).Height = 60;
            using var stream = new MemoryStream(); book.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"pmo-feedback_{DateTime.UtcNow:yyyy-MM-dd_HHmm}.xlsx");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Feedback export failed");
            return StatusCode(503, new { message = "Не удалось подготовить Excel. Попробуйте позже." });
        }
    }
}

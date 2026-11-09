using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace PmoNav.Services;

public sealed record CalendarSnapshot(IReadOnlyDictionary<DateOnly, decimal> Days, string Version, string? Error)
{
    public decimal? MonthHours(DateOnly month)
    {
        decimal total = 0;
        for (var day = new DateOnly(month.Year, month.Month, 1); day.Month == month.Month; day = day.AddDays(1))
        {
            if (!Days.TryGetValue(day, out var hours)) return null;
            total += hours;
        }
        return total > 0 ? total : null;
    }
}

/// <summary>Reads the already resolved daily Qlik/1C calendar. Never infers weekdays or holidays.</summary>
public sealed class DailyWorkCalendar(IConfiguration configuration, IWebHostEnvironment environment)
{
    public CalendarSnapshot Read()
    {
        var configured = configuration["WorkCalendarPath"];
        var path = !string.IsNullOrWhiteSpace(configured) ? configured
            : Path.Combine(configuration["ProjectsDataPath"] ?? environment.ContentRootPath, "КалендарьСПраздниками.csv");
        if (!File.Exists(path))
            return new(new Dictionary<DateOnly, decimal>(), "", "Дневной календарь 1С/Qlik не подключён. Нужен файл с нормой часов на каждую дату, включая выходные.");
        try
        {
            if (new FileInfo(path).Length > 5_000_000) throw new InvalidDataException("Файл календаря слишком большой.");
            var bytes = File.ReadAllBytes(path);
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "|", TrimOptions = TrimOptions.Trim };
            using var reader = new StringReader(text);
            using var csv = new CsvReader(reader, cfg);
            if (!csv.Read()) throw new InvalidDataException("Календарь пуст.");
            csv.ReadHeader();
            var fields = csv.HeaderRecord ?? [];
            if (!fields.Contains("ДатаКалендаряСПраздниками") || !fields.Contains("СтандартныеЧасыРаботы"))
                throw new InvalidDataException("Нужны колонки ДатаКалендаряСПраздниками|СтандартныеЧасыРаботы.");
            var days = new Dictionary<DateOnly, decimal>();
            while (csv.Read())
            {
                var dateText = csv.GetField("ДатаКалендаряСПраздниками");
                var hoursText = csv.GetField("СтандартныеЧасыРаботы")?.Replace(',', '.');
                if (!DateOnly.TryParseExact(dateText, ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || date.Year < 2000 || date.Year > 2100)
                    throw new InvalidDataException($"Некорректная дата в строке {csv.Parser.Row}.");
                if (!decimal.TryParse(hoursText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours)
                    || hours < 0 || hours > 24)
                    throw new InvalidDataException($"Некорректная норма часов для {date:dd.MM.yyyy}.");
                if (days.TryGetValue(date, out var previous) && previous != hours)
                    throw new InvalidDataException($"Разные нормы часов для одной даты {date:dd.MM.yyyy}.");
                days[date] = hours; // exact duplicate records do not multiply hours
            }
            if (days.Count == 0) throw new InvalidDataException("Календарь не содержит дат.");
            return new(days, Convert.ToHexString(SHA256.HashData(bytes)), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CsvHelperException or DecoderFallbackException)
        {
            return new(new Dictionary<DateOnly, decimal>(), "", ex is InvalidDataException ? ex.Message : "Не удалось прочитать дневной календарь. Проверьте формат и доступ к файлу.");
        }
    }
}

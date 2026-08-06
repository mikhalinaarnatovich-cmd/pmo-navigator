namespace PmoNav.Models;

/// <summary>
/// Единый справочник сотрудников (источник правды для иерархии, ставки/FTE
/// и видимости "руководитель -> подчинённые"). Ведётся в БД Навигатора,
/// а не в личных Excel-файлах. Заполняется через /api/employees или
/// массовым импортом /api/employees/bulk-import (CSV).
/// </summary>
public class Employee
{
    public int EmployeeId { get; set; }

    public string FullName { get; set; } = "";

    public string? Login { get; set; }

    public string? Department { get; set; }

    public string? Sector { get; set; }

    /// <summary>ФИО непосредственного руководителя (из этого же справочника).</summary>
    public string? ManagerFullName { get; set; }

    /// <summary>
    /// Ставка сотрудника (FTE), например 1.00, 0.25, 1.25 (многоставочник).
    /// Используется как предел суммарной загрузки вместо жёстких 100%:
    /// лимит = Rate * 100%.
    /// </summary>
    public decimal Rate { get; set; } = 1.00m;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

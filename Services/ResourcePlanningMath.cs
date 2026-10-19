using PmoNav.Data;

namespace PmoNav.Services;

public static class ResourcePlanningMath
{
    public static decimal Rate(ResourceAllocationEntity row, decimal directoryRate) =>
        row.RateSnapshot is > 0 ? row.RateSnapshot.Value : directoryRate;

    public static decimal? OwnPercent(ResourceAllocationEntity row, decimal directoryRate)
    {
        if (row.EmployeeAllocationPercent.HasValue) return row.EmployeeAllocationPercent.Value;
        var rate = Rate(row, directoryRate);
        return rate > 0 ? row.AllocationPercent / rate : null;
    }

    public static decimal Fte(ResourceAllocationEntity row) =>
        row.EmployeeAllocationPercent.HasValue && row.RateSnapshot is > 0
            ? row.EmployeeAllocationPercent.Value * row.RateSnapshot.Value / 100m
            : row.AllocationPercent / 100m;

    public static decimal? Hours(ResourceAllocationEntity row, decimal? calendarHours = null) =>
        row.PlannedHours ?? (Fte(row) == 0 ? 0m
            : (row.CalendarHoursForMonth ?? calendarHours) is decimal norm && norm > 0
                ? Math.Round(norm * Fte(row), 2) : null);
}

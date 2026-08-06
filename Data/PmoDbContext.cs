using Microsoft.EntityFrameworkCore;
using PmoNav.Models;

namespace PmoNav.Data;

public class PmoDbContext : DbContext
{
    public PmoDbContext(DbContextOptions<PmoDbContext> options)
        : base(options)
    {
    }

    public DbSet<ResourceAllocationEntity> ResourceAllocations => Set<ResourceAllocationEntity>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<WorkCalendarEntity> WorkCalendars => Set<WorkCalendarEntity>();
    public DbSet<PeriodLockEntity> PeriodLocks => Set<PeriodLockEntity>();
    public DbSet<ResourceAllocationAuditEntity> ResourceAllocationAudits => Set<ResourceAllocationAuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Ресурсный план ──────────────────────────────────────────────
        modelBuilder.Entity<ResourceAllocationEntity>(entity =>
        {
            entity.ToTable("ResourceAllocations", "dbo");

            entity.HasKey(x => x.ResourceAllocationId);

            entity.Property(x => x.EmployeeName)
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(x => x.EmployeeLogin)
                .HasMaxLength(256);

            entity.Property(x => x.Kind)
                .HasMaxLength(32)
                .HasDefaultValue("Project")
                .IsRequired();

            entity.Property(x => x.ActivityName)
                .HasMaxLength(256);

            entity.Property(x => x.AllocationPercent)
                .HasPrecision(6, 2);

            entity.Property(x => x.PlannedHours)
                .HasPrecision(8, 2);

            entity.Property(x => x.CalendarHoursForMonth)
                .HasPrecision(8, 2);

            entity.Property(x => x.Comment)
                .HasMaxLength(2000);

            entity.Property(x => x.CreatedBy)
                .HasMaxLength(256);

            entity.Property(x => x.UpdatedBy)
                .HasMaxLength(256);

            // ProjectId стал необязательным: для "Операционная деятельность"
            // и "Отпуск" реального проекта нет, вместо него ActivityName.
            entity.HasIndex(x => new
            {
                x.EmployeeName,
                x.ProjectId,
                x.Kind,
                x.ActivityName,
                x.PeriodStart
            })
            .IsUnique();
        });

        // ── Справочник сотрудников ───────────────────────────────────────
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees", "dbo");
            entity.HasKey(x => x.EmployeeId);

            entity.Property(x => x.FullName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Login).HasMaxLength(256);
            entity.Property(x => x.Department).HasMaxLength(256);
            entity.Property(x => x.Sector).HasMaxLength(256);
            entity.Property(x => x.ManagerFullName).HasMaxLength(256);
            entity.Property(x => x.Rate).HasPrecision(5, 2).HasDefaultValue(1.00m);

            entity.HasIndex(x => x.FullName).IsUnique();
        });

        // ── Производственный календарь (для перевода часов в %/FTE) ─────
        modelBuilder.Entity<WorkCalendarEntity>(entity =>
        {
            entity.ToTable("WorkCalendars", "dbo");
            entity.HasKey(x => x.WorkCalendarId);
            entity.Property(x => x.WorkingHours).HasPrecision(7, 2);

            entity.HasIndex(x => new { x.Year, x.Month }).IsUnique();
        });

        // ── Открытие/закрытие периодов по группам сотрудников ────────────
        modelBuilder.Entity<PeriodLockEntity>(entity =>
        {
            entity.ToTable("PeriodLocks", "dbo");
            entity.HasKey(x => x.PeriodLockId);

            entity.Property(x => x.GroupType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.GroupValue).HasMaxLength(256).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(256);

            entity.HasIndex(x => new { x.PeriodStart, x.GroupType, x.GroupValue }).IsUnique();
        });

        // ── Журнал изменений (кто/когда/что, включая удаления) ───────────
        modelBuilder.Entity<ResourceAllocationAuditEntity>(entity =>
        {
            entity.ToTable("ResourceAllocationAudits", "dbo");
            entity.HasKey(x => x.AuditId);

            entity.Property(x => x.Action).HasMaxLength(16).IsRequired();
            entity.Property(x => x.EmployeeName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ActivityName).HasMaxLength(256);
            entity.Property(x => x.ChangedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.OldValueJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.NewValueJson).HasColumnType("nvarchar(max)");

            entity.HasIndex(x => new { x.EmployeeName, x.PeriodStart });
        });
    }
}

public class ResourceAllocationEntity
{
    public long ResourceAllocationId { get; set; }

    public string EmployeeName { get; set; } = "";

    public string? EmployeeLogin { get; set; }

    /// <summary>"Project" | "Operational" | "Vacation".</summary>
    public string Kind { get; set; } = "Project";

    /// <summary>ID реального проекта — только когда Kind == "Project".</summary>
    public int? ProjectId { get; set; }

    /// <summary>
    /// Название вида деятельности для Kind != "Project"
    /// (например "Операционная деятельность: тех. поддержка", "Отпуск").
    /// </summary>
    public string? ActivityName { get; set; }

    public DateOnly PeriodStart { get; set; }

    /// <summary>Загрузка в % от ставки сотрудника (0..Rate*100).</summary>
    public decimal AllocationPercent { get; set; }

    /// <summary>Введённые часы (если сотрудник вводит часы, а не %).</summary>
    public decimal? PlannedHours { get; set; }

    /// <summary>Норма часов по календарю за период — снимок на момент сохранения.</summary>
    public decimal? CalendarHoursForMonth { get; set; }

    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

public class WorkCalendarEntity
{
    public int WorkCalendarId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int WorkingDays { get; set; }
    public decimal WorkingHours { get; set; }
}

public class PeriodLockEntity
{
    public int PeriodLockId { get; set; }
    public DateOnly PeriodStart { get; set; }

    /// <summary>"All" | "Department" | "Sector".</summary>
    public string GroupType { get; set; } = "All";

    /// <summary>Значение группы ("*" для All, название отдела/сектора иначе).</summary>
    public string GroupValue { get; set; } = "*";

    public bool IsOpen { get; set; } = true;
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ResourceAllocationAuditEntity
{
    public long AuditId { get; set; }
    public long? ResourceAllocationId { get; set; }

    /// <summary>"Create" | "Update" | "Delete".</summary>
    public string Action { get; set; } = "";

    public string EmployeeName { get; set; } = "";
    public int? ProjectId { get; set; }
    public string? ActivityName { get; set; }
    public DateOnly PeriodStart { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAt { get; set; }
}

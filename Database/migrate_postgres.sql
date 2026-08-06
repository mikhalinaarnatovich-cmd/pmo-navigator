-- ═══════════════════════════════════════════════════════════════════════════
-- PMO Навигатор — миграция на PostgreSQL
-- Создание всех таблиц (включая новые из мастер-плана).
-- Лицензионно-чистая альтернатива MS SQL (PostgreSQL — свободное ПО).
-- ═══════════════════════════════════════════════════════════════════════════

-- Если пересоздаёте базу с нуля:
-- DROP SCHEMA IF EXISTS dbo CASCADE;
-- CREATE SCHEMA dbo;

-- ── 1. Справочник сотрудников ───────────────────────────────────────────
CREATE TABLE IF NOT EXISTS dbo.Employees (
    EmployeeId      SERIAL PRIMARY KEY,
    FullName        VARCHAR(256) NOT NULL UNIQUE,
    Login           VARCHAR(256),
    Department      VARCHAR(256),
    Sector          VARCHAR(256),
    ManagerFullName VARCHAR(256),
    Rate            NUMERIC(5,2) NOT NULL DEFAULT 1.00,
    IsActive        BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt       TIMESTAMP NOT NULL DEFAULT NOW(),
    UpdatedAt       TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Индексы для фильтрации по отделу/сектору/руководителю
CREATE INDEX IF NOT EXISTS IX_Employees_Department  ON dbo.Employees (Department);
CREATE INDEX IF NOT EXISTS IX_Employees_Sector      ON dbo.Employees (Sector);
CREATE INDEX IF NOT EXISTS IX_Employees_Manager     ON dbo.Employees (ManagerFullName);

-- ── 2. Производственный календарь ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS dbo.WorkCalendars (
    WorkCalendarId SERIAL PRIMARY KEY,
    Year           INT NOT NULL,
    Month          INT NOT NULL,
    WorkingDays    INT NOT NULL DEFAULT 0,
    WorkingHours   NUMERIC(7,2) NOT NULL DEFAULT 0,
    UNIQUE (Year, Month)
);

-- ── 3. Ресурсный план (расширенная версия) ───────────────────────────────
CREATE TABLE IF NOT EXISTS dbo.ResourceAllocations (
    ResourceAllocationId BIGSERIAL PRIMARY KEY,
    EmployeeName    VARCHAR(256) NOT NULL,
    EmployeeLogin   VARCHAR(256),
    Kind            VARCHAR(32) NOT NULL DEFAULT 'Project',
    ProjectId       INT,
    ActivityName    VARCHAR(256),
    PeriodStart     DATE NOT NULL,
    AllocationPercent NUMERIC(6,2) NOT NULL DEFAULT 0,
    PlannedHours     NUMERIC(8,2),
    CalendarHoursForMonth NUMERIC(8,2),
    Comment         VARCHAR(2000),
    CreatedAt       TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy       VARCHAR(256),
    UpdatedAt       TIMESTAMP NOT NULL DEFAULT NOW(),
    UpdatedBy       VARCHAR(256),
    UNIQUE (EmployeeName, COALESCE(ProjectId, -1), Kind, COALESCE(ActivityName, ''), PeriodStart)
);

CREATE INDEX IF NOT EXISTS IX_RA_EmployeePeriod  ON dbo.ResourceAllocations (EmployeeName, PeriodStart);
CREATE INDEX IF NOT EXISTS IX_RA_Period          ON dbo.ResourceAllocations (PeriodStart);
CREATE INDEX IF NOT EXISTS IX_RA_Project        ON dbo.ResourceAllocations (ProjectId);

-- ── 4. Блокировки периодов по группам ────────────────────────────────────
CREATE TABLE IF NOT EXISTS dbo.PeriodLocks (
    PeriodLockId SERIAL PRIMARY KEY,
    PeriodStart  DATE NOT NULL,
    GroupType    VARCHAR(32) NOT NULL DEFAULT 'All',
    GroupValue   VARCHAR(256) NOT NULL DEFAULT '*',
    IsOpen       BOOLEAN NOT NULL DEFAULT TRUE,
    UpdatedBy    VARCHAR(256),
    UpdatedAt    TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE (PeriodStart, GroupType, GroupValue)
);

-- ── 5. Журнал изменений (аудит) ───────────────────────────────────────────
CREATE TABLE IF NOT EXISTS dbo.ResourceAllocationAudits (
    AuditId    BIGSERIAL PRIMARY KEY,
    ResourceAllocationId BIGINT,
    Action     VARCHAR(16) NOT NULL,
    EmployeeName   VARCHAR(256) NOT NULL,
    ProjectId      INT,
    ActivityName   VARCHAR(256),
    PeriodStart    DATE NOT NULL,
    OldValueJson   TEXT,
    NewValueJson   TEXT,
    ChangedBy  VARCHAR(256) NOT NULL,
    ChangedAt TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS IX_Audit_EmployeePeriod ON dbo.ResourceAllocationAudits (EmployeeName, PeriodStart);
CREATE INDEX IF NOT EXISTS IX_Audit_ChangedAt      ON dbo.ResourceAllocationAudits (ChangedAt DESC);

-- ═══════════════════════════════════════════════════════════════════════════
-- НАЧАЛЬНЫЕ ДАННЫЕ
-- ═══════════════════════════════════════════════════════════════════════════

-- Производственный календарь на 2026 год (Беларусь, приближённые нормы).
-- Точные нормы с учётом праздников и переносов нужно уточнить.
INSERT INTO dbo.WorkCalendars (Year, Month, WorkingDays, WorkingHours) VALUES
(2026, 1,  20, 160),
(2026, 2,  20, 160),
(2026, 3,  21, 168),
(2026, 4,  22, 176),
(2026, 5,  19, 152),
(2026, 6,  21, 168),
(2026, 7,  23, 184),
(2026, 8,  21, 168),
(2026, 9,  22, 176),
(2026, 10, 22, 176),
(2026, 11, 20, 160),
(2026, 12, 22, 176)
ON CONFLICT (Year, Month) DO NOTHING;

-- Стартовые блокировки — все периоды открыты по умолчанию.
-- (не вставляем ничего — отсутствие записи = период открыт)

-- ── Импорт сотрудников ────────────────────────────────────────────────────
-- Заполните справочник массовым импортом через /api/employees/bulk-import
-- (CSV: FullName;Login;Department;Sector;ManagerFullName;Rate)
-- или вставьте INSERT'ами вручную ниже.
-- Пример:
-- INSERT INTO dbo.Employees (FullName, Login, Department, Sector, ManagerFullName, Rate) VALUES
-- ('Арнатович Михалина', 'arnatovich_m', 'УБА', 'Сектор бизнес-анализа', 'Иванов Иван', 1.00),
-- ...
-- ON CONFLICT (FullName) DO UPDATE SET
--   Login = EXCLUDED.Login,
--   Department = EXCLUDED.Department,
--   Sector = EXCLUDED.Sector,
--   ManagerFullName = EXCLUDED.ManagerFullName,
--   Rate = EXCLUDED.Rate,
--   UpdatedAt = NOW();

-- ═══════════════════════════════════════════════════════════════════════════
-- QLIK: представление для регламентной выгрузки
-- Qlik должен подтягивать данные из этого представления, а не из Excel.
-- ═══════════════════════════════════════════════════════════════════════════
CREATE OR REPLACE VIEW dbo.vw_ResourcePlanForQlik AS
SELECT
    ra.PeriodStart,
    ra.EmployeeName,
    ra.EmployeeLogin,
    ra.Kind,
    ra.ProjectId,
    COALESCE(p."Название проекта", ra.ActivityName, 'Проект #' || ra.ProjectId) AS ProjectName,
    p."Статус проекта"   AS ProjectStatus,
    p."Отдел владелец проекта" AS Department,
    ra.AllocationPercent,
    ra.PlannedHours,
    ra.CalendarHoursForMonth,
    ra.Comment,
    ra.UpdatedAt,
    ra.UpdatedBy,
    e.Rate     AS EmployeeRate,
    e.Department AS EmployeeDepartment,
    e.Sector   AS EmployeeSector
FROM dbo.ResourceAllocations ra
LEFT JOIN dbo.Employees e ON e.FullName = ra.EmployeeName
LEFT JOIN LATERAL (
    SELECT * FROM (VALUES (1)) AS dummy  -- projects.csv пока не в БД
) AS dummy_table
    ON TRUE
-- Когда projects.csv будет загружен в таблицу dbo.Projects:
-- LEFT JOIN dbo.Projects p ON p.ProjectId = ra.ProjectId
ORDER BY ra.PeriodStart, ra.EmployeeName;

-- Временная версия без JOIN к проектам (пока projects.csv не в БД):
CREATE OR REPLACE VIEW dbo.vw_ResourcePlanForQlik AS
SELECT
    ra.PeriodStart,
    ra.EmployeeName,
    ra.EmployeeLogin,
    ra.Kind,
    ra.ProjectId,
    ra.ActivityName,
    ra.AllocationPercent,
    ra.PlannedHours,
    ra.CalendarHoursForMonth,
    ra.Comment,
    ra.UpdatedAt,
    ra.UpdatedBy,
    e.Rate     AS EmployeeRate,
    e.Department AS EmployeeDepartment,
    e.Sector   AS EmployeeSector
FROM dbo.ResourceAllocations ra
LEFT JOIN dbo.Employees e ON e.FullName = ra.EmployeeName
ORDER BY ra.PeriodStart, ra.EmployeeName;

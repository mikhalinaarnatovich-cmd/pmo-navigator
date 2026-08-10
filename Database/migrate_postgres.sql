-- ═══════════════════════════════════════════════════════════════════════════
-- PMO Навигатор — миграция на PostgreSQL
-- Создание всех таблиц (включая новые из мастер-плана).
-- ═══════════════════════════════════════════════════════════════════════════

-- ── 0. Схема dbo ────────────────────────────────────────────────────────
-- PostgreSQL не имеет схемы dbo по умолчанию (только public).
-- Создаём явно — без этого CREATE TABLE dbo.X падает с 3F000.
CREATE SCHEMA IF NOT EXISTS dbo;

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

-- ── 3. Ресурсный план ───────────────────────────────────────────────────
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
    UpdatedBy       VARCHAR(256)
);

-- PostgreSQL не поддерживает COALESCE в table-level UNIQUE constraint.
-- Создаём partial unique index с COALESCE вместо этого.
CREATE UNIQUE INDEX IF NOT EXISTS UQ_RA_Composite
    ON dbo.ResourceAllocations (EmployeeName, COALESCE(ProjectId, -1), Kind, COALESCE(ActivityName, ''), PeriodStart);

CREATE INDEX IF NOT EXISTS IX_RA_EmployeePeriod  ON dbo.ResourceAllocations (EmployeeName, PeriodStart);
CREATE INDEX IF NOT EXISTS IX_RA_Period          ON dbo.ResourceAllocations (PeriodStart);
CREATE INDEX IF NOT EXISTS IX_RA_Project        ON dbo.ResourceAllocations (ProjectId);

-- ── 4. Блокировки периодов ───────────────────────────────────────────────
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

-- Производственный календарь 2026
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

-- ── Справочник сотрудников (из участников проектов CSV) ──────────────────
INSERT INTO dbo.Employees (FullName, Rate) VALUES
('Алесич (Антоненко) Анастасия', 1.00),
('Ануфриёнок Виктория', 1.00),
('Апранич Евгений', 1.00),
('Арнатович Михалина', 1.00),
('Астапенко Павел', 1.00),
('Ашкинадзе Семён', 1.00),
('Бабенко Наталия', 1.00),
('Бабенко Наталия Артуровна', 1.00),
('Бермудес Ривера Лариса', 1.00),
('Бондаренко Андрей', 1.00),
('Бондаренко Юрий', 1.00),
('Борисёнок Ольга', 1.00),
('Брезицкий Влад', 1.00),
('Бычкова Ольга', 1.00),
('Гарбаль Сергей', 1.00),
('Гутыро (Савина) Виктория', 1.00),
('Дарья Крутько', 1.00),
('Для распределения · Dev', 1.00),
('Добриянец Алексей', 1.00),
('Жалевич Дмитрий', 1.00),
('Журавская Ксения', 1.00),
('Жучкевич Алексей', 1.00),
('Захарчук Павел', 1.00),
('Зелёнко Ольга', 1.00),
('Злобин Роман', 1.00),
('Иванишина Галина', 1.00),
('Ирина Шваб (БА)', 1.00),
('Касичев Павел', 1.00),
('Качура Виктор', 1.00),
('Клачко Анна', 1.00),
('Колосовская Елена', 1.00),
('Колубако Анастасия', 1.00),
('Кот Александр', 1.00),
('Кузмицкая (Романова) Кристина', 1.00),
('Кулешова Наталья', 1.00),
('Куткович Марина', 1.00),
('Леоненко Антон', 1.00),
('Леоненко Егор', 1.00),
('Махнович Светлана', 1.00),
('Мелешко Александр', 1.00),
('Мозолевский Андрей', 1.00),
('Молчан Павел', 1.00),
('Москалёва Ирина', 1.00),
('Никитин Борис', 1.00),
('Норенко Вадим', 1.00),
('Станкевич Ирина', 1.00),
('Сурин Кирилл', 1.00),
('Тарасевич Юрий', 1.00),
('Титов Тимур', 1.00),
('Хвин Юлия', 1.00),
('Цвиликов Виктор', 1.00),
('Шваб Ирина', 1.00),
('Шевчик Валерьян', 1.00),
('Шевчук Алексей', 1.00),
('Шпилёв Андрей', 1.00),
('Шульженко Юлианна', 1.00)
ON CONFLICT (FullName) DO NOTHING;

-- ── Qlik-представление ───────────────────────────────────────────────────
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
    e.Rate       AS EmployeeRate,
    e.Department AS EmployeeDepartment,
    e.Sector     AS EmployeeSector
FROM dbo.ResourceAllocations ra
LEFT JOIN dbo.Employees e ON e.FullName = ra.EmployeeName
ORDER BY ra.PeriodStart, ra.EmployeeName;

-- Приводим колонки дат к TIMESTAMP (без tz): EF Core пишет Unspecified,
-- timestamptz ломал bulk-import сотрудников (Kind=Local/UTC)
ALTER TABLE dbo.Employees ALTER COLUMN CreatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.Employees ALTER COLUMN UpdatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.ResourceAllocations ALTER COLUMN CreatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.ResourceAllocations ALTER COLUMN UpdatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.WorkCalendars ALTER COLUMN UpdatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.PeriodLocks ALTER COLUMN UpdatedAt TYPE TIMESTAMP;
ALTER TABLE dbo.ResourceAllocationAudits ALTER COLUMN ChangedAt TYPE TIMESTAMP;

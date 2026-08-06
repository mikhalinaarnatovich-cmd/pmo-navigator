// ═══════════════════════════════════════════════════════════════════════════
// Qlik Sense — скрипт загрузки данных из PMO Навигатора
// Заменяет выгрузку из Excel на регламентное подтягивание из БД Навигатора.
//
// Источник: CSV-эндпоинт Навигатора
//   http://<server>/api/resource-plan/export/csv?from=2026-08-01&to=2026-12-01
//
// Альтернатива: прямое подключение к PostgreSQL (см. представление
//   dbo.vw_ResourcePlanForQlik) — рекомендуется для продакшена.
// ═══════════════════════════════════════════════════════════════════════════

// ── ВАРИАНТ 1: Загрузка CSV из HTTP-эндпоинта Навигатора ──────────────
// Запланируйте эту загрузку в Qlik Data Manager / Reload по расписанию.

SET ThousandSep=' ';
SET DecimalSep=',';
SET DateFormat='DD.MM.YYYY';
SET TimestampFormat='DD.MM.YYYY hh:mm:ss';
SET CodePage=1251;

// URL Навигатора — укажите ваш сервер
LET vNavUrl = 'http://pmonav.corp.local';

// Период загрузки: с начала текущего года до конца следующего
LET vFrom = YearStart(Today()) & '-01-01';
LET vTo   = YearEnd(Today()+365) & '-12-01';

ResourcePlan:
LOAD
    PeriodStart       AS [Период],
    EmployeeName      AS [Сотрудник],
    EmployeeLogin     AS [Логин сотрудника],
    Kind              AS [Вид деятельности],
    ProjectId         AS [ID проекта],
    ProjectName       AS [Проект/Активность],
    ProjectStatus     AS [Статус проекта],
    Department        AS [Подразделение],
    AllocationPercent AS [Загрузка, %],
    PlannedHours      AS [Часы],
    Comment           AS [Комментарий],
    UpdatedAt         AS [Обновлено],
    UpdatedBy         AS [Кем обновлено]
FROM [$(vNavUrl)/api/resource-plan/export/csv?from=$(vFrom)&to=$(vTo)]
(format, utf8, delimited is ';', header is 1 lines, quotes are 1);

// ── ВАРИАНТ 2: Прямое подключение к PostgreSQL (рекомендуется) ─────────
// Раскомментируйте, создайте ODBC DSN "PmoNavigatorDb" к PostgreSQL.
//
// LIB CONNECT TO 'PmoNavigatorDb';
// ResourcePlan:
// SQL SELECT * FROM dbo.vw_ResourcePlanForQlik;

// ── Контроль качества: сверка с Навигатором ───────────────────────────
// Загружаем контрольные суммы из /api/portfolio/quality-check и сравниваем.
// Если цифры не совпадают — алерт в Qlik (опционально через Qlik Alerting).

QualityCheck:
LOAD
    totalProjects     AS [QC: Всего проектов],
    totalMembers      AS [QC: Всего участников],
    missingHealthStatus AS [QC: Без статуса здоровья],
    missingDepartment AS [QC: Без отдела],
    lastLoaded        AS [QC: Последняя загрузка]
FROM [$(vNavUrl)/api/portfolio/quality-check]
(json, utf8);

// ── Производный календарь ─────────────────────────────────────────────
MasterCalendar:
LOAD
    [Период]           AS [Период],
    Year([Период])     AS [Год],
    Month([Период])    AS [Месяц],
    Quarter([Период])  AS [Квартал],
    Year([Период]) & ' Q' & Ceil(Month([Период])/3) AS [Год-Квартал]
RESIDENT ResourcePlan;

// ── Сводная: загрузка по сотруднику × месяц ───────────────────────────
EmployeeLoad:
LOAD
    [Сотрудник],
    [Период],
    Sum([Загрузка, %]) AS [Суммарная загрузка, %],
    Count(DISTINCT [ID проекта]) AS [Кол-во проектов]
RESIDENT ResourcePlan
GROUP BY [Сотрудник], [Период];

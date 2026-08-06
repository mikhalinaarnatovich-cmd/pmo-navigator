# 🧭 PMO Navigator v2.0

Веб-портал для управления ресурсным планированием, портфелем проектов и документами проектного офиса (ПМО).

Объединяет в одном месте:
- 📊 **Ресурсный план** — распределение сотрудников по проектам в часах / FTE
- 🏛️ **Портфель проектов (ПК)** — дашборды проектного комитета
- 📁 **Документы** — доступ к паспортам проектов и статус-отчётам из сетевой папки

---

## ⚡ Быстрый старт (5 минут)

### Что нужно
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PostgreSQL 16](https://www.enterprisedb.com/downloads/postgres-postgresql-downloads)

### Запуск

```bash
# 1. Клонировать репозиторий
git clone https://github.com/mikhalinaarnatovich-cmd/pmo-navigator.git
cd pmo-navigator

# 2. Создать базу данных PostgreSQL
createdb PmoNavigatorDb

# 3. Выполнить миграцию (создание таблиц)
psql -d PmoNavigatorDb -f Database/migrate_postgres.sql

# 4. Настроить appsettings.json — пароль БД, путь к папке проектов, логины УБА

# 5. Запустить
dotnet restore
dotnet run --urls=http://localhost:5180
```

Открой `http://localhost:5180` в браузере.

---

## 📂 Структура проекта

```
pmo-navigator/
│
├── Controllers/
│   ├── HomeController.cs            # Главная страница портала
│   ├── ProjectsApiController.cs     # CRUD проектов, документы, диаг
│   ├── ResourcePlanController.cs    # Ресурсный план (FTE, часы, лимиты, аудит)
│   ├── EmployeesController.cs       # Справочник сотрудников, иерархия
│   ├── PortfolioController.cs        # Дашборды ПК, контроль качества
│   └── FileController.cs            # Превью и скачивание документов
│
├── Models/
│   ├── Project.cs                   # Модель проекта (35+ полей из CSV)
│   └── Employee.cs                  # Модель сотрудника (ставка, отдел, руководитель)
│
├── Services/
│   ├── DataService.cs               # Загрузка проектов из CSV
│   └── CurrentUserService.cs        # Текущий пользователь (Windows auth, УБА)
│
├── Data/
│   └── PmoDbContext.cs               # EF Core: таблицы, индексы, связи
│
├── Database/
│   ├── migrate_postgres.sql          # SQL-миграция для PostgreSQL (все таблицы)
│   └── qlik_load_script.qs          # Скрипт загрузки данных для Qlik Sense
│
├── Views/
│   ├── Home/Index.cshtml             # Фронтенд (4 000+ строк, Canvas-диаграммы)
│   ├── Shared/_Layout.cshtml         # Базовый шаблон
│   └── _ViewImports.cshtml           # DI-импорты
│
├── wwwroot/
│   └── data/
│       ├── projects.csv              # Данные проектов (источник — ServiceDesk)
│       ├── members.csv               # Участники (резервный источник)
│       ├── config.json               # Конфигурация портфеля
│       ├── pk_comments.json          # Комментарии ПК
│       ├── reads.json                # Отметки "прочитано"
│       └── approvals.json            # Согласования
│
├── Properties/
│   └── PublishProfiles/
│       └── FolderProfile.pubxml      # Профиль публикации для IIS
│
├── Program.cs                       # Точка входа, DI, автоопределение БД
├── appsettings.json                 # Конфигурация (БД, путь к папке, УБА)
├── pmo_nav.csproj                   # Проект .NET 8
├── Dockerfile                       # Контейнер для хостинга (Render/Railway)
└── .gitignore
```

---

## 🔧 Конфигурация (appsettings.json)

```json
{
  "ConnectionStrings": {
    "PmoNavigatorDb": "Host=localhost;Port=5432;Database=PmoNavigatorDb;Username=postgres;Password=postgres;TrustServerCertificate=true"
  },
  "ProjectsBasePath": "W:\\PMO",
  "ProjectsDataPath": "",
  "UbaLogins": ["arnatovich_m", "TP\\arnatovich_m"]
}
```

| Параметр | Описание |
|---|---|
| `PmoNavigatorDb` | Строка подключения к PostgreSQL. Если начинается с `Host=` → Npgsql, иначе MS SQL. |
| `ProjectsBasePath` | Путь к сетевой папке с документами проектов. Папки ищутся по шаблону `{PROJECTID}_*`. |
| `ProjectsDataPath` | Путь к данным проектов (CSV). Если пусто — `wwwroot/data/`. |
| `UbaLogins` | Логины Windows, которым разрешено редактировать документы (УБА). |

---

## 📊 Ресурсный план — логика

### Ввод часов → автопересчёт в %
Сотрудник вводит часы → система пересчитывает в % от производственного календаря:
```
% = (часы / норма_часов_за_месяц) × 100
```
Норма берётся из таблицы `WorkCalendars`. Если записи нет — расчёт: будние дни × 8 часов.

### Лимит по ставке (FTE)
Лимит = `Rate × 100%`. Не жёсткие 100%, а по фактической ставке:

| Ставка | Лимит | Кому |
|---|---|---|
| 1.0 | 100% | Полная ставка |
| 0.5 | 50% | Полставки |
| 0.25 | 25% | Четверть ставки |
| 1.25 | 125% | Совместитель (многоставочник) |

### Виды деятельности
- **Проект** — назначение на реальный проект (из projects.csv)
- **Операционная** — 5 фиксированных видов (тех. поддержка, админ, совещания, обучение, прочее)
- **Отпуск** — отпуск основной, за свой счёт, больничный

### Статусы заполнения
- 🔴 **Не заполнил** — 0% за месяц
- 🟡 **Частично заполнено** — >0%, но меньше лимита
- 🟢 **Заполнено** — ≥ лимита

### Блокировка по группам
Закрытие/открытие редактирования по: всем / отделу / сектору. Приоритет: Department > Sector > All.

### Журнал изменений (аудит)
Каждое действие (создание / изменение / удаление) пишется в `ResourceAllocationAudits` с JSON старого и нового значения, логином и временем.

---

## 🏛️ Портфель проектов (вкладка ПК)

4 дашборда (карусель вкладок):

| Дашборд | Что показывает |
|---|---|
| Здоровье портфеля | Donut: красный / жёлтый / зелёный RAG + donut по статусам |
| По типам проектов | Donut: распределение по полю "Тип проекта" |
| По отделам | Stacked-bar: зелёный/жёлтый/красный по отделам |
| Матрица скоринга | Scatter: Сложность/Риски × Стратегическое соответствие |

**Контроль качества** (`/api/portfolio/quality-check`) — контрольные суммы для сверки с Qlik.

---

## 📁 Документы

- Папки ищутся по шаблону `{PROJECTID}_*` внутри `ProjectsBasePath`
- **Превью**: PDF, PNG, JPG, GIF, SVG, TXT — в браузере без скачивания
- **Скачивание**: все типы файлов
- **Права**: чтение — всем, редактирование — только УБА (по списку `UbaLogins`)

---

## 🔌 Интеграция с Qlik

Файл: `Database/qlik_load_script.qs`

Два варианта:
1. **CSV через HTTP** — Qlik загружает `/api/resource-plan/export/csv`
2. **Прямое подключение к PostgreSQL** (рекомендуется) — через ODBC DSN, представление `vw_ResourcePlanForQlik`

---

## 🐳 Деплой на хостинг (Docker)

```bash
# Сборка образа
docker build -t pmo-navigator .

# Запуск (нужен PostgreSQL, доступный по сети)
docker run -p 10000:10000 \
  -e ConnectionStrings__PmoNavigatorDb="Host=...;Port=5432;Database=PmoNavigatorDb;Username=postgres;Password=..." \
  pmo-navigator
```

### Render.com (бесплатно)
1. New → Web Service → подключить GitHub-репозиторий
2. Environment: Docker
3. Add environment variable: `ConnectionStrings__PmoNavigatorDb`
4. Render создаст PostgreSQL сам (бесплатный план — 90 дней)

### Railway.app
1. New Project → Deploy from GitHub repo
2. Railway определит Dockerfile автоматически
3. Add PostgreSQL plugin → подключить строку

---

## 🗄️ База данных PostgreSQL

5 таблиц + 1 представление:

| Таблица | Назначение |
|---|---|
| `dbo.Employees` | Справочник сотрудников (ФИО, логин, отдел, сектор, руководитель, ставка) |
| `dbo.ResourceAllocations` | Ресурсный план (назначения сотрудников на проекты/активности) |
| `dbo.WorkCalendars` | Производственный календарь (норма часов по месяцам) |
| `dbo.PeriodLocks` | Блокировки периодов по группам |
| `dbo.ResourceAllocationAudits` | Журнал изменений (кто / когда / что) |
| `dbo.vw_ResourcePlanForQlik` | Представление для Qlik (джоин с Employees) |

---

## 🔐 Аутентификация

Windows-аутентификация (Negotiate/Kerberos). Логин определяется автоматически.
Права УБА — по списку логинов в `appsettings.json → UbaLogins`.

Для теста без AD: закомментировать `AddNegotiate()` в `Program.cs`.

---

## 📡 API — основные эндпоинты

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/projects` | Список проектов |
| GET | `/api/projects/{id}` | Карточка проекта + документы |
| GET | `/api/resource-plan/reference` | Справочник для заполнения |
| GET | `/api/resource-plan` | План за месяц + статусы |
| POST | `/api/resource-plan` | Сохранить назначение |
| DELETE | `/api/resource-plan/{id}` | Удалить назначение |
| GET | `/api/resource-plan/history` | История сотрудника |
| GET | `/api/resource-plan/audit` | Журнал изменений |
| POST | `/api/resource-plan/locks` | Блокировка периода |
| GET | `/api/resource-plan/export/csv` | Экспорт CSV |
| GET | `/api/portfolio/health` | Дашборды ПК |
| GET | `/api/portfolio/quality-check` | Контроль качества |
| GET | `/api/file/preview` | Превью документа |
| GET | `/api/employees` | Справочник сотрудников |
| POST | `/api/employees/bulk-import` | Массовый импорт CSV |

---

## 🛠️ Технологии

- **.NET 8** (ASP.NET Core MVC)
- **PostgreSQL 16** (Npgsql + EF Core) — безлицензионная СУБД
- **CsvHelper** — чтение projects.csv
- **ClosedXML** — экспорт в Excel
- **Canvas API** — дашборды (без внешних библиотек)
- **Docker** — контейнер для хостинга

---

## 📄 Лицензия

Внутреннее использование, ПМО.

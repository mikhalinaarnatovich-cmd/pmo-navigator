# PMO Navigator

**Портфолио и ресурсный менеджмент проектного офиса**

Веб-приложение для управления проектным портфелем: дашборды, скоринг проектов,
ресурсное планирование сотрудников, аналитика по типам проектов и отделам.

## Возможности

- **Портфолио** — список всех проектов с фильтрами по статусу, отделу, типу
- **Скоринг** — оценка проектов по критериям (риски, стратегическая значимость, сложность)
- **Ресурсный план** — распределение сотрудников по проектам и месяцам
- **Аналитика** — диаграммы по типам проектов, отделам, статусам
- **Карта проектов** — визуальная схема портфеля по отделам
- **Экспорт в Excel** — выгрузка ресурсного плана через ClosedXML

## Технологии

| Компонент | Технология |
|-----------|-----------|
| Backend | ASP.NET Core 8 MVC + Web API |
| Database | PostgreSQL (через EF Core + Npgsql) |
| Frontend | HTML, CSS, JavaScript (vanilla), Chart.js |
| Excel | ClosedXML |
| Хостинг | Render.com (бесплатный tier) |

## Структура проекта

```
pmo-navigator/
├── Controllers/            # API-контроллеры
│   ├── ProjectsApiController.cs   # CRUD проектов, диаграммы, документы
│   ├── ResourcePlanController.cs  # Ресурсный план (сотрудники, распределение)
│   ├── ScoringApiController.cs    # Скоринг проектов
│   └── HomeController.cs          # Главная страница
├── Data/                  # EF Core DbContext
│   └── PmoDbContext.cs            # Модель БД (Employees, ResourceAllocations, ...)
├── Models/                # Доменные модели
│   ├── Project.cs                 # Проект + участники
│   ├── Employee.cs               # Сотрудник
│   ├── ResourceAllocation.cs     # Запись ресурсного плана
│   └── ...
├── Services/              # Бизнес-логика
│   ├── DataService.cs            # Загрузка проектов из CSV
│   ├── CurrentUserService.cs     # Текущий пользователь (Windows Auth)
│   └── ...
├── Database/              # SQL-миграции
│   └── migrate_postgres.sql      # Создание таблиц + сид-данные
├── Views/Home/            # Razor-страницы
│   └── Index.cshtml              # SPA-страница (вся клиентская логика)
├── wwwroot/               # Статика
│   ├── data/projects.csv         # Данные проектов (315 проектов)
│   ├── css/, js/, img/
│   └── lib/                      # Chart.js, Bootstrap
├── Program.cs             # Конфигурация, авто-миграция БД при старте
├── Dockerfile             # Образ для Render/Railway/Fly.io
├── pmo_nav.csproj         # .NET 8 проект
└── appsettings.json       # Конфигурация
```

## Переменные окружения

| Переменная | Описание | Пример |
|-----------|----------|--------|
| `ConnectionStrings__PmoNavigatorDb` | Строка подключения к PostgreSQL | `Host=...;Port=5432;Database=...;Username=...;Password=...` |
| `RENDER` | Флаг хостинга на Render (отключает Windows Auth) | `true` |
| `ASPNETCORE_URLS` | Порт приложения | `http://+:10000` |

## Локальный запуск

```bash
# 1. Восстановить зависимости
dotnet restore

# 2. Запустить (нужна PostgreSQL с заданной строкой подключения)
dotnet run
```

Приложение доступно на `http://localhost:5000`.

## Деплой на Render

1. Создать Web Service из этого репозитория
2. Dockerfile уже настроен — Render автоматически соберёт образ
3. Создать PostgreSQL instance на Render
4. В Environment Variables добавить:
   - `ConnectionStrings__PmoNavigatorDb` → строка подключения из Render PostgreSQL
   - `RENDER` → `true`
5. Таблицы создаются автоматически при запуске (авто-миграция в `Program.cs`)

## Лицензия

MIT License — см. файл `LICENSE`.

---

**PMO Navigator** — Управление бизнес-анализа и исследований

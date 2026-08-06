using CsvHelper.Configuration.Attributes;

namespace PmoNav.Models;

public class Project
{
    [Name("PROJECTID")]
    public int ProjectId { get; set; }

    [Name("Название проекта")]
    public string Name { get; set; } = "";

    [Name("Статус проекта")]
    public string Status { get; set; } = "";

    [Name("Тип проекта")]
    public string ProjectType { get; set; } = "";

    [Name("Планируемое начало")]
    public string PlanStart { get; set; } = "";

    [Name("Планируемое окончание")]
    public string PlanEnd { get; set; } = "";

    [Name("Фактическое начало")]
    public string FactStart { get; set; } = "";

    [Name("Фактическое окончание")]
    public string FactEnd { get; set; } = "";

    [Name("Заказчик проекта")]
    public string Customer { get; set; } = "";

    [Name("Приоритет Проекта")]
    public string Priority { get; set; } = "";

    [Name("Отдел владелец проекта")]
    public string Department { get; set; } = "";

    [Name("Внутренний отдел")]
    public string InternalDept { get; set; } = "";

    [Name("Владелец проекта")]
    public string ProjectManager { get; set; } = "";

    [Name("Куратор проекта")]
    public string Curator { get; set; } = "";

    [Name("Проект_ссылка")]
    public string ProjectUrl { get; set; } = "";

    [Name("Цель проекта")]
    public string Goal { get; set; } = "";

    [Name("Причина")]
    public string Reason { get; set; } = "";

    [Name("В плане")]
    public string InPlan { get; set; } = "";

    [Name("Глобальный проект")]
    public string GlobalProject { get; set; } = "";

    [Name("Фактические_трудозатраты")]
    public string ActualEffort { get; set; } = "";

    [Name("Планируемые_трудозатраты")]
    public string PlannedEffort { get; set; } = "";

    [Name("Тип процесса")]
    public string ProcessType { get; set; } = "";

    [Name("Вид процесса")]
    public string ProcessKind { get; set; } = "";

    [Name("Статус здоровья проекта")]
    public string HealthStatus { get; set; } = "";

    [Name("Стратегическая важность")]
    public string StrategicImportance { get; set; } = "";

    [Name("Жёсткий дедлайн?")]
    public string HardDeadline { get; set; } = "";

    [Name("Дата дедлайна")]
    public string DeadlineDate { get; set; } = "";

    [Name("Итоговый балл")]
    public string TotalScore { get; set; } = "";

    [Name("Сложность и Риски")]
    public string ComplexityRisk { get; set; } = "";

    [Name("Стратегическое соответствие")]
    public string StrategicAlignment { get; set; } = "";

    [Name("Влияние на клиента / Доход")]
    public string ClientImpact { get; set; } = "";

    [Name("Операционная эффективность")]
    public string OperationalEfficiency { get; set; } = "";

    [Name("Снижение рисков / Срочность")]
    public string RiskReduction { get; set; } = "";

    // Заполняется при загрузке — список участников
    [Ignore]
    public List<Member> Members { get; set; } = new();
}

public class Member
{
    public int ProjectId { get; set; }
    public string Person { get; set; } = "";
    public string Role { get; set; } = "";
}
namespace Modules.Hr.Application;

/// <summary>Referencia a una configuración; <c>Archived</c> para pintarla «(archivada)» en un historial.</summary>
public sealed record SettingRefDto(Guid Id, string Name, bool Archived);

/// <summary>Misma forma que el resto de módulos.</summary>
public sealed record MunicipalityRefDto(Guid Id, string Name, Guid DepartmentId, string DepartmentName);

/// <param name="Company">Fija desde que se crea la sede; <c>IsActive</c> dice si se desactivó después en Empresas.</param>
/// <param name="InUse">
/// La usa un centro de trabajo o un registro de cargo y lugar: no se puede eliminar. La pantalla lo necesita para no ofrecer un
/// botón que va a fallar.
/// </param>
public sealed record SiteDto(
    Guid Id,
    CompanyRefDto Company,
    string Name,
    string? Address,
    MunicipalityRefDto? City,
    string? CompensationFund,
    bool Archived,
    bool InUse,
    long Version);

/// <summary>El centro de trabajo hereda la empresa de su sede (<c>Site.Id</c> ⇒ <c>SiteDto.Company</c>).</summary>
public sealed record WorkplaceDto(
    Guid Id,
    string Name,
    SettingRefDto Site,
    string? CiiuCode,
    string RiskClass,
    bool HighRiskPension,
    bool Archived,
    bool InUse,
    long Version);

public sealed record AreaDto(Guid Id, string Name, bool Archived, bool InUse, long Version);

/// <param name="ExamFrequencyMonths">
/// Months between periodic medical exams for this position (1 to 36), <c>null</c> when it carries none. Last on purpose: an older
/// frontend that does not read it keeps working, and the new one tolerates its absence (backend first).
/// </param>
public sealed record PositionDto(Guid Id, string Name, bool Archived, bool InUse, long Version, int? ExamFrequencyMonths = null);

/// <summary>
/// <c>GET /hr/settings</c>: todo lo que las pantallas necesitan en una sola llamada. <c>Companies</c> son las empresas
/// <b>activas</b> del tenant: el frontend las lee de acá y nunca de Companies (un rol con RRHH puede no tener permiso sobre
/// Empresas). <c>SetupComplete</c>: hay una empresa activa con una sede y un centro sin archivar, y un cargo sin archivar.
/// <c>CompensationFunds</c>: las cajas que una sede puede elegir (catálogo fijo en código); el selector las lee de acá y no las
/// duplica.
/// </summary>
public sealed record HrSettingsDto(
    IReadOnlyList<CompanyRefDto> Companies,
    IReadOnlyList<SiteDto> Sites,
    IReadOnlyList<WorkplaceDto> Workplaces,
    IReadOnlyList<AreaDto> Areas,
    IReadOnlyList<PositionDto> Positions,
    bool SetupComplete,
    IReadOnlyList<string> CompensationFunds);

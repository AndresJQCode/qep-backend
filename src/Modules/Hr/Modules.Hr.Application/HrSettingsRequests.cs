namespace Modules.Hr.Application;

/// <summary>
/// El cuerpo de <c>POST /hr/sites</c> y <c>PUT /hr/sites/{id}</c>. Todo es anulable para que el validador devuelva el campo y no
/// un 400 de binding. <c>CompanyId</c>: requerido, empresa activa al crear; en el <c>PUT</c> igual a la guardada (la empresa de
/// una sede es fija).
/// </summary>
public sealed record SiteRequest(Guid? CompanyId, string? Name, string? Address, Guid? CityId, string? CompensationFund);

/// <param name="HighRiskPension">Nulo es <c>false</c>.</param>
/// <param name="RiskClass">El nombre del miembro de <c>ArlRiskClass</c>: «III», no «3».</param>
public sealed record WorkplaceRequest(string? Name, Guid? SiteId, string? CiiuCode, string? RiskClass, bool? HighRiskPension);

/// <summary>Área y cargo: sólo un nombre.</summary>
public sealed record NamedSettingRequest(string? Name);

namespace Modules.Customers.Domain;

/// <summary>
/// Un municipio donde el cliente está designado agente retenedor de un impuesto municipal (spec ReteICA por empresa §7.4).
/// La designación es del municipio para un impuesto, por eso la clave es (país, tipo, ciudad): un segundo tipo municipal
/// tendría su propia lista sin migrar nada. El tenant se repite como en <see cref="CustomerTaxRate"/>.
/// </summary>
public sealed record CustomerWithholdingAgentCity(Guid TenantId, string Country, string Type, Guid CityId);

/// <summary>Lo que el llamador pide guardar; el agregado normaliza el país y colapsa los repetidos.</summary>
public sealed record WithholdingAgentCityEntry(string Country, string Type, Guid CityId);

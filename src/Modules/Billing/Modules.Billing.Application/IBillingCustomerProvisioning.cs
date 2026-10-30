namespace Modules.Billing.Application;

/// <summary>
/// El cliente que la importación da de alta (§3.2b «Alta del cliente»). <c>Phone</c> ya viene en E.164: así el
/// <c>phone_e164</c> que guarda Customers es el mismo con el que la importación emparejó. <c>Country</c> es ISO alfa-2.
/// </summary>
public sealed record BillingNewCustomer(
    string Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string Country);

/// <summary><c>Created</c> es falso cuando otra alta ganó la carrera del documento y se devolvió ese cliente.</summary>
public sealed record BillingProvisionedCustomer(Guid Id, bool Created);

/// <summary>
/// Alta de clientes desde Billing (spec §5): adaptador en Bootstrapper sobre <c>ICustomerProvisioning</c>, que es el camino
/// y las reglas de Customers; Billing no arma clientes. Nace incompleto (sin CUC) y sin clasificación.
/// </summary>
public interface IBillingCustomerProvisioning
{
    Task<BillingProvisionedCustomer> CreateAsync(
        Guid tenantId, Guid actorUserId, BillingNewCustomer data, CancellationToken cancellationToken);
}

/// <summary>
/// Los tipos de documento que acepta Customers (<c>IdentificationTypeParser.SupportedWireValues</c>), para la regla
/// <c>identification_type_invalid</c> y el desplegable de la plantilla sin que Application referencie Customers.
/// </summary>
public interface IBillingIdentificationTypes
{
    IReadOnlyCollection<string> SupportedWireValues { get; }
}

namespace Modules.Pos.Application;

/// <summary>
/// Un cliente tal como lo necesita el selector de la caja: id, nombre y el documento ya como cadena del contrato (<c>CC</c>,
/// no el enum de Customers). Lleva documento obligatorio porque sólo se ofrecen clientes con documento (spec 2026-10-14);
/// <see cref="IsComplete"/> y <see cref="IsActive"/> viajan para que la pantalla marque la ficha sin otra consulta.
/// </summary>
public sealed record PosCustomerRef(
    Guid Id, string Name, string IdentificationType, string IdentificationNumber, bool IsComplete, bool IsActive);

/// <summary>
/// Lo que la venta congela del comprador. A diferencia de <see cref="PosCustomerRef"/> el documento es opcional: un cliente
/// que nació por WhatsApp no lo tiene, y la caja igual tiene que poder leerlo (y ver que está inactivo) al cobrar.
/// </summary>
public sealed record PosCustomerSnapshot(
    Guid Id, string Name, string? IdentificationType, string? IdentificationNumber, bool IsActive);

/// <summary>Lo que el cajero teclea para dar de alta al comprador: nombre, documento y, si lo dio, teléfono.</summary>
public sealed record NewPosCustomer(string Name, string IdentificationType, string IdentificationNumber, string? Phone);

/// <summary>
/// Pos → Customers, siempre acotado por <c>tenantId</c>. Pos.Application no ve Customers (PosLayerTests), así que el
/// adaptador vive en el Bootstrapper y traduce a estos tipos.
/// </summary>
public interface IPosCustomerLookup
{
    Task<IReadOnlyList<PosCustomerRef>> SearchAsync(Guid tenantId, string? search, int take, CancellationToken cancellationToken);

    /// <summary>El cliente del tenant, activo o no; <c>null</c> si no existe en este tenant.</summary>
    Task<PosCustomerSnapshot?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Devuelve al dueño del documento aunque esté inactivo: el handler decide qué decirle al cajero.</summary>
    Task<PosCustomerRef?> FindByIdentificationAsync(Guid tenantId, string type, string number, CancellationToken cancellationToken);

    Task<PosCustomerRef> CreateFromRegisterAsync(Guid tenantId, NewPosCustomer input, CancellationToken cancellationToken);
}

public static class PosCustomerRules
{
    public const int SearchMaxLength = 120;

    public const int Take = 20;

    // Los anchos de customers.customers (nombre, documento y teléfono), copiados porque Pos no ve Customers;
    // PosCustomerLookupTests los compara con las constantes del dominio.
    public const int NameMaxLength = 160;

    public const int IdentificationNumberMaxLength = 32;

    public const int PhoneMaxLength = 32;

    // Copia de IdentificationTypeParser.SupportedWireValues: Pos.Application no ve Customers. La prueba
    // PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule las compara.
    public static readonly IReadOnlyList<string> IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];
}

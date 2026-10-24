namespace Modules.Suppliers.Domain;

/// <summary>
/// De un teléfono como lo escribió la persona a E.164 con «+», con el país del proveedor como
/// contexto. Puerto propio del dominio de proveedores y no el de Customers: ese vive en otro
/// módulo y su implementación es <c>internal</c> de su Infrastructure. La implementación, sobre
/// <c>libphonenumber-csharp</c>, vive en Suppliers.Infrastructure. Devuelve <c>null</c> si no
/// parsea o no es válido: un teléfono raro nunca impide guardar el proveedor.
/// </summary>
public interface ISupplierPhoneNormalizer
{
    string? ToE164(string? phone, string? country);
}

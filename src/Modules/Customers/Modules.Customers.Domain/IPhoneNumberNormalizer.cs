namespace Modules.Customers.Domain;

/// <summary>
/// Spec 2026-10-09 §6.5: de un teléfono como lo escribió la persona a E.164 con «+», con el país del
/// cliente como contexto. Puerto del dominio para no amarrarlo a <c>libphonenumber-csharp</c>; la
/// implementación vive en Infrastructure. <c>null</c> si no parsea o no es válido: nunca falla la
/// escritura del cliente.
/// </summary>
public interface IPhoneNumberNormalizer
{
    string? ToE164(string? phone, string country);
}

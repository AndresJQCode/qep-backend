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

    /// <summary>Spec 2026-10-10 §6.2: si <paramref name="regionCode"/> (ISO 3166 alfa-2) es una región que
    /// la librería conoce. El prefijo del BSUID sólo da el país del incompleto si lo es.</summary>
    bool IsKnownRegion(string regionCode);

    /// <summary>La región de un número E.164 con «+», o <c>null</c> si no parsea o no tiene una sola.</summary>
    string? RegionOf(string? e164);
}

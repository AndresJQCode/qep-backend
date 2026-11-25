namespace Modules.Customers.Application;

/// <summary>Un teléfono partido en la región (<c>CO</c>), los dígitos nacionales (<c>3001234567</c>) y el
/// indicativo del país, sólo dígitos (<c>57</c>).</summary>
public sealed record PhoneNumberParts(string Country, string NationalNumber, string CallingCode);

/// <summary>Spec 2026-11-10: parte un teléfono en región y número nacional, que es como el cliente lo guarda.
/// Spec 2026-11-25: también devuelve el indicativo.</summary>
public interface IPhoneNumberSplitter
{
    /// <summary>Con «+» la región se ignora; sin «+» el número es nacional de <paramref name="defaultRegion"/>
    /// (ISO 3166-1 alfa-2). Sin región, o con una vacía, se usa <c>ZZ</c> y sólo se parte lo que trae «+».
    /// Null si no se puede leer, no es válido o no tiene región; nunca lanza.</summary>
    PhoneNumberParts? Split(string? phone, string? defaultRegion = null);
}

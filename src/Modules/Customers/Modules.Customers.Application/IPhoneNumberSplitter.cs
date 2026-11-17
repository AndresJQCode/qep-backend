namespace Modules.Customers.Application;

/// <summary>Un E.164 partido en la región (<c>CO</c>) y los dígitos nacionales (<c>3001234567</c>).</summary>
public sealed record PhoneNumberParts(string Country, string NationalNumber);

/// <summary>Spec 2026-11-10: parte un E.164 en región y número nacional, que es como el cliente guarda el teléfono.</summary>
public interface IPhoneNumberSplitter
{
    /// <summary>Null si el número no es válido o no tiene región; nunca lanza.</summary>
    PhoneNumberParts? Split(string? e164);
}

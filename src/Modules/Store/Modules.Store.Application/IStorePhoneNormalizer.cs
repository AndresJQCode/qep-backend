namespace Modules.Store.Application;

/// <summary>El teléfono de un bloque a E.164 con el país de ESE bloque; null cuando no normaliza.</summary>
public interface IStorePhoneNormalizer
{
    string? ToE164(string phone, string country);
}

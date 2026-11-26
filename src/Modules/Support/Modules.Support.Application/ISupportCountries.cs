namespace Modules.Support.Application;

/// <summary>
/// Los países del solicitante del formulario público (plan F19): ISO alfa-2 que conoce el normalizador de teléfonos, porque el backend
/// no tiene catálogo de países (precedente <c>IStoreCountries</c>). La pantalla dibuja los nombres.
/// </summary>
public interface ISupportCountries
{
    bool IsKnown(string countryCode);
}

/// <summary>El celular del solicitante en E.164 con su país (plan F20); null si no es un número válido de ese país.</summary>
public interface ISupportPhoneNormalizer
{
    string? ToE164(string raw, string countryCode);
}

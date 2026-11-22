namespace Modules.Hr.Application;

/// <summary>
/// Normaliza el celular de la importación a E.164 (país Colombia). Adaptador sobre el normalizador de Customers en Bootstrapper:
/// el empleado guarda el teléfono igual que un cliente. <c>null</c> si no es un número válido.
/// </summary>
public interface IHrPhoneNormalizer
{
    string? ToE164(string phone);
}

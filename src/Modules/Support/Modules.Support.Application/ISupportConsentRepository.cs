using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los consentimientos de los tickets del formulario (plan F17). Prueba legal: sólo se insertan, en la misma transacción
/// que el ticket. No hay lectura en la aplicación: la fila se lee por SQL (pruebas, auditoría legal).</summary>
public interface ISupportConsentRepository
{
    void Add(SupportConsent consent);
}

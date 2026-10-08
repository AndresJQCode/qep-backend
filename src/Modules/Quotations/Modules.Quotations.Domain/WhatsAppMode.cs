namespace Modules.Quotations.Domain;

/// <summary>
/// Por dónde sale el WhatsApp de una cotización (spec 2026-10-07). Se guarda y viaja por nombre,
/// como el resto de enums del módulo: sumar un modo no renumera nada.
/// </summary>
public enum WhatsAppMode
{
    /// <summary>La cuenta de Zenvia de QEP, la global. «Sin fila» significa lo mismo, a
    /// propósito: un tenant nuevo envía como hoy.</summary>
    Shared,

    /// <summary>La cuenta de Zenvia del propio tenant.</summary>
    Own,

    /// <summary>Sin WhatsApp: enviar marca la cotización como enviada y el PDF lo comparte la
    /// persona por su cuenta.</summary>
    Disabled
}

/// <summary>Proveedor de la cuenta propia. Hoy sólo Zenvia; el select de la pantalla ya lo lee de
/// la respuesta para no conocer el enum.</summary>
public enum WhatsAppProvider
{
    Zenvia
}

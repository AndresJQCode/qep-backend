namespace Modules.Quotations.Domain;

/// <summary>
/// En qué paso del envío se cayó la cotización.
///
/// Existe porque "no se pudo enviar" no alcanza para saber qué hacer: que falle generar el PDF
/// es un problema de `qcode-pdf`, que falle el destinatario es un dato del cliente que alguien
/// tiene que corregir, y que falle WhatsApp es de Zenvia o de Meta. Cada uno se destraba en un
/// lado distinto.
///
/// Se guarda como texto (<c>HasConversion&lt;string&gt;()</c>), igual que
/// <see cref="QuotationHistoryEventType"/>: sumar un paso no necesita migración.
/// </summary>
public enum QuotationSendStage
{
    /// <summary>Resolver qué asesora está enviando.</summary>
    Advisor,

    /// <summary>Generar el PDF con `qcode-pdf`.</summary>
    Pdf,

    /// <summary>Publicar la copia pública del PDF que Meta tiene que poder descargar.</summary>
    Publish,

    /// <summary>Validar que el cliente exista y tenga a dónde recibir el mensaje.</summary>
    Recipient,

    /// <summary>Entregarle el mensaje a Zenvia.</summary>
    WhatsApp,

    /// <summary>Guardar la cotización ya enviada. El mensaje salió: ver
    /// <c>QuotationSendFailure.Stage</c> para por qué este caso es el más incómodo.</summary>
    Persistence
}

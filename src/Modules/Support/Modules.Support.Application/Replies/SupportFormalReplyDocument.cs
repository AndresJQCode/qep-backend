namespace Modules.Support.Application;

/// <summary>
/// Fase 4b (H12): los datos del PDF de la respuesta formal. Es el contrato con <c>formal-reply.typ</c>: viaja serializado en camelCase
/// (<c>data.ticketNumber</c>) y la plantilla imprime cada texto como texto, nunca como marcado. Renombrar una propiedad compila y rompe
/// el PDF; <c>SupportFormalReplyTemplateTests</c> lo detecta. Las fechas viajan en ISO y la plantilla las arma con
/// <see cref="DateFormat"/>, el patrón del tenant (<c>dd/MM/yyyy</c>, <c>MM/dd/yyyy</c> o <c>yyyy-MM-dd</c>).
/// </summary>
/// <param name="Anonymous">Un ticket anónimo: el destinatario es «Solicitante anónimo», sin documento ni correo.</param>
/// <param name="NextStep">H6: el del tipo o, sin él, el del tenant. Null: la plantilla omite la sección.</param>
/// <param name="SignerName">H3: el responsable del ticket, no quien envía.</param>
/// <param name="TermIsLegal">Viaja pero <c>formal-reply.typ</c> no lo lee: el owner quitó la línea del «término legal» del PDF el
/// 2026-11-16. Se conserva en el contrato para no cambiar la serialización; no vuelvas a imprimir esa línea sin revisarlo con el
/// abogado.</param>
public sealed record SupportFormalReplyDocument(
    SupportLetterheadData Letterhead,
    string TicketNumber,
    DateOnly ReceivedOn,
    DateOnly RespondedOn,
    string TypeName,
    bool TermIsLegal,
    string RecipientName,
    string? RecipientDocument,
    string? RecipientEmail,
    string? RecipientAddress,
    bool Anonymous,
    string Subject,
    string Description,
    string Decision,
    string? NextStep,
    string SignerName,
    string SignerTitle,
    IReadOnlyList<string> Annexes,
    string DateFormat);

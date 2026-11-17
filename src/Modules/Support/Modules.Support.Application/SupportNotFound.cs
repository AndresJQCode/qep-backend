using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

// Las búsquedas siempre van acotadas al tenant de la ruta, así que «no encontrado» significa «no está entre los tuyos»: un id de
// otro tenant responde igual que uno que no existe (pre-flight R12). El tenant ajeno en la ruta ya lo frenó la autorización.
internal static class SupportNotFound
{
    public static ResourceNotFoundException Ticket(Guid ticketId) =>
        new(SupportErrorCodes.TicketNotFound, $"Support ticket '{ticketId}' was not found.");

    public static ResourceNotFoundException TicketType(Guid typeId) =>
        new(SupportErrorCodes.TicketTypeNotFound, $"Support ticket type '{typeId}' was not found.");

    /// <summary>Fase 4b (H19): un token que no existe, mal formado o de otro tenant. El mensaje no lleva el token: el cuerpo es el mismo
    /// para los tres.</summary>
    public static ResourceNotFoundException Survey() =>
        new(SupportErrorCodes.SurveyNotFound, "The survey was not found.");

    public static ResourceNotFoundException Conversation(Guid conversationId) =>
        new(SupportErrorCodes.ConversationNotFound, $"Conversation '{conversationId}' was not found.");
}

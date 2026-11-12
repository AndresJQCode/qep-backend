using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Automations.Domain;

namespace Modules.Automations.Application;

/// <summary>Quién atiende una conexión, chatbot o agente (spec 2026-10-29 §4.3). <see cref="Kind"/> viaja con su nombre en minúscula,
/// <c>chatbot</c> o <c>agent</c>: el diccionario lo tiene el frontend.</summary>
public sealed record ConnectionOccupant(Guid ConnectionId, string Kind, Guid Id, string Name)
{
    public const string Chatbot = "chatbot";
    public const string Agent = "agent";
}

/// <summary>
/// Spec 2026-10-29 §4.3 y D-A2: «una conexión, un solo bot» entre chatbots y agentes, que viven en módulos que no se ven. La tabla neutral
/// es de Mensajería (<c>messaging.connection_bots</c>); el adaptador vive en Bootstrapper y escribe con SQL sobre la conexión y la
/// transacción de <c>AutomationsDbContext</c>, así que la ocupación cambia en el mismo commit que el chatbot y nunca queda a medias.
/// </summary>
public interface IChatbotConnectionSlots
{
    /// <summary>
    /// Deja las filas de chatbot del tenant iguales a sus conexiones activas (<c>automations.chatbot_connections</c>, ya guardadas en esta
    /// transacción): libera las que ningún chatbot activo tiene, toma las nuevas y actualiza el nombre. Devuelve las conexiones que un
    /// chatbot quiere y otro bot (un agente) ya ocupa: el llamador responde 409 y no confirma.
    /// </summary>
    Task<IReadOnlyList<ConnectionOccupant>> SyncChatbotsAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// 409 <c>automations.chatbot.connection_taken</c> con <c>occupant: { kind, id, name }</c> en el ProblemDetails (spec 2026-10-29 §4.3):
/// la pantalla ofrece ir a pausar al que ocupa la conexión, sea un chatbot o un agente, sin otra consulta.
/// </summary>
public sealed class ConnectionTakenException(ConnectionOccupant occupant, Exception? innerException = null)
    : ResourceConflictException(
        AutomationsErrorCodes.ChatbotConnectionTaken,
        occupant.Kind == ConnectionOccupant.Agent
            ? $"La conexión ya la atiende el agente IA «{occupant.Name}»."
            : $"La conexión ya la atiende el chatbot «{occupant.Name}».",
        innerException),
      IHasProblemExtensions
{
    public ConnectionOccupant Occupant { get; } = occupant;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["occupant"] = new { kind = occupant.Kind, id = occupant.Id, name = occupant.Name } };
}

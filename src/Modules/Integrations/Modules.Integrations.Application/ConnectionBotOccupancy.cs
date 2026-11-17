using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>El bot que atiende una conexión, leído de la ocupación neutral de Mensajería (spec 2026-10-29 §4.3). <paramref name="Kind"/>
/// es <c>"chatbot"</c> o <c>"agent"</c>: los mismos literales del 409 de chatbots y agentes, que la pantalla ya conoce.</summary>
public sealed record ConnectionBotOccupant(string Kind, Guid Id, string Name)
{
    public const string Chatbot = "chatbot";
    public const string Agent = "agent";
}

/// <summary>Quién atiende una conexión, si alguien la atiende. Integrations no ve Mensajería (<c>IntegrationsLayerTests</c>): el adaptador
/// vive en Bootstrapper.</summary>
public interface IConnectionBotOccupancy
{
    Task<ConnectionBotOccupant?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Plan 2026-11-12 (correcciones de la revisión), decisión del owner: no se borra una conexión que atiende un chatbot o un
/// agente, aunque esté en pausa. El 409 lleva <c>occupant</c>, con la misma forma que el de chatbots y agentes, para que la pantalla diga
/// a quién quitársela.</summary>
public sealed class ConnectionInUseByBotException(ConnectionBotOccupant occupant)
    : ResourceConflictException(
        IntegrationsErrorCodes.InUseByBot,
        occupant.Kind == ConnectionBotOccupant.Agent
            ? $"El agente IA «{occupant.Name}» atiende esta conexión. Quítasela antes de borrarla."
            : $"El chatbot «{occupant.Name}» atiende esta conexión. Quítasela antes de borrarla."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["occupant"] = new { kind = occupant.Kind, id = occupant.Id, name = occupant.Name },
    };
}

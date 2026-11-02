using BuildingBlocks.Domain;

namespace Modules.Wms.Domain.Control;

/// <summary>Un documento recibió una acción que su estado no admite (<c>wms.document.invalid_transition</c>, 422). Lleva el estado
/// de partida y la acción para que la pantalla diga qué pasó sin adivinar de un texto.</summary>
public sealed class InvalidTransitionException(string from, string action)
    : DomainException(WmsErrorCodes.DocumentInvalidTransition, $"The document is {from} and cannot be {action}ed."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>
    {
        ["from"] = from,
        ["action"] = action,
    };
}

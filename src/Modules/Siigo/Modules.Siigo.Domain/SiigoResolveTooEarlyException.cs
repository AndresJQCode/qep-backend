using BuildingBlocks.Domain;

namespace Modules.Siigo.Domain;

/// <summary>422 <c>siigo.invoice.resolve_too_early</c> (spec 2026-11-24 §3): se declaró que la factura no existe antes de que pase
/// <see cref="SiigoInvoice.MissingResolutionDelay"/> desde el último envío sin respuesta. <c>details.availableAt</c> dice desde cuándo se
/// puede, porque la pantalla no ve ese envío.</summary>
public sealed class SiigoResolveTooEarlyException(DateTimeOffset availableAt)
    : DomainException(
        SiigoErrorCodes.InvoiceResolveTooEarly,
        "Siigo may still be processing the invoice. Wait before declaring that it does not exist."),
      IHasProblemExtensions
{
    public DateTimeOffset AvailableAt { get; } = availableAt;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { availableAt },
    };
}

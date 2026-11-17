using Microsoft.Extensions.Logging;
using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// Cuándo y cómo pide un handler de empresa las tasas por defecto (spec 2026-11-17 §5.3). Vive aparte para que el alta y la
/// edición no repitan la regla ni el manejo del fallo.
/// </summary>
internal static partial class CompanyDefaultTaxRateSeeding
{
    /// <summary>Sólo una empresa colombiana que cobra IVA las necesita.</summary>
    public static bool Applies(bool chargesVat, string country) =>
        chargesVat && country == Company.ColombiaCountryCode;

    /// <summary>
    /// Corre después de guardar la empresa. Un fallo se loguea y no sube: la empresa es lo que el usuario pidió, y las tasas
    /// se pueden crear a mano o se vuelven a pedir con la siguiente empresa. La cancelación sí sube.
    /// </summary>
    public static async Task TryEnsureAsync(
        ICompanyDefaultTaxRates defaultTaxRates,
        ILogger logger,
        Guid tenantId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        try
        {
            await defaultTaxRates.EnsureColombianVatAsync(tenantId, actorId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sólo el tipo: el mensaje de una excepción de base puede traer valores de la fila.
            LogSeedFailed(logger, tenantId, exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The default Colombian VAT rates of tenant {TenantId} could not be seeded ({ExceptionType}); the company was saved.")]
    private static partial void LogSeedFailed(ILogger logger, Guid tenantId, string exceptionType);
}

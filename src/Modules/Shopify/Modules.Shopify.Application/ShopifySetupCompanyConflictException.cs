using BuildingBlocks.Application;
using BuildingBlocks.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 (onboarding) §7, plan D6: la empresa quedó creada (en la transacción de Companies) y la asignación a la tienda
/// perdió por versión. Sigue siendo un 412 <c>concurrency.conflict</c>, pero lleva <c>details: { companyId }</c>: el reintento manda
/// ese <c>companyId</c> y sólo asigna, sin crear otra empresa. No se «arregla» borrando la empresa: no se puede deshacer una
/// creación en otro módulo.
/// </summary>
public sealed class ShopifySetupCompanyConflictException(Guid companyId, Exception? innerException = null)
    : RequestConcurrencyException(
        "concurrency.conflict", "The Shopify store changed after it was loaded; the company was created and is not assigned.", innerException),
      IHasProblemExtensions
{
    public Guid CompanyId { get; } = companyId;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["details"] = new { companyId } };
}

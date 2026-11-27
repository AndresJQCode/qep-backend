using BuildingBlocks.Domain.Catalog;
using FluentValidation;
using FluentValidation.Results;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Baja el bloque <c>project</c> de un cuerpo a la cotización (spec 2026-11-26 §6.3 y §6.5). Lo usan el alta, el
/// guardado y sus vistas previas, por <see cref="QuotationEditsApplication"/>, así que las cuatro aplican las mismas
/// reglas de moneda y de servicio. Corre <b>después</b> de <c>Quotation.UpdateDetails</c>, cuando la moneda en la que
/// queda el documento ya se conoce.
/// </summary>
internal static class ProjectScopeApplication
{
    /// <summary>El 422 de mandar líneas a mano en un proyecto.</summary>
    public static QuotationsDomainException ManualLinesRejected() => new(
        ProjectErrors.ItemsNotAllowed,
        "En una cotización de proyecto no agregas ni cambias productos a mano: la línea sale del alcance.");

    /// <summary>La guarda de las rutas de líneas: va antes de leer el catálogo.</summary>
    public static void EnsureManualLines(Quotation quotation)
    {
        if (quotation.Kind == QuotationKind.Project)
        {
            throw ManualLinesRejected();
        }
    }

    public static async Task ApplyAsync(
        Quotation quotation,
        ProjectScopeRequest? request,
        bool currencyChanged,
        IQuotationProductPricingLookup pricingLookup,
        ITenantPriceRounding priceRounding,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Ausente conserva. Si cambió la moneda, el agregado ya borró los precios.
        if (request is null)
        {
            return;
        }

        var saved = quotation.ProjectScope
            ?? throw new InvalidOperationException("Only a project quotation has a project scope.");
        var scope = request.ToDomain();
        // Precios escritos en otra moneda (§6.5 y §12-H): no hay conversión. Si el documento cambió de moneda quedan
        // vacíos; si no cambió, valen los guardados, cada fase el suyo por id.
        if (scope.HasPrices
            && !string.Equals(request.PricesCurrency?.Trim(), quotation.Currency, StringComparison.OrdinalIgnoreCase))
        {
            scope = currencyChanged ? scope.WithoutPrices() : scope.WithPricesOf(saved);
        }

        // El catálogo se consulta sólo si el servicio cambió (§6.3): uno desactivado después no bloquea el documento.
        // Si la línea ya no está, hay que volver a armarla, así que se resuelve igual.
        var service = scope.ServiceVariantId is { } variantId
            && (variantId != saved.ServiceVariantId || quotation.Items.All(item => item.VariantId != variantId))
                ? await ResolveServiceAsync(pricingLookup, tenantId, variantId, cancellationToken)
                : null;

        // La regla vigente antes de reemplazar: los precios de fase y la línea se redondean con la misma (P8).
        quotation.UsePriceRounding(
            await priceRounding.GetAsync(tenantId, quotation.Currency, cancellationToken), now);
        EnsurePricesFit(scope.RoundedWith(quotation.PriceRounding));
        quotation.ReplaceProjectScope(scope, service, updatedBy, now);
    }

    // Sin precio de catálogo ni escalas (P2): el precio lo escribe el asesor. Mismo código para «no existe», «es de otro
    // tenant», «está inactivo» y «no es un servicio».
    private static async Task<ProjectServiceLine> ResolveServiceAsync(
        IQuotationProductPricingLookup pricingLookup,
        Guid tenantId,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var variant = await pricingLookup.FindVariantAsync(tenantId, variantId, cancellationToken);
        if (variant is null
            || variant.TenantId != tenantId
            || !variant.IsActive
            || variant.Kind != ProductKind.Service)
        {
            throw new QuotationsDomainException(
                ProjectErrors.ServiceInvalid,
                "El servicio del proyecto tiene que ser un servicio activo de tu catálogo.");
        }

        return new ProjectServiceLine(
            variant.VariantId, variant.ProductId, variant.TaxPercentage ?? 0, variant.Kind, variant.SalesUnit);
    }

    // El validador ya frena lo escrito por encima del tope; redondear puede subir un precio que cabía justo.
    private static void EnsurePricesFit(ProjectScope rounded)
    {
        const string message = "El precio redondeado pasa del máximo que admite el documento.";
        var failures = new List<ValidationFailure>();
        if (rounded.GlobalPrice > ProjectScopeLimits.MaxPrice)
        {
            failures.Add(new ValidationFailure("Project.GlobalPrice", message));
        }

        for (var index = 0; index < rounded.Phases.Count; index++)
        {
            if (rounded.Phases[index].Price > ProjectScopeLimits.MaxPrice)
            {
                failures.Add(new ValidationFailure($"Project.Phases[{index}].Price", message));
            }
        }

        if (failures.Count == 0 && rounded.Phases.Sum(phase => phase.Price ?? 0m) > ProjectScopeLimits.MaxPrice)
        {
            failures.Add(new ValidationFailure("Project.Phases", message));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }
}

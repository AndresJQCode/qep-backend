using BuildingBlocks.Application;
using FluentValidation;
using Modules.Store.Domain;

namespace Modules.Store.Application;

public sealed record QuotePublicCartQuery(string Host, string? Currency, IReadOnlyList<PublicCartLineRequest> Lines)
    : IQuery<PublicCartQuoteDto>;

/// <summary>La forma del carrito: de 1 a 100 líneas, cada una con una variante y una cantidad entera positiva y acotada.</summary>
public sealed class QuotePublicCartValidator : AbstractValidator<QuotePublicCartQuery>
{
    public const int MaxLines = 100;

    public const int MaxQuantity = 100_000;

    public QuotePublicCartValidator()
    {
        RuleFor(query => query.Lines)
            .NotNull()
            .Must(lines => lines is null || (lines.Count >= 1 && lines.Count <= MaxLines))
            .WithMessage($"El carrito lleva de 1 a {MaxLines} líneas.")
            .OverridePropertyName("lines");
        RuleForEach(query => query.Lines)
            .Must(line => line is { VariantId: var id } && id != Guid.Empty)
            .WithMessage("Cada línea necesita su variante.")
            .Must(line => line is { Quantity: >= 1 and <= MaxQuantity })
            .WithMessage($"La cantidad va de 1 a {MaxQuantity}.")
            .OverridePropertyName("lines");
    }
}

public sealed class QuotePublicCartHandler(
    StorefrontResolver resolver,
    PublicCartPricing pricing,
    IValidator<QuotePublicCartQuery> validator)
    : IQueryHandler<QuotePublicCartQuery, PublicCartQuoteDto>
{
    public async Task<PublicCartQuoteDto> HandleAsync(QuotePublicCartQuery query, CancellationToken cancellationToken)
    {
        var (store, _) = await resolver.ResolveAsync(query.Host, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, query.Currency);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var (quote, _) = await pricing.PriceAsync(store.TenantId, currency, store.PricingMode, query.Lines, cancellationToken);
        return quote;
    }
}

/// <summary>
/// El precio de un carrito, compartido por la cotización y por el envío de la solicitud (que lo vuelve a correr al enviar). Un carrito
/// viejo no se rechaza entero: se precian las líneas que la tienda todavía vende en esa moneda y las otras se reportan como no
/// disponibles. Los totales salen siempre del motor de pedidos.
/// </summary>
public sealed class PublicCartPricing(IStoreCatalogReader catalog, IStoreOrders orders)
{
    public async Task<(PublicCartQuoteDto Quote, IReadOnlyList<Guid> Unavailable)> PriceAsync(
        Guid tenantId,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<PublicCartLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var (quote, unavailable, _) = await PriceWithOriginsAsync(tenantId, currency, mode, lines, cancellationToken);
        return (quote, unavailable);
    }

    /// <summary>
    /// Lo mismo que <see cref="PriceAsync"/> más el origen del descuento de cada línea (por variante), que la cotización pública no
    /// expone pero la solicitud congela.
    /// </summary>
    public async Task<(PublicCartQuoteDto Quote, IReadOnlyList<Guid> Unavailable, IReadOnlyDictionary<Guid, string> Origins)> PriceWithOriginsAsync(
        Guid tenantId,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<PublicCartLineRequest> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // El motor rechaza una variante repetida: se juntan acá, en el orden en que aparecieron.
        var merged = lines
            .GroupBy(line => line.VariantId)
            .Select(group => (VariantId: group.Key, Quantity: checked(group.Sum(line => line.Quantity))))
            .ToList();

        var sellable = await catalog.SellableVariantsAsync(
            tenantId, currency, merged.Select(line => line.VariantId).ToList(), cancellationToken);
        var available = merged.Where(line => sellable.ContainsKey(line.VariantId)).ToList();
        var unavailable = merged
            .Where(line => !sellable.ContainsKey(line.VariantId))
            .Select(line => line.VariantId)
            .ToList();

        if (available.Count == 0)
        {
            return (new PublicCartQuoteDto(currency, [], unavailable, 0m, 0m, 0m, 0m), unavailable, new Dictionary<Guid, string>());
        }

        var quoted = await orders.QuoteAsync(tenantId, currency, mode, available, cancellationToken);
        var quote = new PublicCartQuoteDto(
            quoted.Currency,
            quoted.Lines.Select(line => new PublicCartQuoteLineDto(
                line.VariantId,
                line.ProductId,
                sellable.TryGetValue(line.VariantId, out var variant) ? variant.Name : line.Name,
                variant?.ImageUrl,
                (int)line.Quantity,
                line.UnitPrice,
                line.DiscountPercentage,
                line.DiscountPercentage > 0,
                line.DiscountedUnitPrice,
                line.LineTotal)).ToList(),
            unavailable,
            quoted.Subtotal,
            quoted.DiscountAmount,
            quoted.TaxAmount,
            quoted.Total);
        return (quote, unavailable, quoted.Lines.ToDictionary(line => line.VariantId, line => line.DiscountOrigin));
    }
}

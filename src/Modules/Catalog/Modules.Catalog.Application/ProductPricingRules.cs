using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Validación de forma para precio y escalas (CAT-09), compartida por <c>POST</c> y
/// <c>PUT</c> por inclusión, mismo criterio que <see cref="ProductWriteRules"/>.
///
/// Sólo lo que es atribuible a un campo concreto vive acá: cada fila de precios, los límites
/// numéricos y el rango desde/hasta de cada escala. Las reglas que cruzan producto y escala
/// —restricción sin su campo obligatorio— las hace cumplir el dominio (<see cref="Product"/>/<see cref="PriceScale"/>)
/// y llegan como 422 con código, sin mapa por campo: no hay un único campo al que apuntar
/// cuando el problema es la relación entre dos.
/// </summary>
internal sealed class ProductPricingRules : AbstractValidator<ProductPricingRequest>
{
    public ProductPricingRules()
    {
        // Per row (CurrencyAmountRules). A missing or empty map is left to the domain, which
        // answers catalog.product.price_required: a code and not a field, because no row is wrong.
        RuleFor(pricing => pricing.Prices).Custom((prices, context) =>
        {
            if (prices is null)
            {
                return;
            }

            foreach (var failure in CurrencyAmountRules.Check(prices, context.PropertyPath))
            {
                context.AddFailure(failure);
            }
        });

        RuleForEach(pricing => pricing.Scales).SetValidator(new PriceScaleRequestRules());
    }
}

internal sealed class PriceScaleRequestRules : AbstractValidator<PriceScaleRequest>
{
    public PriceScaleRequestRules()
    {
        RuleFor(scale => scale.FromUnit).GreaterThanOrEqualTo(1);
        RuleFor(scale => scale.ToUnit)
            .GreaterThan(scale => scale.FromUnit)
            .WithMessage("The price scale's ending unit must be greater than its starting unit.");
        RuleFor(scale => scale.Discount)
            .InclusiveBetween((decimal)PriceScale.MinDiscount, (decimal)PriceScale.MaxDiscount);
    }
}

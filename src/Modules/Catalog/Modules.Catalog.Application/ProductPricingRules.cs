using FluentValidation;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Validación de forma para precio y escalas (CAT-09), compartida por <c>POST</c> y
/// <c>PUT</c> por inclusión, mismo criterio que <see cref="ProductWriteRules"/>.
///
/// Sólo lo que es atribuible a un campo concreto vive acá: límites numéricos y el rango
/// desde/hasta de cada escala. Las reglas que cruzan producto y escala —moneda de la escala
/// sin base del producto, precio final que no cuadra con base × descuento, restricción sin su
/// campo obligatorio— las hace cumplir el dominio (<see cref="Product"/>/<see cref="PriceScale"/>)
/// y llegan como 422 con código, sin mapa por campo: no hay un único campo al que apuntar
/// cuando el problema es la relación entre dos.
/// </summary>
internal sealed class ProductPricingRules : AbstractValidator<ProductPricingRequest>
{
    public ProductPricingRules()
    {
        RuleFor(pricing => pricing.BaseUsd).GreaterThanOrEqualTo(0m).When(p => p.BaseUsd.HasValue);
        RuleFor(pricing => pricing.BaseCop).GreaterThanOrEqualTo(0m).When(p => p.BaseCop.HasValue);
        RuleFor(pricing => pricing.FinalUsd).GreaterThanOrEqualTo(0m).When(p => p.FinalUsd.HasValue);
        RuleFor(pricing => pricing.FinalCop).GreaterThanOrEqualTo(0m).When(p => p.FinalCop.HasValue);
        RuleFor(pricing => pricing.Discount)
            .InclusiveBetween((decimal)PriceScale.MinDiscount, (decimal)PriceScale.MaxDiscount)
            .When(p => p.Discount.HasValue);

        RuleForEach(pricing => pricing.Scales).SetValidator(new PriceScaleRequestRules());
    }
}

internal sealed class PriceScaleRequestRules : AbstractValidator<PriceScaleRequest>
{
    public PriceScaleRequestRules()
    {
        RuleFor(scale => scale.PriceListId).NotEmpty();
        RuleFor(scale => scale.FromUnit).GreaterThanOrEqualTo(1);
        RuleFor(scale => scale.ToUnit)
            .GreaterThan(scale => scale.FromUnit)
            .WithMessage("The price scale's ending unit must be greater than its starting unit.");
        RuleFor(scale => scale.Discount)
            .InclusiveBetween((decimal)PriceScale.MinDiscount, (decimal)PriceScale.MaxDiscount);
        RuleFor(scale => scale.FinalUsd).GreaterThanOrEqualTo(0m).When(s => s.FinalUsd.HasValue);
        RuleFor(scale => scale.FinalCop).GreaterThanOrEqualTo(0m).When(s => s.FinalCop.HasValue);
    }
}

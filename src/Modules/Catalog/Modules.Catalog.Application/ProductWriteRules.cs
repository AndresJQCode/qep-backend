using FluentValidation;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Lo que un POST y un PUT de producto tienen en común, para que las reglas de validación puedan
/// escribirse **una sola vez**.
///
/// Existe por el hallazgo `D` de la revisión de 4 lentes de CAT-04: los bloques de reglas estaban
/// duplicados textualmente entre <see cref="CreateProductValidator"/> y
/// <see cref="UpdateProductValidator"/>, así que corregir una sola copia dejaba `POST` y `PUT`
/// validando distinto — y ninguna prueba lo habría notado, porque cada verbo tenía las suyas.
///
/// `TaxRateId` e `ImageFileId` no están acá a propósito: no los valida el validador. El primero lo
/// resuelve <see cref="ProductTaxRateResolver"/> contra el catálogo del tenant; el segundo es una
/// referencia blanda a `Storage` y hoy no se verifica.
/// </summary>
public interface IProductWriteCommand
{
    string Name { get; }

    string Code { get; }

    string? Description { get; }

    decimal? Price { get; }

    string? Currency { get; }
}

/// <summary>
/// Las reglas de escritura de producto. El dominio hace cumplir las mismas y tiraría un 422 con un
/// solo código; el validador existe para que la respuesta lleve el mapa de errores **por campo**
/// que <c>ApiExceptionHandler</c> arma desde <c>ValidationException</c>, que es lo que un
/// formulario necesita para marcar el input culpable.
/// </summary>
internal sealed class ProductWriteRules : AbstractValidator<IProductWriteCommand>
{
    public ProductWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Product.NameMaxLength);
        RuleFor(command => command.Code)
            .NotEmpty()
            .MaximumLength(Product.CodeMaxLength);
        RuleFor(command => command.Description)
            .MaximumLength(ProductDetails.DescriptionMaxLength);
        RuleFor(command => command.Price)
            .GreaterThanOrEqualTo(0m)
            .When(command => command.Price.HasValue);

        // Hallazgo `F`: la regla comprobaba sólo el largo, mientras el dominio además exige
        // letras. Con Length() solo, "123" atravesaba el validador y lo rechazaba el dominio —
        // 422 con código y sin mapa por campo, la misma forma del hallazgo `A`.
        RuleFor(command => command.Currency)
            .Length(ProductDetails.CurrencyLength)
            .Matches("^[A-Za-z]{3}$")
            .WithMessage("The currency must be a three-letter ISO 4217 code.")
            .When(command => !string.IsNullOrWhiteSpace(command.Currency));

        // Hallazgo `A`: el invariante «precio y moneda van juntos» cruza dos campos y sólo lo
        // hacía cumplir el dominio, que no produce mapa por campo. Escrito en los dos sentidos y
        // apuntando cada uno al campo que hay que corregir: quien mandó precio sin moneda tiene
        // que completar la moneda, y al revés. Una guarda escrita en un solo sentido deja pasar
        // el otro — la misma razón por la que ProductDetails.Normalized() compara los dos.
        RuleFor(command => command.Currency)
            .NotEmpty()
            .When(command => command.Price.HasValue)
            .WithMessage("A price requires its currency.");
        RuleFor(command => command.Price)
            .NotNull()
            .When(command => !string.IsNullOrWhiteSpace(command.Currency))
            .WithMessage("A currency requires its price.");
    }
}

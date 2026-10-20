using FluentValidation;
using FluentValidation.Results;
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
/// `TaxRateId` y los archivos de `Gallery` no están acá a propósito: el primero lo resuelve
/// <see cref="ProductTaxRateResolver"/> contra el catálogo del tenant; los segundos, que son
/// referencia blanda a `Storage`, <see cref="ProductImageResolver"/>. Las opciones y sus valores
/// contra la biblioteca, y los códigos contra el resto del tenant, los revisa
/// <see cref="ProductVariantReferences"/> en el handler: necesitan leer la base.
/// </summary>
public interface IProductWriteCommand
{
    string Name { get; }

    string? Description { get; }

    IReadOnlyList<Guid>? OptionIds { get; }

    IReadOnlyList<ProductVariantRequest>? Variants { get; }

    IReadOnlyList<Guid>? Gallery { get; }
}

/// <summary>
/// Las reglas de escritura de producto. El dominio hace cumplir las mismas y tiraría un 422 con un
/// solo código; el validador existe para que la respuesta lleve el mapa de errores **por campo**
/// (<c>Variants[3].Code</c>) que <c>ApiExceptionHandler</c> arma desde <c>ValidationException</c>,
/// que es lo que un formulario necesita para marcar el input culpable. Cada regla lleva como
/// <c>ErrorCode</c> el código que daría el dominio, para que las dos capas hablen igual.
/// </summary>
internal sealed class ProductWriteRules : AbstractValidator<IProductWriteCommand>
{
    public ProductWriteRules(bool pricingRequired)
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Product.NameMaxLength);
        RuleFor(command => command.Description)
            .MaximumLength(ProductDetails.DescriptionMaxLength);

        RuleFor(command => command.OptionIds)
            .Must(ids => ids is null || ids.Count <= Product.MaxOptions)
                .WithErrorCode("catalog.product.option_limit")
                .WithMessage($"Un producto puede tener hasta {Product.MaxOptions} opciones.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
                .WithErrorCode("catalog.product.option_duplicate")
                .WithMessage("No repitas una opción en el mismo producto.");

        RuleFor(command => command.Gallery)
            .Must(ids => ids is null || ids.Count <= Product.MaxGalleryImages)
                .WithErrorCode("catalog.product.gallery_limit")
                .WithMessage($"Un producto puede tener hasta {Product.MaxGalleryImages} fotos.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
                .WithErrorCode("catalog.product.gallery_duplicate")
                .WithMessage("No repitas una foto en la galería.");

        RuleFor(command => command.Variants)
            .NotEmpty()
                .WithErrorCode("catalog.variant.required")
                .WithMessage("El producto necesita al menos una variante.")
            .Must(variants => variants is null || variants.Count <= Product.MaxVariants)
                .WithErrorCode("catalog.variant.limit_exceeded")
                .WithMessage($"Un producto puede tener hasta {Product.MaxVariants} variantes.");

        RuleForEach(command => command.Variants)
            .NotNull()
            .SetValidator(new ProductVariantRequestRules(pricingRequired));

        RuleFor(command => command).Custom(CheckAcrossVariants);
    }

    // Lo que compara una variante con las demás del mismo cuerpo. El error va en la segunda de
    // cada par repetido: la primera es la que el usuario ya tenía.
    private static void CheckAcrossVariants(
        IProductWriteCommand command,
        ValidationContext<IProductWriteCommand> context)
    {
        if (command.Variants is not { Count: > 0 } variants)
        {
            return;
        }

        var ids = new HashSet<Guid>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var barcodes = new HashSet<string>(StringComparer.Ordinal);
        var gallery = (command.Gallery ?? []).ToHashSet();
        var optionCount = command.OptionIds?.Count ?? 0;

        for (var index = 0; index < variants.Count; index++)
        {
            var variant = variants[index];
            if (variant is null)
            {
                continue;
            }

            var field = $"{nameof(IProductWriteCommand.Variants)}[{index}]";

            if (variant.Id is { } id && !ids.Add(id))
            {
                context.AddFailure(Failure(
                    $"{field}.{nameof(ProductVariantRequest.Id)}",
                    "catalog.variant.duplicated_in_request",
                    "Esta variante viene dos veces."));
            }

            // Recortado y ordinal, igual que el dominio y el índice único: «VS-001 » y «VS-001»
            // son el mismo código; «vs-001» no.
            if (variant.Code?.Trim() is { Length: > 0 } code && !codes.Add(code))
            {
                context.AddFailure(Failure(
                    $"{field}.{nameof(ProductVariantRequest.Code)}",
                    "catalog.product.code_taken",
                    "Otra variante de este producto ya usa este código."));
            }

            if (variant.Barcode?.Trim() is { Length: > 0 } barcode && !barcodes.Add(barcode))
            {
                context.AddFailure(Failure(
                    $"{field}.{nameof(ProductVariantRequest.Barcode)}",
                    "catalog.variant.barcode_taken",
                    "Otra variante de este producto ya usa este código de barras."));
            }

            // Sólo cuando el cuerpo los trae: en el PUT, null en una variante guardada se proyecta
            // (ProductVariantChanges) y la cuenta se revisa después, con la biblioteca en la mano.
            if (variant.OptionValueIds is { } values && values.Count != optionCount)
            {
                context.AddFailure(Failure(
                    $"{field}.{nameof(ProductVariantRequest.OptionValueIds)}",
                    "catalog.variant.value_count_mismatch",
                    "Elige un valor para cada opción del producto."));
            }

            if (variant.GalleryFileId is { } fileId && !gallery.Contains(fileId))
            {
                context.AddFailure(Failure(
                    $"{field}.{nameof(ProductVariantRequest.GalleryFileId)}",
                    "catalog.variant.image_not_in_gallery",
                    "La foto de la variante tiene que estar en la galería del producto."));
            }
        }
    }

    internal static ValidationFailure Failure(string field, string code, string message) =>
        new(field, message) { ErrorCode = code };
}

/// <summary>Una variante por su cuenta: código, código de barras y precio.</summary>
internal sealed class ProductVariantRequestRules : AbstractValidator<ProductVariantRequest>
{
    public ProductVariantRequestRules(bool pricingRequired)
    {
        // El largo se mide recortado, como lo guarda el dominio: 60 caracteres con espacios en
        // los bordes son un código válido de 60.
        RuleFor(variant => variant.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code))
                .WithErrorCode("catalog.product.code_required")
                .WithMessage("Escribe el código de la variante.")
            .Must(code => code is null || code.Trim().Length <= ProductVariant.CodeMaxLength)
                .WithErrorCode("catalog.product.code_too_long")
                .WithMessage($"El código puede tener hasta {ProductVariant.CodeMaxLength} caracteres.");

        RuleFor(variant => variant.Barcode)
            .Must(barcode => barcode is null || barcode.Trim().Length <= ProductVariant.BarcodeMaxLength)
                .WithErrorCode("catalog.variant.barcode_too_long")
                .WithMessage($"El código de barras puede tener hasta {ProductVariant.BarcodeMaxLength} caracteres.");

        if (pricingRequired)
        {
            RuleFor(variant => variant.Pricing)
                .NotNull()
                    .WithErrorCode("catalog.product.price_required")
                    .WithMessage("La variante necesita precio en al menos una moneda.");
        }

        RuleFor(variant => variant.Pricing!)
            .SetValidator(new ProductPricingRules())
            .When(variant => variant.Pricing is not null);
    }
}

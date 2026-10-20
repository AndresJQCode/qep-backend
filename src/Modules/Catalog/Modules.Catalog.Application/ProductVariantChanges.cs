using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Cambiar las opciones de un producto que ya existe (spec 2026-10-20 §3.3.1): convierte lo que
/// manda el formulario en lo que <see cref="Product.Update"/> aplica.
///
/// El PUT empareja por id —una variante con id es esa variante guardada; sin id, una nueva— y el
/// agregado hace cumplir los invariantes. Lo que vive acá es lo que el formulario **no tiene por
/// qué mandar**, para que agregar o quitar una opción no le exija reescribir cada fila:
/// <list type="bullet">
/// <item><b>Agregar opciones a un producto sin opciones.</b> La variante por defecto viene con su
/// id y los valores que el tenant le eligió (o sin valores: toma el primer valor activo de cada
/// opción). Conserva id, código, código de barras, precios, escalas, empaques e historial: es la
/// misma fila. Las demás combinaciones vienen sin id y sin precio, y nacen con el de ella.</item>
/// <item><b>Agregar una opción más.</b> Cada variante guardada que viene sin valores conserva los
/// suyos y toma el primer valor activo de la opción nueva; las combinaciones que faltan vienen sin
/// id y copian el precio de la guardada que más valores comparte con ellas («la de su misma
/// talla»).</item>
/// <item><b>Quitar una opción.</b> Las variantes sueltan el valor de esa opción. Si dos activas
/// quedan con la misma combinación, sigue activa la primera (en el orden del cuerpo) y las demás se
/// desactivan: nunca dos activas iguales. Quitar todas deja una sola activa, que pasa a ser la
/// variante por defecto. Desactivar y no borrar: el agregado no sabe si una variante está en uso.
/// Para borrar una, el formulario la nombra en <c>DeletedVariantIds</c> y <c>UpdateProductHandler</c>
/// decide con <see cref="IVariantUsageChecker"/> (spec §3.3, Tarea 6).</item>
/// </list>
///
/// Lo que el formulario sí manda, gana: valores, precio y estado explícitos no se tocan (salvo la
/// desactivación de repetidas al quitar una opción, que es la regla del spec).
///
/// Corre **antes** de <see cref="ProductPriceChangeDetector"/> y de <see cref="Product.Update"/>:
/// el detector compara cada variante guardada contra lo que esto devuelve, así que una variante
/// que conserva su precio no deja filas de historial.
/// </summary>
internal static class ProductVariantChanges
{
    /// <param name="library">Las opciones de la biblioteca por id, al menos las de
    /// <paramref name="optionIds"/>: de ahí sale el primer valor activo de una opción nueva.</param>
    public static IReadOnlyList<ProductVariantInput> Resolve(
        Product product,
        IReadOnlyList<Guid> optionIds,
        IReadOnlyList<ProductVariantRequest> variants,
        IReadOnlyDictionary<Guid, OptionDefinition> library)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(optionIds);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(library);

        var saved = product.Variants;
        var savedById = saved.ToDictionary(variant => variant.Id.Value);
        var previousOptionIds = product.OptionIds;

        var inputs = new List<ProductVariantInput>(variants.Count);
        foreach (var request in variants)
        {
            // Un id que no es de este producto no se resuelve acá: el agregado lo rechaza con
            // catalog.variant.not_found, y para eso tiene que llegarle tal cual.
            var savedVariant = request.Id is { } id ? savedById.GetValueOrDefault(id) : null;

            var values = request.OptionValueIds
                ?? (savedVariant is null
                    ? []
                    : Project(savedVariant.OptionValueIds, previousOptionIds, optionIds, library));

            var pricing = request.Pricing?.ToDomain()
                ?? PricingOf(savedVariant ?? SourceFor(values, saved));

            inputs.Add(new ProductVariantInput
            {
                Id = request.Id,
                Code = request.Code ?? string.Empty,
                Barcode = request.Barcode,
                OptionValueIds = values,
                Pricing = pricing,
                GalleryFileId = request.GalleryFileId,
                IsActive = request.IsActive ?? savedVariant?.IsActive ?? true,
            });
        }

        // Sólo cuando se quitó una opción: es el único caso en que dos variantes que eran distintas
        // pueden quedar iguales sin que el usuario lo haya pedido. En cualquier otro, dos activas con
        // la misma combinación son un error del cuerpo y las rechaza ProductVariantReferences.
        return previousOptionIds.Any(previous => !optionIds.Contains(previous))
            ? DeactivateRepeatedCombinations(inputs)
            : inputs;
    }

    // Los valores guardados, reordenados a las opciones nuevas: una opción que sigue conserva su
    // valor (aunque haya cambiado de posición), una que se quitó lo suelta y una nueva toma su
    // primer valor activo (spec §3.3.1: «por defecto, el primero»). Si la opción nueva no está en la
    // biblioteca o no tiene valores activos, la lista queda corta y ProductVariantReferences la
    // rechaza con value_count_mismatch.
    private static List<Guid> Project(
        IReadOnlyList<Guid> savedValues,
        IReadOnlyList<Guid> previousOptionIds,
        IReadOnlyList<Guid> optionIds,
        IReadOnlyDictionary<Guid, OptionDefinition> library)
    {
        var projected = new List<Guid>(optionIds.Count);
        foreach (var optionId in optionIds)
        {
            var previousPosition = IndexOf(previousOptionIds, optionId);
            if (previousPosition >= 0 && previousPosition < savedValues.Count)
            {
                projected.Add(savedValues[previousPosition]);
                continue;
            }

            var first = library.GetValueOrDefault(optionId)?.Values
                .Where(value => value.IsActive)
                .OrderBy(value => value.Position)
                .FirstOrDefault();
            if (first is null)
            {
                break;
            }

            projected.Add(first.Id);
        }

        return projected;
    }

    // La variante guardada de la que copia una nueva: la que más valores comparte con ella; ante un
    // empate, la primera en orden. Al agregar opciones a un producto sin opciones es la variante por
    // defecto (la única); al agregar Color a un producto con Talla, la de su misma talla.
    private static ProductVariant SourceFor(IReadOnlyList<Guid> values, IReadOnlyList<ProductVariant> saved) =>
        saved.MaxBy(variant => variant.OptionValueIds.Count(values.Contains))
            ?? throw new InvalidOperationException("A saved product always has at least one variant.");

    private static ProductPricing PricingOf(ProductVariant variant) => new()
    {
        Prices = variant.Prices.ToDictionary(price => price.Currency, price => price.Amount, StringComparer.Ordinal),
        // Una escala incompleta (la que deja la copia de escalas) no se puede volver a crear como
        // completa: el agregado responde price_scale.restriction_required, que es lo mismo que pasa
        // hoy al guardar un producto con escalas copiadas sin completar.
        Scales = variant.PriceScales
            .OrderBy(scale => scale.FromUnit)
            .Select(scale => new PriceScaleInput(
                scale.FromUnit, scale.ToUnit, scale.Discount, scale.Restriction, scale.Multiple, scale.AllowGrouping))
            .ToArray(),
        PackagingUnits = variant.PackagingUnits.ToArray(),
    };

    private static List<ProductVariantInput> DeactivateRepeatedCombinations(List<ProductVariantInput> inputs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return inputs
            .Select(input => input.IsActive && !seen.Add(string.Join(',', input.OptionValueIds))
                ? input with { IsActive = false }
                : input)
            .ToList();
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            if (ids[index] == id)
            {
                return index;
            }
        }

        return -1;
    }
}

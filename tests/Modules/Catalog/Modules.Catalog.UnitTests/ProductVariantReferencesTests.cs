using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Lo que el handler revisa contra la biblioteca antes de guardar (Tarea 5, pendiente de la
/// Tarea 2: el dominio no ve la base) y las reglas por campo del validador. Las del handler salen
/// como <see cref="CatalogDomainException"/> con su código y el mapa por campo; las del validador,
/// como <c>ValidationException</c> (<c>validation.failed</c>).
/// </summary>
public sealed class ProductVariantReferencesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly OptionDefinition _talla =
        OptionDefinition.Create(TenantId, "Talla", ["S", "M"], isActive: true, Now);

    private readonly OptionDefinition _color =
        OptionDefinition.Create(TenantId, "Color", ["Azul"], isActive: true, Now);

    [Fact]
    public void AnOptionMissingFromTheTenantLibraryIsMarkedOnItsPosition()
    {
        var foreign = Guid.CreateVersion7();

        var error = Assert.Throws<CatalogDomainException>(() => ProductVariantReferences.EnsureValid(
            null, [_talla.Id.Value, foreign], [Variant(ValueOf(_talla, "S"), Guid.CreateVersion7())], Library()));

        Assert.Equal("catalog.option.not_found", error.Code);
        Assert.Equal(["OptionIds[1]"], error.FieldErrors.Keys);
    }

    // La FK sólo dice que el valor existe; que sea de la opción de su posición lo dice esto.
    [Fact]
    public void AValueOfAnotherOptionIsMarkedOnTheVariantValue()
    {
        var error = Assert.Throws<CatalogDomainException>(() => ProductVariantReferences.EnsureValid(
            null, [_talla.Id.Value], [Variant(ValueOf(_talla, "S")), Variant(ValueOf(_color, "Azul"))], Library()));

        Assert.Equal("catalog.option.value_not_found", error.Code);
        Assert.Equal(["Variants[1].OptionValueIds[0]"], error.FieldErrors.Keys);
    }

    [Fact]
    public void TwoActiveVariantsWithTheSameCombinationAreRejectedButAnInactiveOneIsNot()
    {
        var small = ValueOf(_talla, "S");

        var error = Assert.Throws<CatalogDomainException>(() => ProductVariantReferences.EnsureValid(
            null, [_talla.Id.Value], [Variant(small), Variant(small)], Library()));
        Assert.Equal("catalog.variant.combination_duplicate", error.Code);
        Assert.Equal(["Variants[1].OptionValueIds"], error.FieldErrors.Keys);

        ProductVariantReferences.EnsureValid(
            null, [_talla.Id.Value], [Variant(small), Variant(small) with { IsActive = false }], Library());
    }

    [Fact]
    public void AProductNeedsAtLeastOneActiveVariant()
    {
        var error = Assert.Throws<CatalogDomainException>(() => ProductVariantReferences.EnsureValid(
            null, [], [new ProductVariantInput { Code = "A", IsActive = false }], Library()));

        Assert.Equal("catalog.variant.active_required", error.Code);
        Assert.Equal(["Variants"], error.FieldErrors.Keys);
    }

    // Spec §3.1: un valor inactivo no se ofrece para variantes nuevas, pero las que ya lo usaban lo
    // conservan.
    [Fact]
    public void AnInactiveValueIsRejectedForNewUseAndKeptWhereTheProductAlreadyUsedIt()
    {
        var small = ValueOf(_talla, "S");
        var product = Product.Create(
            ProductId.New(), TenantId, "Camiseta", ProductDetails.Empty, [_talla.Id.Value],
            [Variant(small) with { Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 1m } } }],
            [], Now);
        _talla.SetValueActive(small, isActive: false);

        var error = Assert.Throws<CatalogDomainException>(() => ProductVariantReferences.EnsureValid(
            null, [_talla.Id.Value], [Variant(small)], Library()));
        Assert.Equal("catalog.option.value_inactive", error.Code);

        ProductVariantReferences.EnsureValid(product, [_talla.Id.Value], [Variant(small)], Library());
    }

    // El mapa por campo que pide el brief (variants[3].code): el código repetido se marca en la
    // segunda variante que lo trae, no en el producto.
    [Fact]
    public void TheValidatorMarksARepeatedCodeOnTheVariantThatRepeatsIt()
    {
        var result = new CreateProductValidator().Validate(new CreateProductCommand(
            TenantId,
            "Camiseta",
            null,
            null,
            null,
            null,
            [Request("CAM-001"), Request("CAM-002"), Request(" CAM-001 ")],
            null));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("Variants[2].Code", failure.PropertyName);
        Assert.Equal("catalog.product.code_taken", failure.ErrorCode);
    }

    [Fact]
    public void TheValidatorMarksABlankCodeAndAPhotoOutsideTheGalleryOnTheirVariant()
    {
        var result = new CreateProductValidator().Validate(new CreateProductCommand(
            TenantId,
            "Camiseta",
            null,
            null,
            null,
            null,
            [Request("CAM-001"), Request("  ") with { GalleryFileId = Guid.CreateVersion7() }],
            []));

        Assert.Contains(result.Errors, failure =>
            failure.PropertyName == "Variants[1].Code" && failure.ErrorCode == "catalog.product.code_required");
        Assert.Contains(result.Errors, failure =>
            failure.PropertyName == "Variants[1].GalleryFileId" && failure.ErrorCode == "catalog.variant.image_not_in_gallery");
    }

    private Dictionary<Guid, OptionDefinition> Library() => new()
    {
        [_talla.Id.Value] = _talla,
        [_color.Id.Value] = _color,
    };

    private static ProductVariantInput Variant(params Guid[] values) =>
        new() { Code = $"V-{Guid.NewGuid():N}"[..10], OptionValueIds = values };

    private static ProductVariantRequest Request(string code) =>
        new(null, code, null, null, new ProductPricingRequest(new Dictionary<string, decimal> { ["USD"] = 1m }, null, null), null, null);

    private static Guid ValueOf(OptionDefinition option, string value) =>
        option.Values.Single(candidate => candidate.Value == value).Id;
}

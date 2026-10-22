using System.Text.RegularExpressions;
using Modules.Pos.Application;

namespace Modules.Pos.UnitTests;

public sealed class PosSaleFingerprintTests
{
    private static readonly Guid Session = Guid.Parse("01920000-0000-7000-8000-0000000000aa");
    private static readonly Guid ProductA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ProductB = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static CreatePosSaleCommand Command(
        PosSaleLineRequest[]? lines = null, PosPaymentRequest[]? payments = null, Guid? id = null,
        Guid? customerId = null) =>
        new(
            PosFixtures.TenantId,
            id ?? Guid.Parse("6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11"),
            Session,
            lines ?? [new(ProductA, 2m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)],
            payments ?? [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)],
            customerId);

    [Fact]
    public void TheSameBodyGivesTheSameLowercaseSha256()
    {
        var first = PosSaleFingerprint.Compute(Command());
        var second = PosSaleFingerprint.Compute(Command());

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), first);
    }

    // La escala ya está validada: 2, 2.0 y 2.00 son el mismo carrito.
    [Fact]
    public void TrailingZerosDoNotChangeTheFingerprint()
    {
        var plain = Command([new(ProductA, 2m, 10m, 11_900m, 19)]);
        var padded = Command([new(ProductA, 2.00m, 10.0m, 11_900.00m, 19)]);

        Assert.Equal(PosSaleFingerprint.Compute(plain), PosSaleFingerprint.Compute(padded));
    }

    [Fact]
    public void AnyChangeInTheCartOrThePaymentsChangesTheFingerprint()
    {
        string[] fingerprints =
        [
            PosSaleFingerprint.Compute(Command()),
            PosSaleFingerprint.Compute(Command([new(ProductA, 3m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
            PosSaleFingerprint.Compute(Command([new(ProductB, 2m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
            PosSaleFingerprint.Compute(Command([new(ProductB, 1.5m, 0m, 5_000m, 0), new(ProductA, 2m, 10m, 11_900m, 19)])),
            PosSaleFingerprint.Compute(Command(payments: [new("Card", 20_000m, null, "1234"), new("Cash", null, 50_000m, null)])),
            PosSaleFingerprint.Compute(Command(payments: [new("Transfer", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)])),
            PosSaleFingerprint.Compute(Command([new(ProductA, 2m, 10m, 11_900m, 5), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ANullReferenceAndAnEmptyOneAreDifferent()
    {
        var withNull = Command(payments: [new("Card", 37_890m, null, null)]);
        var withEmpty = Command(payments: [new("Card", 37_890m, null, "")]);

        Assert.NotEqual(PosSaleFingerprint.Compute(withNull), PosSaleFingerprint.Compute(withEmpty));
    }

    // JSON canónico con cadenas escapadas: una referencia con separadores no imita otro reparto de
    // campos (spec, decisión 43).
    [Fact]
    public void AReferenceWithSeparatorsCannotImitateAnotherSplitOfFields()
    {
        var tricky = Command(payments: [new("Card", 1m, null, "1\",\"Cash"), new("Cash", null, 40_000m, null)]);
        var split = Command(payments: [new("Card", 1m, null, "1"), new("Cash", null, 40_000m, null)]);
        var piped = Command(payments: [new("Card", 1m, null, "1|null|Cash"), new("Cash", null, 40_000m, null)]);

        string[] fingerprints = [PosSaleFingerprint.Compute(tricky), PosSaleFingerprint.Compute(split), PosSaleFingerprint.Compute(piped)];
        Assert.Equal(3, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    // Spec 2026-10-20 §5: la línea es por variante. Otra talla del mismo producto, con la misma
    // cantidad y el mismo precio, es otro carrito: si la huella siguiera el producto, un reintento
    // con la talla cambiada se tomaría por repetición y respondería la venta vieja.
    [Fact]
    public void FingerprintChangesWhenVariantChanges()
    {
        var sizeM = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
        var sizeL = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

        var withM = PosSaleFingerprint.Compute(Command([new(sizeM, 1m, 0m, 30_000m, 19)]));
        var withL = PosSaleFingerprint.Compute(Command([new(sizeL, 1m, 0m, 30_000m, 19)]));

        Assert.NotEqual(withM, withL);
        Assert.Equal(withL, PosSaleFingerprint.Compute(Command([new(sizeL, 1m, 0m, 30_000m, 19)])));
    }

    // Huella fija de referencia: el JSON canónico se arma a mano con la forma de antes de las
    // variantes, [cashSessionId, [[id, quantity, discount, price, tax]], [[method, amount, tendered,
    // reference]]], y se hashea aparte. Sólo cambió qué id va en la primera posición de la línea, así
    // que una venta migrada (variant_id = product_id, AddVariantToSaleLines) recalcula la misma
    // huella que guardó con productId y su reintento se sigue reconociendo.
    [Fact]
    public void TheCanonicalFormIsTheOneBeforeVariantsWithTheVariantInTheLine()
    {
        var migrated = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
        const string canonical =
            """["01920000-0000-7000-8000-0000000000aa",[["01900000-0000-7000-8000-0000000000b1","1","0","30000","19"]],[["Card","30000",null,"1234"]]]""";
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));

        var fingerprint = PosSaleFingerprint.Compute(Command(
            [new(migrated, 1m, 0m, 30_000m, 19)], [new("Card", 30_000m, null, "1234")]));

        Assert.Equal(expected, fingerprint);
    }

    // El id no entra en la huella: la huella dice "qué carrito", el id dice "qué intento".
    [Fact]
    public void TheSaleIdIsNotPartOfTheFingerprint()
    {
        Assert.Equal(
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())),
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())));
    }

    // Valor calculado fuera de línea con el JSON canónico del Command() por defecto, antes del cambio: un reintento en
    // vuelo durante el despliegue manda el cuerpo viejo (sin customerId) y su huella debe ser idéntica byte a byte, o
    // falla con pos.sale.id_conflict (spec decisión 6).
    [Fact]
    public void WithoutACustomerTheFingerprintIsTheLiteralFromBeforeTheChange()
    {
        Assert.Equal(
            "91c8970aedd0a55f6f42f3bda80eff7dce36cc5e6a940932b72b858e8fa5b23e",
            PosSaleFingerprint.Compute(Command()));
    }

    [Fact]
    public void ACustomerChangesTheFingerprintAndTwoCustomersDiffer()
    {
        var customerA = Guid.Parse("01930000-0000-7000-8000-0000000000c1");
        var customerB = Guid.Parse("01930000-0000-7000-8000-0000000000c2");

        string[] fingerprints =
        [
            PosSaleFingerprint.Compute(Command()),
            PosSaleFingerprint.Compute(Command(customerId: customerA)),
            PosSaleFingerprint.Compute(Command(customerId: customerB)),
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSameCustomerGivesTheSameFingerprint()
    {
        var customer = Guid.Parse("01930000-0000-7000-8000-0000000000c1");

        Assert.Equal(
            PosSaleFingerprint.Compute(Command(customerId: customer)),
            PosSaleFingerprint.Compute(Command(customerId: customer)));
    }

    // El literal fija la forma canónica [..., customerId] (decisión 6): sólo cambia con una migración
    // deliberada de los reintentos en vuelo, que si no fallarían con `id_conflict`.
    [Fact]
    public void WithACustomerTheFingerprintIsAPinnedLiteral()
    {
        var customer = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.Equal(
            "4066c2d639157c127616de079e89ce17e576dcd83c5399899ecfa1d770567b59",
            PosSaleFingerprint.Compute(Command(customerId: customer)));
    }
}

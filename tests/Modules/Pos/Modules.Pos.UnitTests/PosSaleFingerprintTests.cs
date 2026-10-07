using System.Text.RegularExpressions;
using Modules.Pos.Application;

namespace Modules.Pos.UnitTests;

public sealed class PosSaleFingerprintTests
{
    private static readonly Guid Session = Guid.Parse("01920000-0000-7000-8000-0000000000aa");
    private static readonly Guid ProductA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ProductB = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static CreatePosSaleCommand Command(
        PosSaleLineRequest[]? lines = null, PosPaymentRequest[]? payments = null, Guid? id = null) =>
        new(
            PosFixtures.TenantId,
            id ?? Guid.Parse("6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11"),
            Session,
            lines ?? [new(ProductA, 2m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)],
            payments ?? [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)]);

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

    // El id no entra en la huella: la huella dice "qué carrito", el id dice "qué intento".
    [Fact]
    public void TheSaleIdIsNotPartOfTheFingerprint()
    {
        Assert.Equal(
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())),
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())));
    }
}

using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.4: la <c>Idempotency-Key</c> sale del id de la fila, en base32 minúsculas sin relleno. Siigo la acepta
/// alfanumérica, sin espacios ni especiales, de 30 caracteres como máximo.</summary>
public sealed class SiigoIdempotencyKeyTests
{
    [Fact]
    public void TheKeyIsAlphanumericAndAtMostThirtyChars()
    {
        for (var index = 0; index < 200; index++)
        {
            var key = SiigoIdempotencyKey.For(Guid.CreateVersion7());

            Assert.InRange(key.Length, 1, 30);
            Assert.All(key, character => Assert.True(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character), key));
        }
    }

    [Fact]
    public void TheKeyHasTwentySixCharactersAndNoPadding() =>
        Assert.Equal(26, SiigoIdempotencyKey.For(Guid.CreateVersion7()).Length);

    [Fact]
    public void TheSameRowAlwaysGivesTheSameKey()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(SiigoIdempotencyKey.For(id), SiigoIdempotencyKey.For(id));
    }

    [Fact]
    public void TwoRowsGiveTwoKeys() =>
        Assert.NotEqual(SiigoIdempotencyKey.For(Guid.CreateVersion7()), SiigoIdempotencyKey.For(Guid.CreateVersion7()));

    [Fact]
    public void TheKeyIsTheRfc4648Base32OfTheSixteenBytes()
    {
        // 0x00 * 16 → 26 veces «a» (el alfabeto de base32 empieza en A); 0xFF * 16 → «7» al final, sin relleno.
        Assert.Equal(new string('a', 26), SiigoIdempotencyKey.For(Guid.Empty));
        Assert.Equal("77777777777777777777777774", SiigoIdempotencyKey.For(new Guid(Enumerable.Repeat((byte)0xFF, 16).ToArray())));
    }
}

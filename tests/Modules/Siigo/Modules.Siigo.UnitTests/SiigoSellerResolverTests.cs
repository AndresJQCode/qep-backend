using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-20 §4.4: el vendedor de la factura es el usuario de Siigo con el correo del asesor del pedido.</summary>
public sealed class SiigoSellerResolverTests
{
    private static readonly SiigoUser[] Users =
    [
        new("744", "ANDREA RITUAL", "asesora1@ritualbotanico.com"),
        new("800", "VANESA HERNANDEZ", "asesora10@ritualbotanico.com"),
        new("67", "RAICES ORGANICAS S.A.S.", null),
    ];

    [Fact]
    public void MatchesByEmailIgnoringCaseAndSpaces() =>
        Assert.Equal("744", SiigoSellerResolver.Resolve(" Asesora1@RitualBotanico.com ", Users));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NullOrBlankEmailFindsNobody(string? email) =>
        Assert.Null(SiigoSellerResolver.Resolve(email, Users));

    [Fact]
    public void NoMatchFindsNobody() =>
        Assert.Null(SiigoSellerResolver.Resolve("nadie@ritualbotanico.com", Users));

    [Fact]
    public void UsersWithoutEmailAreSkipped() =>
        Assert.Null(SiigoSellerResolver.Resolve("RAICES ORGANICAS S.A.S.", Users));

    [Fact]
    public void ADuplicatedEmailTakesTheLowestNumericId() =>
        Assert.Equal(
            "67",
            SiigoSellerResolver.Resolve(
                "compartido@ritualbotanico.com",
                [new SiigoUser("800", "Uno", "compartido@ritualbotanico.com"), new SiigoUser("67", "Dos", "COMPARTIDO@ritualbotanico.com")]));
}

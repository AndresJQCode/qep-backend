using Bootstrapper.Seeding;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>Seed:OperatorOwnerEmail</c> es opcional: sin ella la semilla no crea el tenant operador y
/// sólo lo advierte en el log. Lo que no puede pasar es que venga con un valor que no es un email,
/// porque eso se descubriría recién en el medio de la siembra.
/// </summary>
public sealed class SeedOptionsValidatorTests
{
    private const string OwnerEmail = "semilla@qcode.co";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SeedEnabledWithoutTheOperatorOwnerEmailIsValid(string? operatorOwnerEmail)
    {
        var result = new SeedOptionsValidator().Validate(null, Enabled(operatorOwnerEmail));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void SeedEnabledWithAValidOperatorOwnerEmailIsValid()
    {
        var result = new SeedOptionsValidator().Validate(null, Enabled("Operador@QCode.CO"));

        Assert.True(result.Succeeded);
    }

    // Mismo criterio que Seed:OwnerEmail: el mensaje nombra la clave, que es lo que lee quien
    // encuentra el pod caído.
    [Fact]
    public void SeedEnabledWithAnInvalidOperatorOwnerEmailFailsNamingTheKey()
    {
        var result = new SeedOptionsValidator().Validate(null, Enabled("no-es-un-email"));

        Assert.True(result.Failed);
        Assert.Contains("Seed:OperatorOwnerEmail", result.FailureMessage, StringComparison.Ordinal);
    }

    // Con la semilla apagada la clave no la lee nadie, igual que Seed:OwnerEmail.
    [Fact]
    public void SeedDisabledIgnoresTheOperatorOwnerEmail()
    {
        var options = Enabled("no-es-un-email");
        options.Enabled = false;

        var result = new SeedOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    private static SeedOptions Enabled(string? operatorOwnerEmail) => new()
    {
        Enabled = true,
        OwnerEmail = OwnerEmail,
        OperatorOwnerEmail = operatorOwnerEmail,
    };
}

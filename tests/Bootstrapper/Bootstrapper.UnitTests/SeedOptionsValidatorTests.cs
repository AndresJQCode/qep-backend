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

    // El ConfigMap de producción trae #{SEED_OPERATOR_OWNER_EMAIL}#, que Azure DevOps reemplaza. Hasta
    // que exista la variable del pipeline el token llega literal, y eso vale lo mismo que ausente:
    // tumbar el arranque por una variable que todavía no se creó sería peor que no sembrar QCode.
    [Theory]
    [InlineData("#{SEED_OPERATOR_OWNER_EMAIL}#")]
    [InlineData("  #{SEED_OPERATOR_OWNER_EMAIL}#  ")]
    public void SeedEnabledWithAnUnreplacedPipelineTokenIsValid(string operatorOwnerEmail)
    {
        var result = new SeedOptionsValidator().Validate(null, Enabled(operatorOwnerEmail));

        Assert.True(result.Succeeded);
    }

    // Sólo un token completo cuenta como ausente: algo que se le parece a medias es un error de
    // configuración y se rechaza como cualquier email inválido.
    [Theory]
    [InlineData("#{seed_operator_owner_email}#")]
    [InlineData("#{SEED_OPERATOR_OWNER_EMAIL}")]
    [InlineData("x#{SEED_OPERATOR_OWNER_EMAIL}#")]
    public void SeedEnabledWithSomethingThatOnlyLooksLikeATokenFails(string operatorOwnerEmail)
    {
        var result = new SeedOptionsValidator().Validate(null, Enabled(operatorOwnerEmail));

        Assert.True(result.Failed);
        Assert.Contains("Seed:OperatorOwnerEmail", result.FailureMessage, StringComparison.Ordinal);
    }

    // Seed:OwnerEmail no cambia: un token sin reemplazar ahí sigue tumbando el arranque, porque
    // Origen botánico sí lo necesita para existir.
    [Fact]
    public void AnUnreplacedPipelineTokenInTheOwnerEmailStillFails()
    {
        var options = Enabled(operatorOwnerEmail: null);
        options.OwnerEmail = "#{SEED_OWNER_EMAIL}#";

        var result = new SeedOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Seed:OwnerEmail", result.FailureMessage, StringComparison.Ordinal);
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

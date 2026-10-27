using Modules.Identity.Application;

namespace Modules.Identity.UnitTests;

public sealed class PasswordRulesTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(129)]
    public void APasswordOutsideTenTo128CharactersFailsOnThePasswordField(int length)
    {
        var result = new NewPasswordValidator().Validate(new NewPasswordInput(new string('a', length)));

        Assert.Equal("Password", Assert.Single(result.Errors).PropertyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void EveryPasswordFailureIsASingleErrorCarryingTheInvalidCode(string password)
    {
        var error = Assert.Single(new NewPasswordValidator().Validate(new NewPasswordInput(password)).Errors);

        Assert.Equal("identity.password.invalid", error.ErrorCode);
    }

    // Ruling (NIST, sin reglas de composición): diez espacios cumplen el largo y se aceptan.
    [Fact]
    public void AWhitespaceOnlyPasswordOfTenCharactersIsAccepted()
        => Assert.True(new NewPasswordValidator().Validate(new NewPasswordInput(new string(' ', 10))).IsValid);

    [Theory]
    [InlineData(10)]
    [InlineData(128)]
    public void APasswordOfTenOr128CharactersWithoutCompositionPasses(int length)
        => Assert.True(new NewPasswordValidator().Validate(new NewPasswordInput(new string('a', length))).IsValid);
}

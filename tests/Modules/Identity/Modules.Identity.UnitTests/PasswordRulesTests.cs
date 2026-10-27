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
    [InlineData(10)]
    [InlineData(128)]
    public void APasswordOfTenOr128CharactersWithoutCompositionPasses(int length)
        => Assert.True(new NewPasswordValidator().Validate(new NewPasswordInput(new string('a', length))).IsValid);
}

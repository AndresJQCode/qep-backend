using FluentValidation;
using Modules.Buyers.Application;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerPasswordRulesTests
{
    private sealed record Input(string Password);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator() => RuleFor(i => i.Password).BuyerPassword();
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void TheLengthBoundsAreInclusive(int length, bool valid) =>
        Assert.Equal(valid, new InputValidator().Validate(new Input(new string('a', length))).IsValid);

    [Fact]
    public void TenSpacesAreValidBecauseThereAreNoCompositionRules() =>
        Assert.True(new InputValidator().Validate(new Input("          ")).IsValid);

    [Fact]
    public void AShortPasswordFailsWithTheBuyerCodeAndMessage()
    {
        var error = Assert.Single(new InputValidator().Validate(new Input("short")).Errors);

        Assert.Equal("store.buyer.password_invalid", error.ErrorCode);
        Assert.Equal("La contraseña debe tener entre 10 y 128 caracteres.", error.ErrorMessage);
    }
}

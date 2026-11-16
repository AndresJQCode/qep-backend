using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>El «siguiente paso» de la respuesta formal del tenant (fase 4b, H6): hasta 2000 caracteres medidos recortados, como los
/// guarda el dominio, y sin caracteres de control salvo el salto de línea y la tabulación. El error va en <c>FormalNextStep</c>.</summary>
public sealed class UpdateSupportSettingsValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static IEnumerable<string> ErrorsOf(string? nextStep) =>
        new UpdateSupportSettingsValidator()
            .Validate(UpdateSupportSettingsCommand.From(Tenant, new UpdateSupportSettingsRequest(3, nextStep), 1))
            .Errors
            .Select(error => error.PropertyName);

    [Fact]
    public void TheNextStepIsAtMost2000WithoutControlCharacters()
    {
        Assert.Empty(ErrorsOf(null));
        Assert.Empty(ErrorsOf(""));
        Assert.Empty(ErrorsOf(new string('a', 2000)));
        Assert.Empty(ErrorsOf($"  {new string('a', 2000)}  "));
        Assert.Empty(ErrorsOf("Primera línea\r\nSegunda\tcon tabulación"));

        Assert.Equal(["FormalNextStep"], ErrorsOf(new string('a', 2001)));
        Assert.Equal(["FormalNextStep"], ErrorsOf("Con un \u0007 de control"));
        Assert.Equal(["FormalNextStep"], ErrorsOf("Con un \0 nulo"));
    }

    [Fact]
    public void TheRequestCarriesTheNextStepToTheCommand() =>
        Assert.Equal(
            "Paso",
            UpdateSupportSettingsCommand.From(Tenant, new UpdateSupportSettingsRequest(3, "Paso"), 1).FormalNextStep);
}

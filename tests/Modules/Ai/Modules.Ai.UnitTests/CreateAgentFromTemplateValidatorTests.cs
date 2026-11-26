using Modules.Ai.Application;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-23 §4: un cuerpo sin <c>templateId</c> o con el nombre en blanco es un 422 con el campo, nunca un 500.</summary>
public sealed class CreateAgentFromTemplateValidatorTests
{
    private readonly CreateAgentFromTemplateValidator _validator = new();

    [Fact]
    public void AnEmptyNameOrTemplateIdIsRejected()
    {
        var result = _validator.Validate(new CreateAgentFromTemplateCommand(Guid.NewGuid(), string.Empty, " "));

        Assert.Contains(result.Errors, error => error.PropertyName == "TemplateId");
        Assert.Contains(result.Errors, error => error.PropertyName == "Name");
    }

    [Fact]
    public void ATemplateIdOverTheLimitIsRejected()
    {
        var result = _validator.Validate(new CreateAgentFromTemplateCommand(Guid.NewGuid(), new string('a', 65), "Citas"));

        Assert.Contains(result.Errors, error => error.PropertyName == "TemplateId");
    }

    [Fact]
    public void AKnownTemplateAndANameAreValid() =>
        Assert.True(_validator.Validate(new CreateAgentFromTemplateCommand(Guid.NewGuid(), "asistente-de-citas", "Citas")).IsValid);
}

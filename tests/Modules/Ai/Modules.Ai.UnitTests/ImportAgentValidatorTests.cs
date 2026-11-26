using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 importar/exportar §4: el archivo es de agente (<c>kind</c>) y de una versión conocida (<c>schema</c>), el nombre
/// sigue la regla de todo agente y el <c>config</c> los topes de guardar el borrador.</summary>
public sealed class ImportAgentValidatorTests
{
    private static AgentExportDto File() =>
        new(1, "aiAgent", "Ventas", AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")), DateTimeOffset.UnixEpoch);

    private static ImportAgentCommand Command(AgentExportDto? export = null, string name = "Ventas") =>
        new(Guid.NewGuid(), name, export ?? File());

    private static IEnumerable<string> Errors(ImportAgentCommand command) =>
        new ImportAgentValidator().Validate(command).Errors.Select(error => error.PropertyName);

    [Fact]
    public void AValidFileIsAccepted() => Assert.Empty(Errors(Command()));

    [Fact]
    public void AChatbotFileIsRejected() => Assert.Equal(["Export.Kind"], Errors(Command(File() with { Kind = "" })));

    [Fact]
    public void ANewerSchemaIsRejected() => Assert.Equal(["Export.Schema"], Errors(Command(File() with { Schema = 2 })));

    [Fact]
    public void AMissingConfigIsRejected() => Assert.Equal(["Export.Config"], Errors(Command(File() with { Config = null! })));

    [Fact]
    public void AnEmptyNameIsRejected() => Assert.Equal(["Name"], Errors(Command(name: "")));

    [Fact]
    public void AConfigFieldOutOfRangeIsRejectedWithItsPath() =>
        Assert.Equal(["Export.Config.Tone"], Errors(Command(File() with { Config = File().Config with { Tone = "loud" } })));
}

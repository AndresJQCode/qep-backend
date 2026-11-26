using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 (copiloto) §5.1 y D-C1: la configuración del copiloto por tenant. Sin fila vale la de fábrica (apagado,
/// sin proveedor, 90 días, versión 0); prendido exige proveedor y modelo, como el <c>CHECK</c>.</summary>
public sealed class CopilotSettingsTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid By = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFactorySettingsAreOffWithNinetyDays()
    {
        var settings = CopilotSettings.Unconfigured(Tenant);

        Assert.Equal(
            (false, (string?)null, (string?)null, string.Empty, (long?)null, 90, 0L),
            (settings.Enabled, settings.Provider, settings.Model, settings.Instructions, settings.MonthlyTokenBudget, settings.RetentionDays, settings.Version));
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void EnablingRequiresProviderAndModel()
    {
        var settings = CopilotSettings.Unconfigured(Tenant);

        Assert.Throws<AiDomainException>(() => settings.Update(true, null, null, string.Empty, null, 90, By, Now));
        Assert.Throws<AiDomainException>(() => settings.Update(true, "anthropic", null, string.Empty, null, 90, By, Now));
        Assert.Equal(0L, settings.Version);

        settings.Update(true, "anthropic", "claude-opus-5-5", "  Somos una distribuidora.  ", 500_000, 30, By, Now);

        Assert.Equal(
            (true, "anthropic", "claude-opus-5-5", "Somos una distribuidora.", (long?)500_000, 30, 1L, By, Now),
            (settings.Enabled, settings.Provider, settings.Model, settings.Instructions, settings.MonthlyTokenBudget, settings.RetentionDays,
                settings.Version, settings.UpdatedBy, settings.UpdatedAt));
        Assert.True(settings.IsConfigured);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(366)]
    public void TheRetentionIsSevenTo365Days(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CopilotSettings.Unconfigured(Tenant).Update(false, null, null, string.Empty, null, days, By, Now));

    [Fact]
    public void TheBudgetIsPositiveOrNone() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CopilotSettings.Unconfigured(Tenant).Update(false, null, null, string.Empty, 0, 90, By, Now));

    [Fact]
    public void TheInstructionsAreAtMost4000() =>
        Assert.Throws<ArgumentException>(() =>
            CopilotSettings.Unconfigured(Tenant).Update(
                false, null, null, new string('i', CopilotSettings.InstructionsMaxLength + 1), null, 90, By, Now));
}

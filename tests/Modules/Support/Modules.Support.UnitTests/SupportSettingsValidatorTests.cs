using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>El umbral de «por vencer» va de 0 a 30 días hábiles [owner 2026-11-14]; 0 lo apaga. Sin valor, el comando lleva −1 y el
/// validador lo rechaza en el campo, en vez de que el dominio lance un <c>ArgumentOutOfRangeException</c>.</summary>
public sealed class SupportSettingsValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void AValueOutside0To30Fails(int value)
    {
        var result = new UpdateSupportSettingsValidator().Validate(
            UpdateSupportSettingsCommand.From(Tenant, new UpdateSupportSettingsRequest(value, null, false, 5), 1));

        Assert.Equal(["DueSoonBusinessDays"], result.Errors.Select(error => error.PropertyName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void ZeroAnd30Pass(int value)
    {
        var result = new UpdateSupportSettingsValidator().Validate(
            UpdateSupportSettingsCommand.From(Tenant, new UpdateSupportSettingsRequest(value, null, false, 5), 1));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AMissingValueFailsOnTheField()
    {
        var command = UpdateSupportSettingsCommand.From(Tenant, new UpdateSupportSettingsRequest(null, null, false, 5), 1);

        Assert.Equal(-1, command.DueSoonBusinessDays);
        Assert.Equal(
            ["DueSoonBusinessDays"],
            new UpdateSupportSettingsValidator().Validate(command).Errors.Select(error => error.PropertyName));
    }
}

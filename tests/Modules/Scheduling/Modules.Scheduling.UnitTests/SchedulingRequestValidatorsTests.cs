using FluentValidation;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 7: el validador marca el campo (camelCase) antes de que el dominio, que lanza por un valor imposible, se entere.
public sealed class SchedulingRequestValidatorsTests
{
    private static string[] FailedFields<T>(IValidator<T> validator, T request)
    {
        var error = Assert.Throws<ValidationException>(() => validator.ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ALocationNeedsAName(string? name) =>
        Assert.Equal(["name"], FailedFields(new LocationRequestValidator(), new LocationRequest(name)));

    [Fact]
    public void ALocationNameCannotPass120Characters() =>
        Assert.Equal(["name"], FailedFields(new LocationRequestValidator(), new LocationRequest(new string('a', 121))));

    [Fact]
    public void AResourceNeedsADisplayNameAndAMemberThatIsNotEmpty() =>
        Assert.Equal(
            ["displayName", "memberId"],
            FailedFields(new ResourceRequestValidator(), new ResourceRequest(null, " ", Guid.Empty)));

    [Fact]
    public void AResourceWithoutALocationOrMemberIsValid() =>
        new ResourceRequestValidator().ValidateAndThrow(new ResourceRequest(null, "Dra. Torres", null));

    [Theory]
    [InlineData(32, 0, "durationMinutes")]
    [InlineData(0, 0, "durationMinutes")]
    [InlineData(485, 0, "durationMinutes")]
    [InlineData(30, 7, "bufferMinutes")]
    [InlineData(30, -5, "bufferMinutes")]
    [InlineData(30, 245, "bufferMinutes")]
    public void AServiceDurationAndBufferAreMultiplesOfFiveWithinTheirLimits(int duration, int buffer, string field) =>
        Assert.Equal([field], FailedFields(new ServiceRequestValidator(), new ServiceRequest("Limpieza", duration, buffer, true)));

    [Fact]
    public void AServiceWithoutADurationIsRejected() =>
        Assert.Equal(["durationMinutes"], FailedFields(new ServiceRequestValidator(), new ServiceRequest("Limpieza", null, 0, true)));

    [Fact]
    public void AServiceWithoutABufferHasNone() =>
        new ServiceRequestValidator().ValidateAndThrow(new ServiceRequest("Limpieza", 30, null, null));

    [Fact]
    public void TheRulesRejectEveryValueOutOfItsRange() =>
        Assert.Equal(
            ["confirmationMode", "minNoticeMinutes", "horizonDays", "clientCancelCutoffHours", "reminderOffsetHours"],
            FailedFields(
                new SettingsRequestValidator(),
                new SettingsRequest("Sometimes", 10_081, 366, 721, false, 0, null, null)));

    [Fact]
    public void TheRulesAcceptTheDefaults() =>
        new SettingsRequestValidator().ValidateAndThrow(new SettingsRequest("Automatic", 120, 30, 24, false, 24, null, null));

    // D-18: el remitente es un id de membresía; que sea activo lo decide el caso de uso con el directorio, no la forma del request.
    [Fact]
    public void TheRulesAcceptADefaultReminderSender() =>
        new SettingsRequestValidator().ValidateAndThrow(new SettingsRequest("Automatic", 120, 30, 24, false, 24, null, Guid.CreateVersion7()));
}

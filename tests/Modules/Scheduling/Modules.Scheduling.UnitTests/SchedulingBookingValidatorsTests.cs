using FluentValidation;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 9 (booking and transitions): requests of the appointment routes, validated before the handler loads anything.
public sealed class SchedulingBookingValidatorsTests
{
    private static string[] FailedFields<T>(IValidator<T> validator, T request)
    {
        var error = Assert.Throws<ValidationException>(() => validator.ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    [Fact]
    public void ABookingNeedsServiceCustomerAndStart() =>
        Assert.Equal(
            ["serviceId", "customerId", "startsAt"],
            FailedFields(new BookAppointmentRequestValidator(), new BookAppointmentRequest(null, null, null, null, null, null, null, null)));

    [Fact]
    public void ABookingRejectsAnUnknownSourceAnEmptyIdAndTextThatIsTooLong() =>
        Assert.Equal(
            ["resourceId", "source", "note", "clientRequestId", "conversationId"],
            FailedFields(
                new BookAppointmentRequestValidator(),
                new BookAppointmentRequest(
                    Guid.CreateVersion7(),
                    Guid.Empty,
                    DateTimeOffset.UtcNow,
                    Guid.CreateVersion7(),
                    "Phone",
                    new string('n', BookAppointmentRequestValidator.NoteMaxLength + 1),
                    new string('c', BookAppointmentRequestValidator.ClientRequestIdMaxLength + 1),
                    Guid.Empty)));

    [Fact]
    public void ABackOfficeBookingWithoutOptionalFieldsIsValid() =>
        new BookAppointmentRequestValidator().ValidateAndThrow(
            new BookAppointmentRequest(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, null, null, null));

    [Fact]
    public void ACancellationRejectsALongReasonAndAnUnknownSource() =>
        Assert.Equal(
            ["reason", "source"],
            FailedFields(
                new CancelAppointmentRequestValidator(),
                new CancelAppointmentRequest(new string('r', CancelAppointmentRequestValidator.ReasonMaxLength + 1), "Phone")));

    [Fact]
    public void ARescheduleNeedsTheNewStart() =>
        Assert.Equal(["startsAt"], FailedFields(new RescheduleAppointmentRequestValidator(), new RescheduleAppointmentRequest(null, null)));

    [Fact]
    public void ARescheduleRejectsAnEmptyResource() =>
        Assert.Equal(
            ["resourceId"],
            FailedFields(new RescheduleAppointmentRequestValidator(), new RescheduleAppointmentRequest(Guid.Empty, DateTimeOffset.UtcNow)));
}

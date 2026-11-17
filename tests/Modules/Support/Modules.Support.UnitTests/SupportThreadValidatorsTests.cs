using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Soporte fase 2 (Tarea 9): los validadores de «Seguir el hilo», asignar un entrante sin clasificar y la página del
/// expediente. Un cuerpo que los pasa nunca llega al dominio a lanzar.</summary>
public sealed class SupportThreadValidatorsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static readonly Guid Ticket = Guid.CreateVersion7();

    [Fact]
    public void FollowNeedsAValueAndAVersion()
    {
        var validator = new SetTicketFollowThreadValidator();

        Assert.True(validator.Validate(SetTicketFollowThreadCommand.From(Tenant, Ticket, new(false), 3)).IsValid);
        Assert.Equal(
            ["Follow"],
            validator.Validate(SetTicketFollowThreadCommand.From(Tenant, Ticket, new(null), 3)).Errors.Select(error => error.PropertyName));
        Assert.Equal(
            ["ExpectedVersion"],
            validator.Validate(SetTicketFollowThreadCommand.From(Tenant, Ticket, new(true), 0)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void AssigningNeedsTheTicketAndItsVersion()
    {
        var validator = new AssignUnclassifiedValidator();
        var conversation = Guid.CreateVersion7();
        var message = Guid.CreateVersion7();

        Assert.True(validator.Validate(AssignUnclassifiedCommand.From(Tenant, conversation, message, new(Ticket, 2))).IsValid);
        Assert.Equal(
            ["TicketId", "ExpectedVersion"],
            validator.Validate(AssignUnclassifiedCommand.From(Tenant, conversation, message, new(null, null)))
                .Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void TheMessagesPageTakesBetweenOneAndAHundredAndAReadableCursor()
    {
        var validator = new ListTicketMessagesValidator();
        var cursor = SupportMessageCursor.Encode(DateTimeOffset.UtcNow, Guid.CreateVersion7());

        Assert.True(validator.Validate(new ListTicketMessagesQuery(Tenant, Ticket, null, 50)).IsValid);
        Assert.True(validator.Validate(new ListTicketMessagesQuery(Tenant, Ticket, cursor, 100)).IsValid);
        Assert.Equal(["Take"], validator.Validate(new ListTicketMessagesQuery(Tenant, Ticket, null, 0)).Errors.Select(error => error.PropertyName));
        Assert.Equal(["Take"], validator.Validate(new ListTicketMessagesQuery(Tenant, Ticket, null, 101)).Errors.Select(error => error.PropertyName));
        Assert.Equal(
            ["Before"], validator.Validate(new ListTicketMessagesQuery(Tenant, Ticket, "no-es-un-cursor", 50)).Errors.Select(error => error.PropertyName));
        Assert.True(SupportMessageCursor.TryDecode(cursor, out _));
    }
}

using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Plan L28 and final review S3 (C-2): when an idempotent retry issues a new PIN. An opening email still unrecorded is in flight only
// within FilePublicTicketHandler.OpeningDeliveryGrace of the PIN; past it, it is a failed delivery and the PIN rotates.
public sealed class FilePublicTicketRetryPinTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid FormVersion = Guid.Parse("0193a000-0000-7000-8000-0000000000f1");
    private static readonly Guid City = Guid.Parse("0193a000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Issued = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null, City, null);

    private static SupportTicket FromForm(TicketRequester? requester) =>
        SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 3, SupportTicketTypeDefaults.For(TenantId, Issued)[0], "Asunto", "Descripción.", requester,
            null, FormVersion, Issued, Clock);

    private static SupportTicketAccess Access(SupportTicket ticket) => SupportTicketAccess.Issue(ticket.Id, TenantId, "hash", Issued);

    [Fact]
    public void AnUnrecordedDeliveryWithinTheGraceIsInFlightAndKeepsThePin()
    {
        var ticket = FromForm(Person);

        Assert.False(FilePublicTicketHandler.MustRotate(ticket, Access(ticket), Issued.AddMinutes(1)));
        Assert.False(FilePublicTicketHandler.MustRotate(
            ticket, Access(ticket), Issued + FilePublicTicketHandler.OpeningDeliveryGrace));
    }

    [Fact]
    public void AnUnrecordedDeliveryPastTheGraceIsTakenAsFailedAndRotates()
    {
        var ticket = FromForm(Person);

        Assert.True(FilePublicTicketHandler.MustRotate(
            ticket, Access(ticket), Issued + FilePublicTicketHandler.OpeningDeliveryGrace + TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMinutes(10), FilePublicTicketHandler.OpeningDeliveryGrace);
    }

    [Fact]
    public void AnEmailThatWentOutKeepsThePinWhateverTheTime()
    {
        var ticket = FromForm(Person);
        var access = Access(ticket);
        access.RecordDelivery(SupportOpeningSender.EmailChannel, null, Issued.AddSeconds(5));

        Assert.False(FilePublicTicketHandler.MustRotate(ticket, access, Issued.AddDays(3)));
    }

    [Fact]
    public void AFailedEmailRotates()
    {
        var ticket = FromForm(Person);
        var access = Access(ticket);
        access.RecordDelivery(SupportOpeningSender.EmailChannel, "send_failed", Issued.AddSeconds(5));

        Assert.True(FilePublicTicketHandler.MustRotate(ticket, access, Issued.AddMinutes(1)));
    }

    [Fact]
    public void AnAnonymousTicketAlwaysRotates()
    {
        var ticket = FromForm(requester: null);

        Assert.True(FilePublicTicketHandler.MustRotate(ticket, Access(ticket), Issued.AddSeconds(1)));
    }
}

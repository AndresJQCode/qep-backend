using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// El ticket del formulario público (plan fase 3, F22): sin actor ni responsable, Email si tiene solicitante y Portal si es anónimo.
public sealed class SupportTicketPublicFormTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid FormVersion = Guid.Parse("0193a000-0000-7000-8000-0000000000f1");
    private static readonly Guid City = Guid.Parse("0193a000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null, City, null);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];

    private static SupportTicket FromForm(TicketRequester? requester, Guid? customerId = null) =>
        SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 3, Petition, "No me llegó la factura", "Pedí la factura del pedido 45.", requester,
            customerId, FormVersion, Received, Clock);

    [Fact]
    public void AnAnonymousTicketHasNoRequesterNoCustomerAndRepliesByPortal()
    {
        var ticket = FromForm(requester: null);

        Assert.True(ticket.IsAnonymous);
        Assert.Null(ticket.Requester);
        Assert.Null(ticket.CustomerId);
        Assert.Equal(SupportReplyChannel.Portal, ticket.ReplyChannel);
        Assert.Equal(SupportTicketIntake.PublicForm, ticket.Intake);
        Assert.Null(ticket.ManualMedium);
        Assert.Null(ticket.ConversationId);
        Assert.False(ticket.FollowThread);
        Assert.Equal(SupportTicketStatus.New, ticket.Status);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
    }

    [Fact]
    public void AnAnonymousTicketCannotCarryACustomer() =>
        Assert.Throws<ArgumentException>(() => FromForm(requester: null, customerId: Guid.CreateVersion7()));

    // Revisión final, I1: la promesa a quien radicó sin datos (spec §9.4) la cumple el agregado, no la pantalla. Ningún camino de la
    // consola le pone cliente a un anónimo.
    [Fact]
    public void AnAnonymousTicketRejectsACustomerLink()
    {
        var ticket = FromForm(requester: null);

        var exception = Assert.Throws<SupportDomainException>(
            () => ticket.LinkCustomer(Guid.CreateVersion7(), Guid.CreateVersion7(), Received.AddHours(1)));

        Assert.Equal("support.ticket.anonymous_has_no_customer", exception.Code);
        Assert.Equal(SupportErrorCodes.AnonymousHasNoCustomer, exception.Code);
        Assert.Null(ticket.CustomerId);
        Assert.Single(ticket.Events);
        Assert.Equal(1L, ticket.Version);
    }

    [Fact]
    public void ANamedTicketRepliesByEmail()
    {
        var customer = Guid.CreateVersion7();

        var ticket = FromForm(Person, customer);

        Assert.False(ticket.IsAnonymous);
        Assert.Equal(Person, ticket.Requester);
        Assert.Equal(City, ticket.Requester!.CityId);
        Assert.Equal(customer, ticket.CustomerId);
        Assert.Equal(SupportReplyChannel.Email, ticket.ReplyChannel);
    }

    [Fact]
    public void ItHasNoActorAndNoAssignee()
    {
        var ticket = FromForm(Person);

        Assert.Null(ticket.AssigneeMemberId);
        var created = Assert.Single(ticket.Events);
        Assert.Equal(SupportTicketEventKind.Created, created.Kind);
        Assert.Null(created.ActorMemberId);
        Assert.Equal(1L, ticket.Version);
    }

    [Fact]
    public void ItKeepsTheFormVersion()
    {
        Assert.Equal(FormVersion, FromForm(Person).FormVersionId);
        Assert.Throws<ArgumentException>(() => SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 3, Petition, "Asunto", "Descripción", Person, null, Guid.Empty, Received, Clock));
    }

    [Fact]
    public void ANitWithoutBusinessNameShowsAsNitAndNumber()
    {
        var company = new TicketRequester(
            null, null, null, "NIT", "900123456", "pagos@example.com", "+573001234567", null, "CO", null, City, null);

        var ticket = FromForm(company);

        Assert.Equal("NIT 900123456", ticket.Requester!.DisplayName());
    }

    [Fact]
    public void ANamedTicketNeedsAnEmailToReplyTo() =>
        Assert.Throws<ArgumentException>(() => FromForm(Person with { Email = " " }));

    [Fact]
    public void ManualAndConversationStillRejectAMissingRequester()
    {
        var manual = Assert.Throws<SupportDomainException>(() => SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, Petition, "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            customerId: null, requester: null, assigneeMemberId: null, Guid.CreateVersion7(), Received, Clock));
        var conversation = Assert.Throws<SupportDomainException>(() => SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 1, Petition, "Asunto", "Descripción",
            SupportTicketPriority.Medium,
            new SupportConversationRef(Guid.CreateVersion7(), "WhatsApp"), customerId: null, requester: null,
            assigneeMemberId: null, Guid.CreateVersion7(), Received, Clock));

        Assert.Equal(SupportErrorCodes.RequesterRequired, manual.Code);
        Assert.Equal(SupportErrorCodes.RequesterRequired, conversation.Code);
    }

    [Fact]
    public void ManualAndConversationTicketsAreNotAnonymous()
    {
        var manual = SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, Petition, "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            null, Person, null, Guid.CreateVersion7(), Received, Clock);

        Assert.False(manual.IsAnonymous);
        Assert.Null(manual.FormVersionId);
    }
}

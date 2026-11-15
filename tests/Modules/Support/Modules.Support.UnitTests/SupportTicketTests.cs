using System.Text.Json;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// FakeCalendar: lunes a viernes con festivos 2026-11-16, 2026-12-08 y 2026-12-25; zona America/Bogota (UTC-5). Tipo de 15 días
// salvo que se diga. 2026-11-13 es viernes.
public sealed class SupportTicketTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherMember = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly Guid Customer = Guid.Parse("0193a000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];      // 15 días, legal

    private static SupportTicketType Documents => SupportTicketTypeDefaults.For(TenantId, Received)[1];     // 10 días

    private static SupportTicketType TechSupport => SupportTicketTypeDefaults.For(TenantId, Received)[7];   // sin término

    private static SupportTicket NewTicket(
        SupportTicketType? type = null, DateTimeOffset? receivedAt = null, Guid? customerId = null, TicketRequester? requester = null) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, type ?? Petition, "No me llegó la factura", "Pedí la factura del pedido 45 y no llegó.",
            ManualIntakeMedium.Phone, SupportReplyChannel.Email, customerId, customerId is null ? requester ?? Person : requester,
            assigneeMemberId: null, Actor, receivedAt ?? Received, Clock);

    private static DateTimeOffset At(int month, int day, int hourUtc = 15) => new(2026, month, day, hourUtc, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewTicketIsNewWithDueDateAndACreatedEvent()
    {
        var ticket = NewTicket();

        Assert.Equal(SupportTicketStatus.New, ticket.Status);
        Assert.Equal(TenantId, ticket.TenantId);
        Assert.Equal(1L, ticket.Number);
        Assert.Equal("SOP-000001", ticket.DisplayNumber);
        Assert.Equal(Received, ticket.ReceivedAt);
        Assert.Equal(new DateOnly(2026, 11, 13), ticket.ReceivedOn);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
        Assert.Equal("Petición general", ticket.TypeName);
        Assert.Equal(15, ticket.TermBusinessDays);
        Assert.True(ticket.TermIsLegal);
        Assert.True(ticket.RequiresFormalResponse);
        Assert.Equal(SupportTicketIntake.Manual, ticket.Intake);
        Assert.Equal(ManualIntakeMedium.Phone, ticket.ManualMedium);
        Assert.Equal(SupportReplyChannel.Email, ticket.ReplyChannel);
        Assert.Equal(Person, ticket.Requester);
        Assert.Null(ticket.CustomerId);
        Assert.Null(ticket.AssigneeMemberId);
        Assert.Equal(0, ticket.PausedBusinessDays);
        Assert.Null(ticket.PausedSince);
        Assert.Equal(0, ticket.ResolutionCount);
        Assert.Equal(1L, ticket.Version);
        Assert.Equal(1, ticket.LastEventOrdinal);
        var created = Assert.Single(ticket.Events);
        Assert.Equal(1, created.Ordinal);
        Assert.Equal(SupportTicketEventKind.Created, created.Kind);
        Assert.Equal(Actor, created.ActorMemberId);
        Assert.Equal(Received, created.OccurredAt);
        Assert.Equal(ticket.Id, created.TicketId);
        Assert.Equal(TenantId, created.TenantId);
        Assert.Equal("""{"to":"New"}""", created.Details);
    }

    [Fact]
    public void TheSubjectAndDescriptionAreTrimmed()
    {
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 7, Petition, "  Asunto  ", "  Descripción  ", ManualIntakeMedium.Email,
            SupportReplyChannel.Email, null, Person, OtherMember, Actor, Received, Clock);

        Assert.Equal("Asunto", ticket.Subject);
        Assert.Equal("Descripción", ticket.Description);
        Assert.Equal(OtherMember, ticket.AssigneeMemberId);
        Assert.Equal("SOP-000007", ticket.DisplayNumber);
    }

    [Fact]
    public void ANightReceptionCountsFromTheLocalDate()
    {
        var ticket = NewTicket(receivedAt: new DateTimeOffset(2026, 11, 13, 3, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 12), ticket.ReceivedOn);
        Assert.Equal(new DateOnly(2026, 12, 4), ticket.DueOn);
    }

    [Fact]
    public void ATypeWithoutTermHasNoDueDate()
    {
        var ticket = NewTicket(TechSupport);

        Assert.Null(ticket.DueOn);
        Assert.Null(ticket.TermBusinessDays);
        Assert.False(ticket.TermIsLegal);
    }

    [Fact]
    public void ATicketNeedsACustomerOrARequesterName()
    {
        var withoutName = Person with { FirstNames = " ", LastNames = null };

        var missing = Assert.Throws<SupportDomainException>(() => SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, Petition, "Asunto", "Descripción", ManualIntakeMedium.Phone,
            SupportReplyChannel.Email, null, null, null, Actor, Received, Clock));
        var nameless = Assert.Throws<SupportDomainException>(() => NewTicket(requester: withoutName));

        Assert.Equal(SupportErrorCodes.RequesterRequired, missing.Code);
        Assert.Equal(SupportErrorCodes.RequesterRequired, nameless.Code);
    }

    [Fact]
    public void ACustomerAloneIsEnough()
    {
        var ticket = NewTicket(customerId: Customer);

        Assert.Equal(Customer, ticket.CustomerId);
        Assert.Null(ticket.Requester);
    }

    // Spec §9.3: con NIT la razón social reemplaza a los nombres (plan D6).
    [Fact]
    public void ABusinessNameWithNitIsARequesterName()
    {
        var company = new TicketRequester(null, null, "Ferretería El Tornillo SAS", "NIT", "900123456", null, null, null, "CO", null);

        var ticket = NewTicket(requester: company);

        Assert.Equal("Ferretería El Tornillo SAS", ticket.Requester!.DisplayName());
    }

    [Fact]
    public void PausingAndResumingMovesTheDueDateByTheStoppedBusinessDays()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 16), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17), Clock);
        Assert.Equal(At(11, 17), ticket.PausedSince);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 23, 14), Clock);

        Assert.Equal(3, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 11), ticket.DueOn);
        Assert.Null(ticket.PausedSince);
        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
    }

    [Fact]
    public void PausingAndResumingTheSameDayKeepsTheDueDate()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 17, 14), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17, 15), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 17, 20), Clock);

        Assert.Equal(0, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
        Assert.Null(ticket.PausedSince);
    }

    [Fact]
    public void WaitingInternalKeepsTheClockRunning()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 16), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingInternal, Actor, At(11, 17), Clock);
        Assert.Null(ticket.PausedSince);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 23), Clock);

        Assert.Equal(0, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
    }

    [Fact]
    public void AChainOfStopsCountsThePauseOnceFromItsStart()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 16), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 19), Clock);
        Assert.Equal(At(11, 17), ticket.PausedSince);
        Assert.Equal(At(11, 19), ticket.ResolvedAt);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 23), Clock);

        Assert.Equal(3, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 11), ticket.DueOn);
        Assert.Equal(1, ticket.ResolutionCount);
        Assert.Null(ticket.PausedSince);
    }

    [Fact]
    public void ReopeningAndResolvingAgainCountsASecondCycle()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 18), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 19), Clock);

        Assert.Equal(2, ticket.ResolutionCount);
        Assert.Equal(At(11, 19), ticket.ResolvedAt);
        Assert.Equal(SupportTicketStatus.Resolved, ticket.Status);
    }

    [Fact]
    public void ClosingStampsClosedAtAndIsTerminal()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, At(11, 18), Clock);

        Assert.Equal(At(11, 18), ticket.ClosedAt);
        Assert.Equal(At(11, 17), ticket.PausedSince);
        var reopen = Assert.Throws<SupportDomainException>(
            () => ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 19), Clock));
        Assert.Equal(SupportErrorCodes.TransitionInvalid, reopen.Code);
    }

    [Fact]
    public void AnInvalidTransitionKeepsTheTicketAsItWas()
    {
        var ticket = NewTicket();

        var exception = Assert.Throws<SupportDomainException>(
            () => ticket.ChangeStatus(SupportTicketStatus.WaitingInternal, Actor, At(11, 17), Clock));

        Assert.Equal(SupportErrorCodes.TransitionInvalid, exception.Code);
        Assert.Equal(SupportTicketStatus.New, ticket.Status);
        Assert.Equal(1L, ticket.Version);
        Assert.Single(ticket.Events);
        Assert.Equal(1, ticket.LastEventOrdinal);
    }

    [Fact]
    public void TheSameStatusIsNotATransition()
    {
        var ticket = NewTicket();

        var exception = Assert.Throws<SupportDomainException>(
            () => ticket.ChangeStatus(SupportTicketStatus.New, Actor, At(11, 17), Clock));

        Assert.Equal(SupportErrorCodes.TransitionInvalid, exception.Code);
        Assert.Equal(1L, ticket.Version);
    }

    [Fact]
    public void ChangingTheTypeRecalculatesFromTheReception()
    {
        var ticket = NewTicket();
        var documents = Documents;

        ticket.ChangeType(documents, Actor, At(11, 18), Clock);

        Assert.Equal(new DateOnly(2026, 11, 30), ticket.DueOn);
        Assert.Equal(documents.Id, ticket.TypeId);
        Assert.Equal("Petición de documentos o información", ticket.TypeName);
        Assert.Equal(10, ticket.TermBusinessDays);
        var changed = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.TypeChanged, changed.Kind);
        Assert.Equal(2, changed.Ordinal);
        using var details = JsonDocument.Parse(changed.Details);
        Assert.Equal("Petición general", details.RootElement.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("Petición de documentos o información", details.RootElement.GetProperty("to").GetProperty("name").GetString());
        Assert.Equal(documents.Id.Value, details.RootElement.GetProperty("to").GetProperty("id").GetGuid());
    }

    [Fact]
    public void ChangingTheTypeAfterAPauseKeepsThePausedDays()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 16), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 23), Clock);

        ticket.ChangeType(Documents, Actor, At(11, 24), Clock);

        Assert.Equal(3, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 3), ticket.DueOn);
    }

    [Fact]
    public void ChangingToATypeWithoutTermClearsTheDueDate()
    {
        var ticket = NewTicket();

        ticket.ChangeType(TechSupport, Actor, At(11, 18), Clock);

        Assert.Null(ticket.DueOn);
        Assert.Null(ticket.TermBusinessDays);
        Assert.False(ticket.TermIsLegal);
        Assert.False(ticket.RequiresFormalResponse);
    }

    [Fact]
    public void ATypeOfAnotherTenantIsRejected()
    {
        var ticket = NewTicket();
        var foreign = SupportTicketTypeDefaults.For(Guid.CreateVersion7(), Received)[1];

        Assert.Throws<ArgumentException>(() => ticket.ChangeType(foreign, Actor, At(11, 18), Clock));
        Assert.Equal(1L, ticket.Version);
    }

    [Fact]
    public void EditingTheTypeAfterwardsDoesNotMoveTheTicket()
    {
        var type = Petition;
        var ticket = NewTicket(type);

        type.Update(type.Name, 30, type.TermIsLegal, type.RequiresFormalResponse, type.IsPublished, type.IsActive, At(11, 18));

        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
        Assert.Equal(15, ticket.TermBusinessDays);
    }

    [Fact]
    public void EventOrdinalsAreConsecutive()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 16), Clock);
        ticket.Assign(OtherMember, Actor, At(11, 16, 16));

        Assert.Equal([1, 2, 3], ticket.Events.Select(item => item.Ordinal));
        Assert.Equal(
            [SupportTicketEventKind.Created, SupportTicketEventKind.StatusChanged, SupportTicketEventKind.AssigneeChanged],
            ticket.Events.Select(item => item.Kind));
        Assert.Equal("""{"from":"New","to":"Open"}""", ticket.Events[1].Details);
        Assert.Equal($$"""{"from":null,"to":"{{OtherMember}}"}""", ticket.Events[2].Details);
        Assert.Equal(3, ticket.LastEventOrdinal);
        Assert.Equal(3L, ticket.Version);
        Assert.Equal(OtherMember, ticket.AssigneeMemberId);
        Assert.Equal(At(11, 16, 16), ticket.UpdatedAt);
    }

    [Fact]
    public void NoOpsDoNotWriteEventsNorBumpTheVersion()
    {
        var type = Petition;
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, type, "Asunto", "Descripción", ManualIntakeMedium.Phone,
            SupportReplyChannel.Email, Customer, Person, OtherMember, Actor, Received, Clock);

        ticket.Assign(OtherMember, Actor, At(11, 18));
        ticket.LinkCustomer(Customer, Actor, At(11, 18));
        ticket.ChangeType(type, Actor, At(11, 18), Clock);

        Assert.Single(ticket.Events);
        Assert.Equal(1L, ticket.Version);
        Assert.Equal(Received, ticket.UpdatedAt);
    }

    [Fact]
    public void LinkingAndUnlinkingACustomerWritesCustomerChanged()
    {
        var ticket = NewTicket();

        ticket.LinkCustomer(Customer, Actor, At(11, 18));
        ticket.LinkCustomer(null, Actor, At(11, 19));

        Assert.Null(ticket.CustomerId);
        Assert.Equal(Person, ticket.Requester);
        Assert.Equal(
            [$$"""{"from":null,"to":"{{Customer}}"}""", $$"""{"from":"{{Customer}}","to":null}"""],
            ticket.Events.Skip(1).Select(item => item.Details));
        Assert.All(ticket.Events.Skip(1), item => Assert.Equal(SupportTicketEventKind.CustomerChanged, item.Kind));
        Assert.Equal(3L, ticket.Version);
    }

    // Pre-flight R11: un ticket creado sólo con cliente no tiene foto del solicitante; desvincularlo lo dejaría sin nadie.
    [Fact]
    public void UnlinkingTheCustomerWithoutARequesterNameIsRejected()
    {
        var ticket = NewTicket(customerId: Customer);

        var exception = Assert.Throws<SupportDomainException>(() => ticket.LinkCustomer(null, Actor, At(11, 18)));

        Assert.Equal(SupportErrorCodes.RequesterRequired, exception.Code);
        Assert.Equal(Customer, ticket.CustomerId);
        Assert.Single(ticket.Events);
        Assert.Equal(1L, ticket.Version);
    }

    // R9 [owner 2026-11-15]: resuelto o cerrado, el ticket no se reasigna, no cambia de tipo ni de cliente; ni siquiera con el
    // mismo valor, para que la respuesta no dependa de lo que se mande.
    [Theory]
    [InlineData(SupportTicketStatus.Resolved)]
    [InlineData(SupportTicketStatus.Closed)]
    public void AFinishedTicketCannotBeReassignedRetypedOrRelinked(SupportTicketStatus finished)
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);
        if (finished is SupportTicketStatus.Closed)
        {
            ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, At(11, 18), Clock);
        }

        var version = ticket.Version;

        Assert.Equal(
            SupportErrorCodes.NotEditable,
            Assert.Throws<SupportDomainException>(() => ticket.Assign(OtherMember, Actor, At(11, 19))).Code);
        Assert.Equal(
            SupportErrorCodes.NotEditable,
            Assert.Throws<SupportDomainException>(() => ticket.Assign(null, Actor, At(11, 19))).Code);
        Assert.Equal(
            SupportErrorCodes.NotEditable,
            Assert.Throws<SupportDomainException>(() => ticket.ChangeType(Documents, Actor, At(11, 19), Clock)).Code);
        Assert.Equal(
            SupportErrorCodes.NotEditable,
            Assert.Throws<SupportDomainException>(() => ticket.LinkCustomer(Customer, Actor, At(11, 19))).Code);
        Assert.Equal(finished, ticket.Status);
        Assert.Null(ticket.AssigneeMemberId);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
        Assert.Null(ticket.CustomerId);
        Assert.Equal(version, ticket.Version);
    }

    // Reabrir sigue la tabla de transiciones; reabierto, el ticket se vuelve a editar.
    [Fact]
    public void AReopenedTicketCanBeEditedAgain()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 18), Clock);

        ticket.Assign(OtherMember, Actor, At(11, 19));
        ticket.ChangeType(Documents, Actor, At(11, 19), Clock);
        ticket.LinkCustomer(Customer, Actor, At(11, 19));

        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
        Assert.Equal(OtherMember, ticket.AssigneeMemberId);
        Assert.Equal(Documents.Name, ticket.TypeName);
        Assert.Equal(Customer, ticket.CustomerId);
    }
}

using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Spec 2026-11-22: tareas internas. FakeCalendar: lunes a viernes con festivos 2026-11-16, 2026-12-08 y 2026-12-25; zona America/Bogota
// (UTC-5). 2026-11-13 es viernes.
public sealed class SupportTicketInternalTests
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

    private static SupportTicketType Task => SupportTicketTypeDefaults.Internal(TenantId, 9, Received);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];

    private static SupportTicket NewTask(DateOnly? dueOn = null, DateTimeOffset? now = null) =>
        SupportTicket.CreateInternal(
            SupportTicketId.New(), TenantId, 1, Task, "Cuadrar el inventario", "Revisar las diferencias de octubre.",
            SupportTicketPriority.Medium, OtherMember, dueOn, Actor, now ?? Received, Clock);

    private static SupportTicket NewExternal() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 2, Petition, "No me llegó la factura", "Pedí la factura del pedido 45.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, customerId: null, Person,
            assigneeMemberId: null, Actor, Received, Clock);

    private static DateTimeOffset At(int month, int day, int hourUtc = 15) => new(2026, month, day, hourUtc, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATaskHasNoRequesterCustomerNorReplyChannel()
    {
        var ticket = NewTask();

        Assert.True(ticket.IsInternal);
        Assert.Equal(SupportTicketIntake.Internal, ticket.Intake);
        Assert.Null(ticket.ManualMedium);
        Assert.Equal(SupportReplyChannel.None, ticket.ReplyChannel);
        Assert.Null(ticket.CustomerId);
        Assert.Null(ticket.Requester);
        Assert.Equal(Actor, ticket.CreatedByMemberId);
        Assert.Equal(OtherMember, ticket.AssigneeMemberId);
        Assert.Null(ticket.TermBusinessDays);
        var created = Assert.Single(ticket.Events);
        Assert.Equal(SupportTicketEventKind.Created, created.Kind);
        Assert.Equal(Actor, created.ActorMemberId);
    }

    // Merge with the unassigned alert (spec 2026-11-22 D-15): a task is never «sin asignar». The handler refuses to unassign it, and the
    // domain does not start the alert's clock even if a caller skipped that rule.
    [Fact]
    public void ATaskNeverStartsTheUnassignedClock()
    {
        var ticket = NewTask();
        Assert.Null(ticket.UnassignedSince);

        ticket.Assign(null, Actor, At(11, 13, 16));
        Assert.Null(ticket.UnassignedSince);

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 13, 17), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 13, 18), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 13, 19), Clock);
        Assert.Null(ticket.UnassignedSince);
    }

    [Fact]
    public void ATaskKeepsTheChosenDueDate() =>
        Assert.Equal(new DateOnly(2026, 11, 20), NewTask(new DateOnly(2026, 11, 20)).DueOn);

    [Fact]
    public void ATaskWithoutDueDateHasNone() => Assert.Null(NewTask().DueOn);

    [Fact]
    public void ADueDateBeforeTodayIsRejected()
    {
        var ex = Assert.Throws<SupportDomainException>(() => NewTask(new DateOnly(2026, 11, 12)));
        Assert.Equal(SupportErrorCodes.InternalDueInPast, ex.Code);

        Assert.Equal(new DateOnly(2026, 11, 13), NewTask(new DateOnly(2026, 11, 13)).DueOn);
    }

    // Review Focus 5: 03:00 UTC del 14 son las 22:00 del 13 en Bogotá.
    [Fact]
    public void TodayIsJudgedInTheTenantZone() =>
        Assert.Equal(new DateOnly(2026, 11, 13), NewTask(new DateOnly(2026, 11, 13), At(11, 14, hourUtc: 3)).DueOn);

    [Fact]
    public void ATaskNeedsTheInternalType() =>
        Assert.Throws<ArgumentException>(() => SupportTicket.CreateInternal(
            SupportTicketId.New(), TenantId, 1, Petition, "Cuadrar el inventario", "Revisar.", SupportTicketPriority.Medium,
            OtherMember, null, Actor, Received, Clock));

    [Fact]
    public void NoOtherEntryAcceptsTheInternalType()
    {
        Assert.Throws<ArgumentException>(() => SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, Task, "Asunto", "Texto.", SupportTicketPriority.Medium, ManualIntakeMedium.Phone,
            SupportReplyChannel.Email, customerId: null, Person, assigneeMemberId: null, Actor, Received, Clock));
        Assert.Throws<ArgumentException>(() => SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 1, Task, "Asunto", "Texto.", SupportTicketPriority.Medium,
            new SupportConversationRef(Guid.CreateVersion7(), "WhatsApp"), customerId: null, Person, assigneeMemberId: null, Actor,
            Received, Clock));
        Assert.Throws<ArgumentException>(() => SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 1, Task, "Asunto", "Texto.", Person, customerId: null, Guid.CreateVersion7(), Received,
            Clock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATaskCannotWaitOnTheCustomer(bool open)
    {
        var ticket = NewTask();
        if (open)
        {
            ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received, Clock);
        }

        var ex = Assert.Throws<SupportDomainException>(
            () => ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.TransitionInvalid, ex.Code);
    }

    [Fact]
    public void ResolvingATaskRequestsNoSurvey()
    {
        var ticket = NewTask();
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received, Clock);

        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);

        Assert.Empty(ticket.Surveys);
        Assert.Equal(1, ticket.ResolutionCount);
    }

    // Review Focus 1: con término null, reanudar recalcularía la fecha como null.
    [Fact]
    public void ReopeningATaskKeepsItsDueDate()
    {
        var ticket = NewTask(new DateOnly(2026, 11, 20));
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received, Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 18), Clock);

        Assert.Equal(new DateOnly(2026, 11, 20), ticket.DueOn);
    }

    [Fact]
    public void ATaskCannotChangeType()
    {
        var ex = Assert.Throws<SupportDomainException>(() => NewTask().ChangeType(Petition, Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.InternalTypeLocked, ex.Code);
    }

    [Fact]
    public void AnExternalTicketCannotTakeTheInternalType()
    {
        var ex = Assert.Throws<SupportDomainException>(() => NewExternal().ChangeType(Task, Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.InternalTypeLocked, ex.Code);
    }

    [Fact]
    public void ATaskTakesNoCustomer()
    {
        var ex = Assert.Throws<SupportDomainException>(() => NewTask().LinkCustomer(Customer, Actor, Received));
        Assert.Equal(SupportErrorCodes.InternalNoReply, ex.Code);
    }

    [Fact]
    public void ATaskTakesNoConversation()
    {
        var ex = Assert.Throws<SupportDomainException>(
            () => NewTask().LinkConversation(new SupportConversationRef(Guid.CreateVersion7(), "WhatsApp"), Actor, Received));
        Assert.Equal(SupportErrorCodes.InternalNoReply, ex.Code);
    }

    [Fact]
    public void ATaskHasNoExtension()
    {
        var ticket = NewTask(new DateOnly(2026, 11, 20));

        Assert.Null(ticket.MaxExtensionBusinessDays(new DateOnly(2026, 11, 13)));
        var ex = Assert.Throws<SupportDomainException>(() => ticket.Extend(1, "Motivo", Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.ExtensionNoTerm, ex.Code);
    }

    [Fact]
    public void ChangingTheDueDateOfATaskRecordsIt()
    {
        var ticket = NewTask(new DateOnly(2026, 11, 20));

        ticket.ChangeDueOn(new DateOnly(2026, 11, 25), Actor, At(11, 14), Clock);

        Assert.Equal(new DateOnly(2026, 11, 25), ticket.DueOn);
        Assert.Equal(2L, ticket.Version);
        var last = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.DueDateChanged, last.Kind);
        Assert.Equal("""{"fromDueOn":"2026-11-20","toDueOn":"2026-11-25"}""", last.Details);

        ticket.ChangeDueOn(new DateOnly(2026, 11, 25), Actor, At(11, 14), Clock);
        Assert.Equal(2L, ticket.Version);

        ticket.ChangeDueOn(null, Actor, At(11, 14), Clock);
        Assert.Null(ticket.DueOn);
        Assert.Equal(3L, ticket.Version);
    }

    [Fact]
    public void ADueDateChangeBeforeTodayIsRejected()
    {
        var ticket = NewTask(new DateOnly(2026, 11, 20));

        var ex = Assert.Throws<SupportDomainException>(() => ticket.ChangeDueOn(new DateOnly(2026, 11, 12), Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.InternalDueInPast, ex.Code);
    }

    [Fact]
    public void AResolvedTaskCannotChangeItsDueDate()
    {
        var ticket = NewTask(new DateOnly(2026, 11, 20));
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received, Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);

        var ex = Assert.Throws<SupportDomainException>(() => ticket.ChangeDueOn(new DateOnly(2026, 11, 25), Actor, At(11, 17), Clock));
        Assert.Equal(SupportErrorCodes.NotEditable, ex.Code);
    }

    [Fact]
    public void AnExternalTicketHasNoFreeDueDate()
    {
        var ex = Assert.Throws<SupportDomainException>(() => NewExternal().ChangeDueOn(new DateOnly(2026, 11, 25), Actor, Received, Clock));
        Assert.Equal(SupportErrorCodes.NotInternal, ex.Code);
    }

    [Fact]
    public void CreatedByIsTheActorOrNobody()
    {
        Assert.Equal(Actor, NewExternal().CreatedByMemberId);

        var fromConversation = SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 3, Petition, "Asunto", "Texto.", SupportTicketPriority.Medium,
            new SupportConversationRef(Guid.CreateVersion7(), "WhatsApp"), customerId: null, Person, assigneeMemberId: null, Actor,
            Received, Clock);
        Assert.Equal(Actor, fromConversation.CreatedByMemberId);

        var fromForm = SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 4, Petition, "Asunto", "Texto.", Person, customerId: null, Guid.CreateVersion7(), Received,
            Clock);
        Assert.Null(fromForm.CreatedByMemberId);
    }
}

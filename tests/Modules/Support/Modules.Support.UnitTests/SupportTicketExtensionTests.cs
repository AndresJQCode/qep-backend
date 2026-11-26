using System.Text.Json;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Fase 4a (plan G9 a G12): la prórroga única del plazo. FakeCalendar: lunes a viernes con festivos 2026-11-16, 2026-12-08 y
// 2026-12-25; zona America/Bogota (UTC-5). Radicado el viernes 2026-11-13 en un tipo de 15 días: vence el 2026-12-07.
public sealed class SupportTicketExtensionTests
{
    private const string Reason = "Falta respuesta del proveedor";

    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];      // 15 días, legal

    private static SupportTicketType Documents => SupportTicketTypeDefaults.For(TenantId, Received)[1];     // 10 días

    private static SupportTicketType TechSupport => SupportTicketTypeDefaults.For(TenantId, Received)[7];   // sin término

    private static SupportTicket NewTicket(SupportTicketType? type = null) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, type ?? Petition, "No me llegó la factura", "Pedí la factura del pedido 45 y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, null, Person, assigneeMemberId: null, Actor, Received, Clock);

    private static DateTimeOffset At(int month, int day, int hourUtc = 15) => new(2026, month, day, hourUtc, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnExtensionMovesTheDueDateByBusinessDaysAndWritesTheTrace()
    {
        var ticket = NewTicket();
        var version = ticket.Version;

        ticket.Extend(5, $"  {Reason}  ", Actor, At(11, 17), Clock);

        // 20 días hábiles desde el 2026-11-13, saltando el festivo del 2026-12-08.
        Assert.Equal(new DateOnly(2026, 12, 15), ticket.DueOn);
        Assert.Equal(5, ticket.ExtensionBusinessDays);
        Assert.Equal(Reason, ticket.ExtensionReason);
        Assert.Equal(At(11, 17), ticket.ExtendedAt);
        Assert.Equal(version + 1, ticket.Version);
        var extended = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.Extended, extended.Kind);
        Assert.Equal(Actor, extended.ActorMemberId);
        Assert.Equal(At(11, 17), extended.OccurredAt);
        using var details = JsonDocument.Parse(extended.Details);
        Assert.Equal(5, details.RootElement.GetProperty("businessDays").GetInt32());
        Assert.Equal(Reason, details.RootElement.GetProperty("reason").GetString());
        Assert.Equal("2026-12-07", details.RootElement.GetProperty("fromDueOn").GetString());
        Assert.Equal("2026-12-15", details.RootElement.GetProperty("toDueOn").GetString());
    }

    // Owner decision S6 (spec 2026-11-15 D-11): the current term is the type's snapshot plus the extension.
    [Fact]
    public void TheCurrentTermAddsTheExtensionDays()
    {
        var ticket = NewTicket();
        Assert.Equal(15, ticket.CurrentTermBusinessDays);
        Assert.False(ticket.IsExtended);

        ticket.Extend(5, Reason, Actor, At(11, 17), Clock);

        Assert.Equal(15, ticket.TermBusinessDays);
        Assert.Equal(20, ticket.CurrentTermBusinessDays);
        Assert.True(ticket.IsExtended);
    }

    [Fact]
    public void ATicketWithoutTermHasNoCurrentTerm()
    {
        var ticket = NewTicket(TechSupport);

        Assert.Null(ticket.CurrentTermBusinessDays);
        Assert.False(ticket.IsExtended);
    }

    [Fact]
    public void ASecondExtensionIsRejected()
    {
        var ticket = NewTicket();
        ticket.Extend(5, Reason, Actor, At(11, 17), Clock);
        var version = ticket.Version;

        var exception = Assert.Throws<SupportDomainException>(() => ticket.Extend(3, "Otra vez", Actor, At(11, 18), Clock));

        Assert.Equal(SupportErrorCodes.ExtensionAlreadyUsed, exception.Code);
        Assert.Equal(new DateOnly(2026, 12, 15), ticket.DueOn);
        Assert.Equal(5, ticket.ExtensionBusinessDays);
        Assert.Equal(Reason, ticket.ExtensionReason);
        Assert.Equal(version, ticket.Version);
    }

    [Fact]
    public void ATicketWithoutTermCannotBeExtended()
    {
        var ticket = NewTicket(TechSupport);

        var exception = Assert.Throws<SupportDomainException>(() => ticket.Extend(5, Reason, Actor, At(11, 17), Clock));

        Assert.Equal(SupportErrorCodes.ExtensionNoTerm, exception.Code);
        Assert.Equal(0, ticket.ExtensionBusinessDays);
        Assert.Null(ticket.DueOn);
    }

    [Theory]
    [InlineData(SupportTicketStatus.Resolved)]
    [InlineData(SupportTicketStatus.Closed)]
    public void AFinishedTicketCannotBeExtended(SupportTicketStatus finished)
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);
        if (finished is SupportTicketStatus.Closed)
        {
            ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, At(11, 18), Clock);
        }

        var exception = Assert.Throws<SupportDomainException>(() => ticket.Extend(5, Reason, Actor, At(11, 19), Clock));

        Assert.Equal(SupportErrorCodes.NotEditable, exception.Code);
        Assert.Equal(0, ticket.ExtensionBusinessDays);
    }

    [Fact]
    public void AnExtensionWhileWaitingOnTheCustomerIsRejected()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17), Clock);

        var exception = Assert.Throws<SupportDomainException>(() => ticket.Extend(5, Reason, Actor, At(11, 18), Clock));

        Assert.Equal(SupportErrorCodes.ExtensionClockStopped, exception.Code);
        Assert.Equal(0, ticket.ExtensionBusinessDays);
    }

    // Review Focus 3: el plazo vence al final de su día hábil, así que ese mismo día todavía se prorroga.
    [Fact]
    public void AnExtensionOnTheDueDayIsAccepted()
    {
        var ticket = NewTicket();

        ticket.Extend(5, Reason, Actor, At(12, 7, 22), Clock);

        Assert.Equal(new DateOnly(2026, 12, 15), ticket.DueOn);
    }

    [Fact]
    public void AnExtensionTheDayAfterTheDueDateIsOverdue()
    {
        var ticket = NewTicket();

        // 2026-12-09 es el primer día hábil después del vencimiento (el 2026-12-08 es festivo).
        var exception = Assert.Throws<SupportDomainException>(() => ticket.Extend(5, Reason, Actor, At(12, 9), Clock));

        Assert.Equal(SupportErrorCodes.ExtensionOverdue, exception.Code);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
    }

    [Fact]
    public void AnExtensionLongerThanTheTermIsRejected()
    {
        var rejected = NewTicket();
        var accepted = NewTicket();

        var exception = Assert.Throws<SupportDomainException>(() => rejected.Extend(16, Reason, Actor, At(11, 17), Clock));
        accepted.Extend(15, Reason, Actor, At(11, 17), Clock);

        Assert.Equal(SupportErrorCodes.ExtensionTooLong, exception.Code);
        Assert.Equal(0, rejected.ExtensionBusinessDays);
        // El doble exacto del término: 30 días hábiles desde el 2026-11-13.
        Assert.Equal(new DateOnly(2026, 12, 30), accepted.DueOn);
    }

    // Review Focus 4: la pausa posterior se suma a la prórroga, no la reemplaza.
    [Fact]
    public void APauseAfterAnExtensionKeepsTheExtension()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 17), Clock);
        ticket.Extend(5, Reason, Actor, At(11, 17), Clock);

        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 18), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, At(11, 23), Clock);

        // 2026-11-19 y 2026-11-20 quedan completos dentro de la pausa: 15 + 5 + 2 = 22 días hábiles.
        Assert.Equal(2, ticket.PausedBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 17), ticket.DueOn);
    }

    [Fact]
    public void ChangingTheTypeKeepsTheExtension()
    {
        var ticket = NewTicket();
        ticket.Extend(5, Reason, Actor, At(11, 17), Clock);

        ticket.ChangeType(Documents, Actor, At(11, 18), Clock);

        // Plan G12: 10 del tipo nuevo + 5 de la prórroga.
        Assert.Equal(5, ticket.ExtensionBusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 7), ticket.DueOn);
    }

    [Fact]
    public void MaxExtensionIsTheTermWhileExtendableAndNullOtherwise()
    {
        var extendable = NewTicket();
        var extended = NewTicket();
        extended.Extend(5, Reason, Actor, At(11, 17), Clock);
        var waiting = NewTicket();
        waiting.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, At(11, 17), Clock);
        var resolved = NewTicket();
        resolved.ChangeStatus(SupportTicketStatus.Resolved, Actor, At(11, 17), Clock);

        Assert.Equal(15, extendable.MaxExtensionBusinessDays(new DateOnly(2026, 11, 17)));
        Assert.Equal(15, extendable.MaxExtensionBusinessDays(new DateOnly(2026, 12, 7)));
        Assert.Null(extendable.MaxExtensionBusinessDays(new DateOnly(2026, 12, 9)));
        Assert.Null(extended.MaxExtensionBusinessDays(new DateOnly(2026, 11, 18)));
        Assert.Null(waiting.MaxExtensionBusinessDays(new DateOnly(2026, 11, 18)));
        Assert.Null(resolved.MaxExtensionBusinessDays(new DateOnly(2026, 11, 18)));
        Assert.Null(NewTicket(TechSupport).MaxExtensionBusinessDays(new DateOnly(2026, 11, 17)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyOrTooLongReasonThrows(string? reason)
    {
        var ticket = NewTicket();
        var tooLong = new string('x', SupportTicket.ExtensionReasonMaxLength + 1);

        Assert.ThrowsAny<ArgumentException>(() => ticket.Extend(5, reason!, Actor, At(11, 17), Clock));
        Assert.ThrowsAny<ArgumentException>(() => ticket.Extend(5, tooLong, Actor, At(11, 17), Clock));
        Assert.ThrowsAny<ArgumentException>(() => ticket.Extend(0, Reason, Actor, At(11, 17), Clock));
        Assert.Equal(0, ticket.ExtensionBusinessDays);
        Assert.Null(ticket.ExtendedAt);
    }
}

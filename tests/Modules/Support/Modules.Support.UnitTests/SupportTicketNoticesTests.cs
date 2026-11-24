using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Spec 2026-11-22 §5.4: un externo avisa por <c>TicketChangedAsync</c>; una tarea, por <c>TaskChangedAsync</c> con su creador,
/// su responsable y el responsable anterior, sin repetir miembros ni incluir vacíos.</summary>
public sealed class SupportTicketNoticesTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Creator = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly Guid Former = Guid.Parse("0193a000-0000-7000-8000-0000000000a3");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static SupportTicket NewTask(Guid assignee) =>
        SupportTicket.CreateInternal(
            SupportTicketId.New(), TenantId, 1, SupportTicketTypeDefaults.Internal(TenantId, 9, Received), "Cuadrar el inventario",
            "Revisar las diferencias.", SupportTicketPriority.Medium, assignee, null, Creator, Received, Clock);

    private static SupportTicket NewExternal() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 2, SupportTicketTypeDefaults.For(TenantId, Received)[0], "No me llegó la factura",
            "Pedí la factura.", SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, customerId: null,
            new TicketRequester("Ana", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null),
            assigneeMemberId: null, Creator, Received, Clock);

    [Fact]
    public async Task AnExternalTicketAnnouncesOnItsOwnTopicsOnly()
    {
        var realtime = new RecordingRealtime();
        var ticket = NewExternal();

        await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: Former, CancellationToken.None);

        Assert.Equal([ticket.Id.Value], realtime.TicketNotices);
        Assert.Empty(realtime.TaskNotices);
    }

    [Fact]
    public async Task ATaskAnnouncesToItsCreatorItsAssigneeAndTheFormerAssignee()
    {
        var realtime = new RecordingRealtime();
        var ticket = NewTask(Assignee);

        await SupportTicketNotices.ChangedAsync(realtime, ticket, Former, CancellationToken.None);

        Assert.Empty(realtime.TicketNotices);
        var notice = Assert.Single(realtime.TaskNotices);
        Assert.Equal(ticket.Id.Value, notice.TicketId);
        Assert.Equal(new[] { Creator, Assignee, Former }.Order(), notice.Members.Order());
    }

    [Fact]
    public async Task ATaskNeverRepeatsAMemberNorListsAMissingOne()
    {
        var realtime = new RecordingRealtime();
        var ticket = NewTask(Creator);

        await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);

        Assert.Equal([Creator], Assert.Single(realtime.TaskNotices).Members);
    }

    private sealed class RecordingRealtime : ISupportRealtime
    {
        public List<Guid> TicketNotices { get; } = [];

        public List<(Guid TicketId, IReadOnlyCollection<Guid> Members)> TaskNotices { get; } = [];

        public Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
        {
            TicketNotices.Add(ticketId);
            return Task.CompletedTask;
        }

        public Task TaskChangedAsync(
            Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
        {
            TaskNotices.Add((ticketId, memberIds));
            return Task.CompletedTask;
        }
    }
}

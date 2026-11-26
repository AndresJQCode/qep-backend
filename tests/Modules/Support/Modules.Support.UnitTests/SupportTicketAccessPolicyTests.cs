using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Spec 2026-11-22 §5.6: la tabla de quién ve y quién actúa sobre un ticket. Es la única: la puerta HTTP y el hub la usan.
public sealed class SupportTicketAccessPolicyTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Creator = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly Guid Stranger = Guid.Parse("0193a000-0000-7000-8000-0000000000a3");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private static SupportTicket External() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, SupportTicketTypeDefaults.For(TenantId, Received)[0], "No me llegó la factura",
            "Pedí la factura del pedido 45.", SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            customerId: null, Person, assigneeMemberId: Assignee, Creator, Received, Clock);

    private static SupportTicket Task() =>
        SupportTicket.CreateInternal(
            SupportTicketId.New(), TenantId, 2, SupportTicketTypeDefaults.Internal(TenantId, 9, Received), "Cuadrar el inventario",
            "Revisar las diferencias de octubre.", SupportTicketPriority.Medium, Assignee, dueOn: null, Creator, Received, Clock);

    private const SupportTicketAccessLevel N = SupportTicketAccessLevel.None;
    private const SupportTicketAccessLevel V = SupportTicketAccessLevel.View;
    private const SupportTicketAccessLevel A = SupportTicketAccessLevel.Act;

    public static TheoryData<bool, bool, bool> EveryGrant()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var read in new[] { false, true })
            foreach (var manage in new[] { false, true })
                foreach (var taskUse in new[] { false, true })
                {
                    data.Add(read, manage, taskUse);
                }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryGrant))]
    public void TheAccessTable(bool read, bool manage, bool taskUse)
    {
        var grants = new SupportGrants(read, manage, taskUse);
        var external = External();
        var task = Task();

        // Literal §5.6 expectations, one row per case; they do not repeat the formula of SupportTicketAccessPolicy.For.
        // Gestionar: ve y actúa sobre todo, sea o no parte. Leer sin gestionar: ve todo; actúa sólo donde task.use la hace parte de
        // una tarea. Sólo task.use: lo externo no existe; la tarea, sólo si es creador o responsable.
        var (onExternal, onTaskAsParticipant, onTaskAsStranger) = (read, manage, taskUse) switch
        {
            (_, true, _) => (A, A, A),
            (true, false, true) => (V, A, V),
            (true, false, false) => (V, V, V),
            (false, false, true) => (N, A, N),
            (false, false, false) => (N, N, N),
        };

        Assert.Equal(onExternal, SupportTicketAccessPolicy.For(grants, external, Creator));
        Assert.Equal(onExternal, SupportTicketAccessPolicy.For(grants, external, Assignee));
        Assert.Equal(onTaskAsParticipant, SupportTicketAccessPolicy.For(grants, task, Creator));
        Assert.Equal(onTaskAsParticipant, SupportTicketAccessPolicy.For(grants, task, Assignee));
        Assert.Equal(onTaskAsStranger, SupportTicketAccessPolicy.For(grants, task, Stranger));
    }

    [Fact]
    public void AReaderWithTaskUseSeesAnExternalTicketAndActsOnHerOwnTask()
    {
        var grants = new SupportGrants(Read: true, Manage: false, TaskUse: true);

        Assert.Equal(SupportTicketAccessLevel.View, SupportTicketAccessPolicy.For(grants, External(), Creator));
        Assert.Equal(SupportTicketAccessLevel.Act, SupportTicketAccessPolicy.For(grants, Task(), Assignee));
        Assert.Equal(SupportTicketAccessLevel.View, SupportTicketAccessPolicy.For(grants, Task(), Stranger));
    }

    [Fact]
    public void ParticipationWithoutTaskUseGivesNoAccess()
    {
        var none = new SupportGrants(Read: false, Manage: false, TaskUse: false);

        Assert.Equal(SupportTicketAccessLevel.None, SupportTicketAccessPolicy.For(none, Task(), Creator));
        Assert.Equal(SupportTicketAccessLevel.None, SupportTicketAccessPolicy.For(none, Task(), Assignee));
    }

    [Fact]
    public void ANullMemberIsNeverAParticipant()
    {
        var taskOnly = new SupportGrants(Read: false, Manage: false, TaskUse: true);

        Assert.Equal(SupportTicketAccessLevel.None, SupportTicketAccessPolicy.For(taskOnly, Task(), memberId: null));
    }

    [Fact]
    public void EnsureExternalRefusesATaskWithInternalNoReplyAndLetsAnExternalPass()
    {
        var error = Assert.Throws<SupportDomainException>(() => SupportTicketScope.EnsureExternal(Task()));

        Assert.Equal(SupportErrorCodes.InternalNoReply, error.Code);
        SupportTicketScope.EnsureExternal(External());
    }
}

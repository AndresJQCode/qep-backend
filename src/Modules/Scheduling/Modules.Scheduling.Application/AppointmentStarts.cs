using BuildingBlocks.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// The two questions booking and rescheduling ask about a start, in the order that keeps the answer honest under concurrency.
/// First, before any lock: do the rules offer it (blocks, closures, holidays, notice, horizon)? If not, 422 <c>outside_rules</c>; the
/// server rules over whatever the client sent. Then, holding the resource locks: does a seat remain? If not, 409. The split matters
/// because a start someone holds is not an error of the caller's rules, and the 409 is the only one a retry can fix.
/// </summary>
internal static class AppointmentStarts
{
    public const string OutsideRulesMessage = "Ese horario no está disponible para este servicio. Elige uno de los que ofrece la agenda.";

    public const string ResourceNotChoosableMessage = "En ese horario no se puede elegir quién atiende: la agenda asigna a quien esté libre. Intenta sin elegir.";

    /// <summary>The resources that could take <paramref name="startsAt"/> by the rules alone, in the engine's order (the "first free" order).</summary>
    public static IReadOnlyList<ResourceId> RulesCandidates(
        LoadedAvailability loaded,
        Service service,
        IReadOnlyList<Session> sessions,
        DateTimeOffset startsAt,
        ResourceId? requested,
        Func<ResourceId, bool> allowed)
    {
        var slot = AvailabilityEngine
            .Compute(loaded.Query(service, sessions, requested, ignoreOccupancy: true))
            .FirstOrDefault(candidate => candidate.StartsAt == startsAt);
        var candidates = slot is null
            ? []
            : slot.Options.Select(option => option.ResourceId).Where(allowed).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            throw new SchedulingDomainException(SchedulingErrorCodes.OutsideRules, OutsideRulesMessage);
        }

        return candidates;
    }

    /// <summary>
    /// D-11 and D-16, only on the agent's path: the option the rules give <paramref name="requested"/> at that start must allow the client to
    /// choose it. The back office books any resource it likes and never calls this. Rules only (<c>ignoreOccupancy</c>): whether somebody
    /// holds the start is the seat check's business, and a 409 is the answer a retry can fix, this one is not.
    /// </summary>
    public static void EnsureClientChoice(
        LoadedAvailability loaded, Service service, IReadOnlyList<Session> sessions, DateTimeOffset startsAt, ResourceId requested)
    {
        var slot = AvailabilityEngine
            .Compute(loaded.Query(service, sessions, requested, ignoreOccupancy: true))
            .FirstOrDefault(candidate => candidate.StartsAt == startsAt);
        if (slot is null || !slot.Options.Any(option => option.ResourceId == requested && option.AllowClientChoice))
        {
            throw new SchedulingDomainException(SchedulingErrorCodes.ResourceNotChoosable, ResourceNotChoosableMessage);
        }
    }

    /// <summary>
    /// The option to book, from sessions read <b>after</b> the locks. <paramref name="preferred"/> wins when it still has a seat; otherwise
    /// the first free one. No option is a 409: <c>session_full</c> when a shared session holds the start and is full, <c>slot_taken</c> for
    /// everything else (a single seat gone, an overlapping session, a published session the tenant removed).
    /// </summary>
    public static SlotOption PickFree(
        LoadedAvailability loaded,
        Service service,
        IReadOnlyList<Session> activeSessions,
        DateTimeOffset startsAt,
        IReadOnlyList<ResourceId> candidates,
        ResourceId? preferred)
    {
        var slot = AvailabilityEngine
            .Compute(loaded.Query(service, activeSessions))
            .FirstOrDefault(candidate => candidate.StartsAt == startsAt);
        var options = slot?.Options.Where(option => candidates.Contains(option.ResourceId)).ToArray() ?? [];
        var chosen = options.FirstOrDefault(option => option.ResourceId == preferred) ?? options.FirstOrDefault();
        if (chosen is not null)
        {
            return chosen;
        }

        var full = activeSessions.FirstOrDefault(session =>
            session.ServiceId == service.Id && session.StartsAt == startsAt && candidates.Contains(session.ResourceId) && !session.HasRoom);
        throw full is { Capacity: > 1 }
            ? new ResourceConflictException(SchedulingErrorCodes.SessionFull, "Esa sesión ya no tiene cupos. Elige otra.")
            : SlotTaken();
    }

    public static ResourceConflictException SlotTaken() =>
        new(SchedulingErrorCodes.SlotTaken, "Ese espacio ya no está disponible. Elige otro.");

    /// <summary>
    /// The session that takes the seat: the one already open at that key, or a new ad-hoc one with the seats of the block. A published
    /// session the tenant removed (inactive and published) is never reopened by a booking (PD-3): that is a 409, not a reservation.
    /// </summary>
    public static async Task<Session> TakeSeatAsync(
        ISessionRepository sessions,
        Guid tenantId,
        SlotOption option,
        Service service,
        DateTimeOffset startsAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var session = await sessions.FindByKeyAsync(tenantId, option.ResourceId, service.Id, startsAt, cancellationToken);
        if (session is null)
        {
            session = Session.Create(
                SessionId.New(), tenantId, option.ResourceId, service.Id, startsAt, startsAt.AddMinutes(service.DurationMinutes),
                service.BufferMinutes, option.SeatsLeft, isPublished: false, now);
            sessions.Add(session);
        }
        else if (!session.IsActive && session.IsPublished)
        {
            throw SlotTaken();
        }
        else if (!session.HasRoom)
        {
            throw session.Capacity > 1
                ? new ResourceConflictException(SchedulingErrorCodes.SessionFull, "Esa sesión ya no tiene cupos. Elige otra.")
                : SlotTaken();
        }

        session.Reserve(now);
        return session;
    }
}

using Modules.Hr.Domain;

namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>A person in the digest: an id and a name. Never the birth date or the age.</summary>
public sealed record BirthdayPerson(Guid EmployeeId, string EmployeeName);

/// <summary>The channels of the digest, as the setting was when the run happened (Notifications does not read HR's tables).</summary>
public sealed record BirthdayDigestChannels(Guid? WhatsAppConnectionId, bool Email, Guid SentByMemberId);

/// <summary>
/// The payload of <c>hr.birthdays.digest.v1</c> (spec 2026-11-26 §5.2), one per manager and day. Serialized in camelCase: that is
/// the contract with Notifications. Lists are ordered by name and capped at <see cref="BirthdayDigest.MaxNames"/>; the totals carry
/// the real counts so the consumers can say "y N más".
/// </summary>
public sealed record BirthdayDigestPayload(
    Guid TenantId,
    Guid MemberId,
    DateOnly RunDate,
    IReadOnlyList<BirthdayPerson> Today,
    int TodayTotal,
    IReadOnlyList<BirthdayPerson> Upcoming,
    int UpcomingTotal,
    DateOnly? UpcomingDate,
    BirthdayDigestChannels Channels,
    DateTimeOffset ExpiresAt);

/// <summary>The daily run of one tenant: gates, claims and publishing live in Infrastructure; the worker only asks.</summary>
public interface IHrBirthdayNoticesProcessor
{
    Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Pure composition of the digest: who has a birthday today and who exactly <c>leadDays</c> ahead.</summary>
public static class BirthdayDigest
{
    public const int MaxNames = 50;

    public static (
        IReadOnlyList<BirthdayPerson> Today,
        int TodayTotal,
        IReadOnlyList<BirthdayPerson> Upcoming,
        int UpcomingTotal,
        DateOnly? UpcomingDate) Compose(
        IReadOnlyList<(Guid Id, string Name, DateOnly BirthDate)> activeEmployees, DateOnly today, int leadDays)
    {
        var (todayNames, todayTotal) = Pick(activeEmployees, today);
        if (leadDays <= 0)
        {
            return (todayNames, todayTotal, [], 0, null);
        }

        var upcomingDate = today.AddDays(leadDays);
        var (upcomingNames, upcomingTotal) = Pick(activeEmployees, upcomingDate);
        return (todayNames, todayTotal, upcomingNames, upcomingTotal, upcomingDate);
    }

    private static (IReadOnlyList<BirthdayPerson> Names, int Total) Pick(
        IReadOnlyList<(Guid Id, string Name, DateOnly BirthDate)> employees, DateOnly day)
    {
        var matching = employees
            .Where(employee => BirthdayCalendar.FallsOn(employee.BirthDate, day))
            .OrderBy(employee => employee.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(employee => employee.Id)
            .ToList();
        return (matching.Take(MaxNames).Select(employee => new BirthdayPerson(employee.Id, employee.Name)).ToList(), matching.Count);
    }
}

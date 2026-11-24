namespace Modules.Hr.Application;

/// <summary>An active employee's raw HR phone, keyed by the membership the employee is linked to.</summary>
public sealed record HrMemberPhone(Guid MemberId, string Phone);

/// <summary>
/// Narrow read for the member phone fallback (task notices). The text is free-form HR data; whoever
/// consumes it must normalize it, nothing here guesses a number.
/// </summary>
public interface IHrMemberPhones
{
    /// <summary>
    /// Only employees linked to one of <paramref name="memberIds"/>, with an active contract and a
    /// non-blank phone. A retired or deactivated employee never lends a number.
    /// </summary>
    Task<IReadOnlyList<HrMemberPhone>> ListAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken);
}

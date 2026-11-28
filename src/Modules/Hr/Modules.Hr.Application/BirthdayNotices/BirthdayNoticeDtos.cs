using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>
/// The body of <c>PUT /birthday-notices</c>. Only <see cref="ManagerMemberIds"/> is required (an empty list means the feature is
/// off): a missing <see cref="LeadDays"/> takes the default of 1 and a missing channel flag means off. The PUT replaces
/// everything, so an omitted flag turns that channel off.
/// </summary>
public sealed record BirthdayNoticeRequest(
    IReadOnlyList<Guid>? ManagerMemberIds,
    int? LeadDays,
    bool? WhatsAppEnabled,
    Guid? WhatsAppConnectionId,
    bool? EmailEnabled);

/// <summary>
/// A manager as the screen draws it. BFF: the <c>Has*</c> flags exist so the screen can warn ("sin teléfono: no le llegará el
/// WhatsApp", "ya no tiene acceso a RRHH") without extra calls to Tenancy, Identity or the roles.
/// </summary>
public sealed record BirthdayManagerDto(Guid MemberId, string DisplayName, bool HasPhone, bool HasEmail, bool HasHrAccess);

/// <summary>
/// Spec 2026-11-27: a member the screen offers as a manager. BFF: only the active members with <c>hr.employee.read</c>, exactly the
/// ones the PUT accepts, so the multiselect never offers someone the PUT rejects with <c>manager_without_access</c>, and the screen
/// needs neither <c>GET /memberships</c> nor <c>advisorship.read</c>.
/// </summary>
public sealed record BirthdayCandidateDto(Guid MemberId, string DisplayName);

/// <summary>
/// Spec 2026-11-27: a WhatsApp connection the screen offers for the digest. BFF: <see cref="TemplateStatus"/> (<c>Approved</c>,
/// <c>Pending</c>, <c>Rejected</c>, <c>Missing</c>) saves the screen the Messaging calls, and their permissions, it needed only to know
/// whether the birthday template is approved on each connection.
/// </summary>
public sealed record BirthdayConnectionDto(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);

/// <summary>
/// The setting as the screen needs it. BFF: <see cref="Version"/> is <c>null</c> when nothing was saved yet, and that is what tells
/// the screen to send its first PUT without <c>If-Match</c>; the defaults (1 day, both channels off, nobody) come from the server
/// so the client does not duplicate them. <see cref="Candidates"/> and <see cref="Connections"/> (spec 2026-11-27) let the screen
/// paint with this single call; both come even with nothing saved, which is when the screen needs them most.
/// <see cref="Candidates"/> is <c>null</c> for a caller without <c>hr.settings.manage</c>: that screen is read-only, has nobody to
/// choose, and is not handed the list of who reads HR.
/// </summary>
public sealed record BirthdayNoticesDto(
    IReadOnlyList<BirthdayManagerDto> Managers,
    int LeadDays,
    bool WhatsAppEnabled,
    Guid? WhatsAppConnectionId,
    bool EmailEnabled,
    long? Version,
    IReadOnlyList<BirthdayCandidateDto>? Candidates,
    IReadOnlyList<BirthdayConnectionDto> Connections);

// Public on purpose: AddValidatorsFromAssembly only registers public types.
public sealed class BirthdayNoticeRequestValidator : HrValidator<BirthdayNoticeRequest>
{
    public BirthdayNoticeRequestValidator()
    {
        RuleFor(request => request.ManagerMemberIds)
            .NotNull().WithMessage("Indica quiénes reciben el aviso (puede ser una lista vacía).")
            .Must(ids => ids is null || ids.Distinct().Count() <= BirthdayNoticeSetting.MaxManagers)
            .WithMessage($"Pueden ser máximo {BirthdayNoticeSetting.MaxManagers} personas.");
        RuleFor(request => request.LeadDays)
            .InclusiveBetween(0, BirthdayNoticeSetting.MaxLeadDays)
            .When(request => request.LeadDays is not null)
            .WithMessage($"Los días de anticipación van de 0 a {BirthdayNoticeSetting.MaxLeadDays}.");
    }
}

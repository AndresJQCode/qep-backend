using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>The editor's body (spec 2026-11-15 §7). <c>AuthorizationQuestion</c> and <c>PolicyUrl</c> are only for
/// <c>privacy-notice</c>; empty or null means no value.</summary>
public sealed record PublishLegalDocumentRequest(string? ContentHtml, string? AuthorizationQuestion, string? PolicyUrl);

/// <param name="Kind">The route segment: <c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c>.</param>
/// <param name="ExpectedVersion">The current version of the kind that the screen loaded (0 if none); it comes in <c>If-Match</c>.</param>
public sealed record PublishLegalDocumentCommand(
    Guid TenantId,
    string? Kind,
    string? ContentHtml,
    string? AuthorizationQuestion,
    string? PolicyUrl,
    long ExpectedVersion)
    : ICommand<SupportLegalDocumentDto>
{
    public static PublishLegalDocumentCommand From(
        Guid tenantId, string? kind, PublishLegalDocumentRequest request, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(tenantId, kind, request.ContentHtml, request.AuthorizationQuestion, request.PolicyUrl, expectedVersion);
    }

    internal bool IsPrivacyNotice =>
        SupportLegalDocumentKinds.TryParseRoute(Kind, out var kind) && kind == SupportLegalDocumentKind.PrivacyNotice;
}

/// <summary>
/// The field of each limit (plan L5): the raw content (1 to 120 000 before sanitizing), the question and the URL, which are only for
/// the privacy notice. What only the sanitized HTML can tell (no visible text, more than 60 000) is the domain's, with its own codes.
/// An unknown kind is the handler's 404.
/// </summary>
public sealed class PublishLegalDocumentValidator : AbstractValidator<PublishLegalDocumentCommand>
{
    public PublishLegalDocumentValidator()
    {
        RuleFor(command => command.ContentHtml)
            .Must(html => !string.IsNullOrWhiteSpace(html) && html.Length <= SupportLegalDocumentVersion.RawContentMaxLength)
            .WithMessage($"The content must have between 1 and {SupportLegalDocumentVersion.RawContentMaxLength} characters.");
        RuleFor(command => command.AuthorizationQuestion)
            .Must((command, question) => string.IsNullOrWhiteSpace(question)
                || (command.IsPrivacyNotice && SupportLegalDocumentVersion.IsValidAuthorizationQuestion(question)))
            .WithMessage(
                $"The authorization question is only for the privacy notice: plain text of at most {SupportLegalDocumentVersion.AuthorizationQuestionMaxLength} characters.");
        RuleFor(command => command.PolicyUrl)
            .Must((command, url) => string.IsNullOrWhiteSpace(url)
                || (command.IsPrivacyNotice && SupportLegalDocumentVersion.IsValidPolicyUrl(url)))
            .WithMessage(
                $"The policy URL is only for the privacy notice: an absolute https URL of at most {SupportLegalDocumentVersion.PolicyUrlMaxLength} characters.");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0);
    }
}

/// <summary>
/// Publishes a legal document (spec 2026-11-15 §5.3, §6 and §7, plan L6): each publication is version <c>n+1</c> of its kind with
/// <c>If-Match: n</c>, and none is edited or deleted, because they are legal evidence. It always creates a version, even if the HTML
/// did not change: publishing is an explicit action. <b>The stored HTML is the output of <see cref="ISupportHtmlSanitizer"/></b>, and
/// so is the response, which the editor repaints with. With its audit in the same transaction, never with the content. If someone
/// else published the same version at once, the index answers 409 <c>support.legal_documents.version_taken</c>.
/// </summary>
public sealed class PublishLegalDocumentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ISupportProvisioner provisioner,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportHtmlSanitizer sanitizer,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<PublishLegalDocumentCommand> validator)
    : ICommandHandler<PublishLegalDocumentCommand, SupportLegalDocumentDto>
{
    private static readonly string[] ChangedFields = ["kind", "contentHtml"];

    private static readonly string[] PrivacyChangedFields = ["kind", "contentHtml", "authorizationQuestion", "policyUrl"];

    public async Task<SupportLegalDocumentDto> HandleAsync(PublishLegalDocumentCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        var kind = SupportLegalDocuments.ParseKind(command.Kind);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var currentVersion = (await legalDocuments.CurrentAsync(command.TenantId, kind, cancellationToken))?.Version ?? 0;
        if (currentVersion != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The legal document changed after it was loaded.");
        }

        var isPrivacy = kind == SupportLegalDocumentKind.PrivacyNotice;
        var content = sanitizer.Sanitize(command.ContentHtml!);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var version = SupportLegalDocumentVersion.Create(
            Guid.CreateVersion7(),
            command.TenantId,
            kind,
            currentVersion + 1,
            content,
            isPrivacy ? command.AuthorizationQuestion : null,
            isPrivacy ? command.PolicyUrl : null,
            actor,
            now);

        legalDocuments.Add(version);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.LegalDocumentPublished,
            SupportAuditActions.Resources.LegalDocument,
            version.Id.ToString(),
            "success",
            isPrivacy ? PrivacyChangedFields : ChangedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var names = await SupportLegalDocuments.AuthorNamesAsync(command.TenantId, [version], members, cancellationToken);
        var defaultQuestion = isPrivacy
            ? await SupportLegalDocuments.DefaultQuestionAsync(command.TenantId, tenantDirectory, cancellationToken)
            : null;
        // The author is whoever publishes, so IsMe is true by construction: no ISupportViewer round trip.
        return SupportLegalDocuments.ToDto(version, names, defaultQuestion, actor);
    }
}

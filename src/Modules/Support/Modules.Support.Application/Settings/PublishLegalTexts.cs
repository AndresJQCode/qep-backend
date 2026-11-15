using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>La política va como texto, como URL <c>https</c> o como ambas; sin ninguna es 422
/// <c>support.legal_texts.policy_required</c> (spec §9.5). Un texto opcional vacío queda en null.</summary>
public sealed record PublishLegalTextsRequest(string? AuthorizationText, string? PolicyText, string? PolicyUrl, string? TermsText);

/// <param name="ExpectedVersion">La versión vigente que la pantalla cargó (0 si no hay ninguna publicada); llega por <c>If-Match</c>.</param>
public sealed record PublishLegalTextsCommand(
    Guid TenantId,
    string? AuthorizationText,
    string? PolicyText,
    string? PolicyUrl,
    string? TermsText,
    long ExpectedVersion)
    : ICommand<SupportLegalTextsDto>
{
    public static PublishLegalTextsCommand From(Guid tenantId, PublishLegalTextsRequest request, long expectedVersion) =>
        new(tenantId, request.AuthorizationText, request.PolicyText, request.PolicyUrl, request.TermsText, expectedVersion);
}

/// <summary>Da el campo de cada límite; el dominio (<see cref="SupportLegalTextVersion.Create"/>) los repite. Que falte la política no
/// es de un campo: lo responde el dominio con su código.</summary>
public sealed class PublishLegalTextsValidator : AbstractValidator<PublishLegalTextsCommand>
{
    public PublishLegalTextsValidator()
    {
        RuleFor(command => command.AuthorizationText)
            .Must(text => !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= SupportLegalTextVersion.TextMaxLength)
            .WithMessage($"The authorization text must have between 1 and {SupportLegalTextVersion.TextMaxLength} characters.");
        RuleFor(command => command.PolicyText)
            .Must(AtMostTextMax)
            .WithMessage($"The policy text must have at most {SupportLegalTextVersion.TextMaxLength} characters.");
        RuleFor(command => command.PolicyUrl)
            .Must(url => string.IsNullOrWhiteSpace(url) || SupportLegalTextVersion.IsValidPolicyUrl(url))
            .WithMessage(
                $"The policy URL must be an absolute https URL of at most {SupportLegalTextVersion.PolicyUrlMaxLength} characters.");
        RuleFor(command => command.TermsText)
            .Must(AtMostTextMax)
            .WithMessage($"The terms text must have at most {SupportLegalTextVersion.TextMaxLength} characters.");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0);
    }

    private static bool AtMostTextMax(string? text) => text is null || text.Trim().Length <= SupportLegalTextVersion.TextMaxLength;
}

/// <summary>
/// Publica los textos legales (plan F16): cada publicación es la versión <c>n+1</c> con <c>If-Match: n</c>, y ninguna se edita ni se
/// borra, porque son prueba legal. Siempre crea versión, aunque el texto no cambie: publicar es una acción explícita. Con su
/// auditoría en la misma transacción. Si otro publicó la misma versión a la vez, el índice responde 409
/// <c>support.legal_texts.version_taken</c>.
/// </summary>
public sealed class PublishLegalTextsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportLegalTextRepository legalTexts,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<PublishLegalTextsCommand> validator)
    : ICommandHandler<PublishLegalTextsCommand, SupportLegalTextsDto>
{
    private static readonly string[] ChangedFields = ["authorizationText", "policyText", "policyUrl", "termsText"];

    public async Task<SupportLegalTextsDto> HandleAsync(PublishLegalTextsCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var currentVersion = (await legalTexts.CurrentAsync(command.TenantId, cancellationToken))?.Version ?? 0;
        if (currentVersion != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The legal texts changed after they were loaded.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var text = SupportLegalTextVersion.Create(
            Guid.CreateVersion7(),
            command.TenantId,
            currentVersion + 1,
            command.AuthorizationText!,
            command.PolicyText,
            command.PolicyUrl,
            command.TermsText,
            actor,
            now);

        legalTexts.Add(text);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.LegalTextsPublished,
            SupportAuditActions.Resources.LegalTexts,
            command.TenantId.ToString(),
            "success",
            ChangedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await SupportLegalTexts.ToDtoAsync(text, members, cancellationToken);
    }
}

using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Los seis interruptores son obligatorios (null es un 422 en el campo, no «sin cambio»): el formulario manda la
/// configuración completa. Un texto vacío o con sólo espacios queda en null. <c>AllowAttachments</c> dice si la página ofrece
/// adjuntos (spec §9.3).</summary>
public sealed record UpdateFormSettingsRequest(
    bool? IsPublished,
    bool? ShowDocument,
    bool? ShowAddress,
    bool? ShowLandline,
    bool? BusinessNameRequired,
    bool? AllowAttachments,
    string? AccentColor,
    string? IntroText,
    string? SuccessMessage);

/// <param name="ExpectedVersion">La versión vigente que la pantalla cargó (0 sin filas); llega por <c>If-Match</c>.</param>
public sealed record UpdateFormSettingsCommand(
    Guid TenantId,
    bool? IsPublished,
    bool? ShowDocument,
    bool? ShowAddress,
    bool? ShowLandline,
    bool? BusinessNameRequired,
    bool? AllowAttachments,
    string? AccentColor,
    string? IntroText,
    string? SuccessMessage,
    long ExpectedVersion)
    : ICommand<SupportFormSettingsDto>
{
    public static UpdateFormSettingsCommand From(Guid tenantId, UpdateFormSettingsRequest request, long expectedVersion) =>
        new(
            tenantId,
            request.IsPublished,
            request.ShowDocument,
            request.ShowAddress,
            request.ShowLandline,
            request.BusinessNameRequired,
            request.AllowAttachments,
            request.AccentColor,
            request.IntroText,
            request.SuccessMessage,
            expectedVersion);
}

/// <summary>Da el campo de cada regla; el dominio (<see cref="SupportFormVersion.Create"/>) la repite. Los largos se miden sobre el
/// texto recortado, que es lo que se guarda.</summary>
public sealed class UpdateFormSettingsValidator : AbstractValidator<UpdateFormSettingsCommand>
{
    public UpdateFormSettingsValidator()
    {
        RuleFor(command => command.IsPublished).NotNull();
        RuleFor(command => command.ShowDocument).NotNull();
        RuleFor(command => command.ShowAddress).NotNull();
        RuleFor(command => command.ShowLandline).NotNull();
        RuleFor(command => command.BusinessNameRequired).NotNull();
        RuleFor(command => command.AllowAttachments).NotNull();
        RuleFor(command => command.AccentColor)
            .Must(color => string.IsNullOrWhiteSpace(color) || SupportFormVersion.IsValidAccentColor(color.Trim()))
            .WithMessage("The accent color must be '#' followed by six hexadecimal digits.");
        RuleFor(command => command.IntroText)
            .Must(text => AtMost(text, SupportFormVersion.IntroTextMaxLength))
            .WithMessage($"The intro text must have at most {SupportFormVersion.IntroTextMaxLength} characters.");
        RuleFor(command => command.SuccessMessage)
            .Must(text => AtMost(text, SupportFormVersion.SuccessMessageMaxLength))
            .WithMessage($"The success message must have at most {SupportFormVersion.SuccessMessageMaxLength} characters.");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0);
    }

    private static bool AtMost(string? text, int maxLength) => text is null || text.Trim().Length <= maxLength;
}

/// <summary>
/// Guarda la configuración del formulario público (plan F14): crea la versión <c>n+1</c> con <c>If-Match: n</c> y no toca la anterior,
/// con su auditoría en la misma transacción. Sin cambios frente a la vigente no crea versión ni audita. Publicar con un motivo pendiente
/// (<see cref="SupportFormSettingsDto.PublishBlockers"/>) desde un formulario sin publicar responde 422 con su código: <c>support.form.legal_texts_required</c> (plan F15)
/// o <c>support.form.no_published_types</c>. Si otro guardó la misma versión a la vez, el índice responde 409
/// <c>support.form.version_taken</c>.
/// </summary>
public sealed class UpdateFormSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportFormRepository forms,
    ISupportLegalTextRepository legalTexts,
    ISupportTicketTypeRepository ticketTypes,
    ITenantDirectory tenantDirectory,
    ISupportPublicOptions publicOptions,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<UpdateFormSettingsCommand> validator)
    : ICommandHandler<UpdateFormSettingsCommand, SupportFormSettingsDto>
{
    public async Task<SupportFormSettingsDto> HandleAsync(UpdateFormSettingsCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var current = await forms.CurrentAsync(command.TenantId, cancellationToken) ?? SupportFormVersion.Default(command.TenantId);
        if (current.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The public form changed after it was loaded.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var candidate = SupportFormVersion.Create(
            Guid.CreateVersion7(),
            command.TenantId,
            current.Version + 1,
            command.IsPublished!.Value,
            command.ShowDocument!.Value,
            command.ShowAddress!.Value,
            command.ShowLandline!.Value,
            command.BusinessNameRequired!.Value,
            command.AllowAttachments!.Value,
            command.AccentColor,
            command.IntroText,
            command.SuccessMessage,
            actor,
            now);
        var blockers = await SupportFormSettings.BlockersAsync(legalTexts, ticketTypes, command.TenantId, cancellationToken);

        var changedFields = ChangedFields(current, candidate);
        if (changedFields.Count == 0)
        {
            return await SupportFormSettings.ToDtoAsync(current, blockers, tenantDirectory, publicOptions, cancellationToken);
        }

        // Sólo frena el paso de sin publicar a publicado: un formulario ya publicado cuyos tipos se despublicaron después se sigue
        // pudiendo editar (el motivo sale en PublishBlockers), o el tenant tendría que despublicarlo para cambiar un texto.
        if (!current.IsPublished && candidate.IsPublished && blockers.Count > 0)
        {
            throw new SupportDomainException(
                SupportFormSettings.ErrorCodeOf(blockers[0]), $"The public form cannot be published yet: {blockers[0]}.");
        }

        forms.Add(candidate);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.FormUpdated,
            SupportAuditActions.Resources.PublicForm,
            command.TenantId.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await SupportFormSettings.ToDtoAsync(candidate, blockers, tenantDirectory, publicOptions, cancellationToken);
    }

    private static List<string> ChangedFields(SupportFormVersion current, SupportFormVersion candidate)
    {
        var changed = new List<string>();
        Add(changed, "isPublished", current.IsPublished != candidate.IsPublished);
        Add(changed, "showDocument", current.ShowDocument != candidate.ShowDocument);
        Add(changed, "showAddress", current.ShowAddress != candidate.ShowAddress);
        Add(changed, "showLandline", current.ShowLandline != candidate.ShowLandline);
        Add(changed, "businessNameRequired", current.BusinessNameRequired != candidate.BusinessNameRequired);
        Add(changed, "allowAttachments", current.AllowAttachments != candidate.AllowAttachments);
        Add(changed, "accentColor", !string.Equals(current.AccentColor, candidate.AccentColor, StringComparison.Ordinal));
        Add(changed, "introText", !string.Equals(current.IntroText, candidate.IntroText, StringComparison.Ordinal));
        Add(changed, "successMessage", !string.Equals(current.SuccessMessage, candidate.SuccessMessage, StringComparison.Ordinal));
        return changed;
    }

    private static void Add(List<string> changed, string field, bool differs)
    {
        if (differs)
        {
            changed.Add(field);
        }
    }
}

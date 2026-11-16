using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

public sealed record GetFormSettingsQuery(Guid TenantId) : IQuery<SupportFormSettingsDto>;

/// <summary>La configuración vigente del formulario público (plan F14). Sin versiones, el formulario por defecto: sin publicar, todo
/// visible y la razón social obligatoria, con versión 0. La leen quienes leen los ajustes (<see cref="SupportPolicies.SettingsView"/>).</summary>
public sealed class GetFormSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportProvisioner provisioner,
    ISupportFormRepository forms,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportTicketTypeRepository ticketTypes,
    ITenantDirectory tenantDirectory,
    ISupportPublicOptions publicOptions)
    : IQueryHandler<GetFormSettingsQuery, SupportFormSettingsDto>
{
    public async Task<SupportFormSettingsDto> HandleAsync(GetFormSettingsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var form = await forms.CurrentAsync(query.TenantId, cancellationToken) ?? SupportFormVersion.Default(query.TenantId);
        var blockers = await SupportFormSettings.BlockersAsync(legalDocuments, ticketTypes, query.TenantId, cancellationToken);
        return await SupportFormSettings.ToDtoAsync(form, blockers, tenantDirectory, publicOptions, cancellationToken);
    }
}

/// <summary>Lo que comparten la lectura y el guardado del formulario: los motivos que impiden publicarlo y el armado del DTO.</summary>
internal static class SupportFormSettings
{
    /// <summary>Spec 2026-11-15 §7 (plan L18): without a published version of each legal document the form cannot be published. The
    /// three are required because the form always offers the anonymous path and the terms apply to both.</summary>
    public const string PrivacyNoticeRequired = "privacy_notice_required";

    public const string AnonymousNoticeRequired = "anonymous_notice_required";

    public const string TermsRequired = "terms_required";

    /// <summary>Sin un tipo activo y publicado, la persona no tendría qué elegir al radicar.</summary>
    public const string NoPublishedTypes = "no_published_types";

    public static async Task<IReadOnlyList<string>> BlockersAsync(
        ISupportLegalDocumentRepository legalDocuments,
        ISupportTicketTypeRepository ticketTypes,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var current = await legalDocuments.CurrentAllAsync(tenantId, cancellationToken);
        foreach (var kind in SupportLegalDocumentKinds.All.Where(kind => !current.ContainsKey(kind)))
        {
            blockers.Add(kind switch
            {
                SupportLegalDocumentKind.PrivacyNotice => PrivacyNoticeRequired,
                SupportLegalDocumentKind.AnonymousNotice => AnonymousNoticeRequired,
                _ => TermsRequired,
            });
        }

        if (!(await ticketTypes.ListAsync(tenantId, cancellationToken)).Any(type => type.IsActive && type.IsPublished))
        {
            blockers.Add(NoPublishedTypes);
        }

        return blockers;
    }

    /// <summary>El código de error de un motivo: publicar con él pendiente responde 422 con ese código. Los tres documentos comparten
    /// <c>support.form.legal_texts_required</c> (spec 2026-11-15 §7): el código no cambia, la lista dice cuál falta.</summary>
    public static string ErrorCodeOf(string blocker) =>
        blocker == NoPublishedTypes ? SupportErrorCodes.FormNoPublishedTypes : SupportErrorCodes.FormLegalTextsRequired;

    public static async Task<SupportFormSettingsDto> ToDtoAsync(
        SupportFormVersion form,
        IReadOnlyList<string> blockers,
        ITenantDirectory tenantDirectory,
        ISupportPublicOptions publicOptions,
        CancellationToken cancellationToken)
    {
        // Un tenant sin fila (sólo el simulado del stub) no tiene slug: el enlace queda en la raíz del host.
        var slug = await tenantDirectory.GetSlugAsync(new TenantId(form.TenantId), cancellationToken);
        var publicUrl = slug is null ? publicOptions.BaseUrl : $"{publicOptions.BaseUrl}/{slug}";
        return new SupportFormSettingsDto(
            form.Version,
            form.IsPublished,
            form.ShowDocument,
            form.ShowAddress,
            form.ShowLandline,
            form.BusinessNameRequired,
            form.AllowAttachments,
            form.AccentColor,
            form.IntroText,
            form.SuccessMessage,
            publicUrl,
            blockers);
    }
}

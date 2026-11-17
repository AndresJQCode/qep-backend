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
    ISupportOrganizationDirectory organizations,
    ITenantDirectory tenantDirectory,
    ISupportPublicOptions publicOptions)
    : IQueryHandler<GetFormSettingsQuery, SupportFormSettingsDto>
{
    public async Task<SupportFormSettingsDto> HandleAsync(GetFormSettingsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var form = await forms.CurrentAsync(query.TenantId, cancellationToken) ?? SupportFormVersion.Default(query.TenantId);
        var organization = await SupportFormSettings.OrganizationOfAsync(organizations, form, cancellationToken);
        var blockers = await SupportFormSettings.BlockersAsync(
            legalDocuments, ticketTypes, form, organization, cancellationToken);
        return await SupportFormSettings.ToDtoAsync(
            form, blockers, organization, organizations, tenantDirectory, publicOptions, cancellationToken);
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

    /// <summary>Spec 2026-11-15 §9.3 and §15 D-8 (plan L32): the form does not publish without the company that answers.</summary>
    public const string OrganizationRequired = "organization_required";

    /// <summary>Plan L32: the chosen company was deactivated after it was chosen.</summary>
    public const string OrganizationInactive = "organization_inactive";

    /// <summary>Sin un tipo activo y publicado, la persona no tendría qué elegir al radicar.</summary>
    public const string NoPublishedTypes = "no_published_types";

    /// <summary>The company the version chose, active or not; null if none, or if it is not a company of the tenant.</summary>
    public static async Task<SupportOrganizationRef?> OrganizationOfAsync(
        ISupportOrganizationDirectory organizations, SupportFormVersion form, CancellationToken cancellationToken) =>
        form.OrganizationCompanyId is { } companyId
            ? await organizations.FindAsync(form.TenantId, companyId, cancellationToken)
            : null;

    /// <summary>The reasons the version <paramref name="form"/> cannot be published, in order: the legal documents, the company (D-8)
    /// and the published types. <paramref name="organization"/> is what <see cref="OrganizationOfAsync"/> read for that version.</summary>
    public static async Task<IReadOnlyList<string>> BlockersAsync(
        ISupportLegalDocumentRepository legalDocuments,
        ISupportTicketTypeRepository ticketTypes,
        SupportFormVersion form,
        SupportOrganizationRef? organization,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var current = await legalDocuments.CurrentAllAsync(form.TenantId, cancellationToken);
        foreach (var kind in SupportLegalDocumentKinds.All.Where(kind => !current.ContainsKey(kind)))
        {
            blockers.Add(kind switch
            {
                SupportLegalDocumentKind.PrivacyNotice => PrivacyNoticeRequired,
                SupportLegalDocumentKind.AnonymousNotice => AnonymousNoticeRequired,
                _ => TermsRequired,
            });
        }

        // A company id that no longer resolves for the tenant cannot happen (the FK forbids deleting it and the PUT only takes the
        // tenant's own): if it ever did, it counts as no company.
        if (organization is null)
        {
            blockers.Add(OrganizationRequired);
        }
        else if (!organization.IsActive)
        {
            blockers.Add(OrganizationInactive);
        }

        if (!(await ticketTypes.ListAsync(form.TenantId, cancellationToken)).Any(type => type.IsActive && type.IsPublished))
        {
            blockers.Add(NoPublishedTypes);
        }

        return blockers;
    }

    /// <summary>El código de error de un motivo: publicar con él pendiente responde 422 con ese código. Los tres documentos comparten
    /// <c>support.form.legal_texts_required</c> (spec 2026-11-15 §7): el código no cambia, la lista dice cuál falta.</summary>
    public static string ErrorCodeOf(string blocker) => blocker switch
    {
        NoPublishedTypes => SupportErrorCodes.FormNoPublishedTypes,
        OrganizationRequired => SupportErrorCodes.FormOrganizationRequired,
        OrganizationInactive => SupportErrorCodes.FormOrganizationInactive,
        _ => SupportErrorCodes.FormLegalTextsRequired,
    };

    public static async Task<SupportFormSettingsDto> ToDtoAsync(
        SupportFormVersion form,
        IReadOnlyList<string> blockers,
        SupportOrganizationRef? organization,
        ISupportOrganizationDirectory organizations,
        ITenantDirectory tenantDirectory,
        ISupportPublicOptions publicOptions,
        CancellationToken cancellationToken)
    {
        // Un tenant sin fila (sólo el simulado del stub) no tiene slug: el enlace queda en la raíz del host.
        var slug = await tenantDirectory.GetSlugAsync(new TenantId(form.TenantId), cancellationToken);
        var publicUrl = slug is null ? publicOptions.BaseUrl : $"{publicOptions.BaseUrl}/{slug}";
        var options = await organizations.ListActiveAsync(form.TenantId, cancellationToken);
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
            blockers,
            organization is null ? null : ToDto(organization),
            options.Select(ToDto).ToList());
    }

    private static SupportFormOrganizationDto ToDto(SupportOrganizationRef organization) =>
        new(organization.Id, organization.Name, organization.DocumentTypeCode, organization.DocumentNumber, organization.IsActive);
}

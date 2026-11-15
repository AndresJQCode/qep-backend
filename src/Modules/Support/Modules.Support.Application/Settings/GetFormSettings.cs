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
    ISupportLegalTextRepository legalTexts,
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
        var blockers = await SupportFormSettings.BlockersAsync(legalTexts, ticketTypes, query.TenantId, cancellationToken);
        return await SupportFormSettings.ToDtoAsync(form, blockers, tenantDirectory, publicOptions, cancellationToken);
    }
}

/// <summary>Lo que comparten la lectura y el guardado del formulario: los motivos que impiden publicarlo y el armado del DTO.</summary>
internal static class SupportFormSettings
{
    /// <summary>Sin una versión de los textos legales no se puede publicar (plan F15).</summary>
    public const string LegalTextsRequired = "legal_texts_required";

    /// <summary>Sin un tipo activo y publicado, la persona no tendría qué elegir al radicar.</summary>
    public const string NoPublishedTypes = "no_published_types";

    public static async Task<IReadOnlyList<string>> BlockersAsync(
        ISupportLegalTextRepository legalTexts,
        ISupportTicketTypeRepository ticketTypes,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        if (await legalTexts.CurrentAsync(tenantId, cancellationToken) is null)
        {
            blockers.Add(LegalTextsRequired);
        }

        if (!(await ticketTypes.ListAsync(tenantId, cancellationToken)).Any(type => type.IsActive && type.IsPublished))
        {
            blockers.Add(NoPublishedTypes);
        }

        return blockers;
    }

    /// <summary>El código de error de un motivo: publicar con él pendiente responde 422 con ese código.</summary>
    public static string ErrorCodeOf(string blocker) =>
        blocker == LegalTextsRequired ? SupportErrorCodes.FormLegalTextsRequired : SupportErrorCodes.FormNoPublishedTypes;

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
            form.AccentColor,
            form.IntroText,
            form.SuccessMessage,
            publicUrl,
            blockers);
    }
}

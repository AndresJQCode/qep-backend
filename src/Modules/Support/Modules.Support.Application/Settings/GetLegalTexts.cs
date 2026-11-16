using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record GetLegalTextsQuery(Guid TenantId) : IQuery<SupportLegalTextsDto>;

/// <summary>La versión vigente de los textos legales (plan F16). Sin ninguna publicada, el texto de ejemplo de QEP con
/// <c>isExample</c> en verdadero, que no se guarda. La leen quienes leen los ajustes (<see cref="SupportPolicies.SettingsView"/>).</summary>
public sealed class GetLegalTextsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportLegalTextRepository legalTexts,
    ISupportMemberDirectory members,
    ISupportViewer viewer)
    : IQueryHandler<GetLegalTextsQuery, SupportLegalTextsDto>
{
    public async Task<SupportLegalTextsDto> HandleAsync(GetLegalTextsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var text = await legalTexts.CurrentAsync(query.TenantId, cancellationToken) ?? SupportLegalTextVersion.Example(query.TenantId);
        return await SupportLegalTexts.ToDtoAsync(text, members, viewer, cancellationToken);
    }
}

internal static class SupportLegalTexts
{
    /// <summary>El ejemplo no tiene autor ni fecha. Un autor que ya no se encuentra sale como «Sin nombre», igual que en la traza; el
    /// autor lleva <c>IsMe</c> contra quien pide (fase 4a, plan G8).</summary>
    public static async Task<SupportLegalTextsDto> ToDtoAsync(
        SupportLegalTextVersion text, ISupportMemberDirectory members, ISupportViewer viewer, CancellationToken cancellationToken)
    {
        SupportMemberDto? createdBy = null;
        if (!text.IsExample)
        {
            var names = await members.NamesAsync(text.TenantId, [text.CreatedBy], cancellationToken);
            createdBy = SupportTicketDtoComposer.ToMemberDto(
                text.CreatedBy, names, await viewer.MemberIdAsync(text.TenantId, cancellationToken));
        }

        return new SupportLegalTextsDto(
            text.Version,
            text.IsExample,
            text.AuthorizationText,
            text.PolicyText,
            text.PolicyUrl,
            text.TermsText,
            text.IsExample ? null : text.CreatedAt,
            createdBy);
    }
}

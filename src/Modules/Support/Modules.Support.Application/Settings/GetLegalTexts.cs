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
    ISupportMemberDirectory members)
    : IQueryHandler<GetLegalTextsQuery, SupportLegalTextsDto>
{
    public async Task<SupportLegalTextsDto> HandleAsync(GetLegalTextsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var text = await legalTexts.CurrentAsync(query.TenantId, cancellationToken) ?? SupportLegalTextVersion.Example(query.TenantId);
        return await SupportLegalTexts.ToDtoAsync(text, members, cancellationToken);
    }
}

internal static class SupportLegalTexts
{
    /// <summary>El ejemplo no tiene autor ni fecha. Un autor que ya no se encuentra sale como «Sin nombre», igual que en la traza.</summary>
    public static async Task<SupportLegalTextsDto> ToDtoAsync(
        SupportLegalTextVersion text, ISupportMemberDirectory members, CancellationToken cancellationToken)
    {
        SupportMemberDto? createdBy = null;
        if (!text.IsExample)
        {
            var names = await members.NamesAsync(text.TenantId, [text.CreatedBy], cancellationToken);
            createdBy = new SupportMemberDto(
                text.CreatedBy, names.TryGetValue(text.CreatedBy, out var name) ? name : SupportTicketDtoComposer.UnknownName);
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

using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetWhatsAppChannelQuery(Guid TenantId) : IQuery<WhatsAppChannelDto>;

/// <summary>
/// El canal antes de enviar (spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 1).
/// Permiso de enviar y no de Configuración, porque los roles son editables. Sin gate de capacidad:
/// <c>quotations.quotation.manage</c> ya cae con el enmascaramiento de entitlements.
/// </summary>
public sealed class GetWhatsAppChannelHandler(
    ITenantWhatsAppSettingsRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetWhatsAppChannelQuery, WhatsAppChannelDto>
{
    public async Task<WhatsAppChannelDto> HandleAsync(
        GetWhatsAppChannelQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationManage);

        var stored = await repository.FindReadOnlyAsync(query.TenantId, cancellationToken);
        var mode = stored?.Mode ?? WhatsAppMode.Shared;
        return new WhatsAppChannelDto(mode != WhatsAppMode.Disabled, mode.ToString());
    }
}

using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record HrDownloadUrlDto(string Url);

public sealed record GetHrFileDownloadUrlQuery(Guid TenantId, Guid FileId) : IQuery<HrDownloadUrlDto>;

/// <summary>La URL firmada (5 minutos) de un PDF de Hr. Sólo responde archivos <c>HrDocument</c> del tenant: otro es 404.</summary>
public sealed class GetHrFileDownloadUrlHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IHrDocumentStore store)
    : IQueryHandler<GetHrFileDownloadUrlQuery, HrDownloadUrlDto>
{
    public async Task<HrDownloadUrlDto> HandleAsync(GetHrFileDownloadUrlQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.ContractRead);

        var url = await store.CreateDownloadUrlAsync(query.TenantId, query.FileId, cancellationToken)
            ?? throw HrNotFound.File(query.FileId);
        return new HrDownloadUrlDto(url.AbsoluteUri);
    }
}

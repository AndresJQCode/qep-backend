using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

public sealed record GetCustomerCandidatesQuery(Guid TenantId, Guid RequestId) : IQuery<StoreCustomerCandidatesDto>;

public sealed class GetCustomerCandidatesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreOrderRequestRepository requests,
    IStoreCustomerDirectory customers)
    : IQueryHandler<GetCustomerCandidatesQuery, StoreCustomerCandidatesDto>
{
    public async Task<StoreCustomerCandidatesDto> HandleAsync(GetCustomerCandidatesQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.OrderManage, cancellationToken);

        var request = await requests.FindAsync(query.TenantId, query.RequestId, cancellationToken)
            ?? throw StoreNotFound.OrderRequest(query.RequestId);
        var billing = request.Billing;

        var candidates = await customers.FindCandidatesAsync(
            query.TenantId, billing.PhoneE164, billing.IdentificationType, billing.IdentificationNumber, cancellationToken);

        // Sólo se sugiere con un único cliente distinto: dos candidatos, o el teléfono y el documento apuntando a personas
        // distintas, lo dejan en null y la persona elige. Nunca se elige solo en un caso ambiguo.
        var suggested = candidates.Count == 1 ? candidates[0].Id : (Guid?)null;
        return new StoreCustomerCandidatesDto(
            candidates
                .Select(candidate => new StoreCustomerCandidateDto(
                    candidate.Id, candidate.Name, candidate.PhoneE164, candidate.IdentificationType, candidate.IdentificationNumber,
                    candidate.IsComplete, candidate.MatchedBy))
                .ToList(),
            suggested,
            new StoreNewCustomerPreviewDto(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email, billing.Address,
                billing.Place.Country, billing.Place.CityName, billing.Place.DepartmentName));
    }
}

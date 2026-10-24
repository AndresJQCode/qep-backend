using BuildingBlocks.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record GetSupplierQuery(Guid TenantId, Guid SupplierId) : IQuery<SupplierResponse>;

public sealed class GetSupplierHandler(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    IExecutionContext executionContext)
    : IQueryHandler<GetSupplierQuery, SupplierResponse>
{
    public async Task<SupplierResponse> HandleAsync(
        GetSupplierQuery query,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SuppliersPermissions.Read);

        var supplier = await repository.GetAsync(
            query.TenantId, new SupplierId(query.SupplierId), cancellationToken)
            ?? throw SupplierNotFound.For(query.SupplierId);

        return await supplier.ToResponseAsync(geographyLookup, cancellationToken);
    }
}

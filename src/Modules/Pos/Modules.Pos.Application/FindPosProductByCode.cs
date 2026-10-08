using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>El escaneo: el lector teclea Product.Code (no hay campo EAN, spec decisión 25).</summary>
public sealed record FindPosProductByCodeQuery(Guid TenantId, string Code) : IQuery<PosProductResponse>;

public sealed class FindPosProductByCodeValidator : AbstractValidator<FindPosProductByCodeQuery>
{
    public FindPosProductByCodeValidator()
    {
        RuleFor(query => query.Code).NotEmpty().Must(code => !string.IsNullOrWhiteSpace(code)).MaximumLength(60);
    }
}

public sealed class FindPosProductByCodeHandler(
    IPosProductLookup products,
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IExecutionContext executionContext,
    IValidator<FindPosProductByCodeQuery> validator)
    : IQueryHandler<FindPosProductByCodeQuery, PosProductResponse>
{
    public async Task<PosProductResponse> HandleAsync(
        FindPosProductByCodeQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        // 404 es correcto aquí: la búsqueda ya está acotada al tenant de la ruta.
        var product = await products.FindByCodeAsync(query.TenantId, query.Code, cancellationToken)
            ?? throw PosNotFound.ProductCode(query.Code);
        var currency = await PosSellingCurrency.ResolveAsync(
            sessions, membershipDirectory, tenantDefaultCurrency, executionContext, query.TenantId, cancellationToken);
        return PosProductMapping.ToResponse(product, currency);
    }
}

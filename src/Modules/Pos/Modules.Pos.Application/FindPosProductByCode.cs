using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// El escaneo (spec 2026-10-20 §5): el lector teclea un código de barras o el cajero un
/// <c>Code</c>. Se busca primero el código de barras de una variante y después su <c>Code</c>
/// (IPosProductLookup.FindByCodeAsync); responde la variante con su nombre visible.
/// </summary>
public sealed record FindPosProductByCodeQuery(Guid TenantId, string Code) : IQuery<PosProductResponse>;

public sealed class FindPosProductByCodeValidator : AbstractValidator<FindPosProductByCodeQuery>
{
    /// <summary>
    /// El mayor de los dos largos de Catalog: <c>ProductVariant.BarcodeMaxLength</c> (64) y
    /// <c>CodeMaxLength</c> (60), copiados y no referenciados porque Pos no depende de Catalog.
    /// Con 60, un código de barras válido de 61 a 64 caracteres daría 422 en vez de encontrarse.
    /// </summary>
    private const int ScannedCodeMaxLength = 64;

    public FindPosProductByCodeValidator()
    {
        RuleFor(query => query.Code).NotEmpty().Must(code => !string.IsNullOrWhiteSpace(code)).MaximumLength(ScannedCodeMaxLength);
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

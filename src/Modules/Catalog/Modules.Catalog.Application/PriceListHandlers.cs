using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

// Spec 2026-11-22 (listas de precios por canal): alta, edición, borrado y lectura de las listas. Todo con
// catalog.price_list.manage; el handler revalida tenant y permiso antes de tocar el repositorio (CatalogAuthorization).

internal static class PriceListAuditActions
{
    public const string Created = "catalog.price_list.created";
    public const string Updated = "catalog.price_list.updated";
    public const string Deleted = "catalog.price_list.deleted";
    public const string OverridesChanged = "catalog.price_list.overrides_changed";
    public const string ChannelPricesUpdated = "catalog.channel_prices.updated";
    public const string PriceListResource = "price_list";
    public const string ChannelPricesResource = "channel_prices";
}

public sealed record ListPriceListsQuery(Guid TenantId) : IQuery<PriceListsResponse>;

public sealed class ListPriceListsHandler(
    IExecutionContext executionContext,
    IPriceListRepository priceLists,
    IPriceListUsage usage,
    IProductCategoryRepository categories,
    IProductRepository products)
    : IQueryHandler<ListPriceListsQuery, PriceListsResponse>
{
    public async Task<PriceListsResponse> HandleAsync(ListPriceListsQuery query, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, query.TenantId, CatalogPermissions.PriceListManage);
        var lists = await priceLists.ListAsync(query.TenantId, cancellationToken);
        var overrides = await priceLists.CountOverridesAsync(query.TenantId, cancellationToken);
        var channels = await priceLists.ListChannelsAsync(query.TenantId, cancellationToken);
        var distributors = await usage.CountDistributorsByPriceListAsync(query.TenantId, cancellationToken);
        var names = await PriceListCatalogNames.LoadAsync(categories, products, query.TenantId, [], cancellationToken);

        return new PriceListsResponse(lists
            .OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(list => list.Id.Value)
            .Select(list =>
            {
                var listed = names.CategoriesOf(list);
                return new PriceListSummaryDto(
                    list.Id.Value,
                    list.Name,
                    list.AdjustmentPercent,
                    list.Visibility.ToString(),
                    listed.Count,
                    listed,
                    overrides.GetValueOrDefault(list.Id),
                    PriceListTerms.ChannelsOf(list, channels),
                    distributors.GetValueOrDefault(list.Id.Value),
                    list.Version,
                    list.CreatedAt,
                    list.ShowPrices,
                    list.ExcludedCategoryIds.Count,
                    list.ExcludedProductIds.Count);
            })
            .ToList());
    }
}

public sealed record GetPriceListQuery(Guid TenantId, Guid PriceListId) : IQuery<PriceListDetailDto>;

public sealed class GetPriceListHandler(
    IExecutionContext executionContext,
    IPriceListRepository priceLists,
    IPriceListUsage usage,
    IProductCategoryRepository categories,
    IProductRepository products)
    : IQueryHandler<GetPriceListQuery, PriceListDetailDto>
{
    public async Task<PriceListDetailDto> HandleAsync(GetPriceListQuery query, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, query.TenantId, CatalogPermissions.PriceListManage);
        var list = await priceLists.GetAsync(query.TenantId, new PriceListId(query.PriceListId), cancellationToken)
            ?? throw PriceListTerms.NotFound(query.PriceListId);

        return await PriceListResponses.DetailAsync(
            list, priceLists, usage, categories, products, cancellationToken);
    }
}

public sealed record CreatePriceListCommand(
    Guid TenantId,
    string Name,
    decimal AdjustmentPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds,
    bool? ShowPrices = null,
    IReadOnlyList<PriceListExclusionInput>? Exclusions = null)
    : ICommand<PriceListDetailDto>, IPriceListTermsCommand;

public sealed class CreatePriceListValidator : AbstractValidator<CreatePriceListCommand>
{
    public CreatePriceListValidator() => Include(new PriceListWriteRules());
}

/// <summary>Orden: 403 → 422 de validación → 409 nombre → 422 categorías y exclusiones.</summary>
public sealed class CreatePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IProductCategoryRepository categories,
    IProductRepository products,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IValidator<CreatePriceListCommand> validator)
    : ICommandHandler<CreatePriceListCommand, PriceListDetailDto>
{
    public async Task<PriceListDetailDto> HandleAsync(CreatePriceListCommand command, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, command.TenantId, CatalogPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var name = command.Name.Trim();
        if (await priceLists.NameExistsAsync(command.TenantId, name, null, cancellationToken))
        {
            throw PriceListTerms.NameTaken();
        }

        var names = await PriceListTerms.RequireReferencesAsync(categories, products, command, cancellationToken);
        var now = clock.UtcNow;
        var list = PriceList.Create(
            command.TenantId, name, command.AdjustmentPercent, PriceListTerms.VisibilityOf(command),
            PriceListTerms.CategoryIdsOf(command), now, command.ShowPrices ?? true, PriceListTerms.ExclusionsOf(command));
        priceLists.Add(list);
        auditPublisher.PublishResource(
            command.TenantId, executionContext.SubjectId, PriceListAuditActions.Created, PriceListAuditActions.PriceListResource,
            list.Id.ToString(), PriceListTerms.WrittenFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PriceListTerms.ToDetail(list, names, overrideCount: 0, channels: [], distributorCount: 0);
    }
}

/// <param name="ShowPrices"><c>null</c> conserva el valor guardado (D-2 del spec 2026-11-19).</param>
public sealed record UpdatePriceListCommand(
    Guid TenantId,
    Guid PriceListId,
    string Name,
    decimal AdjustmentPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds,
    long ExpectedVersion,
    bool? ShowPrices = null,
    IReadOnlyList<PriceListExclusionInput>? Exclusions = null)
    : ICommand<PriceListDetailDto>, IPriceListTermsCommand;

public sealed class UpdatePriceListValidator : AbstractValidator<UpdatePriceListCommand>
{
    public UpdatePriceListValidator() => Include(new PriceListWriteRules());
}

public sealed class UpdatePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IPriceListUsage usage,
    IProductCategoryRepository categories,
    IProductRepository products,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IValidator<UpdatePriceListCommand> validator)
    : ICommandHandler<UpdatePriceListCommand, PriceListDetailDto>
{
    public async Task<PriceListDetailDto> HandleAsync(UpdatePriceListCommand command, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, command.TenantId, CatalogPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var id = new PriceListId(command.PriceListId);
        var list = await priceLists.GetAsync(command.TenantId, id, cancellationToken)
            ?? throw PriceListTerms.NotFound(command.PriceListId);
        PriceListTerms.EnsureVersion(list, command.ExpectedVersion);

        var name = command.Name.Trim();
        if (await priceLists.NameExistsAsync(command.TenantId, name, id, cancellationToken))
        {
            throw PriceListTerms.NameTaken();
        }

        await PriceListTerms.RequireReferencesAsync(categories, products, command, cancellationToken);
        var now = clock.UtcNow;
        list.Update(
            name, command.AdjustmentPercent, PriceListTerms.VisibilityOf(command), PriceListTerms.CategoryIdsOf(command), now,
            command.ShowPrices ?? list.ShowPrices, PriceListTerms.ExclusionsOf(command));
        auditPublisher.PublishResource(
            command.TenantId, executionContext.SubjectId, PriceListAuditActions.Updated, PriceListAuditActions.PriceListResource,
            list.Id.ToString(), PriceListTerms.WrittenFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await PriceListResponses.DetailAsync(list, priceLists, usage, categories, products, cancellationToken);
    }
}

public sealed record DeletePriceListCommand(Guid TenantId, Guid PriceListId, long ExpectedVersion) : ICommand<bool>;

/// <summary>
/// Borra una lista que ningún canal ni distribuidor usa (409 <c>in_use</c>). La FK <c>RESTRICT</c> de
/// <c>channel_price_lists</c> cubre la carrera con «Precios por canal» (la unidad de trabajo la traduce al mismo 409). Contra una
/// invitación de distribuidor simultánea no hay FK (otro esquema): ese distribuidor queda sin lista y ve el catálogo cerrado
/// (D-8), nunca un precio equivocado.
/// </summary>
public sealed class DeletePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IPriceListUsage usage,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher)
    : ICommandHandler<DeletePriceListCommand, bool>
{
    public async Task<bool> HandleAsync(DeletePriceListCommand command, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, command.TenantId, CatalogPermissions.PriceListManage);
        var list = await priceLists.GetAsync(command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw PriceListTerms.NotFound(command.PriceListId);
        PriceListTerms.EnsureVersion(list, command.ExpectedVersion);

        var channels = await priceLists.ListChannelsAsync(command.TenantId, cancellationToken);
        var distributors = await usage.CountDistributorsByPriceListAsync(command.TenantId, cancellationToken);
        if (channels.Any(assignment => assignment.PriceListId == list.Id) || distributors.GetValueOrDefault(list.Id.Value) > 0)
        {
            throw PriceListTerms.InUse();
        }

        // Los precios fijos se van por CASCADE.
        priceLists.Remove(list);
        auditPublisher.PublishResource(
            command.TenantId, executionContext.SubjectId, PriceListAuditActions.Deleted, PriceListAuditActions.PriceListResource,
            list.Id.ToString(), [], clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

internal static class PriceListResponses
{
    public static async Task<PriceListDetailDto> DetailAsync(
        PriceList list,
        IPriceListRepository priceLists,
        IPriceListUsage usage,
        IProductCategoryRepository categories,
        IProductRepository products,
        CancellationToken cancellationToken)
    {
        var overrides = await priceLists.CountOverridesAsync(list.TenantId, cancellationToken);
        var channels = await priceLists.ListChannelsAsync(list.TenantId, cancellationToken);
        var distributors = await usage.CountDistributorsByPriceListAsync(list.TenantId, cancellationToken);
        var names = await PriceListCatalogNames.LoadAsync(
            categories, products, list.TenantId, list.ExcludedProductIds, cancellationToken);

        return PriceListTerms.ToDetail(
            list,
            names,
            overrides.GetValueOrDefault(list.Id),
            PriceListTerms.ChannelsOf(list, channels),
            distributors.GetValueOrDefault(list.Id.Value));
    }
}

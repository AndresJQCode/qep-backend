using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record UpdatePriceListCommand(
    Guid TenantId,
    Guid PriceListId,
    long ExpectedVersion,
    string Name,
    decimal DiscountPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds)
    : ICommand<PriceListDetailDto>, IPriceListTermsCommand;

/// <summary>
/// Reemplaza los términos de la lista. Orden: 403 → 422 de validación → 404 → 412 → 409 nombre → 422 categorías. Los
/// distribuidores que la usan ven el cambio de inmediato: la lista no se copia a la relación.
/// </summary>
public sealed class UpdatePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IDistributorUsage usage,
    IWholesaleCatalogReader catalog,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IValidator<UpdatePriceListCommand> validator)
    : ICommandHandler<UpdatePriceListCommand, PriceListDetailDto>
{
    public async Task<PriceListDetailDto> HandleAsync(UpdatePriceListCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var list = await priceLists.GetAsync(command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw WholesaleConflicts.PriceListNotFound(command.PriceListId);
        WholesaleConflicts.EnsureVersion(list.Version, command.ExpectedVersion);

        var name = command.Name.Trim();
        if (await priceLists.NameExistsAsync(command.TenantId, name, list.Id, cancellationToken))
        {
            throw WholesaleConflicts.PriceListNameTaken();
        }

        var categoryIds = PriceListTerms.CategoryIdsOf(command);
        var categories = await PriceListTerms.RequireCategoriesAsync(catalog, command.TenantId, categoryIds, cancellationToken);

        var now = clock.UtcNow;
        list.Update(name, command.DiscountPercent, PriceListTerms.VisibilityOf(command), categoryIds, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.PriceListUpdated,
            WholesaleAuditActions.Resources.PriceList,
            list.Id.ToString(),
            "success",
            CreatePriceListHandler.WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var counts = await usage.CountByPriceListAsync(command.TenantId, cancellationToken);
        return PriceListMapping.ToDetail(list, categories, counts.GetValueOrDefault(list.Id));
    }
}

using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record CreatePriceListCommand(
    Guid TenantId,
    string Name,
    decimal DiscountPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds,
    bool? ShowPrices = null,
    IReadOnlyList<PriceListExclusionInput>? Exclusions = null)
    : ICommand<PriceListDetailDto>, IPriceListTermsCommand;

/// <summary>Crea una lista. Orden: 403 → 422 de validación → 409 nombre → 422 categorías y exclusiones.</summary>
public sealed class CreatePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IWholesaleCatalogReader catalog,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IValidator<CreatePriceListCommand> validator)
    : ICommandHandler<CreatePriceListCommand, PriceListDetailDto>
{
    internal static readonly string[] WrittenFields = ["name", "discountPercent", "visibility", "categoryIds", "showPrices", "exclusions"];

    public async Task<PriceListDetailDto> HandleAsync(CreatePriceListCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var name = command.Name.Trim();
        if (await priceLists.NameExistsAsync(command.TenantId, name, null, cancellationToken))
        {
            throw WholesaleConflicts.PriceListNameTaken();
        }

        var categoryIds = PriceListTerms.CategoryIdsOf(command);
        var categories = await PriceListTerms.RequireCategoriesAsync(catalog, command.TenantId, categoryIds, cancellationToken);
        await PriceListTerms.RequireExclusionsAsync(catalog, command.TenantId, command, cancellationToken);

        var now = clock.UtcNow;
        var list = PriceList.Create(
            command.TenantId, name, command.DiscountPercent, PriceListTerms.VisibilityOf(command), categoryIds, now,
            command.ShowPrices ?? true, PriceListTerms.ExclusionsOf(command));
        priceLists.Add(list);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.PriceListCreated,
            WholesaleAuditActions.Resources.PriceList,
            list.Id.ToString(),
            "success",
            WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var exclusions = await PriceListTerms.ExclusionDtosAsync(catalog, command.TenantId, list, cancellationToken);
        return PriceListMapping.ToDetail(list, categories, distributorCount: 0, exclusions);
    }
}

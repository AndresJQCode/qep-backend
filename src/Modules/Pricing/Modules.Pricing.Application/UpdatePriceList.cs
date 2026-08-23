using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record UpdatePriceListCommand(
    Guid TenantId,
    Guid PriceListId,
    string Name,
    string Prefix) : ICommand<PriceListDto>;

public sealed class UpdatePriceListValidator : AbstractValidator<UpdatePriceListCommand>
{
    public UpdatePriceListValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(PriceList.NameMaxLength);
        RuleFor(command => command.Prefix)
            .NotEmpty()
            .MaximumLength(PriceList.PrefixMaxLength);
    }
}

public sealed class UpdatePriceListHandler(
    IPriceListRepository repository,
    IPricingUnitOfWork unitOfWork,
    IPricingAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdatePriceListCommand> validator)
    : ICommandHandler<UpdatePriceListCommand, PriceListDto>
{
    public async Task<PriceListDto> HandleAsync(
        UpdatePriceListCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreatePriceListHandler.
        PricingAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PricingPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var priceList = await repository.FindAsync(
            command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw PriceListNotFound.For(command.PriceListId);

        var now = clock.UtcNow;
        priceList.Update(command.Name, command.Prefix, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "pricing.price_list.updated",
            priceList.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return priceList.ToDto();
    }
}

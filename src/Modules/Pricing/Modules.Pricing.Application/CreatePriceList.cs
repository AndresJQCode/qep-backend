using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record CreatePriceListCommand(Guid TenantId, string Name, string Prefix)
    : ICommand<PriceListDto>;

// El dominio hace cumplir las mismas reglas y tiraria un 422 con un solo codigo. El validador
// existe para que la respuesta lleve el mapa de errores por campo que ApiExceptionHandler arma
// desde ValidationException, que es lo que un formulario necesita para marcar el input culpable.
public sealed class CreatePriceListValidator : AbstractValidator<CreatePriceListCommand>
{
    public CreatePriceListValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(PriceList.NameMaxLength);
        RuleFor(command => command.Prefix)
            .NotEmpty()
            .MaximumLength(PriceList.PrefixMaxLength);
    }
}

public sealed class CreatePriceListHandler(
    IPriceListRepository repository,
    IPricingUnitOfWork unitOfWork,
    IPricingAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreatePriceListCommand> validator)
    : ICommandHandler<CreatePriceListCommand, PriceListDto>
{
    public async Task<PriceListDto> HandleAsync(
        CreatePriceListCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al reves: quien tiene el permiso para otro tenant se
        // lleva un 403 antes de que el mapa de errores por campo le confirme la forma del
        // contrato. Mismo criterio que CreateClientClassificationHandler.
        PricingAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PricingPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var priceList = PriceList.Create(
            PriceListId.New(), command.TenantId, command.Name, command.Prefix, now);

        repository.Add(priceList);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "pricing.price_list.created",
            priceList.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return priceList.ToDto();
    }
}

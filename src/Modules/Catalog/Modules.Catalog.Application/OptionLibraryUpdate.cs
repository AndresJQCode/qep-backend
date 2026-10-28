using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record UpdateOptionCommand(
    Guid TenantId,
    Guid OptionId,
    string Name,
    bool? IsActive,
    IReadOnlyList<OptionValueRequest> Values) : ICommand<OptionResponse>;

public sealed class UpdateOptionValidator : AbstractValidator<UpdateOptionCommand>
{
    public UpdateOptionValidator()
    {
        OptionLibraryRules.Name(RuleFor(command => command.Name));
        // Obligatorio, en la opción y en cada valor: el PUT reemplaza el estado, y un cuerpo que lo
        // omitiera desactivaría sin que nadie lo pidiera (mismo criterio que Values).
        RuleFor(command => command.IsActive)
            .NotNull()
            .WithErrorCode("catalog.option.active_required")
            .WithMessage("Indica si la opción está activa.");
        RuleForEach(command => command.Values)
            .ChildRules(value => value.RuleFor(item => item.IsActive)
                .NotNull()
                .WithErrorCode("catalog.option.value_active_required")
                .WithMessage("Indica si el valor está activo."));
        // NotNull y no «null = vacío»: como el PUT reemplaza la lista, un cuerpo sin values
        // borraría todos los valores sin que nadie lo pidiera.
        RuleFor(command => command.Values)
            .NotNull()
            .Custom((values, context) => OptionLibraryRules.Values(
                (values ?? []).Select(value => value?.Name).ToArray(), "Values", ".Name", context));
    }
}

public sealed class UpdateOptionHandler(
    IOptionDefinitionRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateOptionCommand> validator)
    : ICommandHandler<UpdateOptionCommand, OptionResponse>
{
    public async Task<OptionResponse> HandleAsync(
        UpdateOptionCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var option = await repository.FindAsync(
            command.TenantId, new OptionDefinitionId(command.OptionId), cancellationToken)
            ?? throw OptionNotFound.For(command.OptionId);

        var values = command.Values
            .Select(value => new OptionValueInput(value.Id, value.Name, value.IsActive!.Value))
            .ToArray();

        // Un valor que alguna variante usa no se borra (spec §3.1). La FK RESTRICT lo frenaría
        // igual en el guardado y CatalogUnitOfWork lo traduciría al mismo 409; esta consulta es la
        // que responde en el caso normal sin llegar a la base.
        var usage = await repository.GetUsageAsync(command.TenantId, cancellationToken);
        if (option.RemovedValueIds(values).Any(usage.ValueIds.Contains))
        {
            throw OptionInUse.Create();
        }

        option.Rename(command.Name);
        var isActive = command.IsActive!.Value;
        if (option.IsActive != isActive)
        {
            option.SetActive(isActive);
        }

        option.ReplaceValues(values);

        var now = clock.UtcNow;
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.option.updated",
            option.Id.ToString(),
            "success",
            now);
        // UpdatedAt lo sella CatalogUnitOfWork: los mutadores del agregado no reciben la hora.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return option.ToResponse(usage);
    }
}

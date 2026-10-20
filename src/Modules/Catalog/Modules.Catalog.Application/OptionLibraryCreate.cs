using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateOptionCommand(Guid TenantId, string Name, IReadOnlyList<string> Values)
    : ICommand<OptionResponse>;

public sealed class CreateOptionValidator : AbstractValidator<CreateOptionCommand>
{
    public CreateOptionValidator()
    {
        OptionLibraryRules.Name(RuleFor(command => command.Name));
        RuleFor(command => command.Values)
            .NotNull()
            .Custom((values, context) =>
                OptionLibraryRules.Values(values ?? [], "Values", string.Empty, context));
    }
}

public sealed class CreateOptionHandler(
    IOptionDefinitionRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateOptionCommand> validator)
    : ICommandHandler<CreateOptionCommand, OptionResponse>
{
    public async Task<OptionResponse> HandleAsync(
        CreateOptionCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateTaxRateHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var option = OptionDefinition.Create(
            command.TenantId, command.Name, command.Values, isActive: true, now);

        repository.Add(option);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.option.created",
            option.Id.ToString(),
            "success",
            now);

        // El nombre repetido lo frena IX_option_definitions_tenant_name y CatalogUnitOfWork lo
        // traduce a catalog.option.name_taken, sin consulta previa: la consulta dejaría la carrera
        // abierta y el índice la cierra igual.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Una opción recién creada no la usa nadie.
        return option.ToResponse(new OptionUsage(new HashSet<Guid>(), new HashSet<Guid>()));
    }
}

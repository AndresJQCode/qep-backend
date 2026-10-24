using BuildingBlocks.Application;
using FluentValidation;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record CreateSupplierCommand(
    Guid TenantId,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string Country,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    int PaymentTermDays,
    string? Category,
    string? Notes,
    IReadOnlyList<SupplierBankAccountPayload> BankAccounts,
    IReadOnlyList<SupplierContactPayload> Contacts) : ICommand<SupplierResponse>, ISupplierWriteCommand
{
    // Se aplana el cuerpo en el comando (y no se anida como Body) para que las claves de errors
    // sean Name y BankAccounts[2].Currency, no Body.Name.
    public static CreateSupplierCommand From(Guid tenantId, SupplierWriteRequest request) => new(
        tenantId,
        request.Name,
        request.BusinessName,
        request.IdentificationType,
        request.IdentificationNumber,
        request.Country,
        request.CityId,
        request.CityName,
        request.Phone,
        request.Email,
        request.Address,
        request.PaymentTermDays,
        request.Category,
        request.Notes,
        request.BankAccounts,
        request.Contacts);
}

// Las reglas viven en SupplierWriteRules y se incluyen, no se copian: dos copias validan distinto
// el día que alguien corrige sólo una.
public sealed class CreateSupplierValidator : AbstractValidator<CreateSupplierCommand>
{
    public CreateSupplierValidator() => Include(new SupplierWriteRules());
}

public sealed class CreateSupplierHandler(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    ISuppliersUnitOfWork unitOfWork,
    ISuppliersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateSupplierCommand> validator,
    ISupplierPhoneNormalizer phoneNormalizer)
    : ICommandHandler<CreateSupplierCommand, SupplierResponse>
{
    public async Task<SupplierResponse> HandleAsync(
        CreateSupplierCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: la política del endpoint frena a quien no tiene el permiso,
        // pero no al que lo tiene en otro tenant. Validando primero, ese llamador ajeno recibiría el
        // mapa de errores por campo antes de que nadie le dijera que no.
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SuppliersPermissions.Manage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var city = await command.ResolveCityAsync(geographyLookup, cancellationToken);
        var now = clock.UtcNow;

        var supplier = Supplier.Create(command.TenantId, command.ToWriteData(), phoneNormalizer, now);

        repository.Add(supplier);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupplierAuditActions.Created,
            supplier.Id.ToString(),
            "success",
            now);

        // La unicidad del documento NO se comprueba con un SELECT previo: la arbitra el índice
        // único y SuppliersUnitOfWork traduce su violación a identification_taken.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return supplier.ToResponse(city);
    }
}

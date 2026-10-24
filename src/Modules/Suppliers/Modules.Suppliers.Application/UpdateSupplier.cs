using BuildingBlocks.Application;
using FluentValidation;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record UpdateSupplierCommand(
    Guid TenantId,
    Guid SupplierId,
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
    public static UpdateSupplierCommand From(Guid tenantId, Guid supplierId, SupplierWriteRequest request) => new(
        tenantId,
        supplierId,
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

public sealed class UpdateSupplierValidator : AbstractValidator<UpdateSupplierCommand>
{
    public UpdateSupplierValidator() => Include(new SupplierWriteRules());
}

// Editar un proveedor inactivo está permitido (decisión del spec): no hay guarda de estado aquí.
public sealed class UpdateSupplierHandler(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    ISuppliersUnitOfWork unitOfWork,
    ISuppliersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateSupplierCommand> validator,
    ISupplierPhoneNormalizer phoneNormalizer)
    : ICommandHandler<UpdateSupplierCommand, SupplierResponse>
{
    public async Task<SupplierResponse> HandleAsync(
        UpdateSupplierCommand command,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SuppliersPermissions.Manage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var supplier = await repository.GetAsync(
            command.TenantId, new SupplierId(command.SupplierId), cancellationToken)
            ?? throw SupplierNotFound.For(command.SupplierId);

        var city = await command.ResolveCityAsync(geographyLookup, cancellationToken);
        var now = clock.UtcNow;

        supplier.Update(command.ToWriteData(), phoneNormalizer, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupplierAuditActions.Updated,
            supplier.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return supplier.ToResponse(city);
    }
}

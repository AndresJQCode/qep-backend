using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-11-10 §4.1: vincula al cliente como contacto de una empresa, o lo desvincula con <c>CompanyId = null</c>.</summary>
public sealed record SetCustomerCompanyCommand(Guid TenantId, Guid CustomerId, Guid? CompanyId)
    : ICommand<CustomerDto>;

// Sin validador: el único campo es un id opcional. Las reglas (misma persona, activa, un solo nivel) las decide el agregado.
public sealed class SetCustomerCompanyHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetCustomerCompanyCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        SetCustomerCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio: consultar primero le confirma al llamador que el id existe.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);

        // Cada return o throw sin CommitAsync deshace la transacción al liberarla. El bloqueo dura lo que ella.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Las dos filas, en orden fijo y antes de leer nada: A→B y B→A a la vez se serializan, y la segunda ve el vínculo de la
        // primera y responde su 422 en vez de dejar dos niveles. Sin empresa sólo se bloquea el cliente.
        await repository.LockForCompanyLinkAsync(
            command.TenantId,
            command.CompanyId is { } lockedCompanyId
                ? [command.CustomerId, lockedCompanyId]
                : [command.CustomerId],
            cancellationToken);

        var customer = await repository.FindAsync(
            command.TenantId, new CustomerId(command.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        var now = clock.UtcNow;
        bool changed;
        if (command.CompanyId is not { } companyId)
        {
            changed = customer.UnlinkFromCompany(now);
        }
        else
        {
            // Un id de otro tenant no aparece: es «no existe», igual que uno inexistente.
            var company = await repository.FindAsync(command.TenantId, new CustomerId(companyId), cancellationToken)
                ?? throw new CustomersDomainException(
                    CustomersErrorCodes.CompanyNotFound, "The company does not exist.");
            var contacts = await companyDirectory.CountContactsAsync(
                command.TenantId, [customer.Id.Value], cancellationToken);
            changed = customer.LinkToCompany(company, contacts.ContainsKey(customer.Id.Value), now);
        }

        // Repetir el vínculo (o desvincular a quien no tiene empresa) no cambia nada: ni versión ni auditoría.
        if (changed)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                CustomerAuditActions.CompanyChanged,
                customer.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

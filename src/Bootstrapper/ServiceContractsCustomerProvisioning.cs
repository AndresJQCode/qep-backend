using Modules.ServiceContracts.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec cobros-recurrentes §5: el alta de clientes de la importación, sobre <see cref="ICustomerProvisioning"/> — el camino y las
/// reglas de Customers, sin duplicarlos. Con documento, la ficha de la caja (<c>CreateFromRegisterAsync</c>, origen
/// <c>billing</c>, con la persona que importa como actor); sin documento, la incompleta de una venta externa
/// (<c>CreateIncompleteAsync</c>). Las dos nacen incompletas, sin CUC ni clasificación.
/// </summary>
internal sealed class ServiceContractsCustomerProvisioning(
    ICustomerProvisioning customers, IServiceContractsCustomerLookup lookup, CustomersDbContext customersContext)
    : IServiceContractsCustomerProvisioning
{
    private const string Source = "billing";

    public async Task<ServiceContractsProvisionedCustomer> CreateAsync(
        Guid tenantId, Guid actorUserId, ServiceContractsNewCustomer data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return await CreateCoreAsync(tenantId, actorUserId, data, cancellationToken);
        }
        catch
        {
            // La importación sigue con la fila siguiente en este mismo scope. Un cliente que no entró quedaría Added en el
            // contexto de Customers y el SaveChanges del alta siguiente lo reintentaría, haciendo fallar también esa fila.
            customersContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ServiceContractsProvisionedCustomer> CreateCoreAsync(
        Guid tenantId, Guid actorUserId, ServiceContractsNewCustomer data, CancellationToken cancellationToken)
    {
        if (data.IdentificationType is null || data.IdentificationNumber is null)
        {
            var incomplete = await customers.CreateIncompleteAsync(
                tenantId, data.Name, data.Phone, data.Email, data.Country, cancellationToken);
            return new ServiceContractsProvisionedCustomer(incomplete.Id, incomplete.Created);
        }

        try
        {
            var registered = await customers.CreateFromRegisterAsync(
                tenantId,
                actorUserId,
                new RegisterCustomerData(data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email),
                Source,
                cancellationToken);
            return new ServiceContractsProvisionedCustomer(registered.Id, registered.Created);
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // §3.2b: otra alta tomó el documento entre el emparejamiento y este commit. Se relee y se usa ese cliente.
            // CustomersUnitOfWork ya limpió su tracker, así que el scope sirve para la fila siguiente.
            var winners = await lookup.FindByIdentificationAsync(
                tenantId, data.IdentificationType, data.IdentificationNumber, cancellationToken);
            return winners.Count == 1
                ? new ServiceContractsProvisionedCustomer(winners[0].Id, Created: false)
                : throw new InvalidOperationException("The identification was taken but its customer cannot be read back.", exception);
        }
    }
}

/// <summary>Los tipos de documento de Customers, para las reglas y la plantilla de la importación de ServiceContracts.</summary>
internal sealed class ServiceContractsIdentificationTypes : IServiceContractsIdentificationTypes
{
    public IReadOnlyCollection<string> SupportedWireValues => IdentificationTypeParser.SupportedWireValues;
}

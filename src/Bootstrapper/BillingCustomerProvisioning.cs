using Modules.Billing.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec cobros-recurrentes §5: el alta de clientes de la importación, sobre <see cref="ICustomerProvisioning"/> — el camino y las
/// reglas de Customers, sin duplicarlos. Con documento, la ficha de la caja (<c>CreateFromRegisterAsync</c>, origen
/// <c>billing</c>, con la persona que importa como actor); sin documento, la incompleta de una venta externa
/// (<c>CreateIncompleteAsync</c>). Las dos nacen incompletas, sin CUC ni clasificación.
/// </summary>
internal sealed class BillingCustomerProvisioning(ICustomerProvisioning customers, IBillingCustomerLookup lookup)
    : IBillingCustomerProvisioning
{
    private const string Source = "billing";

    public async Task<BillingProvisionedCustomer> CreateAsync(
        Guid tenantId, Guid actorUserId, BillingNewCustomer data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.IdentificationType is null || data.IdentificationNumber is null)
        {
            var incomplete = await customers.CreateIncompleteAsync(
                tenantId, data.Name, data.Phone, data.Email, data.Country, cancellationToken);
            return new BillingProvisionedCustomer(incomplete.Id, incomplete.Created);
        }

        try
        {
            var registered = await customers.CreateFromRegisterAsync(
                tenantId,
                actorUserId,
                new RegisterCustomerData(data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email),
                Source,
                cancellationToken);
            return new BillingProvisionedCustomer(registered.Id, registered.Created);
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // §3.2b: otra alta tomó el documento entre el emparejamiento y este commit. Se relee y se usa ese cliente.
            // CustomersUnitOfWork ya limpió su tracker, así que el scope sirve para la fila siguiente.
            var winners = await lookup.FindByIdentificationAsync(
                tenantId, data.IdentificationType, data.IdentificationNumber, cancellationToken);
            return winners.Count == 1
                ? new BillingProvisionedCustomer(winners[0].Id, Created: false)
                : throw new InvalidOperationException("The identification was taken but its customer cannot be read back.", exception);
        }
    }
}

/// <summary>Los tipos de documento de Customers, para las reglas y la plantilla de la importación de Billing.</summary>
internal sealed class BillingIdentificationTypes : IBillingIdentificationTypes
{
    public IReadOnlyCollection<string> SupportedWireValues => IdentificationTypeParser.SupportedWireValues;
}

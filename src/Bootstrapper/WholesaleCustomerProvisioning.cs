using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Customers (D-7): el cliente del distribuidor en el tenant del mayorista, por el camino y las reglas de Customers
/// (<see cref="ICustomerProvisioning.CreateFromRegisterAsync"/>, origen <c>wholesale</c>, con quien invitó como actor). Si el
/// documento ya es de un cliente —uno previo del mayorista o el de un intento anterior de esta aceptación—, se reutiliza
/// (copia de <see cref="ServiceContractsCustomerProvisioning"/>).
/// </summary>
internal sealed class WholesaleCustomerProvisioning(ICustomerProvisioning customers, CustomersDbContext customersContext)
    : IWholesaleCustomerProvisioning
{
    private const string Source = "wholesale";

    public async Task<Guid> EnsureCustomerAsync(
        Guid wholesalerTenantId,
        Guid actorUserId,
        string businessName,
        string country,
        string identificationType,
        string identificationNumber,
        string email,
        string? phone,
        CancellationToken cancellationToken)
    {
        try
        {
            var registered = await customers.CreateFromRegisterAsync(
                wholesalerTenantId,
                actorUserId,
                new RegisterCustomerData(businessName, identificationType, identificationNumber, phone, email, country),
                Source,
                cancellationToken);
            return registered.Id;
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // El documento ya tiene cliente: se relee y se usa ése. Un cliente que no entró quedaría Added en el contexto de
            // Customers y el próximo SaveChanges del scope lo reintentaría.
            customersContext.ChangeTracker.Clear();
            var type = IdentificationTypeParser.Parse(identificationType);
            var number = identificationNumber.Trim();
            var existing = await customersContext.Customers.AsNoTracking()
                .Where(customer => customer.TenantId == wholesalerTenantId
                    && customer.MergedIntoId == null
                    && customer.IdentificationType == type
                    && customer.IdentificationNumber == number)
                .Select(customer => customer.Id)
                .ToListAsync(cancellationToken);
            return existing.Count == 1
                ? existing[0].Value
                : throw new InvalidOperationException(
                    "The identification was taken but its customer cannot be read back.", exception);
        }
        catch
        {
            customersContext.ChangeTracker.Clear();
            throw;
        }
    }
}

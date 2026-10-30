using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec cobros-recurrentes §3.3: los clientes vistos desde ServiceContracts, sobre <c>customers.customers</c>. Vive acá y no en ninguno
/// de los dos módulos, mismo criterio que <see cref="QuotationCustomerLookup"/>: ServiceContracts guarda el cliente como id suelto y
/// el composition root es el único lugar donde ese cruce es legítimo.
///
/// Proyecta sólo las cinco columnas que ServiceContracts usa, sin cargar la libreta de direcciones que trae <c>ICustomerRepository</c>:
/// la lista «Por cobrar» resuelve decenas de clientes por página. El teléfono ya es E.164 (<c>phone_e164</c>); ServiceContracts no lo
/// normaliza de nuevo.
/// </summary>
internal sealed class ServiceContractsCustomerLookup(CustomersDbContext customers) : IServiceContractsCustomerLookup
{
    private const string LikeEscapeCharacter = "\\";

    public async Task<ServiceContractsCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        var row = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == id)
            .Select(customer => new Row(customer.Id, customer.Name, customer.PhoneE164, customer.Email, customer.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
        return row?.ToRef();
    }

    public async Task<IReadOnlyList<ServiceContractsCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        var ids = customerIds.Distinct().Select(id => new CustomerId(id)).ToArray();
        var rows = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && ids.Contains(customer.Id))
            .Select(customer => new Row(customer.Id, customer.Name, customer.PhoneE164, customer.Email, customer.IsActive))
            .ToListAsync(cancellationToken);
        return rows.Select(row => row.ToRef()).ToArray();
    }

    public async Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken)
    {
        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // «%%» matchearía a todos: un término vacío no es un criterio, y quien no quiere filtrar no llama.
            return [];
        }

        // La barra va primero: escaparla después convertiría en literal la que agregan los otros dos reemplazos
        // (mismo orden que CustomerRepository.EscapeLikeWildcards).
        var pattern = "%" + trimmed
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var ids = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && EF.Functions.ILike(customer.Name, pattern, LikeEscapeCharacter))
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);
        return ids.Select(id => id.Value).ToArray();
    }

    public async Task<IReadOnlyList<ServiceContractsCustomerRef>> FindByIdentificationAsync(
        Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken)
    {
        // Mismo par que guarda el agregado: el tipo parseado y el número recortado (CustomerIdentification.Normalized).
        var type = IdentificationTypeParser.Parse(identificationType);
        var number = identificationNumber.Trim();
        return await ListAsync(
            customers.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId
                && customer.MergedIntoId == null
                && customer.IdentificationType == type
                && customer.IdentificationNumber == number),
            cancellationToken);
    }

    public Task<IReadOnlyList<ServiceContractsCustomerRef>> FindByPhoneE164Async(
        Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
        ListAsync(
            customers.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId
                && customer.MergedIntoId == null
                && customer.PhoneE164 == phoneE164),
            cancellationToken);

    public Task<IReadOnlyList<ServiceContractsCustomerRef>> FindByEmailAsync(
        Guid tenantId, string email, CancellationToken cancellationToken)
    {
        // Customers guarda el correo en minúsculas (CustomerContactInfo.NormalizeEmail y el alta incompleta): igualdad
        // exacta contra el buscado en minúsculas, y el índice de la columna sirve.
        var lowered = email.Trim().ToLowerInvariant();
        return ListAsync(
            customers.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId
                && customer.MergedIntoId == null
                && customer.Email == lowered),
            cancellationToken);
    }

    private static async Task<IReadOnlyList<ServiceContractsCustomerRef>> ListAsync(
        IQueryable<Customer> query, CancellationToken cancellationToken)
    {
        var rows = await query
            .Select(customer => new Row(customer.Id, customer.Name, customer.PhoneE164, customer.Email, customer.IsActive))
            .ToListAsync(cancellationToken);
        return rows.Select(row => row.ToRef()).ToArray();
    }

    private sealed record Row(CustomerId Id, string Name, string? PhoneE164, string? Email, bool IsActive)
    {
        public ServiceContractsCustomerRef ToRef() => new(Id.Value, Name, PhoneE164, Email, IsActive);
    }
}

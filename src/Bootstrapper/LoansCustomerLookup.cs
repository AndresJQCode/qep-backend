using Microsoft.EntityFrameworkCore;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Loans.Application;

namespace Bootstrapper;

/// <summary>
/// Los clientes vistos desde Loans, sobre <c>customers.customers</c>. Vive acá y no en ninguno de los dos módulos, mismo criterio que
/// <see cref="ServiceContractsCustomerLookup"/>: Loans guarda el cliente como id suelto y el composition root es el único lugar
/// donde ese cruce es legítimo.
///
/// Proyecta sólo las cinco columnas que Loans usa, sin cargar la libreta de direcciones que trae <c>ICustomerRepository</c>: las
/// listas resuelven decenas de clientes por página.
/// </summary>
internal sealed class LoansCustomerLookup(CustomersDbContext customers) : ILoansCustomerLookup
{
    private const string LikeEscapeCharacter = "\\";

    public async Task<LoansCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        var row = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == id)
            .Select(customer => new Row(customer.Id, customer.Name, customer.PhoneE164, customer.Email, customer.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
        return row?.ToRef();
    }

    public async Task<IReadOnlyList<LoansCustomerRef>> FindManyAsync(
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

    private sealed record Row(CustomerId Id, string Name, string? PhoneE164, string? Email, bool IsActive)
    {
        public LoansCustomerRef ToRef() => new(Id.Value, Name, PhoneE164, Email, IsActive);
    }
}
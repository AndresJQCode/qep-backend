using BuildingBlocks.Domain.Identification;
using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Los clientes vistos desde Soporte, sobre <c>customers.customers</c> (copia recortada de <see cref="LoansCustomerLookup"/>).
/// Vive acá y no en ninguno de los dos módulos: Soporte guarda el cliente como id suelto y el composition root es el único lugar
/// donde ese cruce es legítimo.
///
/// Proyecta sólo las columnas que cada llamada usa: la bandeja resuelve decenas de clientes por página y no lee el contacto; sólo
/// <see cref="FindAsync"/> (alta y vinculación) lo trae. El contacto es <c>email</c> y <c>phone_e164</c>, nunca el <c>phone</c>
/// crudo: la foto del solicitante guarda el celular en E.164.
/// </summary>
internal sealed class SupportCustomerLookup(CustomersDbContext customers, ICustomerDuplicateQueries duplicates) : ISupportCustomerLookup
{
    private const string LikeEscapeCharacter = "\\";

    public async Task<SupportCustomerContact?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        var row = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == id)
            .Select(customer => new ContactRow(customer.Id, customer.Name, customer.IsActive, customer.Email, customer.PhoneE164))
            .SingleOrDefaultAsync(cancellationToken);
        return row?.ToContact();
    }

    public async Task<IReadOnlyList<SupportCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        var ids = customerIds.Distinct().Select(id => new CustomerId(id)).ToArray();
        var rows = await customers.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && ids.Contains(customer.Id))
            .Select(customer => new RefRow(customer.Id, customer.Name, customer.IsActive))
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
            // Orden fijo antes del tope: sin él, qué clientes quedan fuera dependería del plan de la consulta.
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .Take(ISupportCustomerLookup.SearchLimit)
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);
        return ids.Select(id => id.Value).ToArray();
    }

    // Fase 3 (plan F21): por la consulta de duplicados de Customers, que compara con la expresión del índice
    // IX_customers_tenant_identification_normalized y ya deja fuera a los fusionados; acá no se copia la expresión. El tipo se compara
    // por el valor del contrato, y un tipo que Customers no conoce no casa con nadie.
    public async Task<IReadOnlyList<Guid>> FindIdsByDocumentAsync(
        Guid tenantId, string documentType, string documentNumber, CancellationToken cancellationToken)
    {
        var normalized = CustomerIdentificationNormalizer.Normalize(documentNumber);
        var type = documentType?.Trim();
        if (normalized.Length == 0 || string.IsNullOrEmpty(type) || !DocumentTypesByCountry.IsKnown(type))
        {
            return [];
        }

        // Se piden algunos de más: los de otro tipo o inactivos se descartan después y no deben dejar fuera a uno que sí casa.
        var matches = await duplicates.FindByNormalizedIdentificationAsync(
            tenantId, normalized, excludeCustomerId: null, ISupportCustomerLookup.DocumentMatchLimit * 5, cancellationToken);
        return matches
            .Where(customer => customer.IsActive
                && customer.IdentificationType is { } candidate
                && string.Equals(candidate.ToWireValue(), type, StringComparison.OrdinalIgnoreCase))
            .Take(ISupportCustomerLookup.DocumentMatchLimit)
            .Select(customer => customer.Id.Value)
            .ToList();
    }

    private sealed record RefRow(CustomerId Id, string Name, bool IsActive)
    {
        public SupportCustomerRef ToRef() => new(Id.Value, Name, IsActive);
    }

    private sealed record ContactRow(CustomerId Id, string Name, bool IsActive, string? Email, string? PhoneE164)
    {
        public SupportCustomerContact ToContact() => new(Id.Value, Name, IsActive, Email, PhoneE164);
    }
}

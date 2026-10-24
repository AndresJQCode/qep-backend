using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed partial class CustomerPhoneDirectory(CustomersDbContext dbContext, ILogger<CustomerPhoneDirectory> logger)
    : ICustomerPhoneDirectory
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Customer phone lookup by name hit the cap of {Cap} for tenant {TenantId}; the conversation search by customer name is incomplete.")]
    private static partial void LogCapHit(ILogger logger, int cap, Guid tenantId);

    public async Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
        Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e164);

        var result = new Dictionary<string, CustomerPhoneMatch>(StringComparer.Ordinal);
        if (e164.Count == 0)
        {
            return result;
        }

        var phones = e164.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 != null && phones.Contains(customer.PhoneE164))
            // Spec 2026-10-24 §3.3: un fusionado nunca es «el más viejo»; va último y sólo gana si es el único del número.
            .OrderBy(customer => customer.MergedIntoId != null).ThenBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .Select(customer => new { customer.PhoneE164, customer.Id, customer.Name, customer.Completeness, customer.MergedIntoId })
            .ToListAsync(cancellationToken);
        var mergedWinners = new Dictionary<string, CustomerId>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // D-M7: el primero por created_at, id gana; los demás con el mismo número se ignoran.
            if (result.TryAdd(row.PhoneE164!, new CustomerPhoneMatch(row.Id.Value, row.Name, row.Completeness == CustomerCompleteness.Complete))
                && row.MergedIntoId is { } survivorId)
            {
                mergedWinners[row.PhoneE164!] = survivorId;
            }
        }

        if (mergedWinners.Count == 0)
        {
            return result;
        }

        // El número sólo lo tiene un fusionado: sale el que quedó, con su nombre y su completitud. Un salto basta (D-F2).
        var survivorIds = mergedWinners.Values.Distinct().ToArray();
        var survivors = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && survivorIds.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name, customer.Completeness })
            .ToDictionaryAsync(customer => customer.Id, cancellationToken);
        foreach (var (phone, survivorId) in mergedWinners)
        {
            if (survivors.TryGetValue(survivorId, out var survivor))
            {
                result[phone] = new CustomerPhoneMatch(survivor.Id.Value, survivor.Name, survivor.Completeness == CustomerCompleteness.Complete);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(
        Guid tenantId, string e164, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e164);

        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 == e164 && customer.MergedIntoId == null)
            .OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .Take(take)
            .Select(customer => new { customer.Id, customer.Name, customer.Completeness })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CustomerPhoneMatch(row.Id.Value, row.Name, row.Completeness == CustomerCompleteness.Complete))];
    }

    public async Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(term);

        var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var phones = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 != null && EF.Functions.ILike(customer.Name, pattern, "\\"))
            .Select(customer => customer.PhoneE164!)
            .Distinct()
            .Take(cap + 1)
            .ToListAsync(cancellationToken);
        if (phones.Count > cap)
        {
            LogCapHit(logger, cap, tenantId);
            phones.RemoveAt(phones.Count - 1);
        }

        return phones;
    }
}

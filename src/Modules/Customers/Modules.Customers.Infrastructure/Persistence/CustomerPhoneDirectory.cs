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
            .OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .Select(customer => new { customer.PhoneE164, customer.Id, customer.Name, customer.Completeness })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            // D-M7: el primero por created_at, id gana; los demás con el mismo número se ignoran.
            result.TryAdd(row.PhoneE164!, new CustomerPhoneMatch(row.Id.Value, row.Name, row.Completeness == CustomerCompleteness.Complete));
        }

        return result;
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

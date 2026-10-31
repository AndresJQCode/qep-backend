using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ChargeRepository(ServiceContractsDbContext dbContext) : IChargeRepository
{
    public void AddRange(IEnumerable<Charge> charges) => dbContext.Charges.AddRange(charges);

    public Task<Charge?> FindAsync(Guid tenantId, Guid chargeId, CancellationToken cancellationToken) =>
        dbContext.Charges.FirstOrDefaultAsync(
            charge => charge.TenantId == tenantId && charge.Id == chargeId, cancellationToken);

    public async Task<IReadOnlyList<Charge>> ListPendingByServiceAsync(
        ServiceContractId serviceId, CancellationToken cancellationToken) =>
        await dbContext.Charges
            .Where(charge => charge.ServiceId == serviceId && charge.Status == ChargeStatus.Pending)
            .OrderBy(charge => charge.DueDate)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Charge>> ListPendingByServicesAsync(
        IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken cancellationToken)
    {
        if (serviceIds.Count == 0)
        {
            return [];
        }

        var ids = serviceIds.ToArray();
        return await dbContext.Charges
            .AsNoTracking()
            .Where(charge => ids.Contains(charge.ServiceId) && charge.Status == ChargeStatus.Pending)
            .OrderBy(charge => charge.DueDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Charge>> ListPendingByTenantAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Charges
            .Where(charge => charge.TenantId == tenantId && charge.Status == ChargeStatus.Pending)
            .OrderBy(charge => charge.DueDate)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<Charge> Items, int Total)> ListAsync(
        ChargeQuery query, CancellationToken cancellationToken)
    {
        var charges = dbContext.Charges
            .AsNoTracking()
            .Where(charge => charge.TenantId == query.TenantId);

        // `Overdue` no se guarda: `open` es Pending, vencido o no (spec §4).
        charges = query.Status switch
        {
            ChargeListStatus.Open => charges.Where(charge => charge.Status == ChargeStatus.Pending),
            ChargeListStatus.Paid => charges.Where(charge => charge.Status == ChargeStatus.Paid),
            _ => charges.Where(charge => charge.Status == ChargeStatus.Cancelled),
        };

        if (query.CustomerId is { } customerId)
        {
            charges = charges.Where(charge => charge.CustomerId == customerId);
        }

        // El encargado vive en el perfil del cliente, no en el cobro.
        if (query.AssigneeMemberId is { } assigneeId)
        {
            charges = charges.Where(charge => dbContext.Profiles.Any(profile =>
                profile.TenantId == charge.TenantId &&
                profile.CustomerId == charge.CustomerId &&
                profile.AssigneeMemberId == assigneeId));
        }

        var pattern = LikeSearch.ContainsPattern(query.SearchTerm);
        var customerIds = query.SearchCustomerIds is { Count: > 0 } ids ? ids.ToArray() : [];
        if (pattern is not null)
        {
            charges = charges.Where(charge =>
                customerIds.Contains(charge.CustomerId) ||
                dbContext.Services.Any(service =>
                    service.Id == charge.ServiceId &&
                    EF.Functions.ILike(service.Name, pattern, LikeSearch.EscapeCharacter)));
        }

        var total = await charges.CountAsync(cancellationToken);
        var ordered = query.Status switch
        {
            ChargeListStatus.Open => charges.OrderBy(charge => charge.DueDate),
            ChargeListStatus.Paid => charges.OrderByDescending(charge => charge.PaidOn),
            _ => charges.OrderByDescending(charge => charge.DueDate),
        };
        var items = await ordered
            .ThenBy(charge => charge.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<Charge>> ListByServiceAsync(
        ServiceContractId serviceId, CancellationToken cancellationToken) =>
        await dbContext.Charges
            .AsNoTracking()
            .Where(charge => charge.ServiceId == serviceId)
            .OrderByDescending(charge => charge.DueDate)
            .ToListAsync(cancellationToken);

    public async Task<ChargeSummaryDto> SummaryAsync(
        Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var pending = dbContext.Charges
            .AsNoTracking()
            .Where(charge => charge.TenantId == tenantId && charge.Status == ChargeStatus.Pending);

        // Se agrupa en SQL sobre el tipo anónimo del Where, no sobre un record: EF no agrega sobre
        // una proyección a record.
        var open = await pending
            .GroupBy(charge => charge.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count(), Amount = group.Sum(charge => charge.Amount) })
            .OrderBy(group => group.Currency)
            .ToListAsync(cancellationToken);
        var overdue = await pending
            .Where(charge => charge.DueDate < today)
            .GroupBy(charge => charge.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count(), Amount = group.Sum(charge => charge.Amount) })
            .OrderBy(group => group.Currency)
            .ToListAsync(cancellationToken);

        // Un cobro tiene el aviso fallido si el último mensaje que lo cubrió quedó Failed.
        var failed = await pending.CountAsync(
            charge => dbContext.CustomerMessages
                .Where(message => dbContext.ReminderSteps.Any(step =>
                    step.ChargeId == charge.Id && step.MessageId == message.Id))
                .OrderByDescending(message => message.AttemptedAt)
                .Select(message => message.Status)
                .FirstOrDefault() == MessageDeliveryStatus.Failed,
            cancellationToken);

        return new ChargeSummaryDto(
            open.Sum(row => row.Count),
            overdue.Sum(row => row.Count),
            failed,
            open.Select(row => new CurrencyAmountDto(row.Currency, row.Amount)).ToList(),
            overdue.Select(row => new CurrencyAmountDto(row.Currency, row.Amount)).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, ReminderDto>> ListLastRemindersAsync(
        IReadOnlyCollection<Guid> chargeIds, CancellationToken cancellationToken)
    {
        if (chargeIds.Count == 0)
        {
            return new Dictionary<Guid, ReminderDto>();
        }

        var ids = chargeIds.ToArray();
        // Un cobro tiene a lo sumo un puñado de pasos (before_5, due, after_3, ...), así que se traen
        // todos los de la página y el último por cobro se elige en memoria: "último" es por intento
        // del mensaje, no por nombre del paso (after_10 no sigue a due por orden alfabético).
        var rows = await (
            from step in dbContext.ReminderSteps
            join message in dbContext.CustomerMessages on step.MessageId equals message.Id
            where ids.Contains(step.ChargeId)
            select new
            {
                step.ChargeId,
                step.Step,
                message.Kind,
                message.Channel,
                message.Status,
                message.FailureCode,
                message.AttemptedAt,
                message.SentAt,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ChargeId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var last = group.MaxBy(row => row.AttemptedAt)!;
                    return new ReminderDto(
                        last.Step,
                        last.Kind.ToString(),
                        last.Channel.ToString(),
                        last.Status.ToString(),
                        last.FailureCode,
                        last.AttemptedAt,
                        last.SentAt);
                });
    }
}

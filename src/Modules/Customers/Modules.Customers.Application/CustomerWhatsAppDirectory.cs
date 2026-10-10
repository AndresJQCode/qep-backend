using BuildingBlocks.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §8.2–§8.3. Corre fuera y antes de la transacción de Messaging (dos DbContext, dos
/// unidades de trabajo): si Messaging falla después, el reintento encuentra el cliente por BSUID.</summary>
public sealed class CustomerWhatsAppDirectory(
    ICustomerRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IPhoneNumberNormalizer phoneNormalizer,
    IClock clock) : ICustomerWhatsAppDirectory
{
    private const string Success = "success";

    public async Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (await repository.FindByWhatsAppUserIdAsync(tenantId, contact.UserId, cancellationToken) is { } known)
        {
            return new EnsuredCustomer(known.Id.Value, EnsureOutcome.Existing);
        }

        if (contact.PhoneE164 is { } phone && await repository.FindOldestByPhoneE164Async(tenantId, phone, cancellationToken) is { } byPhone)
        {
            if (byPhone.AttachWhatsAppUserId(contact.UserId))
            {
                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (WhatsAppUserIdTakenException)
                {
                    return await WinnerAsync(tenantId, contact.UserId, cancellationToken);
                }
            }

            return new EnsuredCustomer(byPhone.Id.Value, EnsureOutcome.Linked);
        }

        var now = clock.UtcNow;
        var customer = Customer.CreateIncomplete(
            CustomerId.New(),
            tenantId,
            IncompleteCustomerProfile.NameFor(contact),
            contact.PhoneE164,
            IncompleteCustomerProfile.CountryFor(contact, phoneNormalizer),
            contact.UserId,
            now,
            phoneNormalizer);
        repository.Add(customer);
        // D-A9: actor vacío, no hay persona detrás de un webhook.
        auditPublisher.Publish(tenantId, Guid.Empty, CustomerAuditActions.CreatedFromMessaging, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (WhatsAppUserIdTakenException)
        {
            return await WinnerAsync(tenantId, contact.UserId, cancellationToken);
        }

        return new EnsuredCustomer(customer.Id.Value, EnsureOutcome.Created);
    }

    public async Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken)
    {
        if (await repository.FindByWhatsAppUserIdAsync(tenantId, current, cancellationToken) is not null)
        {
            return false;
        }

        var customer = await repository.FindByWhatsAppUserIdAsync(tenantId, previous, cancellationToken);
        if (customer is null || !customer.ReplaceWhatsAppUserId(previous, current))
        {
            return false;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (WhatsAppUserIdTakenException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        (await repository.FindWhatsAppRefsAsync(tenantId, ids, cancellationToken)).ToDictionary(item => item.Id);

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
        repository.FindIdsByNameAsync(tenantId, term, cap, cancellationToken);

    private async Task<EnsuredCustomer> WinnerAsync(Guid tenantId, string userId, CancellationToken cancellationToken)
    {
        var winner = await repository.FindByWhatsAppUserIdAsync(tenantId, userId, cancellationToken)
            ?? throw new InvalidOperationException("The WhatsApp user id was taken but its customer cannot be read back.");
        return new EnsuredCustomer(winner.Id.Value, EnsureOutcome.Existing);
    }
}

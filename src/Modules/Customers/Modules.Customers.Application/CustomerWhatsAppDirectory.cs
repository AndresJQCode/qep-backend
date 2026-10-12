using System.Runtime.CompilerServices;
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
    IClock clock,
    ICustomerAudienceRepository audience) : ICustomerWhatsAppDirectory
{
    /// <summary>Spec 2026-10-11 §4.4: de a cuántos clientes se lee la audiencia.</summary>
    public const int AudienceBatchSize = 500;

    private const string Success = "success";

    public async Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (await repository.FindByWhatsAppUserIdAsync(tenantId, contact.UserId, cancellationToken) is { } known)
        {
            return new EnsuredCustomer(known.Id.Value, EnsureOutcome.Existing);
        }

        // Ningún camino de acá puede dar RequestConcurrencyException: vincular es un UPDATE condicional sin chequeo de
        // versión, así que una edición humana entre la lectura y el UPDATE no choca (y el UPDATE no pisa lo que la
        // persona cambió: sólo toca whatsapp_user_id), y crear es un INSERT.
        if (contact.PhoneE164 is { } phone && await repository.FindOldestByPhoneE164Async(tenantId, phone, cancellationToken) is { } byPhone)
        {
            try
            {
                // D-A6: false si ya tenía otro BSUID; el cliente igual es el del teléfono y queda con el suyo.
                _ = await repository.TryAttachWhatsAppUserIdAsync(tenantId, byPhone.Id, contact.UserId, cancellationToken);
            }
            catch (WhatsAppUserIdTakenException)
            {
                return await WinnerAsync(tenantId, contact.UserId, cancellationToken);
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
        // Un solo UPDATE condicional (WHERE whatsapp_user_id = @previous): dos reemplazos simultáneos no se pisan, y
        // si el nuevo ya es de otro cliente lo dice el índice, no una lectura previa que la carrera dejaría vieja.
        try
        {
            return await repository.TryReplaceWhatsAppUserIdAsync(tenantId, previous, current, cancellationToken);
        }
        catch (WhatsAppUserIdTakenException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        (await repository.FindWhatsAppRefsAsync(tenantId, ids, cancellationToken)).ToDictionary(item => item.Id);

    /// <summary>Un término en blanco no busca nada: con <c>ILIKE '%%'</c> devolvería los primeros
    /// <paramref name="cap"/> clientes del tenant, que no es «ninguno coincide».</summary>
    public async Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(term) ? [] : await repository.FindIdsByNameAsync(tenantId, term.Trim(), cap, cancellationToken);

    public Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        audience.FindContactAsync(tenantId, customerId, cancellationToken);

    public async IAsyncEnumerable<CustomerContact> ListAudienceAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guid? after = null;
        while (true)
        {
            var batch = await audience.ListAudienceBatchAsync(tenantId, tagIds, customerIds, after, AudienceBatchSize, cancellationToken);
            foreach (var contact in batch)
            {
                yield return contact;
            }

            // Un lote corto es el último; uno completo puede serlo también, y se averigua con una lectura más.
            if (batch.Count < AudienceBatchSize)
            {
                yield break;
            }

            after = batch[^1].CustomerId;
        }
    }

    public Task<AudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        audience.CountAudienceAsync(tenantId, tagIds, customerIds, cancellationToken);

    public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        audience.MarkMarketingOptOutAsync(tenantId, customerId, MarketingConsentSource.Meta, clock.UtcNow, cancellationToken);

    public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        audience.MarkMarketingOptOutAsync(tenantId, customerId, MarketingConsentSource.Button, clock.UtcNow, cancellationToken);

    private async Task<EnsuredCustomer> WinnerAsync(Guid tenantId, string userId, CancellationToken cancellationToken)
    {
        var winner = await repository.FindByWhatsAppUserIdAsync(tenantId, userId, cancellationToken)
            ?? throw new InvalidOperationException("The WhatsApp user id was taken but its customer cannot be read back.");
        return new EnsuredCustomer(winner.Id.Value, EnsureOutcome.Existing);
    }
}

using BuildingBlocks.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-14 decisión 10. Corre fuera y antes de la transacción de Messaging (dos DbContext, dos unidades de trabajo):
/// si Messaging falla después, el reintento encuentra el cliente por IGSID. Espejo de <see cref="CustomerWhatsAppDirectory"/> sin la
/// rama del teléfono.</summary>
public sealed class CustomerInstagramDirectory(
    ICustomerRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IClock clock) : ICustomerInstagramDirectory
{
    public const string FallbackName = "Contacto de Instagram";

    private const string Success = "success";

    public async Task<EnsuredCustomer> EnsureAsync(Guid tenantId, InstagramContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        // Spec 2026-10-24 §3.3: el IGSID que se quedó en un fusionado resuelve al que quedó, y no nace otro (un salto basta, D-F2).
        if (await repository.FindByInstagramUserIdAsync(tenantId, contact.UserId, cancellationToken) is { } known)
        {
            return new EnsuredCustomer((known.MergedIntoId ?? known.Id).Value, EnsureOutcome.Existing);
        }

        var now = clock.UtcNow;
        var customer = Customer.CreateIncompleteFromInstagram(CustomerId.New(), tenantId, NameFor(contact), contact.UserId, contact.Username, now);
        repository.Add(customer);
        // D-A9: actor vacío, no hay persona detrás de un webhook.
        auditPublisher.Publish(tenantId, Guid.Empty, CustomerAuditActions.CreatedFromMessaging, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (InstagramUserIdTakenException)
        {
            var winner = await repository.FindByInstagramUserIdAsync(tenantId, contact.UserId, cancellationToken)
                ?? throw new InvalidOperationException("The Instagram user id was taken but its customer cannot be read back.");
            return new EnsuredCustomer((winner.MergedIntoId ?? winner.Id).Value, EnsureOutcome.Existing);
        }

        return new EnsuredCustomer(customer.Id.Value, EnsureOutcome.Created);
    }

    /// <summary>Decisión 10, en este orden: el nombre del perfil, el <c>@usuario</c>, y si Meta no dio ninguno (V-7), un texto fijo.</summary>
    public static string NameFor(InstagramContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var name = FirstNonBlank(contact.Name) ?? (FirstNonBlank(contact.Username) is { } username ? "@" + username.TrimStart('@') : FallbackName);
        return name.Length <= Customer.NameMaxLength ? name : name[..Customer.NameMaxLength];
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}

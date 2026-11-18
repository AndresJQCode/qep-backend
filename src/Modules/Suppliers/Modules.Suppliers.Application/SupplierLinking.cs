using BuildingBlocks.Application;
using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Application;

/// <summary>
/// Spec 2026-11-17 (mayorista), D-3 y D-8: deja al mayorista como proveedor vinculado en el tenant del distribuidor. Es una
/// escritura del sistema dentro de la aceptación de una invitación: no mira <c>IExecutionContext</c> ni permisos (quien acepta
/// todavía puede no tener sesión en ese tenant); quien llama ya decidió que el actor puede.
/// </summary>
public interface ISupplierLinking
{
    /// <summary>
    /// Idempotente, en este orden: 1) el proveedor ya vinculado a <paramref name="linkedTenantId"/> se devuelve sin tocarlo;
    /// 2) uno sin vínculo con el mismo documento se vincula; 3) si no hay, se crea vinculado, con la ciudad validada contra
    /// Geography. Audita <c>suppliers.supplier.linked</c> con <paramref name="actorUserId"/> cuando escribe.
    /// </summary>
    /// <returns>El id del proveedor vinculado.</returns>
    Task<Guid> EnsureLinkedAsync(
        Guid tenantId,
        Guid linkedTenantId,
        Guid actorUserId,
        SupplierWriteData data,
        CancellationToken cancellationToken);
}

public sealed class SupplierLinking(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    ISuppliersUnitOfWork unitOfWork,
    ISuppliersAuditPublisher auditPublisher,
    IClock clock,
    ISupplierPhoneNormalizer phoneNormalizer)
    : ISupplierLinking
{
    private const string IdentificationTakenCode = "suppliers.supplier.identification_taken";
    private const string LinkedElsewhereCode = "suppliers.supplier.linked_elsewhere";

    public async Task<Guid> EnsureLinkedAsync(
        Guid tenantId,
        Guid linkedTenantId,
        Guid actorUserId,
        SupplierWriteData data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            return await AttemptAsync(tenantId, linkedTenantId, actorUserId, data, cancellationToken);
        }
        catch (Exception exception) when (IsRaceWithAnotherAttempt(exception))
        {
            // Dos pestañas (o un doble clic) aceptando a la vez: la otra ganó entre la búsqueda y el commit. Se descarta el
            // intento fallido y se relee una sola vez desde el paso 1, que ahora encuentra lo que la otra dejó. Un segundo
            // choque ya no es una carrera de pestañas y sube tal cual.
            unitOfWork.DiscardPendingChanges();
            return await AttemptAsync(tenantId, linkedTenantId, actorUserId, data, cancellationToken);
        }
    }

    private async Task<Guid> AttemptAsync(
        Guid tenantId,
        Guid linkedTenantId,
        Guid actorUserId,
        SupplierWriteData data,
        CancellationToken cancellationToken)
    {
        // 1) Ya vinculado: nada que escribir.
        var linked = await repository.FindByLinkedTenantAsync(tenantId, linkedTenantId, cancellationToken);
        if (linked is not null)
        {
            return linked.Id.Value;
        }

        var now = clock.UtcNow;

        // 2) Mismo documento sin vínculo: se vincula ése, en vez de crear un duplicado que choque con
        // IX_suppliers_tenant_identification. Uno con el documento pero vinculado a otro mayorista lanza linked_elsewhere.
        var identification = SupplierIdentification.Create(
            IdentificationTypeParser.Parse(data.IdentificationType), data.IdentificationNumber);
        var supplier = await repository.FindByIdentificationAsync(
            tenantId, identification.Type, identification.Number, cancellationToken);
        if (supplier is not null)
        {
            supplier.LinkTo(linkedTenantId, now);
        }
        else
        {
            // 3) Nuevo. La ciudad colombiana se valida contra Geography con el mismo código que el alta del formulario.
            await EnsureCityExistsAsync(data, cancellationToken);
            supplier = Supplier.CreateLinked(tenantId, linkedTenantId, data, phoneNormalizer, now);
            repository.Add(supplier);
        }

        auditPublisher.Publish(tenantId, actorUserId, SupplierAuditActions.Linked, supplier.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return supplier.Id.Value;
    }

    private async Task EnsureCityExistsAsync(SupplierWriteData data, CancellationToken cancellationToken)
    {
        var isColombian = string.Equals(
            data.Country?.Trim(), Supplier.ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);
        // Sin ciudad, Supplier.Create responde city_required: no hay nada que buscar.
        if (!isColombian || data.CityId is not { } cityId)
        {
            return;
        }

        _ = await geographyLookup.FindCityAsync(cityId, cancellationToken)
            ?? throw new SuppliersDomainException("suppliers.supplier.city_not_found", "The city was not found.");
    }

    // Los dos índices únicos que una aceptación simultánea puede pisar (SuppliersUnitOfWork los traduce por nombre) y la
    // versión del proveedor que las dos vincularon por documento.
    private static bool IsRaceWithAnotherAttempt(Exception exception) => exception switch
    {
        SuppliersDomainException { Code: IdentificationTakenCode } => true,
        ResourceConflictException { Code: LinkedElsewhereCode } => true,
        RequestConcurrencyException => true,
        _ => false,
    };
}

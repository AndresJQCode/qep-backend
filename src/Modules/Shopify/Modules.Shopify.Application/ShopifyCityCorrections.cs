using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-26: por qué un trabajo de corrección terminó sin escribir nada. Viajan tal cual a la base y a la consola.</summary>
public static class ShopifyCityCorrectionSkipReasons
{
    public const string SynonymDeleted = "synonym_deleted";
    public const string MirrorMissing = "mirror_missing";
    public const string OrderNotFound = "order_not_found";
    public const string OrderNotPending = "order_not_pending";
    public const string StorePickup = "store_pickup";
    public const string ShippingPresent = "shipping_present";
    public const string ShippingChanged = "shipping_changed";
    public const string PartyIncomplete = "party_incomplete";
    public const string CustomerNotFound = "customer_not_found";
    public const string CustomerNotEligible = "customer_not_eligible";
    public const string CustomerNotCompletable = "customer_not_completable";
}

/// <summary>Una ocurrencia del texto confirmado, como la publica Geography. <see cref="SourceRef"/> es la fila del espejo
/// (<c>shopify.orders.id</c>) cuando <see cref="Source"/> es <c>shopify</c>; <see cref="CustomerId"/> es el cliente que esa
/// importación creó incompleto (desvío S4).</summary>
public sealed record CitySynonymOccurrenceFact(
    Guid OccurrenceId,
    Guid TenantId,
    string Source,
    Guid SourceRef,
    Guid OrderId,
    Guid? CustomerId,
    bool PartyCityOnly,
    bool CustomerCityOnly);

/// <summary><c>geography.city_synonym.resolved.v1</c>: el operador confirmó un texto como sinónimo, con sus ocurrencias.</summary>
public sealed record CitySynonymResolvedFact(
    Guid SynonymId,
    Guid DepartmentId,
    Guid CityId,
    string NormalizedText,
    Guid OperatorSubjectId,
    Guid OperatorTenantId,
    IReadOnlyList<CitySynonymOccurrenceFact> Occurrences);

/// <summary><c>geography.city_synonym.corrected.v1</c>: el operador cambió la ciudad de un sinónimo (§4.6).</summary>
public sealed record CitySynonymCorrectedFact(
    Guid SynonymId,
    Guid DepartmentId,
    Guid PreviousCityId,
    Guid CityId,
    Guid OperatorSubjectId,
    Guid OperatorTenantId);

/// <summary>
/// Spec 2026-10-26 D-C6: un evento abarca muchos pedidos de muchos tenants y el inbox reclama por mensaje, así que el consumidor no
/// corrige: siembra una fila de trabajo por pedido o cliente (desvío S5) y el worker que corrige va de a uno. Sólo agrega al tracker:
/// el procesador guarda con el inbox en el mismo SaveChanges, así que un mensaje procesado nunca queda sin sus trabajos.
/// </summary>
public sealed class ShopifyCitySynonymEventApplier(
    IShopifyCityCorrectionJobRepository jobs, IShopifyCityCorrectionRepository corrections, IClock clock)
{
    /// <summary>El <c>source</c> con que Shopify reporta sus ocurrencias a Geography.</summary>
    public const string ShopifySource = "shopify";

    /// <summary>Por ocurrencia de Shopify, lo que <see cref="ResolveJobs"/> dice. El mismo pedido o cliente en dos ocurrencias (la
    /// fila del espejo se borró y el pedido volvió a llegar) se siembra una vez: el índice único (mensaje, destino, id) rechazaría
    /// el guardado y el mensaje no terminaría nunca.</summary>
    public Task ApplyResolvedAsync(Guid messageId, CitySynonymResolvedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var now = clock.UtcNow;
        var seeded = new HashSet<(ShopifyCityCorrectionTarget, Guid)>();
        foreach (var occurrence in fact.Occurrences.Where(occurrence => string.Equals(occurrence.Source, ShopifySource, StringComparison.Ordinal)))
        {
            foreach (var job in ResolveJobs(
                occurrence.TenantId,
                fact.SynonymId,
                occurrence.SourceRef,
                occurrence.OrderId,
                occurrence.CustomerId,
                occurrence.PartyCityOnly,
                occurrence.CustomerCityOnly,
                fact.OperatorSubjectId,
                messageId,
                now))
            {
                if (seeded.Add((job.Target, job.TargetId)))
                {
                    jobs.Add(job);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>§4.6 y O8: un <c>Recorrect</c> por cada huella del sinónimo, de los dos orígenes y de todos los tenants, salvo las
    /// que ya están en la ciudad nueva. Si la persona editó algo después, lo decide el worker al correr (<c>shipping_changed</c>).</summary>
    public async Task ApplyCorrectedAsync(Guid messageId, CitySynonymCorrectedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var now = clock.UtcNow;
        foreach (var correction in await corrections.ListBySynonymAsync(fact.SynonymId, cancellationToken))
        {
            if (correction.CityId != fact.CityId)
            {
                jobs.Add(ShopifyCityCorrectionJob.Recorrect(correction, fact.OperatorSubjectId, messageId, now));
            }
        }
    }

    /// <summary>
    /// La regla de la siembra de un <c>Resolve</c>, la misma para el evento y para el importador (<c>SynonymExists</c>): un trabajo
    /// de pedido si la ciudad era lo único que le faltaba a la parte de envío (<paramref name="partyCityOnly"/>, con correo: desvío
    /// S10), y uno de cliente si esa importación lo creó incompleto sólo por la ciudad (desvíos S4 y S7).
    /// </summary>
    internal static IEnumerable<ShopifyCityCorrectionJob> ResolveJobs(
        Guid tenantId,
        Guid synonymId,
        Guid mirrorOrderId,
        Guid orderId,
        Guid? customerId,
        bool partyCityOnly,
        bool customerCityOnly,
        Guid operatorSubjectId,
        Guid? sourceMessageId,
        DateTimeOffset now)
    {
        if (partyCityOnly)
        {
            yield return ShopifyCityCorrectionJob.Resolve(
                tenantId, synonymId, ShopifyCityCorrectionTarget.Order, orderId, mirrorOrderId, operatorSubjectId, sourceMessageId, now);
        }

        if (customerId is { } customer && customerCityOnly)
        {
            yield return ShopifyCityCorrectionJob.Resolve(
                tenantId, synonymId, ShopifyCityCorrectionTarget.Customer, customer, mirrorOrderId, operatorSubjectId, sourceMessageId, now);
        }
    }
}

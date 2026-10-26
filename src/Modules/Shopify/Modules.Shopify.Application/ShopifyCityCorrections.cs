using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
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
    IShopifyCityCorrectionJobRepository jobs, IShopifyCityCorrectionRepository corrections, IShopifyUnitOfWork unitOfWork, IClock clock)
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
    /// que ya están en la ciudad nueva. Si la persona editó algo después, lo decide el worker al correr (<c>shipping_changed</c>).
    /// Lista bajo el candado del sinónimo (<see cref="IShopifyUnitOfWork.LockCitySynonymAsync"/>), que el llamador abre en una
    /// transacción que dura hasta su guardado: una huella que el worker escribe a la vez no se pierde.</summary>
    public async Task ApplyCorrectedAsync(Guid messageId, CitySynonymCorrectedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        await unitOfWork.LockCitySynonymAsync(fact.SynonymId, cancellationToken);
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

/// <summary>
/// Spec 2026-10-26 §4.4, §4.6 y D-C6: corrige el pedido o el cliente de un trabajo de <c>shopify.city_correction_jobs</c>. Lo llama
/// el worker después de reclamarlo, en un scope propio por trabajo, y todo corre en una transacción de Shopify.
/// <list type="number">
/// <item>Toma la fila del trabajo (<see cref="IShopifyCityCorrectionJobRepository.LockIfPendingAsync"/>): si otra réplica ya lo
/// terminó (el lease venció mientras corría), no escribe nada.</item>
/// <item>Lee el sinónimo al correr: uno borrado (o que ya no existe) termina <c>synonym_deleted</c> sin escribir nada (O6), y la
/// ciudad que se pide es la <b>actual</b> del sinónimo, no la del evento que sembró el trabajo.</item>
/// <item>Lo que ya hizo otro trabajo no se repite: un <c>Resolve</c> cuyo pedido o cliente ya tiene la huella de este sinónimo (el
/// importador siembra sin mensaje, así que un pedido reimportado puede sembrar otro) y un <c>Recorrect</c> cuya huella ya está en
/// la ciudad del sinónimo terminan <c>Skipped</c> sin llamar a Quotations ni a Customers.</item>
/// <item>Escribe por <see cref="IShopifyCityCorrectionWriter"/>, que confirma en su propio scope. Con éxito, también con
/// <c>AlreadyApplied</c> (un intento anterior escribió y cayó antes de guardar acá), la huella guarda la ciudad que de verdad quedó
/// escrita, que en un reintento puede ser la que el sinónimo tenía antes. Cualquier rechazo termina <c>Skipped</c> con su motivo,
/// sin huella ni auditoría.</item>
/// <item>Con éxito, bajo el candado del sinónimo (<see cref="IShopifyUnitOfWork.LockCitySynonymAsync"/>, el mismo que toma el evento
/// <c>corrected</c> antes de listar las huellas) relee el sinónimo y guarda en un solo guardado la huella, la auditoría, el trabajo
/// <c>Applied</c> y, si la ciudad del sinónimo ya no es la escrita, el <c>Recorrect</c> de esa huella. Así ninguna huella queda en
/// la ciudad vieja: o el evento la lista, o esta relectura ve la ciudad nueva.</item>
/// </list>
/// Lo que lanza (concurrencia, base, red, un payload ilegible) sale tal cual: la transacción se deshace y el worker lo reintenta.
/// </summary>
public sealed class ShopifyCityCorrectionProcessor(
    IShopifyCitySynonymLookup synonyms,
    IShopifyCityCorrectionWriter writer,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyStoreRepository stores,
    IShopifyCityCorrectionRepository corrections,
    IShopifyCityCorrectionJobRepository jobs,
    IShopifyAuditRecorder audit,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>Desvío S7: la versión con que nace el incompleto de una venta externa. Si ya no es ésa, alguien lo editó después
    /// de la importación y no se completa.</summary>
    public const int IncompleteCustomerVersion = 1;

    // El detalle de la auditoría en camelCase, como los DTO de la API.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(ShopifyCityCorrectionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await jobs.LockIfPendingAsync(job.Id, cancellationToken))
        {
            return;
        }

        var synonym = await synonyms.FindAsync(job.SynonymId, cancellationToken);
        if (synonym is not { IsDeleted: false })
        {
            await SkipAsync(job, ShopifyCityCorrectionSkipReasons.SynonymDeleted, transaction, cancellationToken);
            return;
        }

        var existing = await ExistingCorrectionAsync(job, cancellationToken);
        if (existing is not null && (job.Kind == ShopifyCityCorrectionJobKind.Resolve || existing.CityId == synonym.CityId))
        {
            await SkipAsync(job, AlreadyDoneReason(job.Target), transaction, cancellationToken);
            return;
        }

        var (result, preconditionFailed) = await WriteAsync(job, synonym, existing, cancellationToken);
        if (result is not { Kind: ShopifyCityWriteKind.Applied or ShopifyCityWriteKind.AlreadyApplied })
        {
            await SkipAsync(job, preconditionFailed ?? SkipReasonFor(result!.Kind, job.Target), transaction, cancellationToken);
            return;
        }

        await unitOfWork.LockCitySynonymAsync(job.SynonymId, cancellationToken);
        var current = await synonyms.FindAsync(job.SynonymId, cancellationToken);
        var now = clock.UtcNow;
        var previousCityId = existing?.CityId;
        var correction = RecordCorrection(job, synonym, existing, result, now);
        audit.Record(
            job.TenantId,
            Guid.Empty,
            AuditActorType.System,
            AuditAction(job),
            job.Target == ShopifyCityCorrectionTarget.Order ? ShopifyAuditActions.OrderResourceType : ShopifyAuditActions.CustomerResourceType,
            job.TargetId.ToString(),
            JsonSerializer.Serialize(
                new
                {
                    synonymId = job.SynonymId,
                    operatorSubjectId = job.OperatorSubjectId,
                    cityId = correction.CityId,
                    previousCityId,
                    origin = correction.Origin.ToString(),
                    jobId = job.Id,
                },
                Json),
            now);
        job.MarkApplied(now);
        if (current is { IsDeleted: false } && current.CityId != correction.CityId)
        {
            // Sin operador ni mensaje: acá no se sabe quién corrigió, sólo que la ciudad ya no es la escrita.
            jobs.Add(ShopifyCityCorrectionJob.Recorrect(correction, Guid.Empty, sourceMessageId: null, now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Un Resolve busca si ya hay huella de este sinónimo en su destino; un Recorrect lee la suya. La huella no se borra nunca (ni
    // con el sinónimo, §4.7): un Recorrect sin ella, o con la de otro destino, es un error de programación y no un motivo de Skipped.
    private async Task<ShopifyCityCorrection?> ExistingCorrectionAsync(ShopifyCityCorrectionJob job, CancellationToken cancellationToken)
    {
        if (job.Kind == ShopifyCityCorrectionJobKind.Resolve)
        {
            return await corrections.FindAsync(job.TenantId, job.SynonymId, job.Target, job.TargetId, cancellationToken);
        }

        var correction = job.CorrectionId is { } correctionId ? await corrections.FindByIdAsync(correctionId, cancellationToken) : null;
        return correction is not null
            && correction.TenantId == job.TenantId
            && correction.Target == job.Target
            && correction.TargetId == job.TargetId
            ? correction
            : throw new InvalidOperationException($"The city correction job {job.Id} has no correction of its own target to recorrect.");
    }

    // La escritura del trabajo, o el motivo por el que ni se intentó (lo que el espejo no trae).
    private async Task<(ShopifyCityWriteResult? Result, string? PreconditionFailed)> WriteAsync(
        ShopifyCityCorrectionJob job, ShopifyCitySynonymRef synonym, ShopifyCityCorrection? existing, CancellationToken cancellationToken)
    {
        if (job.Kind == ShopifyCityCorrectionJobKind.Recorrect)
        {
            var recorrected = job.Target == ShopifyCityCorrectionTarget.Order
                ? await writer.CorrectShippingCityAsync(
                    job.TenantId, job.TargetId, existing!.Party!, synonym.DepartmentId, synonym.CityId, cancellationToken)
                : await writer.CorrectCustomerCityAsync(
                    job.TenantId, job.TargetId, existing!.CustomerVersion!.Value, existing.CityId, synonym.CityId, cancellationToken);
            return (recorrected, null);
        }

        // Un Resolve relee del espejo lo que mandó Shopify: la dirección del pedido o los datos del comprador.
        var mirror = job.MirrorOrderId is { } mirrorId ? await mirrors.FindByIdAsync(job.TenantId, mirrorId, cancellationToken) : null;
        if (mirror is null)
        {
            return (null, ShopifyCityCorrectionSkipReasons.MirrorMissing);
        }

        var snapshot = ShopifyOrderIngestion.ReadPayload(mirror.Payload);
        if (job.Target == ShopifyCityCorrectionTarget.Order)
        {
            if (ShopifyOrderImporter.ShippingPartyFields(snapshot) is not { } fields)
            {
                return (null, ShopifyCityCorrectionSkipReasons.PartyIncomplete);
            }

            var party = new ShopifyPartyDraft(fields.Name, fields.Phone, fields.Email, fields.Address, synonym.DepartmentId, synonym.CityId);
            return (await writer.AttachShippingAsync(
                job.TenantId, job.TargetId, party, ShopifyOrderImporter.RawAddressLabel, cancellationToken), null);
        }

        // D-C5: con la configuración actual de la tienda; si ya no tiene clasificación por defecto, el cliente sigue incompleto.
        var store = await stores.FindAsync(job.TenantId, mirror.ConnectionId, cancellationToken);
        if (store is null || ShopifyCustomerResolver.CompleteDataWithCity(store, snapshot, synonym.CityId) is not { } data)
        {
            return (null, ShopifyCityCorrectionSkipReasons.CustomerNotCompletable);
        }

        return (await writer.CompleteCustomerAsync(
            job.TenantId, job.TargetId, IncompleteCustomerVersion, data, cancellationToken), null);
    }

    // La huella sale de lo que devolvió el escritor (la parte como la guardó Quotations, la versión y la ciudad que dejó
    // Customers), nunca de lo que se le mandó: en un reintento la ciudad escrita puede ser la que el sinónimo tenía antes. Un Recorrect actualiza la suya y conserva el origen: lo que casó al importarse sigue siendo de la
    // importación.
    private ShopifyCityCorrection RecordCorrection(
        ShopifyCityCorrectionJob job,
        ShopifyCitySynonymRef synonym,
        ShopifyCityCorrection? existing,
        ShopifyCityWriteResult result,
        DateTimeOffset now)
    {
        var isOrder = job.Target == ShopifyCityCorrectionTarget.Order;
        if (existing is not null)
        {
            if (isOrder)
            {
                existing.Recorrected(result.Party ?? throw MissingFingerprint(job), now);
            }
            else
            {
                existing.Recorrected(
                    result.CustomerCityId ?? throw MissingFingerprint(job), result.CustomerVersion ?? throw MissingFingerprint(job), now);
            }

            return existing;
        }

        var correction = isOrder
            ? ShopifyCityCorrection.ForOrder(
                job.TenantId,
                job.SynonymId,
                ShopifyCityCorrectionOrigin.Correction,
                job.TargetId,
                job.MirrorOrderId!.Value,
                result.Party ?? throw MissingFingerprint(job),
                now)
            : ShopifyCityCorrection.ForCustomer(
                job.TenantId,
                job.SynonymId,
                ShopifyCityCorrectionOrigin.Correction,
                job.TargetId,
                job.MirrorOrderId,
                synonym.DepartmentId,
                result.CustomerCityId ?? throw MissingFingerprint(job),
                result.CustomerVersion ?? throw MissingFingerprint(job),
                now);
        corrections.Add(correction);
        return correction;
    }

    private async Task SkipAsync(
        ShopifyCityCorrectionJob job, string reason, IShopifyTransaction transaction, CancellationToken cancellationToken)
    {
        job.MarkSkipped(reason, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Lo que otro trabajo ya escribió: la parte de envío del pedido ya está puesta; el cliente ya no es el incompleto de la
    // importación.
    private static string AlreadyDoneReason(ShopifyCityCorrectionTarget target) =>
        target == ShopifyCityCorrectionTarget.Order
            ? ShopifyCityCorrectionSkipReasons.ShippingPresent
            : ShopifyCityCorrectionSkipReasons.CustomerNotEligible;

    private static string SkipReasonFor(ShopifyCityWriteKind kind, ShopifyCityCorrectionTarget target) => kind switch
    {
        ShopifyCityWriteKind.NotFound => target == ShopifyCityCorrectionTarget.Order
            ? ShopifyCityCorrectionSkipReasons.OrderNotFound
            : ShopifyCityCorrectionSkipReasons.CustomerNotFound,
        ShopifyCityWriteKind.NotPending => ShopifyCityCorrectionSkipReasons.OrderNotPending,
        ShopifyCityWriteKind.StorePickup => ShopifyCityCorrectionSkipReasons.StorePickup,
        ShopifyCityWriteKind.ShippingPresent => ShopifyCityCorrectionSkipReasons.ShippingPresent,
        ShopifyCityWriteKind.ShippingChanged => ShopifyCityCorrectionSkipReasons.ShippingChanged,
        ShopifyCityWriteKind.PartyIncomplete => ShopifyCityCorrectionSkipReasons.PartyIncomplete,
        ShopifyCityWriteKind.NotEligible => ShopifyCityCorrectionSkipReasons.CustomerNotEligible,
        ShopifyCityWriteKind.NotCompletable => ShopifyCityCorrectionSkipReasons.CustomerNotCompletable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a refusal."),
    };

    private static string AuditAction(ShopifyCityCorrectionJob job) => (job.Kind, job.Target) switch
    {
        (ShopifyCityCorrectionJobKind.Resolve, ShopifyCityCorrectionTarget.Order) => ShopifyAuditActions.OrderCityResolved,
        (ShopifyCityCorrectionJobKind.Resolve, _) => ShopifyAuditActions.CustomerCityResolved,
        (_, ShopifyCityCorrectionTarget.Order) => ShopifyAuditActions.OrderCityCorrected,
        _ => ShopifyAuditActions.CustomerCityCorrected,
    };

    private static InvalidOperationException MissingFingerprint(ShopifyCityCorrectionJob job) =>
        new($"The city correction writer succeeded on job {job.Id} without the fingerprint of its {job.Target}.");
}

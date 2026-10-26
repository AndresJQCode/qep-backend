using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-26 §4.4 y §4.6: el trabajo de corrección de Shopify → Quotations (<see cref="IExternalOrderWriter"/>) y Customers
/// (<see cref="ICustomerProvisioning"/>). Traduce los tipos propios de Shopify y los resultados de cada módulo a
/// <see cref="ShopifyCityWriteResult"/>.
/// <para><b>Cada llamada corre en su propio scope</b>, como <c>ShopifyOrderWriter</c>: cada una confirma lo suyo, y lo que Quotations
/// o Customers dejen en su tracker no se mezcla con la unidad de trabajo de Shopify del trabajo.</para>
/// <para>Lo que lanzan (<c>concurrency.conflict</c>, la base, la red) se propaga: el trabajo no se guarda y el worker reintenta.
/// Quotations y Customers son idempotentes con lo que ya hicieron (<c>AlreadyApplied</c>), así que el reintento no escribe dos
/// veces.</para>
/// </summary>
internal sealed class ShopifyCityCorrectionWriter(IServiceScopeFactory scopes) : IShopifyCityCorrectionWriter
{
    public async Task<ShopifyCityWriteResult> AttachShippingAsync(
        Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(party);
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>().AttachShippingAsync(
            tenantId,
            orderId,
            new ExternalShippingParty(party.Name, party.Phone, party.Email, party.Address, party.DepartmentId, party.CityId),
            notesLinePrefix,
            cancellationToken);
        return FromOrder(outcome);
    }

    public async Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>().CorrectShippingCityAsync(
            tenantId,
            orderId,
            new ExternalShippingParty(expected.Name, expected.Phone, expected.Email, expected.Address, expected.DepartmentId, expected.CityId),
            departmentId,
            cityId,
            cancellationToken);
        return FromOrder(outcome);
    }

    public async Task<ShopifyCityWriteResult> CompleteCustomerAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        ShopifyCompleteCustomer data,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ICustomerProvisioning>().CompleteExternalSaleAsync(
            tenantId, customerId, expectedVersion, ShopifyCustomerDirectory.ToData(data), requestedCityIds, cancellationToken);
        return FromCustomer(outcome);
    }

    public async Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        Guid previousCityId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ICustomerProvisioning>().CorrectCityAsync(
            tenantId, customerId, expectedVersion, previousCityId, cityId, requestedCityIds, cancellationToken);
        return FromCustomer(outcome);
    }

    // La huella sólo viaja con un éxito: es la parte como quedó guardada (en un reintento, quizá en la ciudad que el sinónimo tenía
    // antes), que es contra lo que la próxima recorrección compara. Un
    // pedido que no es de una plataforma (order.order.source_invalid) es, para Shopify, un pedido que no encuentra.
    private static ShopifyCityWriteResult FromOrder(ExternalShippingOutcome outcome) => outcome.Kind switch
    {
        ExternalShippingOutcomeKind.Applied => new(ShopifyCityWriteKind.Applied, ShopifyOrderWriter.Fingerprint(outcome.Party), null, null),
        ExternalShippingOutcomeKind.AlreadyApplied =>
            new(ShopifyCityWriteKind.AlreadyApplied, ShopifyOrderWriter.Fingerprint(outcome.Party), null, null),
        ExternalShippingOutcomeKind.NotFound => Refused(ShopifyCityWriteKind.NotFound),
        ExternalShippingOutcomeKind.NotExternal => Refused(ShopifyCityWriteKind.NotFound, ExternalOrderErrors.SourceInvalid),
        ExternalShippingOutcomeKind.NotPending => Refused(ShopifyCityWriteKind.NotPending, outcome.Status?.ToString()),
        ExternalShippingOutcomeKind.StorePickup => Refused(ShopifyCityWriteKind.StorePickup),
        ExternalShippingOutcomeKind.ShippingPresent => Refused(ShopifyCityWriteKind.ShippingPresent),
        ExternalShippingOutcomeKind.ShippingChanged => Refused(ShopifyCityWriteKind.ShippingChanged),
        ExternalShippingOutcomeKind.PartyIncomplete => Refused(ShopifyCityWriteKind.PartyIncomplete),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "Unknown external shipping outcome."),
    };

    private static ShopifyCityWriteResult FromCustomer(ExternalCustomerCityOutcome outcome) => outcome.Kind switch
    {
        ExternalCustomerCityOutcomeKind.Applied => new(ShopifyCityWriteKind.Applied, null, outcome.Version, null, outcome.CityId),
        ExternalCustomerCityOutcomeKind.AlreadyApplied =>
            new(ShopifyCityWriteKind.AlreadyApplied, null, outcome.Version, null, outcome.CityId),
        ExternalCustomerCityOutcomeKind.NotFound => Refused(ShopifyCityWriteKind.NotFound),
        ExternalCustomerCityOutcomeKind.NotEligible => Refused(ShopifyCityWriteKind.NotEligible, outcome.Reason),
        ExternalCustomerCityOutcomeKind.NotCompletable => Refused(ShopifyCityWriteKind.NotCompletable, outcome.Reason),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "Unknown external customer city outcome."),
    };

    private static ShopifyCityWriteResult Refused(ShopifyCityWriteKind kind, string? detail = null) => new(kind, null, null, detail);
}

using System.Reflection;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-24 §11.1.4: lo que Quotations le pregunta a Shopify antes de guardar la entrega de un pedido de la tienda,
/// con las mismas reglas con las que el worker arma el envío.</summary>
public sealed class ExternalOrderDeliveryCheckTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid RegionId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyOrderMirror Mirror()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        return ShopifyOrderMirror.Create(
            TenantId, Guid.CreateVersion7(), 5_550_000_000_001L, "{}", now,
            new ShopifyOrderHeader("#1001", null, now, null, "paid", "COP", 119_000m, true));
    }

    private static ExternalOrderDeliveryCheck Check(
        ShopifyOrderMirror? mirror, (string CountryCode, string Code)? region = null) =>
        new(
            Port<IShopifyOrderMirrorRepository>(
                nameof(IShopifyOrderMirrorRepository.FindByQepOrderIdAsync),
                args => Task.FromResult(Equals(args[0], TenantId) && Equals(args[1], OrderId) ? mirror : null)),
            Port<IShopifyGeographyLookup>(
                nameof(IShopifyGeographyLookup.DescribeRegionAsync),
                args => Task.FromResult(Equals(args[0], RegionId) ? region : null)));

    [Fact]
    public async Task ShopifySpainWithoutRegionIsRejected() =>
        Assert.Equal("order.delivery.shopify_region_required", await Check(Mirror()).CheckAsync(TenantId, OrderId, "ES", null, Ct));

    // Una provincia que la tabla no conoce: hoy no hay ninguna sembrada, así que sólo se puede probar con un doble.
    [Fact]
    public async Task ShopifyUnmappedRegionIsRejected() =>
        Assert.Equal(
            "order.delivery.shopify_region_unmapped",
            await Check(Mirror(), ("ES", "99")).CheckAsync(TenantId, OrderId, "ES", RegionId, Ct));

    [Theory]
    [InlineData("CO")]
    [InlineData("PT")]
    public async Task ACountryTheWorkerCanSendIsAccepted(string country) =>
        Assert.Null(await Check(Mirror()).CheckAsync(TenantId, OrderId, country, null, Ct));

    [Fact]
    public async Task AMappedSpanishRegionIsAccepted() =>
        Assert.Null(await Check(Mirror(), ("ES", "28")).CheckAsync(TenantId, OrderId, "ES", RegionId, Ct));

    // Sin fila del espejo no hay a quién empujar la dirección.
    [Fact]
    public async Task AnOrderWithoutMirrorRowIsNotChecked() =>
        Assert.Null(await Check(mirror: null).CheckAsync(TenantId, OrderId, "ES", null, Ct));

    /// <summary>Un puerto que sólo contesta un método; cualquier otro lanza.</summary>
    private static T Port<T>(string method, Func<object?[], object> answer)
        where T : class
    {
        var port = DispatchProxy.Create<T, OneMethodProxy>();
        var proxy = (OneMethodProxy)(object)port;
        proxy.Method = method;
        proxy.Answer = answer;
        return port;
    }

    // DispatchProxy deriva en runtime de este tipo: no puede ir sellado.
#pragma warning disable CA1852
    public class OneMethodProxy : DispatchProxy
    {
        public string Method { get; set; } = string.Empty;

        public Func<object?[], object> Answer { get; set; } = _ => throw new InvalidOperationException("No answer configured.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == Method
                ? Answer(args!)
                : throw new InvalidOperationException($"The delivery check must not call {targetMethod?.Name}.");
    }
#pragma warning restore CA1852
}

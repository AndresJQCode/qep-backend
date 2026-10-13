using Modules.Pipeline.Infrastructure.Messaging;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.5: el payload es contrato y se lee por nombre. Uno roto no se reintenta para siempre: se lee como
/// inválido y el worker lo marca procesado.</summary>
public sealed class QuotationEventPayloadsTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid QuotationId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");
    private static readonly Guid ClientId = Guid.Parse("01900000-0000-7000-8000-0000000000e3");
    private static readonly Guid AdvisorId = Guid.Parse("01900000-0000-7000-8000-0000000000e4");
    private static readonly Guid OrderId = Guid.Parse("01900000-0000-7000-8000-0000000000e5");

    private static string Sent(string extra = "") =>
        $$"""{"tenantId":"{{TenantId}}","quotationId":"{{QuotationId}}","quotationNumber":"COT-0007","clientId":"{{ClientId}}","advisorMemberId":"{{AdvisorId}}","total":1234.50,"currency":"USD","occurredAt":"2026-10-13T15:00:00+00:00"{{extra}}}""";

    [Fact]
    public void ASentPayloadIsReadByName()
    {
        Assert.True(QuotationEventPayloads.TryReadQuotation(Sent(), out var fact));

        Assert.Equal((TenantId, QuotationId, "COT-0007", ClientId, AdvisorId), (fact!.TenantId, fact.QuotationId, fact.QuotationNumber, fact.ClientId, fact.AdvisorMemberId));
        Assert.Equal((1234.50m, "USD", (Guid?)null), (fact.Total, fact.Currency, fact.OrderId));
    }

    [Fact]
    public void AConvertedPayloadCarriesTheOrder()
    {
        Assert.True(QuotationEventPayloads.TryReadQuotation(Sent($$""","orderId":"{{OrderId}}","orderNumber":"PED-0003" """), out var fact));

        Assert.Equal((Guid?)OrderId, fact!.OrderId);
    }

    [Fact]
    public void ACancelledPayloadIsReadByName()
    {
        var json = $$"""{"tenantId":"{{TenantId}}","orderId":"{{OrderId}}","quotationId":"{{QuotationId}}","occurredAt":"2026-10-13T15:00:00+00:00"}""";

        Assert.True(QuotationEventPayloads.TryReadOrderCancelled(json, out var fact));

        Assert.Equal((TenantId, OrderId, QuotationId), (fact!.TenantId, fact.OrderId, fact.QuotationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"tenantId":"x"}""")]
    public void ABrokenPayloadIsNotRead(string json)
    {
        Assert.False(QuotationEventPayloads.TryReadQuotation(json, out _));
        Assert.False(QuotationEventPayloads.TryReadOrderCancelled(json, out _));
    }

    [Fact]
    public void ABlankNumberOrCurrencyIsNotRead()
    {
        Assert.False(QuotationEventPayloads.TryReadQuotation(Sent().Replace("\"COT-0007\"", "\"\"", StringComparison.Ordinal), out _));
        Assert.False(QuotationEventPayloads.TryReadQuotation(Sent().Replace("\"USD\"", "null", StringComparison.Ordinal), out _));
    }
}

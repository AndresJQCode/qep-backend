using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Messaging;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-26, «Contrato de eventos» del plan: el payload lo escribe <c>GeographyEventPublisher</c> y Shopify lo lee por nombre.
/// Estos literales son los mismos de <c>GeographyEventPublisherTests</c> (con los ids de la prueba): si cambias uno, cambia el otro.
/// Un payload roto es determinista; reintentarlo no lo arregla, así que se lee como inválido.
/// </summary>
public sealed class CitySynonymEventPayloadsTests
{
    private const string ResolvedGolden = """
        {"synonymId":"01900000-0000-7000-8000-000000000001","departmentId":"01900000-0000-7000-8000-000000000002","cityId":"01900000-0000-7000-8000-000000000003","normalizedText":"CALLI","operatorSubjectId":"01900000-0000-7000-8000-000000000004","operatorTenantId":"01900000-0000-7000-8000-000000000005",
         "occurredAt":"2026-10-26T15:00:00+00:00",
         "occurrences":[{"occurrenceId":"01900000-0000-7000-8000-000000000006","tenantId":"01900000-0000-7000-8000-000000000007","source":"shopify","sourceRef":"01900000-0000-7000-8000-000000000008","orderId":"01900000-0000-7000-8000-000000000009","customerId":null,
                         "partyCityOnly":true,"customerCityOnly":false}]}
        """;

    private const string CorrectedGolden = """
        {"synonymId":"01900000-0000-7000-8000-000000000001","departmentId":"01900000-0000-7000-8000-000000000002","previousCityId":"01900000-0000-7000-8000-00000000000a","cityId":"01900000-0000-7000-8000-000000000003","operatorSubjectId":"01900000-0000-7000-8000-000000000004","operatorTenantId":"01900000-0000-7000-8000-000000000005",
         "occurredAt":"2026-10-26T15:00:00+00:00"}
        """;

    private static Guid Id(int suffix) => Guid.Parse($"01900000-0000-7000-8000-{suffix:x12}");

    [Fact]
    public void TheResolvedGoldenPayloadReads()
    {
        Assert.True(CitySynonymEventPayloads.TryReadResolved(ResolvedGolden, out var fact));

        Assert.Equal(
            (Id(1), Id(2), Id(3), "CALLI", Id(4), Id(5)),
            (fact.SynonymId, fact.DepartmentId, fact.CityId, fact.NormalizedText, fact.OperatorSubjectId, fact.OperatorTenantId));
        Assert.Equal(
            new CitySynonymOccurrenceFact(Id(6), Id(7), "shopify", Id(8), Id(9), null, PartyCityOnly: true, CustomerCityOnly: false),
            Assert.Single(fact.Occurrences));
    }

    [Fact]
    public void TheCorrectedGoldenPayloadReads()
    {
        Assert.True(CitySynonymEventPayloads.TryReadCorrected(CorrectedGolden, out var fact));

        Assert.Equal(new CitySynonymCorrectedFact(Id(1), Id(2), Id(10), Id(3), Id(4), Id(5)), fact);
    }

    // Cada forma rota que reintentar no arregla: no es JSON, falta un campo, un id que no es Guid, un texto en blanco, un
    // booleano que no lo es, una ocurrencia sin origen.
    [Fact]
    public void AMalformedPayloadIsRejected()
    {
        string[] resolved =
        [
            "not json",
            ResolvedGolden.Replace("\"synonymId\"", "\"synonym\"", StringComparison.Ordinal),
            ResolvedGolden.Replace("01900000-0000-7000-8000-000000000003", "CALI", StringComparison.Ordinal),
            ResolvedGolden.Replace("\"CALLI\"", "\"  \"", StringComparison.Ordinal),
            ResolvedGolden.Replace("\"partyCityOnly\":true", "\"partyCityOnly\":\"yes\"", StringComparison.Ordinal),
            ResolvedGolden.Replace("\"source\":\"shopify\"", "\"source\":null", StringComparison.Ordinal),
            ResolvedGolden.Replace("\"customerId\":null", "\"customerId\":42", StringComparison.Ordinal),
            ResolvedGolden.Replace("\"occurrences\":[", "\"occurrences\":{\"x\":[", StringComparison.Ordinal).Replace("}]}", "}]}}", StringComparison.Ordinal),
        ];
        string[] corrected =
        [
            "[]",
            CorrectedGolden.Replace("\"previousCityId\"", "\"previous\"", StringComparison.Ordinal),
            CorrectedGolden.Replace("01900000-0000-7000-8000-000000000004", "", StringComparison.Ordinal),
        ];

        Assert.All(resolved, json => Assert.False(CitySynonymEventPayloads.TryReadResolved(json, out _)));
        Assert.All(corrected, json => Assert.False(CitySynonymEventPayloads.TryReadCorrected(json, out _)));
    }
}

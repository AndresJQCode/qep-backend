using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// «Contrato de eventos» del plan (ruling P7): el payload que escribe <c>GeographyEventPublisher</c> es el que lee
/// <c>CitySynonymEventPayloads</c> (Shopify) por nombre. Estos literales son los mismos de <c>CitySynonymEventPayloadsTests</c>,
/// con los ids de la prueba: si cambias uno, cambia el otro. Se compara lo guardado en <c>platform.outbox_messages</c> como JSON
/// (la columna es <c>jsonb</c>, que reordena las claves), además del nombre del evento y la correlación.
/// </summary>
public sealed class GeographyEventPublisherTests
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

    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    private static Guid Id(int suffix) => Guid.Parse($"01900000-0000-7000-8000-{suffix:x12}");

    // La ocurrencia no tiene constructor (la escribe el SQL del reporte): la siembra el reporter con los ids 7, 8 y 9 de la
    // prueba, en un departamento real (la fila tiene FK). El id de la ocurrencia y el del departamento son los que dio la base, y
    // reemplazan a los ids 6 y 2 del literal.
    [Fact]
    public async Task TheResolvedPayloadMatchesTheGoldenContract()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var valle = await CityOperatorApiTests.DepartmentAsync(factory, "76");
        await CityOperatorApiTests.ReportAsync(factory, CityOperatorApiTests.Report(valle, "Calli", OccurredAt) with
        {
            TenantId = Id(7),
            SourceRef = Id(8),
            OrderId = Id(9),
            PartyCityOnly = true,
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var occurrence = await dbContext.UnresolvedCityOccurrences.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var synonym = CitySynonym.Create(new CitySynonymId(Id(1)), valle, "Calli", new CityId(Id(3)), Id(4), OccurredAt);

        scope.ServiceProvider.GetRequiredService<IGeographyEventPublisher>()
            .PublishSynonymResolved(synonym, [occurrence], Id(5), OccurredAt);
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);

        var expected = ResolvedGolden
            .Replace(Id(2).ToString(), valle.Value.ToString(), StringComparison.Ordinal)
            .Replace(Id(6).ToString(), occurrence.Id.ToString(), StringComparison.Ordinal);
        await AssertOutboxAsync(dbContext, CitySynonymEventNames.Resolved, Id(1), expected);
    }

    [Fact]
    public async Task TheCorrectedPayloadMatchesTheGoldenContract()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        // El sinónimo no se guarda: sólo el mensaje del outbox, que no tiene FK.
        var synonym = CitySynonym.Create(
            new CitySynonymId(Id(1)), new DepartmentId(Id(2)), "Calli", new CityId(Id(10)), Guid.CreateVersion7(), OccurredAt);
        var previous = synonym.ChangeCity(new CityId(Id(3)), Id(4), OccurredAt);

        scope.ServiceProvider.GetRequiredService<IGeographyEventPublisher>()
            .PublishSynonymCorrected(synonym, previous, Id(5), OccurredAt);
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);

        await AssertOutboxAsync(dbContext, CitySynonymEventNames.Corrected, Id(1), CorrectedGolden);
    }

    private static async Task AssertOutboxAsync(GeographyDbContext dbContext, string eventName, Guid synonymId, string expectedJson)
    {
        var rows = await dbContext.Database.SqlQuery<OutboxRow>($"""
            SELECT event_name AS "EventName", correlation_id AS "CorrelationId", payload::text AS "Payload"
            FROM platform.outbox_messages
            WHERE event_name LIKE 'geography.%'
            """).ToListAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal(eventName, row.EventName);
        Assert.Equal(synonymId.ToString(), row.CorrelationId);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(row.Payload)),
            $"Expected {expectedJson}{Environment.NewLine}Actual {row.Payload}");
    }

    private sealed record OutboxRow(string EventName, string CorrelationId, string Payload);
}

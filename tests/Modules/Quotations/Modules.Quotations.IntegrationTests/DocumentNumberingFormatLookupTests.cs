using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El lector del formato de numeración (spec 2026-09-17): la fila del tenant, o el default si no
/// hay. Contra la base de verdad y no contra un doble, porque lo que se prueba es justo el SQL y el
/// mapeo — y los CHECK, que sólo existen ahí.
/// </summary>
public sealed class DocumentNumberingFormatLookupTests
{
    [Fact]
    public async Task ATenantWithoutARowGetsTheDefaultFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();

        var quotation = await LookupAsync(factory, tenantId, DocumentNumberType.Quotation);
        var order = await LookupAsync(factory, tenantId, DocumentNumberType.Order);

        Assert.Equal("QUO-", quotation.Prefix);
        Assert.Equal("PED-", order.Prefix);
        Assert.True(quotation.IncludeYear);
        Assert.True(order.IncludeYear);
        Assert.Equal("-", quotation.YearSeparator);
        Assert.Equal(4, quotation.MinDigits);
        Assert.Equal(4, order.MinDigits);
    }

    [Fact]
    public async Task ATenantWithARowGetsItsOwnFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 1);

        var order = await LookupAsync(factory, tenantId, DocumentNumberType.Order);

        Assert.Equal("PW", order.Prefix);
        Assert.False(order.IncludeYear);
        Assert.Equal("", order.YearSeparator);
        Assert.Equal(1, order.MinDigits);
    }

    // La fila es por (tenant, tipo): configurar pedidos no toca cotizaciones.
    [Fact]
    public async Task TheRowOfOneDocumentTypeDoesNotAffectTheOther()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 1);

        var quotation = await LookupAsync(factory, tenantId, DocumentNumberType.Quotation);

        Assert.Equal("QUO-", quotation.Prefix);
        Assert.True(quotation.IncludeYear);
    }

    // La fila de un tenant no la ve otro: el aislamiento es por la PK, no por un filtro del handler.
    [Fact]
    public async Task TheRowOfOneTenantDoesNotAffectAnother()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var configured = Guid.CreateVersion7();
        var untouched = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, configured, "order", "PW", includeYear: false, "", 1);

        var order = await LookupAsync(factory, untouched, DocumentNumberType.Order);

        Assert.Equal("PED-", order.Prefix);
    }

    /// <summary>
    /// El CHECK es la única red de una tabla que se escribe a mano. Un min_digits de 11 no entra, así
    /// que el lector nunca ve un formato imposible.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRejectsAnOutOfRangeFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 11));

        Assert.Equal("CK_document_numbering_formats_min_digits", error.ConstraintName);
    }

    private static async Task<DocumentNumberFormat> LookupAsync(
        QepApiFactory factory, Guid tenantId, DocumentNumberType documentType)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentNumberingFormatLookup>()
            .GetAsync(tenantId, documentType, TestContext.Current.CancellationToken);
    }

    /// <summary>El mismo UPSERT del runbook del README, palabra por palabra: si el runbook deja de
    /// funcionar, estas pruebas se caen con él.</summary>
    internal static async Task SetDocumentNumberFormatAsync(
        QepApiFactory factory,
        Guid tenantId,
        string documentType,
        string prefix,
        bool includeYear,
        string yearSeparator,
        int minDigits)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.document_numbering_formats
                   (tenant_id, document_type, prefix, include_year, year_separator, min_digits)
            VALUES ({tenantId}, {documentType}, {prefix}, {includeYear}, {yearSeparator}, {minDigits})
            ON CONFLICT (tenant_id, document_type) DO UPDATE
            SET prefix = EXCLUDED.prefix, include_year = EXCLUDED.include_year,
                year_separator = EXCLUDED.year_separator, min_digits = EXCLUDED.min_digits
            """,
            TestContext.Current.CancellationToken);
    }
}

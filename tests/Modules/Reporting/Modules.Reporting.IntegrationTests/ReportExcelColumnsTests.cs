using ClosedXML.Excel;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Las columnas de los cuatro Excel, con sus nombres y su orden exacto.
///
/// El contrato de API las fija una por una, y son lo unico del reporte que ninguna otra prueba
/// mira: las de cada reporte verifican el status y el content-type, pero un archivo con las
/// columnas cambiadas de orden pasa esas igual y llega roto a quien lo abre.
///
/// Los cuatro reportes se siembran y se descargan en una sola prueba, contra un solo contenedor:
/// levantar cuatro Postgres para leer cuatro filas de encabezado es minuto y medio de reloj a
/// cambio de nada.
/// </summary>
public sealed class ReportExcelColumnsTests
{
    [Fact]
    public async Task EveryExportHasTheColumnsTheContractFixes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToSaleAsync(client, factory, tenant.TenantId, quotation);

        Assert.Equal(
            [
                "Numero Venta", "Numero Cotizacion", "Fecha", "Asesor", "Cliente", "CUC",
                "Estado", "Estado Pago", "Subtotal", "Impuesto", "Total"
            ],
            await HeaderRowAsync(client, tenant.TenantId, "sales"));

        Assert.Equal(
            [
                "Numero", "Fecha", "Valida Hasta", "Asesor", "Cliente", "CUC", "Estado",
                "Subtotal", "Impuesto", "Total"
            ],
            await HeaderRowAsync(client, tenant.TenantId, "quotations"));

        Assert.Equal(
            [
                "Fecha", "Producto", "Codigo", "Campo", "Escala Desde", "Escala Hasta",
                "Valor Anterior", "Valor Nuevo", "Diferencia", "Usuario"
            ],
            await HeaderRowAsync(client, tenant.TenantId, "price-changes"));

        Assert.Equal(
            [
                "CUC", "Nombre", "Tipo Identificacion", "Numero Identificacion", "Clasificacion",
                "Departamento", "Ciudad", "Activo", "Creado"
            ],
            await HeaderRowAsync(client, tenant.TenantId, "customers"));
    }

    /// <summary>El padron se escribe con "Si"/"No" y no con true/false: es el vocabulario de quien
    /// abre el archivo, y el mismo que ya usa la exportacion de clientes.</summary>
    [Fact]
    public async Task TheCustomerExportWritesTheActiveFlagAsSiOrNo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        await CreateActiveCustomerAsync(client, tenant.TenantId);

        using var workbook = await DownloadWorkbookAsync(client, tenant.TenantId, "customers");
        var sheet = workbook.Worksheets.First();

        Assert.Equal("Si", sheet.Cell(2, 8).GetString());
    }

    private static async Task<string[]> HeaderRowAsync(
        HttpClient client, Guid tenantId, string report)
    {
        using var workbook = await DownloadWorkbookAsync(client, tenantId, report);
        var sheet = workbook.Worksheets.First();
        return [.. sheet.Row(1).CellsUsed().Select(cell => cell.GetString())];
    }

    private static async Task<XLWorkbook> DownloadWorkbookAsync(
        HttpClient client, Guid tenantId, string report)
    {
        var response = await client.GetAsync(
            $"{ReportsUrl(tenantId)}/{report}/export", TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"El export de '{report}' devolvio {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var bytes = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);
        // El stream tiene que sobrevivir al workbook, asi que no se libera aca: XLWorkbook lo lee
        // completo al abrirlo, y el MemoryStream sobre un byte[] no tiene nada que soltar.
        return new XLWorkbook(new MemoryStream(bytes));
    }
}

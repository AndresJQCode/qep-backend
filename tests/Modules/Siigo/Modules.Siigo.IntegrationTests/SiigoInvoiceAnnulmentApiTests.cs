using System.Net;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>Spec 2026-11-25: la factura que una persona anuló en Siigo deja de ser vigente y libera el pedido, de punta a punta con el
/// host entero y el doble <see cref="FakeSiigoApi"/>. Ninguna prueba llama a Siigo.</summary>
public sealed class SiigoInvoiceAnnulmentApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Deja anuladas las facturas que existen en Siigo sin pasar por la acción: sólo el efecto que ven los lectores de la tabla.
    private static Task AnnulBySqlAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoices SET status = 'Annulled', error_code = 'siigo.invoice.annulled_in_siigo' "
            + "WHERE status IN ('Created','Stamped','StampRejected')");

    private static Task RemoveTheOrderMarkAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            """
            UPDATE quotations.orders
            SET invoiced_at = NULL, invoiced_by = NULL, invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL
            WHERE id = @id
            """,
            ("id", world.OrderId));

    // El índice parcial y SiigoOpenInvoiceCheck filtran por el texto del estado: los dos tienen que dejar por fuera a la anulada.
    [Fact]
    public async Task AnAnnulledRowNoLongerBlocksTheManualMarkNorANewInvoice()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        await RemoveTheOrderMarkAsync(world);
        await AnnulBySqlAsync(world);

        var manual = await world.Client.PostAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/invoice", null, Ct);

        Assert.True(manual.StatusCode == HttpStatusCode.OK, await manual.Content.ReadAsStringAsync(Ct));

        await using var otherDatabase = await StartDatabaseAsync();
        using var other = await ArrangeAsync(otherDatabase);
        await IssueRowsThroughBatchAsync(other);
        await RemoveTheOrderMarkAsync(other);
        await AnnulBySqlAsync(other);
        other.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-124", "id-2", 0m));

        var rows = await IssueRowsThroughBatchAsync(other);

        var row = rows[0];
        Assert.Equal(("Created", "FV-1-124"), (row.GetProperty("status").GetString(), row.GetProperty("number").GetString()));
        Assert.Equal(2, await ScalarAsync<long>(other.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE kind = 'Order'"));
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                other.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE kind = 'Order' AND status = 'Annulled'"));
    }
}

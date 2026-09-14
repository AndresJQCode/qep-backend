using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La columna de consumidor final, contra el modelo de EF y no contra una base. El nombre en
/// snake_case lo fija el mapeo a mano —por convencion EF la llamaria "BillsToFinalConsumer"— y un
/// nombre equivocado no lo ve el compilador: lo ve la migracion, que crearia otra columna.
/// </summary>
public sealed class QuotationsDbContextMappingTests
{
    [Fact]
    public void BillsToFinalConsumerMapsToItsSnakeCaseColumn()
    {
        // El factory de diseño arma el contexto sin abrir conexion: construir el modelo no la
        // necesita.
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        // El modelo de diseño y no `context.Model`: el de runtime puede recortar anotaciones que
        // solo usan las migraciones.
        var model = context.GetService<IDesignTimeModel>().Model;

        var property = model.FindEntityType(typeof(Quotation))!
            .FindProperty(nameof(Quotation.BillsToFinalConsumer))!;

        Assert.Equal("bills_to_final_consumer", property.GetColumnName());
        Assert.False(property.IsNullable);
    }

    /// <summary>
    /// El SQL crudo de la toma (ExportJobQueue) nombra las columnas a mano, así que un nombre que
    /// EF pusiera por convención rompería la toma sin que el compilador lo vea. Y `attempts` es
    /// token de concurrencia: es lo que impide que un worker con el lease vencido cierre un job
    /// que ya retomó otro.
    /// </summary>
    [Fact]
    public void ExportJobMapsToItsTableWithAttemptsAsConcurrencyToken()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var job = model.FindEntityType(typeof(ExportJob));

        Assert.NotNull(job);
        Assert.Equal("export_jobs", job.GetTableName());
        Assert.Equal("quotations", job.GetSchema());
        Assert.Equal(
            ["attempts", "completed_at", "file_name", "filters", "id", "kind", "last_error",
             "locked_until", "next_attempt_at", "requested_at", "requested_by", "row_count",
             "status", "tenant_id"],
            job.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("jsonb", job.FindProperty(nameof(ExportJob.Filters))!.GetColumnType());
        Assert.True(job.FindProperty(nameof(ExportJob.Attempts))!.IsConcurrencyToken);

        var claim = job.GetIndexes().Single(index => index.GetDatabaseName() == "IX_export_jobs_claim");
        Assert.Equal(["Status", "NextAttemptAt"], claim.Properties.Select(property => property.Name));
        Assert.Equal("status IN ('Pending', 'Processing')", claim.GetFilter());

        var requester = job.GetIndexes().Single(index => index.GetDatabaseName() == "IX_export_jobs_requester");
        Assert.Equal(
            ["TenantId", "RequestedBy", "Status"],
            requester.Properties.Select(property => property.Name));
        Assert.Equal("status IN ('Pending', 'Processing')", requester.GetFilter());
    }

    /// <summary>
    /// Las exportaciones leen por keyset (spec 2026-09-12, D8): cada lote pide lo que viene
    /// después de la última fila, en el orden del listado. Sin un índice que arranque por el
    /// tenant y siga por la clave de orden, cada lote recorre todas las filas del tenant.
    /// </summary>
    [Fact]
    public void QuotationsAndOrdersHaveAnIndexForTheExportKeyset()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var quotations = model.FindEntityType(typeof(Quotation))!.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_quotations_tenant_created_at_number");
        Assert.Equal(
            ["TenantId", "CreatedAt", "QuotationNumber"],
            quotations.Properties.Select(property => property.Name));

        var orders = model.FindEntityType(typeof(Order))!.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_orders_tenant_converted_at_number");
        Assert.Equal(
            ["TenantId", "ConvertedAt", "OrderNumber"],
            orders.Properties.Select(property => property.Name));
    }

    /// <summary>
    /// Los nombres de base de los pedidos (spec 2026-09-14). Van a mano en el mapeo y la migración
    /// que los renombra se escribió a mano: un nombre que no coincida no lo ve el compilador, lo ve
    /// la próxima migración generada, que intentaría recrear la tabla. La FK y la PK salen por
    /// convención del nombre de la tabla, así que también se fijan acá.
    /// </summary>
    [Fact]
    public void OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var order = model.FindEntityType(typeof(Order))!;
        Assert.Equal("orders", order.GetTableName());
        Assert.Equal("quotations", order.GetSchema());
        Assert.Equal("order_number", order.FindProperty(nameof(Order.OrderNumber))!.GetColumnName());
        Assert.Equal("PK_orders", order.FindPrimaryKey()!.GetName());
        Assert.Equal(
            ["IX_orders_quotation", "IX_orders_tenant", "IX_orders_tenant_converted_at_number", "IX_orders_tenant_number"],
            order.GetIndexes().Select(index => index.GetDatabaseName()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            "FK_orders_quotations_quotation_id",
            Assert.Single(order.GetForeignKeys()).GetConstraintName());

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        Assert.Equal("order_payment_proofs", proof.GetTableName());
        Assert.Equal("order_id", proof.FindProperty(nameof(OrderPaymentProof.OrderId))!.GetColumnName());
        Assert.Equal("PK_order_payment_proofs", proof.FindPrimaryKey()!.GetName());
        Assert.Equal("IX_order_payment_proofs_order", Assert.Single(proof.GetIndexes()).GetDatabaseName());
        Assert.Equal(
            "FK_order_payment_proofs_orders_order_id",
            Assert.Single(proof.GetForeignKeys()).GetConstraintName());

        var counter = model.FindEntityType(typeof(OrderNumberCounter))!;
        Assert.Equal("order_number_counters", counter.GetTableName());
        Assert.Equal("PK_order_number_counters", counter.FindPrimaryKey()!.GetName());
    }

    /// <summary>
    /// El modelo y el último snapshot describen la misma base. Renombrar un tipo CLR sin tocar
    /// tablas, columnas ni índices no pide migración (plan 2026-09-14, Task 1), y una migración
    /// generada y después escrita a mano tiene que dejar el snapshot al día (Tasks 2 y 5). No abre
    /// conexión: compara el modelo con el snapshot, no con una base.
    /// </summary>
    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}

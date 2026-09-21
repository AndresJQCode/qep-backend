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
    /// El unitario con descuento se deriva de cantidad, precio y descuento cada vez que se lee:
    /// no hay columna, no hay migracion, y una linea guardada antes de que el campo existiera lo
    /// reporta igual. Convertirlo en una propiedad con setter lo volveria persistido sin que el
    /// compilador diga nada, y ahi si haria falta una migracion.
    /// </summary>
    [Fact]
    public void TheDiscountedUnitPriceIsDerivedAndNeverPersisted()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var item = model.FindEntityType(typeof(QuotationItem))!;

        Assert.Null(item.FindProperty(nameof(QuotationItem.DiscountedUnitPrice)));
        Assert.NotNull(item.FindProperty(nameof(QuotationItem.DiscountAmount)));
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
        Assert.Equal(
            ["IX_order_payment_proofs_file", "IX_order_payment_proofs_order", "IX_order_payment_proofs_public_key"],
            proof.GetIndexes().Select(index => index.GetDatabaseName()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            "FK_order_payment_proofs_orders_order_id",
            Assert.Single(proof.GetForeignKeys()).GetConstraintName());

        var counter = model.FindEntityType(typeof(OrderNumberCounter))!;
        Assert.Equal("order_number_counters", counter.GetTableName());
        Assert.Equal("PK_order_number_counters", counter.FindPrimaryKey()!.GetName());
    }

    /// <summary>
    /// La clave de la copia pública de un comprobante (spec 2026-09-15, P5). Nullable, porque los
    /// privados no tienen. Sin el mapeo a mano EF la llamaría "PublicStorageKey", y el error lo vería
    /// recién la migración.
    /// </summary>
    [Fact]
    public void OrderPaymentProofPublicStorageKeyMapsToANullableColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        var property = proof.FindProperty(nameof(OrderPaymentProof.PublicStorageKey))!;

        Assert.Equal("public_storage_key", property.GetColumnName());
        Assert.True(property.IsNullable);
        Assert.Equal(200, property.GetMaxLength());
    }

    /// <summary>
    /// Spec 2026-09-16, D17: Storage consulta esta tabla en cada barrido, por archivo
    /// (IFileReferenceProbe) y por clave pública (IPublicObjectReferenceProbe). Un índice por columna,
    /// con nombre fijo: un nombre por convención no lo ve el compilador, lo ve la próxima migración.
    /// </summary>
    [Fact]
    public void OrderPaymentProofsHaveAnIndexForEachReferenceProbe()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var indexes = model.FindEntityType(typeof(OrderPaymentProof))!.GetIndexes().ToArray();

        var byFile = Assert.Single(indexes, index => index.GetDatabaseName() == "IX_order_payment_proofs_file");
        Assert.Equal([nameof(OrderPaymentProof.FileId)], byFile.Properties.Select(property => property.Name));
        Assert.False(byFile.IsUnique);

        var byPublicKey = Assert.Single(
            indexes, index => index.GetDatabaseName() == "IX_order_payment_proofs_public_key");
        Assert.Equal(
            [nameof(OrderPaymentProof.PublicStorageKey)], byPublicKey.Properties.Select(property => property.Name));
        Assert.False(byPublicKey.IsUnique);
    }

    /// <summary>
    /// Spec 2026-09-16: las tres columnas de la anulación, nullable (un pedido vivo no las tiene)
    /// y con el nombre en snake_case que fija el mapeo a mano. `cancelled_by` necesita la
    /// conversión de <see cref="MemberId"/>: sin ella EF no puede mapear el struct.
    /// </summary>
    [Fact]
    public void OrderCancellationMapsToNullableSnakeCaseColumns()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(Order))!;

        var cancelledAt = order.FindProperty(nameof(Order.CancelledAt))!;
        var cancelledBy = order.FindProperty(nameof(Order.CancelledBy))!;
        var reason = order.FindProperty(nameof(Order.CancellationReason))!;

        Assert.Equal("cancelled_at", cancelledAt.GetColumnName());
        Assert.Equal("cancelled_by", cancelledBy.GetColumnName());
        Assert.Equal("cancellation_reason", reason.GetColumnName());
        Assert.True(cancelledAt.IsNullable);
        Assert.True(cancelledBy.IsNullable);
        Assert.True(reason.IsNullable);
        Assert.Equal(Order.CancellationReasonMaxLength, reason.GetMaxLength());
    }

    /// <summary>
    /// La tabla de formatos de numeración (spec 2026-09-17). La PK es (tenant, tipo de documento) y
    /// los tres rangos del spec van como CHECK: es configuración que se escribe a mano con SQL, y el
    /// CHECK es la única red que no depende de quién corra ese SQL. Los nombres van a mano, así que
    /// un typo no lo ve el compilador: lo vería la próxima migración, creando otra columna.
    /// </summary>
    [Fact]
    public void DocumentNumberingFormatsMapToTheirTableColumnsAndCheckConstraints()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var format = model.FindEntityType(typeof(DocumentNumberingFormat));

        Assert.NotNull(format);
        Assert.Equal("document_numbering_formats", format.GetTableName());
        Assert.Equal("quotations", format.GetSchema());
        Assert.Equal(
            ["document_type", "include_year", "min_digits", "prefix", "tenant_id", "year_separator"],
            format.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("PK_document_numbering_formats", format.FindPrimaryKey()!.GetName());
        Assert.Equal(
            ["TenantId", "DocumentType"],
            format.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(10, format.FindProperty(nameof(DocumentNumberingFormat.Prefix))!.GetMaxLength());
        Assert.Equal(1, format.FindProperty(nameof(DocumentNumberingFormat.YearSeparator))!.GetMaxLength());
        Assert.Equal(
            ["CK_document_numbering_formats_document_type",
             "CK_document_numbering_formats_min_digits",
             "CK_document_numbering_formats_prefix",
             "CK_document_numbering_formats_year_separator"],
            format.GetCheckConstraints().Select(constraint => constraint.Name).Order(StringComparer.Ordinal));
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

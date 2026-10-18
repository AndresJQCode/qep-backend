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
    /// Mismo riesgo que arriba: por convencion EF llamaria "IsRetail" a la columna, y el mapeo a
    /// mano es lo unico que la deja en snake_case. NOT NULL ademas — una cotizacion anterior al
    /// campo es "no detal", no "no se sabe".
    /// </summary>
    [Fact]
    public void IsRetailMapsToItsSnakeCaseColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var property = model.FindEntityType(typeof(Quotation))!
            .FindProperty(nameof(Quotation.IsRetail))!;

        Assert.Equal("is_retail", property.GetColumnName());
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
        Assert.Null(item.FindProperty(nameof(QuotationItem.DiscountedUnitPriceWithoutTax)));
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
            ["IX_orders_quotation", "IX_orders_tenant", "IX_orders_tenant_advisor", "IX_orders_tenant_client",
             "IX_orders_tenant_converted_at_number", "IX_orders_tenant_external_key", "IX_orders_tenant_number",
             "IX_orders_tenant_source"],
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
    /// Spec 2026-10-15 §4 y §9: el pedido tiene cabecera, líneas, partes e historial propios;
    /// `quotation_id` es opcional con índice único parcial, y la cuenta de cobro es un owned
    /// obligatorio. Contra el modelo de diseño, por el mismo motivo que los casos de arriba.
    /// </summary>
    [Fact]
    public void OrdersOwnTheirHeaderLinesPartiesAndHistory()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(Order))!;

        Assert.Equal("client_id", order.FindProperty(nameof(Order.ClientId))!.GetColumnName());
        Assert.Equal("source", order.FindProperty(nameof(Order.Source))!.GetColumnName());
        Assert.True(order.FindProperty(nameof(Order.QuotationId))!.IsNullable);
        Assert.Equal("net_total", order.FindProperty(nameof(Order.NetTotal))!.GetColumnName());
        Assert.Equal("text", order.FindProperty(nameof(Order.Notes))!.GetColumnType());
        var quotationIndex = order.GetIndexes().Single(index => index.GetDatabaseName() == "IX_orders_quotation");
        Assert.True(quotationIndex.IsUnique);
        Assert.Equal("quotation_id IS NOT NULL", quotationIndex.GetFilter());

        Assert.Equal("order_items", model.FindEntityType(typeof(OrderItem))!.GetTableName());
        Assert.Equal("order_parties", model.FindEntityType(typeof(OrderParty))!.GetTableName());
        Assert.Equal("order_history", model.FindEntityType(typeof(OrderHistoryEntry))!.GetTableName());
        var billing = order.FindNavigation(nameof(Order.BillingAccount))!;
        Assert.True(billing.ForeignKey.IsOwnership);
        Assert.True(billing.ForeignKey.IsRequiredDependent);
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
    /// La fecha de pago de un comprobante (a pedido, 2026-10-08): `date` y no `timestamp`, porque
    /// es la fecha del soporte y no un instante; nullable, porque los comprobantes anteriores al
    /// campo no la tienen.
    /// </summary>
    [Fact]
    public void OrderPaymentProofPaidOnMapsToANullableDateColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        var property = proof.FindProperty(nameof(OrderPaymentProof.PaidOn))!;

        Assert.Equal("paid_on", property.GetColumnName());
        Assert.True(property.IsNullable);
        Assert.Equal("date", property.GetColumnType());
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
    /// Spec 2026-10-05: las dos columnas de la facturación, nullable (un pedido sin facturar no las
    /// tiene, y revertir las vuelve a null) y con el nombre en snake_case que fija el mapeo a mano.
    /// `invoiced_by` necesita la conversión de <see cref="MemberId"/>: sin ella EF no puede mapear
    /// el struct.
    /// </summary>
    [Fact]
    public void OrderInvoicingMapsToNullableSnakeCaseColumns()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(Order))!;

        var invoicedAt = order.FindProperty(nameof(Order.InvoicedAt))!;
        var invoicedBy = order.FindProperty(nameof(Order.InvoicedBy))!;

        Assert.Equal("invoiced_at", invoicedAt.GetColumnName());
        Assert.Equal("invoiced_by", invoicedBy.GetColumnName());
        Assert.True(invoicedAt.IsNullable);
        Assert.True(invoicedBy.IsNullable);
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
    /// El layout de columnas del Excel de pedidos (spec 2026-09-24, D6 y D7): la PK es el tenant y
    /// las columnas van en una sola jsonb, con los nombres del JSON fijados a mano. Es el primer
    /// OwnsMany().ToJson() del repo: un nombre por convención ("Kind", "Header") no lo ve el
    /// compilador, lo ve quien lea la fila a mano y el frontend que no la lee.
    /// </summary>
    [Fact]
    public void OrdersExportLayoutMapsToItsTableWithTheColumnsInOneJsonColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var layout = model.FindEntityType(typeof(OrdersExportLayout));
        Assert.NotNull(layout);
        Assert.Equal("orders_export_layouts", layout.GetTableName());
        Assert.Equal("quotations", layout.GetSchema());
        Assert.Equal("PK_orders_export_layouts", layout.FindPrimaryKey()!.GetName());
        Assert.Equal(["TenantId"], layout.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            ["sheet_name", "tenant_id", "updated_at", "version"],
            layout.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.True(layout.FindProperty(nameof(OrdersExportLayout.Version))!.IsConcurrencyToken);

        // Spec 2026-10-05, D2 y D3: columna propia y no dentro del jsonb, con el tope de Excel y el
        // default que conserva el nombre de toda fila que ya existía.
        var sheetName = layout.FindProperty(nameof(OrdersExportLayout.SheetName))!;
        Assert.False(sheetName.IsNullable);
        Assert.Equal(OrdersExportLayout.SheetNameMaxLength, sheetName.GetMaxLength());
        Assert.Equal(OrdersExportLayout.DefaultSheetName, sheetName.GetDefaultValue());

        var setting = model.FindEntityType(typeof(OrdersExportColumnSetting));
        Assert.NotNull(setting);
        Assert.True(setting.IsOwned());
        Assert.Equal("columns", setting.GetContainerColumnName());
        // Sin las de sombra: el ordinal sintetizado y la FK al layout no viajan en el JSON.
        Assert.Equal(
            ["header", "key", "kind", "value", "visible"],
            setting.GetProperties()
                .Where(property => !property.IsShadowProperty())
                .Select(property => property.GetJsonPropertyName()!)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// El código y el nombre del producto congelados al enviar (owner, 2026-09-26). Nulos: las
    /// líneas de un borrador y las enviadas antes de la columna no los tienen y se leen en vivo.
    /// Los largos espejan los de <c>Catalog.Product</c>: más cortos, un producto válido allá no
    /// cabría acá al enviarlo.
    /// </summary>
    [Fact]
    public void TheProductSnapshotMapsToNullableColumnsSizedLikeTheCatalog()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var item = model.FindEntityType(typeof(QuotationItem))!;

        var code = item.FindProperty(nameof(QuotationItem.ProductCode))!;
        var name = item.FindProperty(nameof(QuotationItem.ProductName))!;

        Assert.Equal(("product_code", true, 60), (code.GetColumnName(), code.IsNullable, code.GetMaxLength()));
        Assert.Equal(("product_name", true, 200), (name.GetColumnName(), name.IsNullable, name.GetMaxLength()));
    }

    /// <summary>
    /// Spec 2026-10-17 (flete): las transportadoras del tenant y sus nombres. El índice único de
    /// `carrier_names` es el que QuotationsUnitOfWork traduce a `quotations.carrier.name_taken`, así
    /// que su nombre es contrato; `version` es el token de concurrencia.
    /// </summary>
    [Fact]
    public void CarriersMapToTheirTablesColumnsConstraintsAndIndexes()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var carrier = model.FindEntityType(typeof(Carrier))!;
        Assert.Equal("carriers", carrier.GetTableName());
        Assert.Equal("quotations", carrier.GetSchema());
        Assert.Equal("PK_carriers", carrier.FindPrimaryKey()!.GetName());
        Assert.Equal("tenant_id", carrier.FindProperty(nameof(Carrier.TenantId))!.GetColumnName());
        var name = carrier.FindProperty(nameof(Carrier.Name))!;
        Assert.Equal("name", name.GetColumnName());
        Assert.Equal(60, name.GetMaxLength());
        Assert.Equal(
            "supports_cash_on_delivery",
            carrier.FindProperty(nameof(Carrier.SupportsCashOnDelivery))!.GetColumnName());
        Assert.Equal("sort_order", carrier.FindProperty(nameof(Carrier.SortOrder))!.GetColumnName());
        Assert.Equal("is_active", carrier.FindProperty(nameof(Carrier.IsActive))!.GetColumnName());
        var version = carrier.FindProperty(nameof(Carrier.Version))!;
        Assert.Equal("version", version.GetColumnName());
        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("created_at", carrier.FindProperty(nameof(Carrier.CreatedAt))!.GetColumnName());
        Assert.Equal("updated_at", carrier.FindProperty(nameof(Carrier.UpdatedAt))!.GetColumnName());
        var sortIndex = Assert.Single(carrier.GetIndexes());
        Assert.Equal("IX_carriers_tenant_sort", sortIndex.GetDatabaseName());
        Assert.False(sortIndex.IsUnique);
        Assert.Equal(["TenantId", "SortOrder"], sortIndex.Properties.Select(property => property.Name));

        var entry = model.FindEntityType(typeof(CarrierNameEntry))!;
        Assert.Equal("carrier_names", entry.GetTableName());
        Assert.Equal("quotations", entry.GetSchema());
        Assert.Equal(["carrier_id", "normalized_name"], entry.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()));
        var kind = entry.FindProperty(nameof(CarrierNameEntry.Kind))!;
        Assert.Equal("kind", kind.GetColumnName());
        Assert.Equal(16, kind.GetMaxLength());
        Assert.Equal(120, entry.FindProperty(nameof(CarrierNameEntry.Value))!.GetMaxLength());
        Assert.Equal("value", entry.FindProperty(nameof(CarrierNameEntry.Value))!.GetColumnName());
        Assert.Equal(120, entry.FindProperty(nameof(CarrierNameEntry.NormalizedName))!.GetMaxLength());
        Assert.Equal("tenant_id", entry.FindProperty(nameof(CarrierNameEntry.TenantId))!.GetColumnName());
        var unique = Assert.Single(entry.GetIndexes());
        Assert.Equal("IX_carrier_names_tenant_name", unique.GetDatabaseName());
        Assert.True(unique.IsUnique);
        var foreignKey = Assert.Single(entry.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Contains(
            entry.GetCheckConstraints(),
            check => check.Name == "CK_carrier_names_kind" && check.Sql.Contains("'Name'") && check.Sql.Contains("'External'"));
    }

    /// <summary>
    /// Spec 2026-10-17: el envío vive en columnas propias de cotización y pedido. Los montos y el
    /// porcentaje son NOT NULL con default 0 (las filas anteriores quedan sin envío); modo y
    /// transportadora son nulos, y la transportadora no tiene FK. Lo derivado no se persiste.
    /// </summary>
    [Theory]
    [InlineData(typeof(Quotation), "CK_quotations_freight_charge_mode")]
    [InlineData(typeof(Order), "CK_orders_freight_charge_mode")]
    public void DocumentsMapTheirFreightColumns(Type documentType, string checkName)
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var document = model.FindEntityType(documentType)!;

        var carrierId = document.FindProperty("CarrierId")!;
        Assert.Equal("carrier_id", carrierId.GetColumnName());
        Assert.True(carrierId.IsNullable);
        var carrierName = document.FindProperty("CarrierName")!;
        Assert.Equal("carrier_name", carrierName.GetColumnName());
        Assert.Equal(60, carrierName.GetMaxLength());
        Assert.True(carrierName.IsNullable);
        var mode = document.FindProperty("FreightChargeMode")!;
        Assert.Equal("freight_charge_mode", mode.GetColumnName());
        Assert.Equal(16, mode.GetMaxLength());
        Assert.True(mode.IsNullable);
        Assert.Equal(typeof(string), mode.GetProviderClrType());
        foreach (var (property, column) in new[]
                 {
                     ("FreightAmount", "freight_amount"),
                     ("FreightSubtotal", "freight_subtotal"),
                     ("FreightTaxAmount", "freight_tax_amount"),
                 })
        {
            var money = document.FindProperty(property)!;
            Assert.Equal(column, money.GetColumnName());
            Assert.Equal(14, money.GetPrecision());
            Assert.Equal(2, money.GetScale());
            Assert.False(money.IsNullable);
            Assert.Equal(0m, money.GetDefaultValue());
        }

        var percentage = document.FindProperty("FreightTaxPercentage")!;
        Assert.Equal("freight_tax_percentage", percentage.GetColumnName());
        Assert.Equal(0, percentage.GetDefaultValue());
        var cashOnDelivery = document.FindProperty("CashOnDelivery")!;
        Assert.Equal("cash_on_delivery", cashOnDelivery.GetColumnName());
        Assert.Equal(false, cashOnDelivery.GetDefaultValue());

        Assert.Null(document.FindProperty("ProductsTotal"));
        Assert.Null(document.FindProperty("ShippingDetails"));
        Assert.DoesNotContain(document.GetForeignKeys(), key => key.Properties.Any(p => p.Name == "CarrierId"));
        Assert.Contains(
            document.GetCheckConstraints(),
            check => check.Name == checkName
                && check.Sql.Contains("'Charged'") && check.Sql.Contains("'OnDelivery'") && check.Sql.Contains("'Free'"));
    }

    [Fact]
    public void TheSettingsRowMapsTheFreightTaxRateAsANullableColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var property = model.FindEntityType(typeof(TenantQuotationSettingsRow))!
            .FindProperty(nameof(TenantQuotationSettingsRow.FreightTaxRateId))!;

        Assert.Equal("freight_tax_rate_id", property.GetColumnName());
        Assert.True(property.IsNullable);
    }

    // Spec §4.4: se guardan las apagadas, así que una fila sin tocar (y las de antes de la migración) tiene las
    // tres activas. El CHECK impide guardar un nombre que el enum no conoce.
    [Fact]
    public void TheSettingsRowMapsTheDisabledFreightChargeModesAsARequiredTextArray()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(TenantQuotationSettingsRow))!;

        var property = entity.FindProperty(nameof(TenantQuotationSettingsRow.DisabledFreightChargeModes))!;

        Assert.Equal("disabled_freight_charge_modes", property.GetColumnName());
        Assert.False(property.IsNullable);
        Assert.Equal("text[]", property.GetColumnType());
        Assert.Equal("'{}'::text[]", property.GetDefaultValueSql());
        Assert.Contains(
            entity.GetCheckConstraints(),
            check => check.Name == "CK_tenant_quotation_settings_disabled_freight_charge_modes"
                && check.Sql.Contains("'Charged'") && check.Sql.Contains("'OnDelivery'") && check.Sql.Contains("'Free'"));
    }

    // Spec §4.4.1: el por defecto es un nombre del enum o null, y nunca uno de los apagados de la misma fila.
    [Fact]
    public void TheSettingsRowMapsTheDefaultFreightChargeModeAsANullableEnabledName()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(TenantQuotationSettingsRow))!;

        var property = entity.FindProperty(nameof(TenantQuotationSettingsRow.DefaultFreightChargeMode))!;

        Assert.Equal("default_freight_charge_mode", property.GetColumnName());
        Assert.True(property.IsNullable);
        Assert.Equal(16, property.GetMaxLength());
        Assert.Contains(
            entity.GetCheckConstraints(),
            check => check.Name == "CK_tenant_quotation_settings_default_freight_charge_mode"
                && check.Sql.Contains("'Charged'") && check.Sql.Contains("disabled_freight_charge_modes"));
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

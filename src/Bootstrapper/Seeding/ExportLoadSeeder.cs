using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Identity.Infrastructure.Seed;
using Modules.Tenancy.Infrastructure.Seed;
using Npgsql;

namespace Bootstrapper.Seeding;

/// <summary>
/// La carga sintética para medir la exportación en producción (spec 2026-09-13, A10).
///
/// El tenant, el usuario, la membresía y el catálogo se crean por el dominio, con los mismos seeders
/// de la semilla de arranque. Clientes, cotizaciones, líneas y ventas van en SQL masivo, en una sola
/// transacción: no pasan por los handlers, así que no generan outbox, auditoría, correos ni WhatsApp.
///
/// Es idempotente: si el tenant ya tiene cotizaciones, no siembra. Si falla a mitad, la transacción no
/// deja nada, y la próxima corrida siembra de cero sobre el tenant y el catálogo que ya existen.
/// </summary>
public static class ExportLoadSeeder
{
    /// <summary>Fijo, para que la limpieza (ops/export-load-cleanup.sql) sepa a quién borrar. No choca
    /// con el de desarrollo (...0001), el sujeto de desarrollo (...0002) ni origen-botanico
    /// (...0003).</summary>
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000004");

    /// <summary>
    /// La empresa de la cuenta de cobro de las cotizaciones sembradas. No existe en companies: el
    /// detalle de la cotización la busca (QuotationResponseComposer), no la encuentra y muestra la cuenta
    /// bancaria sin nombre ni NIT de empresa. No la reutilices donde la búsqueda es estricta, como
    /// QuotationBillingAccountResolver al editar. La cuenta completa está para que el listado muestre las
    /// cotizaciones como completas y las ventas tengan de dónde salir.
    /// </summary>
    public static readonly Guid BillingCompanyId = Guid.Parse("01900000-0000-7000-8000-000000000005");

    public const string TenantSlug = "carga-export";

    public const string TenantDisplayName = "Carga de exportación";

    public const string MembershipOrigin = "seed-export-load";

    public const int QuotationsPerCustomer = 25;

    public const int ItemsPerQuotation = 3;

    // Es un trabajo de una sola vez: con 50 000 cotizaciones, los INSERT de 150 000 filas pueden pasar los
    // 30 s que Npgsql da por defecto, y un timeout deshace la transacción entera.
    private const int CommandTimeoutSeconds = 600;

    public static async Task<ExportLoadSeedResult> SeedExportLoadAsync(
        this IServiceProvider services,
        string ownerEmail,
        int quotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quotations);
        var startedAt = Stopwatch.GetTimestamp();

        await services.SeedTenantAsync(TenantId, TenantSlug, TenantDisplayName, cancellationToken);
        var ownerUserId = await services.SeedUserAsync(ownerEmail, cancellationToken);
        var advisorId = await services.SeedAdminMembershipAsync(
            TenantId, ownerUserId, MembershipOrigin, cancellationToken);
        await services.SeedCatalogAsync(TenantId, cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (await AlreadySeededAsync(connection, cancellationToken))
        {
            return ExportLoadSeedResult.Skipped;
        }

        var customers = Math.Max(1, quotations / QuotationsPerCustomer);
        var classificationId = Guid.CreateVersion7();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, ClassificationSql, cancellationToken,
            ("classification", classificationId), ("tenant", TenantId), ("now", now));
        var seededCustomers = await ExecuteAsync(connection, transaction, CustomersSql, cancellationToken,
            ("tenant", TenantId), ("classification", classificationId), ("customers", customers), ("now", now));
        if (seededCustomers != customers)
        {
            throw new InvalidOperationException(
                $"Export load seed expected {customers} customers and inserted {seededCustomers}: geography.cities has no rows.");
        }

        await ExecuteAsync(connection, transaction, AddressesSql, cancellationToken,
            ("tenant", TenantId), ("now", now));
        await ExecuteAsync(connection, transaction, CucCounterSql, cancellationToken,
            ("tenant", TenantId), ("customers", customers));

        await ExecuteAsync(connection, transaction, LoadTablesSql, cancellationToken);
        await ExecuteAsync(connection, transaction, LoadQuotationsSql, cancellationToken,
            ("tenant", TenantId), ("quotations", quotations), ("customers", customers), ("now", now));
        await ExecuteAsync(connection, transaction, LoadItemsSql, cancellationToken,
            ("tenant", TenantId), ("itemsPerQuotation", ItemsPerQuotation));

        var seededQuotations = await ExecuteAsync(connection, transaction, QuotationsSql, cancellationToken,
            ("tenant", TenantId), ("advisor", advisorId), ("today", today), ("billingCompany", BillingCompanyId));
        if (seededQuotations != quotations)
        {
            throw new InvalidOperationException(
                $"Export load seed expected {quotations} quotations and inserted {seededQuotations}: the load tenant has no active products.");
        }

        var seededItems = await ExecuteAsync(connection, transaction, ItemsSql, cancellationToken);
        var seededSales = await ExecuteAsync(connection, transaction, SalesSql, cancellationToken,
            ("tenant", TenantId), ("advisor", advisorId), ("now", now));
        await ExecuteAsync(connection, transaction, QuotationCountersSql, cancellationToken,
            ("tenant", TenantId));
        await ExecuteAsync(connection, transaction, SaleCountersSql, cancellationToken,
            ("tenant", TenantId));

        await transaction.CommitAsync(cancellationToken);

        return new ExportLoadSeedResult(
            true, seededCustomers, seededQuotations, seededItems, seededSales, Stopwatch.GetElapsedTime(startedAt));
    }

    private static async Task<bool> AlreadySeededAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM quotations.quotations WHERE tenant_id = @tenant)", connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("tenant", TenantId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string ClassificationSql = """
        INSERT INTO customers.client_classifications (id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES (@classification, @tenant, 'Carga de exportación', 'CLI', true, 1, @now, @now)
        """;

    // El CUC es {prefijo}{departamento DIVIPOLA}{consecutivo de 6}, con el departamento de la ciudad de
    // la dirección principal. greatest(6, …) porque lpad trunca lo que pasa del ancho.
    private const string CustomersSql = """
        WITH city AS (
            SELECT left(divipola_code, 2) AS department
            FROM geography.cities
            ORDER BY divipola_code
            LIMIT 1
        )
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number, is_active,
            phone, email, classification_id, with_retention, vat_surplus, version, created_at, updated_at)
        SELECT gen_random_uuid(), @tenant,
               'CLI' || city.department || lpad(n::text, greatest(6, length(n::text)), '0'),
               'Cliente de carga ' || n, NULL, 'Nit', (800000000 + n)::text, true,
               NULL, NULL, @classification, false, false, 1, @now, @now
        FROM generate_series(1, @customers) AS n
        CROSS JOIN city
        """;

    // GET /customers y el detalle exigen una dirección principal (Customer.RequirePrincipalAddress).
    private const string AddressesSql = """
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        SELECT gen_random_uuid(), customer.id, 'Principal', 'Calle ' || right(customer.cuc, 6) || ' # 10-20', NULL,
               (SELECT id FROM geography.cities ORDER BY divipola_code LIMIT 1), true, @now, @now
        FROM customers.customers AS customer
        WHERE customer.tenant_id = @tenant
        """;

    // Cada contador guarda el próximo número a emitir (CucGenerator.NextBatchAsync).
    private const string CucCounterSql = """
        INSERT INTO customers.cuc_counters (tenant_id, next_value)
        VALUES (@tenant, @customers + 1)
        ON CONFLICT (tenant_id) DO UPDATE
            SET next_value = greatest(cuc_counters.next_value, EXCLUDED.next_value)
        """;

    // Tablas de trabajo que se descartan con el commit: calcular una vez fechas, números y totales, y
    // después insertar en las tablas de verdad.
    private const string LoadTablesSql = """
        CREATE TEMP TABLE load_quotations (
            n integer PRIMARY KEY,
            id uuid NOT NULL,
            client_id uuid NOT NULL,
            created_at timestamptz NOT NULL,
            year integer NOT NULL,
            quotation_number varchar(20) NOT NULL,
            sent_at timestamptz NOT NULL,
            valid_until date NOT NULL,
            converted boolean NOT NULL
        ) ON COMMIT DROP;
        CREATE TEMP TABLE load_items (
            id uuid NOT NULL,
            quotation_id uuid NOT NULL,
            position integer NOT NULL,
            product_id uuid NOT NULL,
            quantity numeric(10,2) NOT NULL,
            unit_price numeric(14,2) NOT NULL,
            tax integer NOT NULL,
            tax_amount numeric(14,2) NOT NULL,
            subtotal numeric(14,2) NOT NULL,
            created_at timestamptz NOT NULL
        ) ON COMMIT DROP
        """;

    // Fechas repartidas en los últimos 364 días. El número es QUO-{año UTC}-{consecutivo del año}: D4 es
    // un mínimo, y greatest(4, …) evita que lpad trunque a partir de 10 000. La vigencia es la de la app,
    // alta + 15 días. El 30 % se convierte en venta.
    private const string LoadQuotationsSql = """
        INSERT INTO load_quotations (n, id, client_id, created_at, year, quotation_number, sent_at, valid_until, converted)
        SELECT numbered.n, gen_random_uuid(), seeded_customers.id, numbered.created_at, numbered.year,
               'QUO-' || numbered.year || '-'
                   || lpad(numbered.sequence::text, greatest(4, length(numbered.sequence::text)), '0'),
               least(numbered.created_at + interval '1 hour', @now),
               (numbered.created_at AT TIME ZONE 'UTC')::date + 15,
               numbered.n % 10 < 3
        FROM (
            SELECT dated.n,
                   dated.created_at,
                   extract(year FROM dated.created_at AT TIME ZONE 'UTC')::int AS year,
                   row_number() OVER (
                       PARTITION BY extract(year FROM dated.created_at AT TIME ZONE 'UTC')
                       ORDER BY dated.created_at, dated.n) AS sequence
            FROM (
                SELECT n,
                       @now - make_interval(
                           days => ((n::bigint * 7919) % 364)::int,
                           mins => ((n::bigint * 37) % 1440)::int) AS created_at
                FROM generate_series(1, @quotations) AS n
            ) AS dated
        ) AS numbered
        JOIN (
            SELECT id, row_number() OVER (ORDER BY cuc) AS position
            FROM customers.customers
            WHERE tenant_id = @tenant
        ) AS seeded_customers ON seeded_customers.position = ((numbered.n - 1) % @customers) + 1
        """;

    // Las fórmulas del dominio sin descuento: el precio incluye IVA, así que el impuesto sale de adentro
    // de la línea (tax = round(line * t / (100 + t), 2)) y el subtotal es la base. round(x, 2) de
    // Postgres redondea alejando del cero, igual que la app.
    private const string LoadItemsSql = """
        INSERT INTO load_items (id, quotation_id, position, product_id, quantity, unit_price, tax, tax_amount, subtotal, created_at)
        SELECT gen_random_uuid(), line.quotation_id, line.position, line.product_id, line.quantity, line.unit_price,
               line.tax,
               round(line.line_total * line.tax / (100 + line.tax), 2),
               line.line_total - round(line.line_total * line.tax / (100 + line.tax), 2),
               line.created_at
        FROM (
            SELECT quotation.id AS quotation_id,
                   quotation.created_at,
                   slot AS position,
                   product.id AS product_id,
                   (1 + (quotation.n + slot) % 5)::numeric AS quantity,
                   product.unit_price,
                   product.tax,
                   round((1 + (quotation.n + slot) % 5)::numeric * product.unit_price, 2) AS line_total
            FROM load_quotations AS quotation
            CROSS JOIN generate_series(1, @itemsPerQuotation) AS slot
            JOIN (
                SELECT candidate.id,
                       coalesce(candidate.price_base_cop, 100000.00) AS unit_price,
                       coalesce(rate.percentage, 0) AS tax,
                       row_number() OVER (ORDER BY candidate.code) - 1 AS position,
                       count(*) OVER () AS total
                FROM catalog.products AS candidate
                LEFT JOIN catalog.tax_rates AS rate ON rate.id = candidate.tax_rate_id
                WHERE candidate.tenant_id = @tenant AND candidate.is_active
            ) AS product ON product.position = (quotation.n * @itemsPerQuotation + slot) % product.total
        ) AS line
        """;

    // Sin retención ni excedente de IVA (los flags del cliente en false): tax_amount es la suma de las
    // líneas y net_total es igual a total. Las vencidas se siembran ya Expired y las vigentes Sent, para
    // que el barrido de vencimiento no escriba historial ni auditoría. updated_at = sent_at: más tarde
    // contaría como "editada después de enviar".
    private const string QuotationsSql = """
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total,
            billing_uses_business_name, is_store_pickup, bills_to_final_consumer,
            customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, created_at, updated_at, sent_at, valid_until, pdf_file_id, version,
            billing_company_id, billing_bank_name, billing_account_number, billing_account_currency)
        SELECT quotation.id, @tenant, quotation.quotation_number, quotation.client_id, @advisor,
               CASE WHEN quotation.valid_until < @today THEN 'Expired' ELSE 'Sent' END,
               'COP',
               CASE WHEN quotation.n % 2 = 0 THEN 'Transferencia' END,
               totals.subtotal,
               CASE WHEN totals.subtotal > 0 THEN round(totals.tax_amount / totals.subtotal * 100, 2) ELSE 0 END,
               totals.tax_amount, 0, totals.subtotal + totals.tax_amount,
               false, false, false,
               false, false, 0, totals.subtotal + totals.tax_amount, NULL,
               @advisor, NULL, quotation.created_at, quotation.sent_at, quotation.sent_at, quotation.valid_until,
               NULL, 1,
               @billingCompany, 'Banco de la carga', '000000000000', 'COP'
        FROM load_quotations AS quotation
        JOIN (
            SELECT quotation_id, sum(subtotal) AS subtotal, sum(tax_amount) AS tax_amount
            FROM load_items
            GROUP BY quotation_id
        ) AS totals ON totals.quotation_id = quotation.id
        """;

    private const string ItemsSql = """
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, quantity, unit_price, discount_percentage, discount_amount,
            subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        SELECT id, quotation_id, product_id, quantity, unit_price, 0, 0,
               subtotal, tax, tax_amount, position, created_at, created_at
        FROM load_items
        """;

    // Convertida un día después del envío, dentro de la vigencia. VEN-{año UTC de la conversión}-{n}.
    // PaymentPending porque cualquier otro estado de pago exige comprobantes en Storage.
    private const string SalesSql = """
        INSERT INTO quotations.sales (
            id, tenant_id, sale_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        SELECT gen_random_uuid(), @tenant,
               'VEN-' || sale.year || '-' || lpad(sale.sequence::text, greatest(4, length(sale.sequence::text)), '0'),
               sale.quotation_id, 'Pending', 'PaymentPending', NULL, sale.converted_at, @advisor,
               NULL, NULL, NULL, sale.converted_at, sale.converted_at, 1
        FROM (
            SELECT converted.quotation_id,
                   converted.converted_at,
                   extract(year FROM converted.converted_at AT TIME ZONE 'UTC')::int AS year,
                   row_number() OVER (
                       PARTITION BY extract(year FROM converted.converted_at AT TIME ZONE 'UTC')
                       ORDER BY converted.converted_at, converted.n) AS sequence
            FROM (
                SELECT id AS quotation_id, n, least(sent_at + interval '1 day', @now) AS converted_at
                FROM load_quotations
                WHERE converted
            ) AS converted
        ) AS sale
        """;

    private const string QuotationCountersSql = """
        INSERT INTO quotations.quotation_number_counters (tenant_id, year, next_value)
        SELECT @tenant, year, count(*) + 1
        FROM load_quotations
        GROUP BY year
        ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = greatest(quotation_number_counters.next_value, EXCLUDED.next_value)
        """;

    private const string SaleCountersSql = """
        INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)
        SELECT @tenant, extract(year FROM converted_at AT TIME ZONE 'UTC')::int, count(*) + 1
        FROM quotations.sales
        WHERE tenant_id = @tenant
        GROUP BY 2
        ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = greatest(sale_number_counters.next_value, EXCLUDED.next_value)
        """;
}

/// <summary>Lo que dejó una corrida de la carga. <see cref="Skipped"/> es "el tenant ya tenía
/// cotizaciones".</summary>
public sealed record ExportLoadSeedResult(
    bool Seeded,
    int Customers,
    int Quotations,
    int Items,
    int Sales,
    TimeSpan Duration)
{
    public static ExportLoadSeedResult Skipped { get; } = new(false, 0, 0, 0, 0, TimeSpan.Zero);
}

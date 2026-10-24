using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Npgsql;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>
/// Spec 2026-10-24 §4 y §6 «Movimiento»: la fusión de punta a punta contra el esquema migrado de todos los módulos. Vive en
/// Pipeline porque su harness ya siembra clientes, conversaciones y negocios por los servicios del host. Las réplicas de
/// <c>CustomerMergeReferencesTests</c> no siguen solas a las migraciones: estas pruebas son las que se ponen en rojo si alguien
/// renombra una de las columnas que la fusión toca por nombre (<c>42703</c>, <c>42P01</c>).
///
/// Cada prueba siembra también filas de <b>otro cliente del mismo tenant</b> en las diez tablas: si una sentencia perdiera su
/// <c>= ANY(ids)</c>, la fusión reescribiría la tabla entera del tenant y sólo esas filas lo delatan.
/// </summary>
public sealed class CustomerMergeAcrossModulesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Las diez tablas de §4, en el orden de Owners.
    private static readonly string[] Tables =
    [
        "quotations.quotations", "quotations.orders", "pos.sales", "pipeline.deals", "pipeline.deal_quotations",
        "pipeline.deal_orders", "pipeline.triage_decisions", "messaging.conversations", "messaging.campaigns", "shopify.orders",
    ];

    private sealed record Arranged(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid Owner, Guid Survivor, string SurvivorName, Guid Merged, Guid Bystander);

    /// <summary>Una fila por tabla de §4 a nombre de un cliente. <see cref="Deal"/> es el negocio que abrió el sent y ganó el
    /// pedido; <see cref="Quotation"/> y <see cref="Order"/> son también los ids de sus fotos en Pipeline.</summary>
    private sealed record Footprint(
        Guid TenantId, Guid Quotation, Guid Order, Guid Sale, Guid Deal, Guid Triage, Guid Conversation, Guid Campaign, Guid ShopifyOrder);

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        var (survivor, survivorName) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var (merged, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura G");
        var (bystander, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Sofía");
        return new Arranged(factory, connectionString, tenant, owner, survivor, survivorName, merged, bystander);
    }

    private static HttpClient Merger(Arranged arranged) =>
        CreateClient(arranged.Factory, arranged.Tenant.OwnerUserId, arranged.Tenant.TenantId,
            CustomersPermissions.CustomerMerge, PipelinePermissions.DealRead);

    private static Task<long> VersionAsync(string connectionString, Guid customerId) =>
        ScalarAsync<long>(connectionString, "SELECT version FROM customers.customers WHERE id = @id", ("id", customerId));

    /// <summary>Como el frontend: <c>If-Match</c> con la versión del que queda (B1).</summary>
    private static async Task<HttpResponseMessage> PostMergeAsync(Arranged arranged, HttpClient client, params Guid[] merged)
    {
        var version = await VersionAsync(arranged.ConnectionString, arranged.Survivor);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenants/{arranged.Tenant.TenantId}/customers/{arranged.Survivor}/merge")
        {
            Content = JsonContent.Create(new { mergedCustomerIds = merged }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Ct);
    }

    private static async Task ApplySentAsync(QepApiFactory factory, Guid tenantId, Guid quotationId, Guid customerId, Guid advisor)
    {
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplySentAsync(
            new QuotationFact(tenantId, quotationId, "COT-0042", customerId, advisor, 750m, "COP", null), DateTimeOffset.UtcNow, Ct));
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static Task<Guid> DealOfAsync(string connectionString, Guid tenantId, Guid quotationId) =>
        ScalarAsync<Guid>(connectionString,
            "SELECT deal_id FROM pipeline.deal_quotations WHERE tenant_id = @tenant AND quotation_id = @quotation",
            ("tenant", tenantId), ("quotation", quotationId));

    /// <summary>
    /// Pipeline y la conversación por los servicios del host (como el worker y la ingesta); Quotations, Pos, la campaña y Shopify
    /// por SQL con sólo las columnas obligatorias. La sesión de caja exige una empresa (<c>FK_cash_sessions_companies</c>) que
    /// nada de esto necesita: el INSERT corre con <c>session_replication_role = replica</c>, que en esa conexión sola apaga las FK.
    /// </summary>
    private static async Task<Footprint> SeedFootprintAsync(
        QepApiFactory factory, string connectionString, Guid tenantId, Guid customerId, Guid advisor)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now, now);
        var quotation = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        await ApplySentAsync(factory, tenantId, quotation, customerId, advisor);
        Guid triage;
        using (var scope = factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplyOrderCreatedAsync(
                new OrderFact(tenantId, order, "PED-0042", customerId, advisor, 750m, "COP", "Manual", null), now, Ct));
            var decision = TriageDecision.Dismissed(tenantId, conversation, customerId, Guid.CreateVersion7(), now, DealActor.ForSystem, now);
            scope.ServiceProvider.GetRequiredService<ITriageDecisionRepository>().Add(decision);
            triage = decision.Id;
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        var footprint = new Footprint(tenantId, quotation, order, Guid.CreateVersion7(), await DealOfAsync(connectionString, tenantId, quotation),
            triage, conversation, Guid.CreateVersion7(), Guid.CreateVersion7());
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SET session_replication_role = replica;
            INSERT INTO quotations.quotations (id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, subtotal,
                tax_percentage, tax_amount, discount_amount, total, created_by, updated_at, version)
            VALUES (@quotation, @tenant, 'COT-' || @suffix, @customer, @advisor, 'Draft', now(), 0, 0, 0, 0, 0, @advisor, now(), 1);
            INSERT INTO quotations.orders (id, tenant_id, order_number, status, payment_status, converted_at, converted_by, created_at,
                updated_at, version, client_id, advisor_id, currency, billing_company_id, billing_bank_name, billing_account_number,
                billing_account_currency, source)
            VALUES (@order, @tenant, 'PED-' || @suffix, 'Pending', 'PaymentPending', now(), @advisor, now(), now(), 1, @customer,
                @advisor, 'COP', @advisor, 'Banco', '123', 'COP', 'Manual');
            INSERT INTO pos.cash_sessions (id, tenant_id, cashier_id, cashier_name, company_id, company_name, company_tax_id, status,
                opening_float, opened_at, sales_count, voided_count, sales_total, cash_total, card_total, transfer_total, version,
                created_at, updated_at)
            VALUES (@session, @tenant, @advisor, 'Caja', @advisor, 'Empresa', '900', 'Closed', 0, now(), 0, 0, 0, 0, 0, 0, 1, now(), now());
            INSERT INTO pos.sales (id, request_fingerprint, tenant_id, cash_session_id, cashier_id, sale_number, customer_id,
                customer_name, customer_identification_number, subtotal, tax_amount, discount_amount, total, change_amount, status,
                created_at)
            VALUES (@sale, repeat('a', 64), @tenant, @session, @advisor, 'POS-' || @suffix, @customer, 'Laura', '123', 0, 0, 0, 0, 0,
                'Completed', now());
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category,
                parameters, audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@campaign, @tenant, @advisor, 'Campaña', 'promo', 'es', 'MARKETING', '[]'::jsonb, '{}'::uuid[], ARRAY[@customer],
                'Draft', @advisor, now(), now(), 1);
            INSERT INTO shopify.orders (id, tenant_id, connection_id, shopify_order_id, order_name, shopify_created_at,
                shopify_updated_at, currency, total_price, taxes_included, payload, import_state, attempts, version, qep_customer_id)
            VALUES (@shopify, @tenant, @advisor, @shopifyOrderId, '#' || @suffix, now(), now(), 'COP', 0, false, '{}'::jsonb,
                'Imported', 0, 1, @customer);
            """,
            connection);
        command.Parameters.AddWithValue("quotation", footprint.Quotation);
        command.Parameters.AddWithValue("order", footprint.Order);
        command.Parameters.AddWithValue("sale", footprint.Sale);
        command.Parameters.AddWithValue("session", Guid.CreateVersion7());
        command.Parameters.AddWithValue("campaign", footprint.Campaign);
        command.Parameters.AddWithValue("shopify", footprint.ShopifyOrder);
        command.Parameters.AddWithValue("shopifyOrderId", Random.Shared.NextInt64(1, long.MaxValue));
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("advisor", advisor);
        command.Parameters.AddWithValue("suffix", suffix);
        await command.ExecuteNonQueryAsync(Ct);
        return footprint;
    }

    /// <summary>El cliente de cada fila de la huella, tabla por tabla (la campaña, su audiencia entera).</summary>
    private static async Task<string[]> OwnersAsync(string connectionString, Footprint footprint)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT ARRAY[
                (SELECT client_id::text FROM quotations.quotations WHERE id = @quotation),
                (SELECT client_id::text FROM quotations.orders WHERE id = @order),
                (SELECT customer_id::text FROM pos.sales WHERE id = @sale),
                (SELECT customer_id::text FROM pipeline.deals WHERE id = @deal),
                (SELECT customer_id::text FROM pipeline.deal_quotations WHERE tenant_id = @tenant AND quotation_id = @quotation),
                (SELECT customer_id::text FROM pipeline.deal_orders WHERE tenant_id = @tenant AND order_id = @order),
                (SELECT customer_id::text FROM pipeline.triage_decisions WHERE id = @triage),
                (SELECT customer_id::text FROM messaging.conversations WHERE id = @conversation),
                (SELECT array_to_string(audience_customer_ids, ',') FROM messaging.campaigns WHERE id = @campaign),
                (SELECT qep_customer_id::text FROM shopify.orders WHERE id = @shopify)]
            """,
            connection);
        command.Parameters.AddWithValue("tenant", footprint.TenantId);
        command.Parameters.AddWithValue("quotation", footprint.Quotation);
        command.Parameters.AddWithValue("order", footprint.Order);
        command.Parameters.AddWithValue("sale", footprint.Sale);
        command.Parameters.AddWithValue("deal", footprint.Deal);
        command.Parameters.AddWithValue("triage", footprint.Triage);
        command.Parameters.AddWithValue("conversation", footprint.Conversation);
        command.Parameters.AddWithValue("campaign", footprint.Campaign);
        command.Parameters.AddWithValue("shopify", footprint.ShopifyOrder);
        var owners = (string?[])(await command.ExecuteScalarAsync(Ct))!;
        return [.. owners.Select((owner, index) => $"{Tables[index]}={owner ?? "(sin fila)"}")];
    }

    private static string[] All(Guid customerId) => [.. Tables.Select(table => $"{table}={customerId}")];

    // §4: el negocio que abrió el sent del fusionado, su foto y la conversación pasan al que queda, y el tablero lo dibuja a su
    // nombre. Las diez tablas del fusionado cambian; las del otro cliente del tenant, no.
    [Fact]
    public async Task TheMergeMovesTheDealItsQuotationAndTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var arranged = await ArrangeAsync(factory, connectionString);
        var merged = await SeedFootprintAsync(factory, connectionString, arranged.Tenant.TenantId, arranged.Merged, arranged.Owner);
        var bystander = await SeedFootprintAsync(factory, connectionString, arranged.Tenant.TenantId, arranged.Bystander, arranged.Owner);
        using var client = Merger(arranged);

        var response = await PostMergeAsync(arranged, client, arranged.Merged);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(All(arranged.Survivor), await OwnersAsync(connectionString, merged));
        Assert.Equal(All(arranged.Bystander), await OwnersAsync(connectionString, bystander));
        var board = (await client.GetFromJsonAsync<PipelineBoardDto>($"{PipelineUrl(arranged.Tenant.TenantId)}/board", Ct))!;
        var card = Assert.Single(board.Columns.SelectMany(column => column.Deals), deal => deal.Id == merged.Deal);
        Assert.Equal((arranged.Survivor, arranged.SurvivorName), (card.CustomerId, card.CustomerName));
    }

    // Aislamiento de tenant: otro tenant con filas a nombre del MISMO id (los ids no tienen FK entre esquemas) no se toca.
    [Fact]
    public async Task AnotherTenantsRowsStayPut()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var arranged = await ArrangeAsync(factory, connectionString);
        var other = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, other.TenantId);
        var foreign = await SeedFootprintAsync(
            factory, connectionString, other.TenantId, arranged.Merged, await OwnerMembershipIdAsync(connectionString, other));
        var mine = await SeedFootprintAsync(factory, connectionString, arranged.Tenant.TenantId, arranged.Merged, arranged.Owner);
        using var client = Merger(arranged);

        var response = await PostMergeAsync(arranged, client, arranged.Merged);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(All(arranged.Survivor), await OwnersAsync(connectionString, mine));
        Assert.Equal(All(arranged.Merged), await OwnersAsync(connectionString, foreign));
    }

    // B3: el adaptador del contenedor, no una réplica. Cuenta exacto lo sembrado (ni el otro cliente ni el otro tenant), mueve
    // sólo lo del fusionado y, movido, ya no cuenta nada.
    [Fact]
    public async Task TheRealAdapterRunsAgainstTheMigratedSchema()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var arranged = await ArrangeAsync(factory, connectionString);
        var tenantId = arranged.Tenant.TenantId;
        var other = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, other.TenantId);
        var merged = await SeedFootprintAsync(factory, connectionString, tenantId, arranged.Merged, arranged.Owner);
        var bystander = await SeedFootprintAsync(factory, connectionString, tenantId, arranged.Bystander, arranged.Owner);
        var foreign = await SeedFootprintAsync(
            factory, connectionString, other.TenantId, arranged.Merged, await OwnerMembershipIdAsync(connectionString, other));

        using var scope = factory.Services.CreateScope();
        var references = scope.ServiceProvider.GetRequiredService<ICustomerMergeReferences>();

        Assert.Equal(new CustomerMergeReferenceCounts(1, 1, 1, 1, 1), await references.CountAsync(tenantId, [arranged.Merged], Ct));
        Assert.Equal(
            new CustomerMergeReferenceCounts(2, 2, 2, 2, 2), await references.CountAsync(tenantId, [arranged.Merged, arranged.Bystander], Ct));

        await references.MoveAsync(tenantId, arranged.Survivor, [arranged.Merged], Ct);

        Assert.Equal(All(arranged.Survivor), await OwnersAsync(connectionString, merged));
        Assert.Equal(All(arranged.Bystander), await OwnersAsync(connectionString, bystander));
        Assert.Equal(All(arranged.Merged), await OwnersAsync(connectionString, foreign));
        Assert.Equal(new CustomerMergeReferenceCounts(0, 0, 0, 0, 0), await references.CountAsync(tenantId, [arranged.Merged], Ct));
        Assert.Equal(new CustomerMergeReferenceCounts(1, 1, 1, 1, 1), await references.CountAsync(tenantId, [arranged.Survivor], Ct));
    }

    // §4: la fusión no se puede deshacer, así que queda completa o no pasa. Un trigger por sentencia rompe el último UPDATE
    // (shopify.orders, CustomerMergeReferences) aunque no haya filas que cambiar: todo lo de antes, Customers incluido, vuelve.
    [Fact]
    public async Task AFailureInTheLastUpdateLeavesEverythingAsItWas()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var arranged = await ArrangeAsync(factory, connectionString);
        var tenantId = arranged.Tenant.TenantId;
        var merged = await SeedFootprintAsync(factory, connectionString, tenantId, arranged.Merged, arranged.Owner);
        var note = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO customers.customer_notes (id, tenant_id, customer_id, body, author_user_id, version, created_at, updated_at)
            VALUES (@id, @tenant, @customer, 'Llamar el lunes', @author, 1, now(), now())
            """,
            ("id", note), ("tenant", tenantId), ("customer", arranged.Merged), ("author", arranged.Tenant.OwnerUserId));
        var survivorVersion = await VersionAsync(connectionString, arranged.Survivor);
        await ExecuteAsync(connectionString,
            """
            CREATE FUNCTION public.fail_customer_merge() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'fusión rota a propósito';
            END $$;
            CREATE TRIGGER fail_customer_merge BEFORE UPDATE ON shopify.orders
                FOR EACH STATEMENT EXECUTE FUNCTION public.fail_customer_merge();
            """);
        using var client = Merger(arranged);

        var response = await PostMergeAsync(arranged, client, arranged.Merged);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM customers.customers WHERE tenant_id = @tenant AND merged_into_id IS NOT NULL", ("tenant", tenantId)));
        Assert.Equal(survivorVersion, await VersionAsync(connectionString, arranged.Survivor));
        Assert.Equal(arranged.Merged, await ScalarAsync<Guid>(connectionString,
            "SELECT customer_id FROM customers.customer_notes WHERE id = @id", ("id", note)));
        Assert.Equal(All(arranged.Merged), await OwnersAsync(connectionString, merged));
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = @action", ("action", CustomerAuditActions.Merged)));
    }

    // §4 «lo que llega tarde»: el sent que estaba en vuelo con el id del fusionado llega después de la fusión y abre el negocio
    // a nombre del que quedó, no del fusionado (que ya no aparece en ninguna lista).
    [Fact]
    public async Task ALateQuotationEventOfAMergedCustomerLandsOnTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var arranged = await ArrangeAsync(factory, connectionString);
        var tenantId = arranged.Tenant.TenantId;
        using var client = Merger(arranged);
        var merge = await PostMergeAsync(arranged, client, arranged.Merged);
        Assert.True(merge.StatusCode == HttpStatusCode.OK, await merge.Content.ReadAsStringAsync(Ct));
        var quotation = Guid.CreateVersion7();

        await ApplySentAsync(factory, tenantId, quotation, arranged.Merged, arranged.Owner);

        var deal = await DealOfAsync(connectionString, tenantId, quotation);
        Assert.Equal(arranged.Survivor, await ScalarAsync<Guid>(connectionString,
            "SELECT customer_id FROM pipeline.deals WHERE id = @id", ("id", deal)));
        Assert.Equal(arranged.Survivor, await ScalarAsync<Guid>(connectionString,
            "SELECT customer_id FROM pipeline.deal_quotations WHERE tenant_id = @tenant AND quotation_id = @quotation",
            ("tenant", tenantId), ("quotation", quotation)));
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @tenant AND customer_id = @merged",
            ("tenant", tenantId), ("merged", arranged.Merged)));
    }
}

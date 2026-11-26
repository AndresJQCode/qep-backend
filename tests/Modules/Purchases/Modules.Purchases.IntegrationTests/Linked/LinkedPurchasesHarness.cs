using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Messaging;
using Modules.Quotations.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wholesale.Application;

namespace Modules.Purchases.IntegrationTests.Linked;

/// <summary>
/// Mayorista fase 3: dos tenants con la relación armada por el camino real de fase 2 (el mayorista invita, el distribuidor acepta
/// vinculando su tenant), y el catálogo del mayorista con tres variantes al 19 %. Cada prueba arma el suyo, así que varias
/// comparten host (<see cref="SharedPurchasesHost"/>).
/// </summary>
internal static class LinkedPurchasesHarness
{
    public const string BuyerName = "Distribuciones Norte";

    public const string PriceListName = "Distribuidor estándar";

    public const decimal DiscountPercent = 15m;

    /// <summary>El precio de <see cref="PurchasesApiHarness.CreateVariantAsync"/> en COP, IVA incluido.</summary>
    public const decimal BasePrice = 10_000m;

    /// <summary><see cref="BasePrice"/> con <see cref="DiscountPercent"/>.</summary>
    public const decimal ListPrice = 8_500m;

    private const string ApprovedEvent = "quotations.order.wholesale-approved.v1";

    private const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string CatalogUrl(Guid tenantId, Guid supplierId) =>
        $"/api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog";

    public static string EmailOf(PurchasesTestTenant tenant) => $"linked-{tenant.SubjectId:N}@example.com";

    /// <summary>
    /// Como <see cref="PurchasesApiHarness.NewTenantAsync"/>, pero la membresía sembrada es el owner del tenant (la aceptación con
    /// <c>LinkTenant</c> lo exige y D-14 cae a él) y el correo del usuario es <see cref="EmailOf"/>.
    /// </summary>
    public static async Task<PurchasesTestTenant> NewTenantAsync(
        PurchasesApiHarness.QepApiFactory factory, string label, string displayName = "Purchases API Tests")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenantId = new TenantId(Guid.CreateVersion7());
        var subjectId = Guid.CreateVersion7();
        var membershipId = MembershipId.New();
        var now = DateTimeOffset.UtcNow;

        tenancy.Tenants.Add(Tenant.Create(
            tenantId, $"{label}-{tenantId.Value:N}", displayName, "es-CO", "America/Bogota", "yyyy-MM-dd", membershipId, now));
        await tenancy.SaveChangesAsync(Ct);
        foreach (var key in TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({tenantId.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                Ct);
        }

        var user = User.CreateInvited(UserId.Parse(subjectId.ToString()), $"linked-{subjectId:N}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(membershipId, user.Id.Value, tenantId, ["admin"], "test", now);
        membership.UpdateProfile(PurchasesApiHarness.MemberName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);

        return new PurchasesTestTenant(tenantId.Value, subjectId, membership.Id.Value);
    }

    /// <summary>El distribuidor: compras, proveedores y catálogo propio.</summary>
    public static HttpClient CreateBuyer(PurchasesApiHarness.QepApiFactory factory, PurchasesTestTenant tenant) =>
        PurchasesApiHarness.CreateManager(factory, tenant);

    /// <summary>El mayorista: listas, distribuidores, empresas, catálogo y pedidos (editar, aprobar, anular).</summary>
    public static HttpClient CreateWholesaler(PurchasesApiHarness.QepApiFactory factory, PurchasesTestTenant tenant) =>
        PurchasesApiHarness.CreateClient(
            factory,
            tenant.SubjectId,
            tenant.TenantId,
            WholesalePermissions.PriceListManage,
            WholesalePermissions.DistributorRead,
            WholesalePermissions.DistributorManage,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage,
            CatalogPermissions.ProductRead,
            CatalogPermissions.ProductManage,
            CatalogPermissions.TaxRateManage,
            OrdersPermissions.OrderRead,
            OrdersPermissions.OrderManage,
            OrdersPermissions.OrderCancel);

    /// <summary>
    /// El mayorista con su catálogo (ACE-1, JAB-2 y SAL-3 a <see cref="BasePrice"/> con IVA del 19 %), la lista «Distribuidor
    /// estándar» al <see cref="DiscountPercent"/> y su empresa facturadora; el distribuidor «Distribuciones Norte» que aceptó la
    /// invitación vinculando su tenant, y el proveedor vinculado que esa aceptación le dejó.
    /// </summary>
    public static async Task<LinkedWorld> NewWorldAsync(PurchasesApiHarness.QepApiFactory factory)
    {
        var wholesalerTenant = await NewTenantAsync(factory, "mayorista", "Mayorista Central");
        var buyerTenant = await NewTenantAsync(factory, "distribuidor", BuyerName);
        var wholesaler = CreateWholesaler(factory, wholesalerTenant);
        var buyer = CreateBuyer(factory, buyerTenant);

        var taxRate = await PurchasesApiHarness.CreateTaxRateAsync(wholesaler, wholesalerTenant.TenantId, "IVA", 19);
        var variantA = await PurchasesApiHarness.CreateVariantAsync(wholesaler, wholesalerTenant.TenantId, "ACE-1", "Aceite", taxRate);
        var variantB = await PurchasesApiHarness.CreateVariantAsync(wholesaler, wholesalerTenant.TenantId, "JAB-2", "Jabón", taxRate);
        var variantC = await PurchasesApiHarness.CreateVariantAsync(wholesaler, wholesalerTenant.TenantId, "SAL-3", "Sal", taxRate);

        var priceList = await PostAsync<PriceListDetailDto>(
            wholesaler,
            $"/api/v1/tenants/{wholesalerTenant.TenantId}/catalog/price-lists",
            new { name = PriceListName, adjustmentPercent = -DiscountPercent, visibility = "AllActive", categoryIds = Array.Empty<Guid>() });
        var companyId = await CreateCompanyAsync(wholesaler, wholesalerTenant.TenantId);
        var distributor = await PostAsync<DistributorDetailDto>(
            wholesaler,
            $"/api/v1/tenants/{wholesalerTenant.TenantId}/wholesale/distributors",
            new
            {
                email = EmailOf(buyerTenant),
                businessName = "Distribuciones Norte SAS",
                country = "CO",
                identificationType = "NIT",
                identificationNumber = $"9{Random.Shared.NextInt64(100_000_000, 999_999_999)}",
                contactName = "Ana Pérez",
                priceListId = priceList.Id,
                advisorId = wholesalerTenant.MemberId,
                billingCompanyId = companyId,
                cityId = await MedellinAsync(wholesaler),
            });

        var token = await LatestTokenAsync(factory, distributor.Id);
        using (var session = PurchasesApiHarness.CreateClient(factory, buyerTenant.SubjectId, buyerTenant.TenantId))
        {
            var accept = await session.PostAsJsonAsync(
                $"/api/v1/wholesale-invitations/{Uri.EscapeDataString(token)}/accept",
                new { mode = "LinkTenant", tenantId = buyerTenant.TenantId },
                Ct);
            await EnsureSuccessAsync(accept);
        }

        var supplierId = await PurchasesApiHarness.ScalarAsync<Guid>(
            factory,
            $"""
            SELECT id AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {buyerTenant.TenantId} AND linked_tenant_id = {wholesalerTenant.TenantId}
            """);

        return new LinkedWorld(
            factory, wholesalerTenant, wholesaler, buyerTenant, buyer, supplierId, variantA, variantB, variantC, distributor.Id,
            priceList.Id);
    }

    public static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await PurchasesApiHarness.BodyAsync(response)}");
        }
    }

    private static async Task<string> LatestTokenAsync(PurchasesApiHarness.QepApiFactory factory, Guid distributorId)
    {
        var payloads = await PurchasesApiHarness.RowsAsync<string>(
            factory,
            $"""
            SELECT payload::text AS "Value" FROM platform.outbox_messages
            WHERE event_name = {DistributorInvitedEvent} AND payload ->> 'distributorId' = {distributorId.ToString()}
            ORDER BY occurred_at DESC, id DESC
            """);
        using var document = JsonDocument.Parse(payloads[0]);
        return document.RootElement.GetProperty("token").GetString()!;
    }

    // Copia de WholesaleApiHarness.CreateCompanyAsync: NIT en Medellín (o la primera ciudad sembrada).
    private static async Task<Guid> CreateCompanyAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Mayorista Central SAS",
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber = "123456789", currency = "COP" } },
                taxId = "900111222",
                country = "CO",
                documentType = "NIT",
                phone = "3001234567",
                email = "ventas@mayorista.co",
                address = "Calle 10 # 40-20",
                cityId = await MedellinAsync(client),
            },
            Ct);
        await EnsureSuccessAsync(response);
        using var document = JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    // Medellín, o la primera ciudad sembrada si no está.
    private static async Task<Guid> MedellinAsync(HttpClient client)
    {
        Guid? first = null;
        var departments = await client.GetFromJsonAsync<List<GeographyRow>>("/api/v1/departments", Ct) ?? [];
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyRow>>($"/api/v1/cities?departmentId={department.Id}", Ct) ?? [];
            first ??= cities.FirstOrDefault()?.Id;
            var medellin = cities.FirstOrDefault(city => city.Name.StartsWith("Medell", StringComparison.OrdinalIgnoreCase));
            if (medellin is not null)
            {
                return medellin.Id;
            }
        }

        return first ?? throw new InvalidOperationException("No seeded DIVIPOLA city.");
    }

    private sealed record GeographyRow(Guid Id, string Name);

    /// <summary>Lo que arma <see cref="NewWorldAsync"/> y lo que cada prueba hace sobre él, de los dos lados.</summary>
    internal sealed class LinkedWorld(
        PurchasesApiHarness.QepApiFactory factory,
        PurchasesTestTenant wholesalerTenant,
        HttpClient wholesaler,
        PurchasesTestTenant buyerTenant,
        HttpClient buyer,
        Guid supplierId,
        Guid variantA,
        Guid variantB,
        Guid variantC,
        Guid distributorId,
        Guid priceListId) : IDisposable
    {
        public PurchasesTestTenant WholesalerTenant => wholesalerTenant;

        public HttpClient Wholesaler => wholesaler;

        public PurchasesTestTenant BuyerTenant => buyerTenant;

        public HttpClient Buyer => buyer;

        public Guid SupplierId => supplierId;

        public Guid VariantA => variantA;

        public Guid VariantB => variantB;

        public Guid VariantC => variantC;

        public string CatalogUrl() => LinkedPurchasesHarness.CatalogUrl(buyerTenant.TenantId, supplierId);

        /// <summary>El cuerpo del alta: sin costo, porque con proveedor vinculado lo pone la lista (D-11).</summary>
        public Dictionary<string, object?> DraftBody(params (Guid VariantId, decimal Quantity)[] lines) => new()
        {
            ["supplierId"] = supplierId,
            ["currency"] = "COP",
            ["notes"] = "Entregar en bodega",
            ["lines"] = lines
                .Select(line => new { variantId = line.VariantId, quantity = line.Quantity, unitCost = (decimal?)null })
                .ToArray(),
        };

        public Task<PurchaseOrderDetailDto> CreateDraftAsync(params (Guid VariantId, decimal Quantity)[] lines) =>
            PurchasesApiHarness.CreateOrderAsync(buyer, buyerTenant.TenantId, DraftBody(lines));

        public async Task BlockRelationshipAsync()
        {
            var url = $"/api/v1/tenants/{wholesalerTenant.TenantId}/wholesale/distributors/{distributorId}";
            var detail = (await wholesaler.GetFromJsonAsync<DistributorDetailDto>(url, Ct))!;
            var response = await PurchasesApiHarness.WriteAsync(wholesaler, HttpMethod.Post, $"{url}/block", detail.Version);
            await EnsureSuccessAsync(response);
        }

        /// <summary>El mayorista cambia el descuento de la lista del distribuidor.</summary>
        public async Task ChangeDiscountAsync(decimal discountPercent)
        {
            var url = $"/api/v1/tenants/{wholesalerTenant.TenantId}/catalog/price-lists/{priceListId}";
            var list = (await wholesaler.GetFromJsonAsync<PriceListDetailDto>(url, Ct))!;
            var response = await PurchasesApiHarness.WriteAsync(
                wholesaler,
                HttpMethod.Put,
                url,
                list.Version,
                new { name = PriceListName, adjustmentPercent = -discountPercent, visibility = "AllActive", categoryIds = Array.Empty<Guid>() });
            await EnsureSuccessAsync(response);
        }

        /// <summary>Spec 2026-11-20: una categoría del catálogo del mayorista, opcionalmente bajo otra.</summary>
        public async Task<Guid> CreateWholesalerCategoryAsync(string name, Guid? parentId = null) =>
            (await PostAsync<CategoryResponse>(
                wholesaler, $"/api/v1/tenants/{wholesalerTenant.TenantId}/catalog/categories", new { name, parentId })).Id;

        /// <summary>Spec 2026-11-20: la lista del distribuidor pasa a ver sólo esas categorías (y sus descendientes).</summary>
        public async Task UseCategoriesAsync(params Guid[] categoryIds)
        {
            var url = $"/api/v1/tenants/{wholesalerTenant.TenantId}/catalog/price-lists/{priceListId}";
            var list = (await wholesaler.GetFromJsonAsync<PriceListDetailDto>(url, Ct))!;
            var response = await PurchasesApiHarness.WriteAsync(
                wholesaler,
                HttpMethod.Put,
                url,
                list.Version,
                new { name = PriceListName, adjustmentPercent = -DiscountPercent, visibility = "Categories", categoryIds });
            await EnsureSuccessAsync(response);
        }

        /// <summary>Spec 2026-11-19: el mayorista oculta (o muestra) los precios de la lista del distribuidor.</summary>
        public async Task SetShowPricesAsync(bool showPrices)
        {
            var url = $"/api/v1/tenants/{wholesalerTenant.TenantId}/catalog/price-lists/{priceListId}";
            var list = (await wholesaler.GetFromJsonAsync<PriceListDetailDto>(url, Ct))!;
            var response = await PurchasesApiHarness.WriteAsync(
                wholesaler,
                HttpMethod.Put,
                url,
                list.Version,
                new { name = PriceListName, adjustmentPercent = -DiscountPercent, visibility = "AllActive", categoryIds = Array.Empty<Guid>(), showPrices });
            await EnsureSuccessAsync(response);
        }

        public Task<string> WholesaleOrderNumberAsync(Guid orderId) =>
            PurchasesApiHarness.ScalarAsync<string>(
                factory,
                $"""
                SELECT order_number AS "Value" FROM quotations.orders WHERE id = {orderId}
                """);

        public Task DeactivateWholesalerProductAsync(Guid variantId) =>
            PurchasesApiHarness.ExecuteAsync(
                factory,
                $"""
                UPDATE catalog.products SET is_active = false
                WHERE id = (SELECT product_id FROM catalog.product_variants WHERE id = {variantId})
                """);

        /// <summary>
        /// La ventana de D-8: el pedido quedó creado en el mayorista y la orden no alcanzó a guardarse. La orden vuelve a ser el
        /// borrador vinculado sin pedido.
        /// </summary>
        public Task ForgetTheSendAsync(Guid purchaseOrderId) =>
            PurchasesApiHarness.ExecuteAsync(
                factory,
                $"""
                UPDATE purchases.purchase_orders
                SET status = 'Draft', linked_order_id = NULL, linked_order_number = NULL
                WHERE id = {purchaseOrderId}
                """);

        public Task<int> WholesaleOrderCountAsync() =>
            PurchasesApiHarness.ScalarAsync<int>(
                factory,
                $"""
                SELECT count(*)::int AS "Value" FROM quotations.orders
                WHERE tenant_id = {wholesalerTenant.TenantId} AND buyer_tenant_id = {buyerTenant.TenantId}
                """);

        public Task<Guid> WholesaleOrderIdAsync(Guid purchaseOrderId) =>
            PurchasesApiHarness.ScalarAsync<Guid>(
                factory,
                $"""
                SELECT id AS "Value" FROM quotations.orders
                WHERE tenant_id = {wholesalerTenant.TenantId} AND purchase_order_id = {purchaseOrderId}
                """);

        /// <summary>El detalle HTTP del pedido en el mayorista, leído como JSON.</summary>
        public async Task<JsonDocument> WholesaleOrderAsync(Guid purchaseOrderId)
        {
            var orderId = await WholesaleOrderIdAsync(purchaseOrderId);
            var response = await wholesaler.GetAsync(OrderUrl(orderId), Ct);
            await EnsureSuccessAsync(response);
            return JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
        }

        public async Task ApproveAsync(Guid purchaseOrderId)
        {
            var orderId = await WholesaleOrderIdAsync(purchaseOrderId);
            var response = await wholesaler.PostAsync($"{OrderUrl(orderId)}/approve", null, Ct);
            await EnsureSuccessAsync(response);
        }

        /// <summary>«Editar pedido» del mayorista con el estado final de las líneas; precio nulo deja el de la lista (D-5).</summary>
        public async Task EditWholesaleOrderAsync(Guid orderId, params (Guid VariantId, decimal Quantity, decimal? UnitPrice)[] items)
        {
            var current = await wholesaler.GetAsync(OrderUrl(orderId), Ct);
            await EnsureSuccessAsync(current);
            using var document = JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(current));
            var version = document.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            var response = await PurchasesApiHarness.WriteAsync(
                wholesaler,
                HttpMethod.Put,
                OrderUrl(orderId),
                version,
                new
                {
                    items = items
                        .Select(item => new { variantId = item.VariantId, quantity = item.Quantity, unitPrice = item.UnitPrice })
                        .ToArray(),
                    proofs = (object?)null,
                    notes = (string?)null,
                    isRetail = false,
                });
            await EnsureSuccessAsync(response);
        }

        public async Task CancelWholesaleOrderAsync(Guid orderId)
        {
            var response = await wholesaler.PostAsJsonAsync($"{OrderUrl(orderId)}/cancel", new { reason = "Sin existencias" }, Ct);
            await EnsureSuccessAsync(response);
        }

        /// <summary>Corre el procesador del inbox de Compras hasta vaciar el lote (precedente de Pipeline).</summary>
        public async Task ProcessEventsAsync()
        {
            var processor = factory.Services.GetRequiredService<WholesaleOrderEventsProcessor>();
            while (await processor.ProcessBatchAsync(Ct))
            {
            }
        }

        public async Task<string> ApprovedPayloadAsync(Guid orderId) =>
            (await PurchasesApiHarness.RowsAsync<string>(
                factory,
                $"""
                SELECT payload::text AS "Value" FROM platform.outbox_messages
                WHERE event_name = {ApprovedEvent} AND payload ->> 'orderId' = {orderId.ToString()}
                """)).Single();

        /// <summary>Las filas del producto y de la variante, enteras, para comparar byte por byte.</summary>
        public Task<string> ProductRowAsync(Guid variantId) =>
            PurchasesApiHarness.ScalarAsync<string>(
                factory,
                $"""
                SELECT row_to_json(p)::text || row_to_json(v)::text AS "Value"
                FROM catalog.product_variants v JOIN catalog.products p ON p.id = v.product_id
                WHERE v.id = {variantId}
                """);

        /// <summary>El estado de la relación que muestra el detalle del proveedor (adenda).</summary>
        public async Task<string?> SupplierStatusAsync()
        {
            var response = await buyer.GetAsync($"/api/v1/tenants/{buyerTenant.TenantId}/suppliers/{supplierId}", Ct);
            await EnsureSuccessAsync(response);
            using var document = JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
            return document.RootElement.GetProperty("linkedRelationshipStatus").GetString();
        }

        public void Dispose()
        {
            wholesaler.Dispose();
            buyer.Dispose();
        }

        private string OrderUrl(Guid orderId) => $"/api/v1/tenants/{wholesalerTenant.TenantId}/orders/{orderId}";
    }
}

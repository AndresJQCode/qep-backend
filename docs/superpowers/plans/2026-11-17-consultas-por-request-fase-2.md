# Consultas por request, fase 2 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Quitar los viajes a Postgres que la fase 1 (rama `perf/consultas-por-request`, commits `133febab..914abd69`) dejó documentados como pendientes en las rutas de cotizaciones, pedidos, canales de WhatsApp y la bandeja de Messaging, y presupuestarlos con pruebas que cuenten comandos.

**Architecture:** La latencia de un endpoint es consultas en serie × ida y vuelta de red al nodo de Postgres (10-20 ms). Cada tarea reemplaza «una consulta por elemento» o «la misma consulta dos veces en el request» por una sola consulta o por un memo del scope, sin cambiar contratos HTTP ni reglas de autorización. Los cruces entre módulos siguen pasando por los puertos y adaptadores de Bootstrapper que ya existen; sólo se les agregan métodos por lote. Las pruebas de presupuesto reusan los dos contadores que el repo ya tiene: el `DbCommandInterceptor` por header de `RequestQueryBudgetTests` (Tenancy) y el `ActivityListener` de la fuente `Npgsql` de `QuotationPreviewStatementCountApiTests` (Quotations), que es lo mismo que mide Tempo en producción.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3, Testcontainers (Postgres 18).

**Spec:** No hay spec aparte. La autoridad es el conteo por código de la fase 1, recogido en el resumen de la sesión del 2026-11-17 y en `docs/superpowers/plans/2026-11-17-consultas-por-request-fase-2.md` (este archivo, § Inventario). Cada tarea cita archivo:línea del código de hoy.

## Inventario (lo medido en la fase 1, por código)

| Ruta | Hoy | Causa | Tarea |
|---|---|---|---|
| `POST quotations/preview` | 70-80 + 2T | `CatalogVariantReads.LoadTaxPercentagesAsync` hace un `tax_rates` por tasa distinta, y la ruta lo llama dos veces | 1 |
| `GET quotations/whatsapp-channels`, `POST quotations/{id}/send` | 7 + 3M + 3Z | por conexión: `FindAsync` + `tenant_modules` + `tenant_providers` (`QuotationsWhatsAppChannelDirectory.cs:24-27, 37-39`) | 2, 3 |
| `GET messaging/conversations` | ~15 + N | 6 `COUNT`/`SUM` separados (`ConversationQueries.cs:110-126`); la lista de conexiones se consulta dos veces (`MessagingConnectionDirectory.cs:121-122`) | 3, 4 |
| `POST quotations/{id}/order` | 26-55 + 3P | por comprobante: `file_resources` + `order_payment_proofs` (`ConvertQuotationToOrder.cs:81-85`) | 5 |
| `GET quotations/{id}`, `GET orders/{id}`, preview, order | +2-4 cada uno | el composer recarga el cliente y la empresa que ya cargó (`QuotationWithholdingLookup.cs:30, 52, 86`) | 6 |
| `quotations/{id}`, `orders/{id}` | sin presupuesto | nada falla si alguien vuelve a resolver por fila | 7 |

Fuera del plan, a propósito: el `publisher.PublishAsync` por comprobante (`PaymentProofCopies.cs:28-30`) es una copia en R2, no una consulta; `FindActiveMembershipIdAsync` (1 consulta por request de negocio) exigiría meter el id de membresía en la entrada cacheada de permisos, que es otro diseño; y el `HasApprovedTemplateAsync` por conexión de Meta se queda, porque lee el espejo de plantillas de esa conexión y hoy hay una o dos.

## Global Constraints

- TDD: RED antes que GREEN, con la salida literal de las dos. Un RED de compilación no levanta contenedores.
- Ningún módulo de negocio referencia `Modules.Integrations.Application` ni otro módulo: los cruces van por los puertos existentes y sus adaptadores en `src/Bootstrapper/` (`IntegrationsLayerTests`, `QuotationsLayerTests`).
- No cambia ningún contrato HTTP ni ningún código de error. No hay migraciones.
- Un memo por scope se escribe en un `Dictionary` de instancia sólo en servicios registrados `AddScoped`, y el repo no usa `Task.WhenAll` sobre un `DbContext` en handlers (`CustomerGeographyLookup.cs:108`). Verificar el registro antes de memoizar.
- Mientras se itera, la integración se filtra por nombre de prueba; la clase completa corre una vez por tarea; la suite completa la corre el owner. Antes de una corrida con contenedores: `Get-Process testhost -ErrorAction SilentlyContinue`.
- Commits convencionales en inglés, sin atribución de IA. Comentarios de código y pruebas tutean, en español colombiano.
- Rama: continuar en `perf/consultas-por-request` (worktree `qep-backend-worktrees/consultas-por-request`).

## Review Focus

1. **Una tasa de impuesto borrada entre el producto y la lectura.** `LoadTaxPercentagesAsync` hoy devuelve el diccionario sin esa tasa y quien llama decide `null` o `0`; con `ListAsync` tiene que seguir faltando, no salir con 0. Pinned en Tarea 1.
2. **Una conexión `Paused` o `NeedsAttention` en la lista.** `ListResolvedActiveAsync` tiene que excluirla igual que `ListActiveAsync` + `ResolveAsync` hacían juntos, y una con un secreto que no descifra tampoco se ofrece. Pinned en Tarea 2.
3. **Un tenant sin conversaciones.** El `COUNT … FILTER` único devuelve una fila con ceros, no cero filas: el DTO no puede quedar en `null`. Pinned en Tarea 4.
4. **Dos comprobantes con el mismo archivo en el mismo cuerpo.** Hoy el segundo falla con `order.payment_proof.file_not_available` por la consulta de «en uso» que ve el primero sólo después de guardar; con el lote, el duplicado dentro del cuerpo tiene que rechazarse igual. Pinned en Tarea 5.
5. **El memo del cliente en el composer y un request que lo modifica.** `QuotationWithholdingLookup` sólo lee; si un handler escribe el cliente en el mismo scope, lee la instancia que sigue el `DbContext`, no una copia. Pinned en Tarea 6 (prueba de que dos lecturas devuelven la misma instancia).

---

### Task 1: Porcentajes de IVA en una sola lectura del catálogo

**Files:**
- Modify: `src/Bootstrapper/CatalogVariantReads.cs:134-157`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantReadsTests.cs` (crear)

**Interfaces:**
- Consumes: `ITaxRateRepository.ListAsync(Guid tenantId, CancellationToken)` (`Modules.Catalog.Application/ITaxRateRepository.cs:13`); `FakeTaxRateRepository(params TaxRate[] rates)` (`tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:62`), al que hay que sumar contadores `ListCalls` y `FindCalls`.
- Produces: la misma firma `LoadTaxPercentagesAsync(ITaxRateRepository, Guid, IEnumerable<Product>, CancellationToken) → IReadOnlyDictionary<TaxRateId, int>`; cambia sólo cuántas veces lee.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task ThePercentagesOfThreeRatesAreOneListNotThreeFinds()
{
    var rates = new[] { Rate(19), Rate(5), Rate(0) };
    var repository = new FakeTaxRateRepository(rates);
    var products = rates.Select(rate => ProductTaxedWith(rate.Id)).Concat([ProductTaxedWith(rates[0].Id)]).ToArray();

    var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(repository, TenantId, products, CancellationToken.None);

    Assert.Equal(3, percentages.Count);
    Assert.Equal(19, percentages[rates[0].Id]);
    Assert.Equal(1, repository.ListCalls);
    Assert.Equal(0, repository.FindCalls);
}

// Review Focus 1: una tasa que ya no existe no aparece, y no sale con 0.
[Fact]
public async Task ADeletedRateIsAbsentNotZero()
{
    var repository = new FakeTaxRateRepository(Rate(19));
    var products = new[] { ProductTaxedWith(TaxRateId.New()) };

    var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(repository, TenantId, products, CancellationToken.None);

    Assert.Empty(percentages);
}
```

`Rate(int vat)` y `ProductTaxedWith(TaxRateId)` son helpers del archivo de prueba construidos con `TaxRate.Create` y `Product.Create` como hace `CatalogVariantTestDoubles.cs`. `FakeTaxRateRepository` gana dos propiedades `int ListCalls` / `int FindCalls` que suben en cada llamada.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariantReadsTests"`
Expected: FAIL, `Assert.Equal(1, repository.ListCalls)` con `0`, y `FindCalls` igual a `3`.

- [ ] **Step 3: Implement**

En `LoadTaxPercentagesAsync`: si ningún producto tiene `TaxRateId`, devolver vacío sin consultar; si no, `var rates = await taxRates.ListAsync(tenantId, cancellationToken)` una vez y armar el diccionario con `rates.Where(rate => wanted.Contains(rate.Id))`. Actualizar el comentario del método: «una lectura por request, no por tasa; las tasas de un tenant son un puñado (`CustomerTaxRateLookup.cs:22-23`)».

- [ ] **Step 4: Run tests to verify they pass**

Run: el mismo filtro de Step 2, más `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariant"`.
Expected: PASS.

- [ ] **Step 5: Confirm the preview no longer reads per rate**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationPreviewStatementCountApiTests"`
Expected: PASS; en la salida de `Report` ya no aparece `tax_rates … WHERE … id = @id` repetida.

- [ ] **Step 6: Commit**

```bash
git add src/Bootstrapper/CatalogVariantReads.cs tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantReadsTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs
git commit -m "perf(catalog): load tax percentages with one list instead of one find per rate"
```

---

### Task 2: Conexiones activas resueltas en una pasada

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs:71-92` (interfaz) y `:94-165` (implementación)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantProviders.cs` (memo por scope, como `TenantModules.cs`)
- Modify: `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs:20-57`
- Modify: `src/Bootstrapper/MessagingConnectionDirectory.cs:95-116` (`ListAsync`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationConnectionsTests.cs` (crear si no existe; dobles en `IntegrationsTestDoubles.cs`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:595` (`FakeIntegrationConnections` implementa el método nuevo)

**Interfaces:**
- Produces: en `IIntegrationConnections`,
  `Task<IReadOnlyList<ResolvedConnection>> ListResolvedActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken)`: las conexiones `Active` del proveedor que `ResolveAsync` resolvería, ordenadas por nombre e id como `ListActiveAsync`, en una sola `repository.ListAsync`; vacío si el proveedor no es visible o no está habilitado; una conexión con secreto ilegible se omite. `ResolveAsync` y `ListActiveAsync` no cambian.
- `TenantProviders.FindEnabledAsync` memoiza por `tenantId` dentro del scope (`Dictionary<Guid, IReadOnlySet<string>?>`), igual que `TenantModules`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task ListResolvedActiveReadsTheRepositoryOnceAndSkipsWhatResolveWouldRefuse()
{
    var active = Connection(status: ConnectionStatus.Active);
    var paused = Connection(status: ConnectionStatus.Paused);
    var unreadable = Connection(status: ConnectionStatus.Active, secretUnreadable: true);
    var repository = new FakeConnectionRepository(active, paused, unreadable);
    var connections = NewIntegrationConnections(repository);

    var resolved = await connections.ListResolvedActiveAsync(TenantId, IntegrationProviders.WhatsAppCloud.Key, CancellationToken.None);

    Assert.Equal([active.Id], resolved.Select(connection => connection.Id));
    Assert.Equal(1, repository.ListCalls);
    Assert.Equal(0, repository.FindCalls);
}

[Fact]
public async Task ListResolvedActiveIsEmptyWhenTheProviderIsHiddenForTheTenant()
{
    var connections = NewIntegrationConnections(new FakeConnectionRepository(Connection(status: ConnectionStatus.Active)), modules: TenantModuleSet.Empty);

    Assert.Empty(await connections.ListResolvedActiveAsync(TenantId, IntegrationProviders.WhatsAppCloud.Key, CancellationToken.None));
}
```

`FakeConnectionRepository` gana `ListCalls`/`FindCalls`; `NewIntegrationConnections` arma la clase con los dobles de `IntegrationsTestDoubles.cs` (catálogo real, `ITenantModules` fijo, `ITenantProviders` fijo, `ISecretProtector` de prueba).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests/Modules/Integrations/Modules.Integrations.UnitTests`
Expected: no compila: `IIntegrationConnections` no tiene `ListResolvedActiveAsync`.

- [ ] **Step 3: Implement `ListResolvedActiveAsync`**

Extraer de `ResolveAsync` un método privado `Resolve(IntegrationConnection connection, IntegrationProvider provider)` que haga la lectura de secretos y arme `ResolvedConnection` (o `null` si `stored.Unreadable.Count > 0`). `ListResolvedActiveAsync` repite las dos guardas de `ListActiveAsync` (visible y habilitado), lista una vez, filtra `Active` y proveedor, ordena, y aplica `Resolve` descartando `null`. Memoizar `TenantProviders.FindEnabledAsync` por scope (confirmar `AddScoped<ITenantProviders, TenantProviders>()` en `TenancyInfrastructureExtensions.cs:32`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationConnectionsTests"`
Expected: PASS.

- [ ] **Step 5: Use it in the two directories**

En `QuotationsWhatsAppChannelDirectory.ListAvailableAsync`, cada `foreach` recorre `await connections.ListResolvedActiveAsync(tenantId, <proveedor>, cancellationToken)` y desaparece el `ResolveAsync` por elemento; el filtro de plantilla y los campos de Zenvia quedan igual. En `MessagingConnectionDirectory.ListAsync` (líneas 95-116), lo mismo para WhatsApp e Instagram. `FakeIntegrationConnections` (`ShopifyAdaptersTestDoubles.cs:595`) implementa el método nuevo filtrando sus conexiones `Active`.

- [ ] **Step 6: Run the consumers' tests**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WhatsApp|FullyQualifiedName~MessagingConnection"` y `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~WhatsAppChannelApiTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Integrations src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantProviders.cs src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs src/Bootstrapper/MessagingConnectionDirectory.cs tests/Modules/Integrations tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs
git commit -m "perf(integrations): resolve the active connections of a provider in one read"
```

---

### Task 3: La lista de nombres de conexiones en una sola consulta

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs` (interfaz e implementación)
- Modify: `src/Bootstrapper/MessagingConnectionDirectory.cs:119-133` (`ListNamesAsync`, `ListAllAsync`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationConnectionsTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<ConnectionListing>> ListByProvidersAsync(Guid tenantId, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken)`: una `repository.ListAsync`, filtra por cualquiera de las claves, mismo orden que `ListByProviderAsync`; `ConnectionListing` gana `string ProviderKey` como último parámetro con valor por defecto `""` para no romper a quien ya lo construye.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task ListByProvidersReadsOnceForTwoProviders()
{
    var repository = new FakeConnectionRepository(
        Connection(provider: IntegrationProviders.WhatsAppCloud.Key), Connection(provider: IntegrationProviders.Instagram.Key), Connection(provider: IntegrationProviders.Zenvia.Key));
    var connections = NewIntegrationConnections(repository);

    var listed = await connections.ListByProvidersAsync(
        TenantId, [IntegrationProviders.WhatsAppCloud.Key, IntegrationProviders.Instagram.Key], CancellationToken.None);

    Assert.Equal(2, listed.Count);
    Assert.Equal(1, repository.ListCalls);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build tests/Modules/Integrations/Modules.Integrations.UnitTests`
Expected: no compila: `ListByProvidersAsync` no existe.

- [ ] **Step 3: Implement and use it**

Implementar `ListByProvidersAsync` en `IntegrationConnections`; `ListByProviderAsync` pasa a delegar en él con una sola clave. `MessagingConnectionDirectory.ListNamesAsync` y `ListAllAsync` llaman una vez con `[Provider, InstagramProvider]`; `ListAllAsync` decide el canal por `listing.ProviderKey`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationConnectionsTests"` y `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationListApiTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Integrations src/Bootstrapper/MessagingConnectionDirectory.cs tests/Modules/Integrations
git commit -m "perf(messaging): list the connection names of both channels in one query"
```

---

### Task 4: Los seis contadores de la bandeja en una consulta

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs:106-127`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListApiTests.cs` (prueba nueva en la misma clase)

**Interfaces:**
- Consumes: `ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned, int Automated, int AutomationActive)` (`MessagingDtos.cs:112`), sin cambios.
- Produces: la misma firma `CountsAsync(Guid tenantId, Guid? memberId, CancellationToken)`; una sola sentencia.

- [ ] **Step 1: Write the failing test**

Copiar a `ConversationListApiTests` el contador de sentencias de `QuotationPreviewStatementCountApiTests.StatementsOfAsync` (`ActivityListener` sobre la fuente `Npgsql`, filtrando por `TraceId`), adaptado a `GET`.

```csharp
[Fact]
public async Task TheCountsOfTheInboxAreOneStatement()
{
    // Siembra: una conversación abierta asignada al miembro, una abierta sin asignar, una resuelta (como la prueba de counts de arriba).
    var statements = await StatementsOfAsync(client, $"/api/v1/tenants/{tenantId}/messaging/conversations");

    var counts = statements.Where(sql => sql.Contains("count(", StringComparison.OrdinalIgnoreCase) && sql.Contains("messaging.conversations", StringComparison.Ordinal)).ToList();
    Assert.Single(counts);
}

// Review Focus 3: sin conversaciones, ceros y no null.
[Fact]
public async Task AnEmptyTenantGetsZeroCounts()
{
    var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{tenantId}/messaging/conversations");
    Assert.Equal(0, page.GetProperty("counts").GetProperty("open").GetInt32());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TheCountsOfTheInboxAreOneStatement"`
Expected: FAIL, `Assert.Single` con 6 (5 sin miembro).

- [ ] **Step 3: Implement**

Reemplazar las seis consultas por una proyección de LINQ sobre `dbContext.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId)` agrupada por constante:

```csharp
.GroupBy(_ => 1)
.Select(g => new
{
    Open = g.Count(c => c.Status == ConversationStatus.Open),
    Unread = g.Sum(c => c.UnreadCount > 0 ? c.UnreadCount : 0),
    Mine = g.Count(c => memberId != null && c.AssignedMemberId == memberId && c.Status == ConversationStatus.Open),
    Unassigned = g.Count(c => c.AssignedMemberId == null && c.Status == ConversationStatus.Open),
    Automated = g.Count(c => c.Status == ConversationStatus.Open && c.AutomationStatus != ConversationAutomationStatus.None),
    AutomationActive = g.Count(c => c.Status == ConversationStatus.Open && c.AutomationStatus == ConversationAutomationStatus.Active),
})
.SingleOrDefaultAsync(cancellationToken)
```

EF lo traduce a `count(*) FILTER (WHERE …)`. `null` (tenant sin filas) → `new ConversationCountsDto(0, 0, 0, 0, 0, 0)`. Reescribir el comentario de las líneas 108-109: la consulta única recorre `IX_conversations_tenant_*` por `tenant_id` en vez de seis index-only scans; con la base en otro nodo, un viaje gana a seis aunque cada uno fuera más barato.

- [ ] **Step 4: Check the plan once against the migrated schema**

Run: con el contenedor de la prueba vivo (`QepApiFactory` de Messaging), `EXPLAIN` del SQL que loguea EF para un tenant sembrado.
Expected: un solo `Index Scan` o `Bitmap` sobre un índice que empieza por `tenant_id`; sin `Seq Scan` sobre `messaging.conversations`. Si sale `Seq Scan`, agregar `.Where(c => c.Status == ConversationStatus.Open || c.UnreadCount > 0)` antes del `GroupBy` (las seis cuentas sólo miran abiertas o no leídas) y repetir.

- [ ] **Step 5: Run the class**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationListApiTests|FullyQualifiedName~ConversationListFiltersApiTests"`
Expected: PASS, incluida la aserción existente `(3, 1, 1)` de `open/mine/unassigned`.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListApiTests.cs
git commit -m "perf(messaging): compute the inbox counts in one statement"
```

---

### Task 5: Comprobantes de pago resueltos por lote al convertir

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationFileLookup.cs:13`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs:155`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs` (junto a `IsPaymentProofFileInUseAsync`)
- Modify: `src/Bootstrapper/QuotationFileLookup.cs` (adaptador de `IQuotationFileLookup`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs:22-60`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:81-85` y los demás llamadores de `ResolveAsync` (`rg -n "OrderPaymentProofResolver.ResolveAsync" src`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPaymentProofResolverTests.cs` (crear); `QuotationsTestDoubles.cs:169` (`StubQuotationFileLookup`) y el doble de `IOrderRepository` implementan los métodos nuevos

**Interfaces:**
- Produces: `IQuotationFileLookup.FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken) → IReadOnlyDictionary<Guid, QuotationFileRef>` (una `WHERE id = ANY`, filtrada por tenant en SQL; los que faltan no vienen); `IOrderRepository.ListPaymentProofFilesInUseAsync(IReadOnlyCollection<Guid> fileIds, OrderPaymentProofId? exceptProofId, CancellationToken) → IReadOnlySet<Guid>`; `OrderPaymentProofResolver.ResolveManyAsync(IQuotationFileLookup, IOrderRepository, Guid tenantId, IReadOnlyCollection<Guid> fileIds, OrderPaymentProofId? exceptProofId, CancellationToken)`, que lanza las mismas `QuotationsDomainException` con los mismos códigos (`order.payment_proof.file_not_found`, `file_not_available`, `file_type_not_allowed`, `file_too_large`), para el primer archivo que falle en el orden del cuerpo. `ResolveAsync` queda como `ResolveManyAsync` con un solo id.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task ThreeProofsAreTwoReadsNotSix()
{
    var files = new[] { Pdf(), Pdf(), Pdf() };
    var lookup = new CountingFileLookup(files);
    var orders = new CountingOrderRepository(inUse: []);

    await OrderPaymentProofResolver.ResolveManyAsync(lookup, orders, TenantId, files.Select(f => f.Id).ToArray(), null, CancellationToken.None);

    Assert.Equal(1, lookup.FindManyCalls);
    Assert.Equal(1, orders.InUseCalls);
}

[Fact]
public async Task AFileOfAnotherTenantIsNotFound()
{
    var foreign = Pdf(tenantId: Guid.CreateVersion7());
    var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
        OrderPaymentProofResolver.ResolveManyAsync(new CountingFileLookup(foreign), new CountingOrderRepository([]), TenantId, [foreign.Id], null, CancellationToken.None));
    Assert.Equal("order.payment_proof.file_not_found", error.Code);
}

// Review Focus 4: el mismo archivo dos veces en el cuerpo es "ya adjunto", como hoy.
[Fact]
public async Task TheSameFileTwiceInOneBodyIsNotAvailable()
{
    var proof = Pdf(isPaymentProof: true);
    var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
        OrderPaymentProofResolver.ResolveManyAsync(new CountingFileLookup(proof), new CountingOrderRepository([]), TenantId, [proof.Id, proof.Id], null, CancellationToken.None));
    Assert.Equal("order.payment_proof.file_not_available", error.Code);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`
Expected: no compila: `ResolveManyAsync`, `FindManyAsync`, `ListPaymentProofFilesInUseAsync` no existen.

- [ ] **Step 3: Implement**

`ResolveManyAsync`: `FindManyAsync` una vez; recorrer los ids en el orden del cuerpo aplicando las guardas de hoy (no encontrado o de otro tenant; disponible; mime; tamaño); los que son `IsPaymentProof` van a una sola `ListPaymentProofFilesInUseAsync`; un id repetido dentro del lote cuenta como en uso. `ConvertQuotationToOrder.cs:81-85` pasa a una llamada con `command.PaymentProofs.Select(p => p.FileId).ToArray()`; los demás llamadores de `ResolveAsync` siguen igual (un solo archivo).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderPaymentProofResolverTests|FullyQualifiedName~ConvertQuotationToOrder"` y `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~PaymentProof"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations src/Bootstrapper/QuotationFileLookup.cs tests/Modules/Quotations
git commit -m "perf(orders): resolve the payment proofs of a conversion in two reads"
```

---

### Task 6: El composer no recarga el cliente ni la empresa

**Files:**
- Modify: `src/Bootstrapper/QuotationWithholdingLookup.cs:27-100`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationWithholdingLookupTests.cs` (existe; extender)

**Interfaces:**
- Produces: sin cambio de firma. `QuotationWithholdingLookup` (registrado `AddScoped`, `QepServiceCollectionExtensions.cs:1973`) memoiza por scope `customers.FindAsync(tenantId, customerId)` y `companies.FindAsync(tenantId, companyId)` en dos `Dictionary` de instancia; `IsWithholdingAgentAsync`, `ForCustomerAsync` y `ForCompanyMunicipalAsync` leen del memo.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task TheCustomerIsReadOnceAcrossAgentCheckAndCustomerRates()
{
    var customers = new CountingCustomerRepository(Customer());
    var lookup = NewLookup(customers, new CountingCompanyRepository());

    await lookup.IsWithholdingAgentAsync(TenantId, CustomerId, "CO", "ReteICA", CityId, CancellationToken.None);
    await lookup.ForCustomerAsync(TenantId, CustomerId, DocumentDate, CancellationToken.None);

    Assert.Equal(1, customers.FindCalls);
}

// Review Focus 5: la misma instancia, no una copia.
[Fact]
public async Task TheMemoReturnsTheTrackedInstance()
{
    var customer = Customer();
    var customers = new CountingCustomerRepository(customer);
    var lookup = NewLookup(customers, new CountingCompanyRepository());

    await lookup.IsWithholdingAgentAsync(TenantId, CustomerId, "CO", "ReteICA", CityId, CancellationToken.None);

    Assert.Same(customer, customers.LastReturned);
}
```

Los dobles contadores se construyen sobre `FakeCustomerRepository` (`ShopifyAdaptersTestDoubles.cs:231`) y `FakeCompanyRepository` (`:699`), sumándoles `FindCalls` y `LastReturned`; `NewLookup` arma el adaptador como ya lo hace `QuotationWithholdingLookupTests`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationWithholdingLookupTests"`
Expected: FAIL, `FindCalls` igual a `2`.

- [ ] **Step 3: Implement**

Dos métodos privados `FindCustomerAsync`/`FindCompanyAsync` con el memo; los tres públicos los usan. Comentario de clase: scoped a propósito; el memo vive lo que el request, y el composer pide el mismo cliente dos o tres veces (`MunicipalWithholdingExplanation.cs:66-72`, `MunicipalContexts.cs:83-86`, `QuotationDraftBuilder.cs:76`).

- [ ] **Step 4: Run tests to verify they pass**

Run: el filtro de Step 2 y `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~Withholding|FullyQualifiedName~Municipal"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Bootstrapper/QuotationWithholdingLookup.cs tests/Bootstrapper/Bootstrapper.UnitTests
git commit -m "perf(quotations): read the customer and company once per request in the withholding lookup"
```

---

### Task 7: Presupuesto de consultas para quotations/{id} y orders/{id}

**Files:**
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RequestQueryBudgetApiTests.cs`
- Modify: `README.md` § Verificación › Consultas por request (una fila por ruta nueva)

**Interfaces:**
- Consumes: `StatementsOfAsync` de `QuotationPreviewStatementCountApiTests.cs:270-306` (moverlo a `QuotationsApiHarness` como `public static Task<IReadOnlyList<string>> StatementsOfAsync(HttpClient client, HttpRequestMessage request)` y hacer que la prueba de preview lo use desde ahí); `SharedQuotationsHost.StartTestAsync()`; `RegisterTenantAsync`, `CreateActiveCustomerAsync`, `CreateProductsAsync`, `BillingAccountOfNewCompanyAsync` del harness.

- [ ] **Step 1: Measure before setting the numbers**

Escribir la prueba con el presupuesto en `int.MaxValue` e imprimir el listado con `TestContext.Current.TestOutputHelper`:

```csharp
[Fact]
public async Task GettingAQuotationStaysWithinItsQueryBudget()
{
    var factory = await sharedHost.StartTestAsync();
    var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
    using var _ = client;
    var quotation = await CreateQuotationWithTwoLinesAndBillingAsync(client, tenantId);   // helper del archivo: cliente activo, dos productos, cuenta de cobro de una empresa CO
    await StatementsOfAsync(client, Get(tenantId, $"quotations/{quotation.Id}"));           // calienta el memo por host

    var statements = await StatementsOfAsync(client, Get(tenantId, $"quotations/{quotation.Id}"));

    TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, statements));
    Assert.True(statements.Count <= QuotationDetailBudget, $"{statements.Count} sentencias:{Environment.NewLine}{string.Join(Environment.NewLine, statements)}");
}
```

Igual para `GettingAnOrderStaysWithinItsQueryBudget` con `POST quotations/{id}/order` previo y `GET orders/{id}`. El stub de auth hace 2 consultas transversales (`tenants.status` y módulos); quedan dentro del número.

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~RequestQueryBudgetApiTests" --logger "console;verbosity=detailed"`
Expected: PASS con el listado impreso. Anotar los dos conteos en el `<remarks>` de la clase como «medido el <fecha>, antes de fijar el presupuesto».

- [ ] **Step 2: Set the budgets**

`QuotationDetailBudget` y `OrderDetailBudget` = el conteo medido en Step 1, redondeado hacia arriba al múltiplo de 5 más cercano. Es un techo contra regresiones, no una meta.

- [ ] **Step 3: Run to verify they pass**

Run: el mismo filtro.
Expected: PASS.

- [ ] **Step 4: Document and commit**

En README § «Consultas por request», una fila por ruta con el conteo y el presupuesto, y la nota de que el contador de Quotations es el `ActivityListener` de `Npgsql` (lo mismo que Tempo).

```bash
git add tests/Modules/Quotations/Modules.Quotations.IntegrationTests README.md
git commit -m "test(quotations): budget the SQL statements of the quotation and order detail"
```

---

## Verificación final de la rama

- `dotnet build Backend.slnx` sin errores (TreatWarningsAsErrors).
- `dotnet test tests/ArchitectureTests` completo: las capas de Integrations y Quotations no ganaron referencias.
- `RequestQueryBudgetTests` (Tenancy) sigue en 8 / 3.
- Revisión fuerte una sola vez, al final de la rama, con la lista de pendientes que quedan: `FindActiveMembershipIdAsync` por request y el `HasApprovedTemplateAsync` por conexión de Meta.
- `docs/estado.md` se actualiza al mergear a `develop`, no en la rama.

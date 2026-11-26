# Foto en las líneas del pedido del comprador — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** cada línea del detalle del pedido del comprador trae `imageUrl` (nullable), la foto actual de la variante o, sin ella, la principal del producto.

**Architecture:** `BuyerOrderGateway` (Quotations.Application) recibe el puerto existente `IQuotationProductLookup` y lo llama una vez por detalle, junto con la geografía, antes de anular cuando anula. `BuyerOrderLine` → `BuyerOrdersAdapter` (Bootstrapper) → `BuyerOrderLineDto` (Buyers.Application) llevan el campo hasta el JSON.

**Tech Stack:** .NET 10, xUnit, Testcontainers (integración).

**Spec:** `docs/superpowers/specs/2026-11-15-foto-linea-pedido-comprador-design.md`

## Global Constraints

- Sin migración ni columna nueva: la foto se resuelve al leer, desde `OrderItem.VariantId`.
- Una sola llamada a `IQuotationProductLookup.FindManyAsync` por detalle, con los `VariantId` distintos; sin líneas, ninguna.
- `ImageUrl` va **al final** de `BuyerOrderLine` y de `BuyerOrderLineDto`, tipo `string?`.
- Una excepción del lookup sale tal cual; nunca se convierte en foto nula.
- Presupuesto de pruebas del workspace: RED por compilación sin contenedores; integración filtrada por nombre mientras se itera; nunca dos corridas de integración a la vez.

## Review Focus

- Variante borrada del catálogo: el lookup no la devuelve → `ImageUrl` null, el detalle no falla (test en Task 1).
- Anular con el lookup de fotos caído: el pedido queda `Open`, sin guardar ni auditar (test en Task 1).
- Pedido no anulable: responde 409 sin llamar al lookup de fotos, como hoy con geografía (test en Task 1).
- Dos líneas de la misma variante no piden dos veces: ids distintos (test en Task 1).
- El JSON de la línea lleva la clave `imageUrl` aunque sea null (test en Task 2).

---

### Task 1: el gateway resuelve la foto de cada línea

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs` (record `BuyerOrderLine` l.10; constructor l.104-110; `Places` l.269-270; `ResolvePlacesIfCancellableAsync` / `ResolvePlacesAsync` l.283-302; `ToDetail` l.304; `ToLine` l.380-384)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/BuyerOrderGatewayTests.cs`

**Interfaces:**
- Consumes: `IQuotationProductLookup.FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken)` → `IReadOnlyDictionary<Guid, QuotationVariantRef>`; doble existente `StubQuotationProductLookup` (`QuotationsTestDoubles.cs:1266`, expone `FindManyCalls`).
- Produces: `public sealed record BuyerOrderLine(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal, string? ImageUrl);`
  constructor `BuyerOrderGateway(IOrderRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationLifecycleEventPublisher, IClock, IQuotationGeographyLookup, IQuotationProductLookup products)`.

- [ ] **Step 1: tests que fallan.** El helper `Gateway(...)` gana `IQuotationProductLookup? products = null`, por defecto un `StubQuotationProductLookup` sembrado con la variante de «Aceite» → `ImageUrl = "https://cdn.test/aceite.webp"` y sin la de «Banano». Agregar un `ThrowingProductLookup` privado (cuenta `Calls`, lanza `InvalidOperationException("catalog down")`). Tests:
  - `EachLineCarriesThePhotoOfItsVariantAndAnUnknownVariantHasNone`: `detail.Lines.Select(l => l.ImageUrl)` == `["https://cdn.test/aceite.webp", null]`.
  - `ThePhotosComeFromOneLookupWithDistinctVariantIds`: `FindManyCalls == 1`, ids pedidos == variantes distintas del pedido.
  - `APhotoLookupFailureIsNotDisguisedAsAMissingPhoto`: `FindAsync` lanza `"catalog down"`.
  - `APhotoLookupFailureLeavesTheOrderUncancelled`: igual a `ALookupFailureLeavesTheOrderUncancelled` (l.690) con `ThrowingProductLookup`; `Status == Open`, `SaveCalls == 0`, historial, auditoría y eventos vacíos.
  - `ANonCancellableOrderMakesNoPhotoLookup`: pedido `paidMarked: true`, `CancelAsync` → `ResourceConflictException` y `ThrowingProductLookup.Calls == 0`.
  - `CancelReturnsTheSameExtendedDetail` (l.525): agregar que las fotos de la respuesta de anular son las mismas que las de `FindAsync`.

- [ ] **Step 2: RED.** `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → falla por compilación (`BuyerOrderLine` sin `ImageUrl`, constructor sin el lookup). La salida del build es la evidencia.

- [ ] **Step 3: implementación.** Inyectar `IQuotationProductLookup products`. Renombrar el record privado `Places` a `DetailRefs(Departments, Cities, IReadOnlyDictionary<Guid, QuotationVariantRef> Variants)` y `ResolvePlaces*Async` a `ResolveRefs*Async`; la rama no anulable devuelve los tres diccionarios vacíos sin llamar a nada. `ResolveRefsAsync` llama a la geografía como hoy y después, si hay ítems, `products.FindManyAsync(order.TenantId, order.Items.Select(i => i.VariantId).Distinct().ToArray(), ct)`. `ToLine(item, appliesVatSurplus, variants)` pone `variants.GetValueOrDefault(item.VariantId)?.ImageUrl` al final.

- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~BuyerOrderGatewayTests"` → todos pasan, incluidos los existentes de geografía (`allowedCalls: 2` sigue valiendo: las fotos van después de la geografía).

- [ ] **Step 5: commit.** `feat(quotations): resolve the current product photo on buyer order lines`

### Task 2: el campo llega al contrato de la tienda

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs:6` (`BuyerOrderLineDto`)
- Modify: `src/Bootstrapper/BuyerOrdersAdapter.cs:48`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs` (l.57, l.107-109)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs` (`TheDetailCarriesLinesAndCanCancel`, l.357)
- Revisar: `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs` si construye líneas.

**Interfaces:**
- Consumes: `BuyerOrderLine(..., string? ImageUrl)` de Task 1.
- Produces: `public sealed record BuyerOrderLineDto(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal, string? ImageUrl);` → JSON `lines[].imageUrl`.

- [ ] **Step 1: tests que fallan.** En `BuyerOrdersAdapterTests`, la muestra pasa a `new BuyerOrderLine("Camisa", 1.5m, 10m, 17.85m, "https://cdn.test/camisa.webp")` y `new BuyerOrderLine("Gorra", 2m, 50m, 101.65m, null)`; `FindMapsEveryFieldIncludingLinesFlagAndCancellation` espera los mismos `ImageUrl` en los DTO. En `TheDetailCarriesLinesAndCanCancel`: las claves de la línea son exactamente `["imageUrl", "lineTotal", "name", "quantity", "unitPrice"]` y `line["imageUrl"]` es null (el producto sembrado no tiene foto).
- [ ] **Step 2: RED.** `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → falla por compilación.
- [ ] **Step 3: implementación.** `BuyerOrderLineDto` gana `string? ImageUrl` al final; el adaptador lo copia de `line.ImageUrl`.
- [ ] **Step 4: GREEN.** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~BuyerOrdersAdapterTests"`; después (sin otra corrida de integración en la máquina) `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicBuyerOrdersApiTests" -- xUnit.MaxParallelThreads=4` → pasan.
- [ ] **Step 5: commit.** `feat(buyers): expose the line photo in the buyer order detail`

### Cierre (antes de mergear a `develop`)

- [ ] Suite de los proyectos tocados, uno a la vez: `Modules.Quotations.UnitTests`, `Bootstrapper.UnitTests`, `Modules.Buyers.UnitTests`, `Modules.Store.IntegrationTests`, `ArchitectureTests`.
- [ ] Al mergear, en `develop`: `docs/estado.md` registra el campo y que el backend va primero que el storefront.

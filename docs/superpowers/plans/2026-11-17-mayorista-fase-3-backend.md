# Mayorista, fase 3 (órdenes de compra vinculadas entre tenants) — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El distribuidor arma una orden de compra con el catálogo de su mayorista a precio de lista, la envía y en el mayorista
nace un `Order` con `Source = Wholesale`; el mayorista la aprueba (tal cual o editada entera) o la anula, la respuesta vuelve
por outbox a la orden de compra, y al confirmarse se crean en el catálogo del distribuidor los productos que le faltan.

**Architecture:** Dos documentos enlazados por id (spec, enfoque B). Los cruces entre tenants son sólo dos puertos declarados en
`Modules.Purchases.Application` —`IWholesaleCatalog` (lectura) e `IWholesaleOrderIntake` (escritura y anulación del
comprador)— con adaptadores en `src/Bootstrapper/` que componen `WholesaleOffers` (servicio nuevo de
`Modules.Wholesale.Application`: relación `Active`, lista, visibilidad y precio) con `IWholesaleOrderGateway` (puerto nuevo de
`Modules.Quotations.Application`, gemelo de `IStoreOrderGateway`). La vuelta es asíncrona: Quotations escribe
`quotations.order.wholesale-approved.v1` / `…-cancelled.v1` en su outbox y Purchases los consume con su propio
`ClaimedOutboxConsumer` e inbox (copia del de Pipeline). La reconciliación, la creación de productos (por
`IExternalProductWriter`, con el `CatalogDbContext` enrolado en la transacción de Purchases como hace `ShopifyCatalogWriter`) y
la marca del inbox se confirman en una sola transacción.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-compras-y-mayorista-design.md` (§«Proveedor vinculado», «Productos nuevos en el
catálogo del comprador», «El pedido en el mayorista», «Lectura entre tenants», «Errores», «Respuestas al builder check», P-6
resuelta). Base: lo que ya está en `develop` de las fases 1 y 2 (`2026-11-17-compras-fase-1-backend.md` y
`2026-11-17-mayorista-fase-2-backend.md`, sus «Contrato HTTP», adendas y resoluciones). El plan del frontend cita la sección
«Contrato HTTP» de este documento.

**Rama:** `feature/mayorista-fase-3`, en el worktree `qep-backend-worktrees/mayorista-f3` (ya existe). Antes de **cada** tarea y
de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- **TDD estricto:** RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea. Un RED de compilación se prueba
  con la salida de `dotnet build`/`dotnet test`, sin levantar contenedores.
- **Durante la ejecución no se corren pruebas de integración ni suites** (regla del owner: hay otras sesiones en la máquina). Cada
  tarea se verifica con `dotnet build` y pruebas **unitarias filtradas** (incluidas las de `ArchitectureTests`, que no levantan
  contenedores). Las pruebas de integración **se escriben** en su tarea, se compilan (`dotnet build` del proyecto de pruebas) y su
  comando filtrado queda anotado como **«pendiente para el owner»**; nadie las corre en esta rama sin su orden.
- Presupuesto de pruebas (workspace, owner 2026-11-12): **tres bloques** —A: tareas 1-3, B: 4-6, C: 7-9—, un implementador y una
  revisión por bloque, revisión fuerte de toda la rama al final; cada tarea conserva su commit. Pruebas en primer plano.
- **Conventional commits sin atribución de IA** (sin `Co-Authored-By` ni similares; la regla del repo pisa cualquier recordatorio
  del harness).
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: avísale al developer; no lo detengas si no lo lanzaste.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- `Modules.Purchases.Application`, `Modules.Wholesale.Application` y `Modules.Quotations.Application` no se referencian entre sí:
  todo cruce va por puerto + adaptador en Bootstrapper (`PurchasesLayerTests`, `WholesaleLayerTests`, `QuotationsLayerTests`).
- **Todo método que cruza tenants recibe los dos ids** (`buyerTenantId` y `wholesalerTenantId`) y verifica en el servidor la
  relación; el tenant del comprador sale siempre de la ruta autorizada, nunca del cuerpo.
- Ningún status HTTP a mano: 403 `RequestForbiddenException`, 404 `ResourceNotFoundException`, 409 `ResourceConflictException`,
  412/428 como en fase 1, 422 `DomainException`/`ValidationException` (`src/Api/ApiExceptionHandler.cs`).
- Migraciones con el factory de diseño, todas aditivas (columnas nullable, `CHECK` ensanchado, tabla de inbox nueva).
- BFF: precios, totales, estados de línea y `allowedActions` los calcula el backend. Cada DTO nuevo dice en su `<summary>` por qué
  tiene esa forma. Enums por nombre. Comentarios y textos al usuario en español tuteando; identificadores en inglés.
- Sin paquetes nuevos (`Directory.Packages.props`, `global.json`, `.mcp.json` no se tocan). `docs/estado.md` **no se toca** en
  esta rama (se actualiza en `develop` al mergear).
- Fuera de alcance: stock, pagos, correo al mayorista por pedido nuevo (P-4), match por `Barcode` (P-5: sólo `Code`), recepción
  parcial.

## Decisiones de este plan (no del owner ni del spec)

Las marcadas (P) necesitan visto bueno del owner; las demás se revierten sin tocar el contrato.

| # | Decisión | Por qué |
|---|---|---|
| D-1 | (P) **«Por aprobar» es una marca, no un estado.** `Order` gana `WholesaleApprovedAt`/`WholesaleApprovedBy` y `POST /orders/{id}/approve` (sólo `Source = Wholesale`). Pendiente = `Open` sin aprobar; aprobado = `Open` aprobado; anulado = `Cancelled`. Los otros ejes (pago, preparación, facturado, archivo) no cambian | El spec habla de «Por aprobar» y `/approve`, pero el modelo de cuatro ejes (spec 2026-11-10) ya no tiene borrador ni aprobación: `OrderStatus` es `Open/Archived/Cancelled` |
| D-2 | (P) `approve` exige `quotations.order.manage` (el de editar), no `quotations.order.approve` | `quotations.order.approve` es hoy «marcar pagado» y el asesor no lo tiene (`QepServiceCollectionExtensions.cs:2391`); quien edita el pedido es quien lo revisa |
| D-3 | Aprobado ⇒ el pedido `Wholesale` deja de ser editable (`IsEditable` falso). Antes de aprobar, la edición no tiene restricciones nuevas (P-6) | El evento de vuelta sale sólo al aprobar: una edición posterior nunca llegaría al distribuidor |
| D-4 | (P) **Precio por línea editable sólo en pedidos `Wholesale`**: `PUT /orders/{id}` y su `/preview` aceptan `items[].unitPrice` opcional; en otro origen, 422 `order.item.unit_price_not_editable` | P-6 pide cambiar precios y la edición de hoy sólo manda `variantId` y `quantity` |
| D-5 | En un pedido `Wholesale` no hay escalas ni descuentos: la línea guarda `UnitPrice` = precio de lista (IVA incluido, ya redondeado) y `DiscountPercentage = 0`. Una línea agregada al editar se precia con la lista del distribuidor (`IWholesaleLinePricing`), sin mirar visibilidad ni estado de la relación; sin relación, precio base. Cambiar la cantidad conserva el precio. `isRetail` y `globalScaleFloor` se ignoran | Spec: «las escalas por cantidad no aplican a distribuidores»; la edición de hoy reprecia con escalas (`OrderItemEdits.cs:78-88`) |
| D-6 | Redondeo de un pedido `Wholesale`: la regla vigente del tenant (se suma a la lista de `OrderPricingRecalculation.cs:27`) | Es la misma regla que usa `WholesalePricing.ListPrice` (fase 2, D-10): el precio del pedido y el de la orden no divergen |
| D-7 | (P) Número y nombre que ve el mayorista (P-2): `ExternalReference` = número de la orden (`OC-0001`, ya se pinta en la lista) y el detalle suma `wholesale.buyerTenant.name` (el nombre del tenant, que Distribuidores ya muestra). El cliente sigue siendo el `Customer` del distribuidor (razón social) | Ningún dato nuevo cruza: ya se veía en Distribuidores |
| D-8 | Idempotencia de «Enviar»: `ExternalKey = "purchase-order:{purchaseOrderId:D}"` con el índice único `(tenant_id, source, external_key)` y `KeyedOrderCommit`. Si ya existe, el adaptador devuelve el pedido tal como está guardado (sin revalidar relación ni precio) y la orden de compra **reemplaza sus líneas** por las del pedido, con los ids de línea que el pedido guarda | Un reintento después de una falla entre los dos commits no crea otro pedido ni deja la orden con otras líneas que el pedido |
| D-9 | (P) **Costo sin IVA en la orden de compra.** La orden guarda `UnitCost = round2(precio de lista × 100 / (100 + tasa))` y la tasa efectiva de la línea del pedido; también congela `ListBasePrice`, `ListDiscountPercent` y `ListUnitPrice` (IVA incluido) para pintar «base y descuento». Total de la orden y del pedido pueden diferir en centavos | La fase 1 trabaja costos sin IVA (`PurchaseOrderMath`) y el costo del catálogo también es sin IVA (spec costo, P2); los precios del mayorista llevan IVA |
| D-10 | El modo vinculado se fija al guardar el borrador: `PurchaseOrder.SupplierTenantId` = `LinkedTenantId` del proveedor en ese momento. Un borrador de fase 2 a un proveedor vinculado (columna nula) sigue siendo externo hasta que se edite | Fase 2 dejó elegir el vinculado como proveedor manual (adenda); cambiar su modo en silencio rompería esas órdenes |
| D-11 | Con proveedor vinculado, `currency` y `lines[].unitCost` del cuerpo se ignoran: la moneda es la por defecto del mayorista y el costo sale de la lista. `unitCost` pasa a opcional en el validador; con proveedor externo sigue obligatorio (> 0) | BFF: el precio se calcula en el servidor |
| D-12 | El comprador cancela una orden vinculada sólo en `Draft` o `Sent`. En `Sent` cancela primero el pedido (puerto); si el mayorista ya aprobó, preparó, facturó o archivó → 409 `purchases.order.not_cancellable` | Spec: «mientras el mayorista no apruebe». Preparado/facturado no se anulan en Quotations sin deshacerlos |
| D-13 | Una orden vinculada no ofrece `confirm` (lo hace el evento). `allowedActions`: `Draft` → `edit, delete, send, cancel`; `Sent` → `cancel`; `Confirmed` → `receive`; resto `[]` | La confirmación manual es del proveedor externo |
| D-14 | Asesor del pedido: el del distribuidor si su membresía sigue activa; si no, el owner del tenant mayorista | Fase 2, D-15 lo dejó para esta fase; mismo criterio que la tienda («vacío = owner») |
| D-15 | Si el motor de pedidos rechaza el alta (cliente inactivo, empresa facturadora inválida, variante sin precio…) → 409 `purchases.order.wholesaler_rejected`; el código interno va al log, nunca al distribuidor | Precedente `StoreOrdersAdapter` (`LineUnavailable`): un código `quotation.*` del otro tenant no le sirve al comprador |
| D-16 | (P) **Producto sin precio de venta**: `ProductPricing.AllowsNoPrice` (init, sólo lo pone `IExternalProductWriter` con `Price = null`) salta `catalog.product.price_required`. El formulario nunca la pone, así que guardar el producto desde la pantalla exige precio | El dominio exige hoy al menos un precio (`ProductVariant.cs:204`); el spec pide «sin precio de venta» |
| D-17 | `ExternalProductDraft.Source` pasa a `string?`: el producto creado por Compras lleva `ExternalSource = null` | Con origen, `ExternallyManagedProducts` lo bloquea y el distribuidor no podría ponerle precio |
| D-18 | Producto creado: nombre = nombre de la línea (recortado a `Product.NameMaxLength`), una variante con el `Code` recortado, sin categoría, sin tasa, activo, costo = costo confirmado sin IVA en la moneda de la orden; sin costo si ese valor no es > 0. Match por `Code` exacto recortado contra variantes de cualquier estado. La línea guarda la variante encontrada o creada (`VariantId`) y `CreatedInCatalog` | Spec «Producto creado por el sistema»; `ProductVariantCost` exige monto > 0 |
| D-19 | La historia de la orden vinculada registra confirmación y anulación del mayorista con `MemberId = Guid.Empty`; el composer lo pinta «El mayorista» | Spec: «autor (miembro o el mayorista)» |
| D-20 | Un evento que no calza (orden de otro tenant, `LinkedOrderId` distinto, `SupplierTenantId` distinto del emisor) se marca procesado sin efecto y se loguea | Un evento ajeno nunca debe tocar una orden; reintentarlo no lo arregla |
| D-21 | Reconciliación: con id de línea conocido → `Unchanged`/`Changed`; línea de la orden que no vuelve → `Rejected` (confirmada en 0 con su costo); línea sin id o con id desconocido → `Added` (`Quantity = null`, `UnitCost` = confirmado). Repetir el evento en una orden ya `Confirmed`/`Received`/`Cancelled` no hace nada. Anulación del mayorista: `Cancelled` salvo `Received` | Spec «Respuestas al builder check» (P-6) |
| D-22 | Auditoría en el comprador: `purchases.order.{confirmed,cancelled}` con actor `Guid.Empty` y `changedFields` `["wholesaler"]`; en el mayorista, `quotations.order.wholesale_approved` y `quotations.order.cancelled` con actor `Guid.Empty` cuando cancela el comprador | La traza queda en el tenant dueño del dato |

## Review Focus

1. **Reintento de «Enviar» tras una falla entre los dos commits.** El pedido ya existe y la orden sigue `Draft`: el segundo `send`
   devuelve el mismo pedido, la orden pasa a `Sent` con las líneas del pedido y no hay un segundo `Order`. Pruebas:
   `SendRetryAfterTheOrderExistsAdoptsTheOrderLines` (Task 6) y `SendingTwiceCreatesOneWholesaleOrder` (Task 8, pendiente).
2. **El comprador cancela mientras el mayorista aprueba.** Gana uno por la versión del `Order`; si ganó la aprobación, la
   cancelación es 409 `not_cancellable` y la orden queda `Sent` hasta que llega el evento. Pruebas:
   `BuyerCancelAfterApprovalIsRejected` (Task 2) y `CancelAfterApprovalIs409AndTheEventStillConfirms` (Task 8, pendiente).
3. **Evento repetido o fuera de orden.** Aprobado dos veces, aprobado después de anulado, anulado después de `Received`: sin
   segundo producto, sin cambio de estado. Pruebas: `ApplyingTheApprovalTwiceChangesNothing`,
   `CancellationAfterReceivedIsIgnored` (Task 1) y `RedeliveredApprovalCreatesProductsOnce` (Task 6).
4. **Un `Code` que ya existe en el comprador, inactivo o con otras mayúsculas de espacio.** `" ABC-1 "` encuentra `ABC-1`
   inactivo y no lo toca; `abc-1` no lo encuentra (match exacto) y crea otro. Pruebas: `AnInactiveVariantWithTheCodeIsReusedUntouched`
   (Task 6) y `ExistingProductsStayByteForByte` (Task 8, pendiente).
5. **Producto sin precio en las pantallas de venta.** Una variante creada por Compras no aparece en tienda, POS ni selector de
   cotizaciones (sin precio en la moneda) y su edición desde el formulario exige precio. Pruebas:
   `ASystemProductWithoutPriceIsAccepted`, `TheFormStillRequiresAPrice` (Task 3).

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Purchases/Modules.Purchases.Domain/{PurchaseOrder.cs, PurchaseOrderLine.cs, PurchaseOrderLineStatus.cs, PurchaseOrderTotals.cs, PurchasesErrorCodes.cs, LinkedOrderLines.cs (nuevo)}` | Modificar / Crear | Modo vinculado, reconciliación |
| `src/Modules/Quotations/Modules.Quotations.Domain/{OrderSource.cs, Order.cs, OrderItem.cs, OrderInputs.cs, WholesaleOrderErrors.cs (nuevo)}` | Modificar / Crear | `Wholesale`, aprobación, precio editable |
| `src/Modules/Catalog/Modules.Catalog.Domain/{ProductPricing.cs, ProductVariant.cs}`, `…Application/{IExternalProductWriter.cs, ExternalProductWriter.cs}`, `src/Bootstrapper/ShopifyCatalogWriter.cs` | Modificar | Costo y precio opcionales, origen nulo, buscar por código |
| `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs`, `src/Bootstrapper/{OrdersReportSource.cs, MarginReportSource.cs}` | Modificar | `Wholesale` en `OrderSourceFilter` |
| `src/Modules/Wholesale/Modules.Wholesale.Application/{WholesaleOffers.cs (nuevo), Ports.cs}`, `src/Bootstrapper/WholesaleCatalogReader.cs` | Crear / Modificar | Relación, visibilidad y precio de lista |
| `src/Modules/Quotations/Modules.Quotations.Application/{IWholesaleOrderGateway.cs (nuevo), IWholesaleLinePricing.cs (nuevo), ApproveWholesaleOrder.cs (nuevo), OrderItemEdits.cs, SaveOrderEdits.cs, PreviewOrderEdits.cs, CancelOrder.cs, OrdersDtos.cs, OrderResponseComposer.cs, ListOrders.cs, OrderListing.cs, IQuotationLifecycleEventPublisher.cs, OrderPricingRecalculation.cs}`, `…Api/OrderEndpoints.cs`, `…Infrastructure/Persistence/{QuotationLifecycleEventPublisher.cs, QuotationsDbContext.cs, OrderRepository.cs, Migrations/*_AddWholesaleOrderLink.cs}` | Crear / Modificar | Pedido del mayorista |
| `src/Modules/Purchases/Modules.Purchases.Application/{WholesalePorts.cs (nuevo), PurchaseOrderLineBuilder.cs, CreatePurchaseOrder.cs, UpdatePurchaseOrder.cs, PreviewPurchaseOrder.cs, TransitionPurchaseOrder.cs, ConfirmPurchaseOrder.cs, PurchaseOrderWriteRules.cs, PurchasesDtos.cs, PurchaseOrderComposer.cs, PurchaseOrderPdfMapper.cs, ListSupplierCatalog.cs (nuevo), WholesaleOrderEventApplier.cs (nuevo), Ports.cs}` | Crear / Modificar | Casos de uso vinculados |
| `src/Modules/Purchases/Modules.Purchases.Infrastructure/{Persistence/PurchasesDbContext.cs, Persistence/PurchasesInbox.cs (nuevo), Persistence/Migrations/*_AddLinkedPurchaseOrders.cs, Messaging/{ClaimedOutboxConsumer.cs, WholesaleOrderEventPayloads.cs, WholesaleOrderEventsProcessor.cs, WholesaleOrderEventsWorker.cs} (nuevos), PurchasesInfrastructureExtensions.cs}` | Crear / Modificar | Persistencia e inbox |
| `src/Modules/Purchases/Modules.Purchases.Api/PurchaseOrderEndpoints.cs` | Modificar | Ruta del catálogo del mayorista |
| `src/Bootstrapper/{PurchasesWholesaleCatalog.cs, PurchasesWholesaleOrderIntake.cs, PurchasesCatalogWriter.cs, QuotationsWholesaleLinePricing.cs} (nuevos), PurchasesSupplierLookup.cs, QepServiceCollectionExtensions.cs` | Crear / Modificar | Adaptadores y registro |
| `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | Modificar | Worker nuevo (`Claimed`) |
| `tests/Modules/{Purchases,Quotations,Catalog,Wholesale,Reporting}/*.UnitTests/*`, `tests/Bootstrapper/Bootstrapper.UnitTests/*`, `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/Linked/*` | Crear / Modificar | Ver cada tarea |
| `README.md`, `CLAUDE.md` | Modificar | Tabla de endpoints y gotchas |

**Comandos** (PowerShell, desde la raíz del worktree). Unitarias, sí durante la ejecución:

```powershell
dotnet build src/Api
dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

Integración, **pendiente para el owner**: `dotnet build` del proyecto de pruebas sí; `dotnet test`, no.

---

## Contrato HTTP

Todo error es ProblemDetails con `code`. Montos y porcentajes como decimales JSON; enums por nombre. Lo que no se nombra acá no
cambia respecto de las fases 1 y 2.

### Distribuidor — catálogo del mayorista

`GET /api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog?search=&page=&pageSize=` — permiso
`purchases.order.manage` (es para armar una orden).

```
SupplierCatalogPage { currency, items: SupplierCatalogItem[], total, page, pageSize }
SupplierCatalogItem { variantId, code, name, basePrice, discountPercent, listPrice, taxRatePercent, unitCost }
```

- Sólo variantes activas de productos activos, con precio en `currency` (la moneda por defecto del mayorista) y dentro de la
  visibilidad de la lista del distribuidor (categorías con descendientes). `listPrice` = `WholesalePricing.ListPrice` (IVA
  incluido); `unitCost` = lo que la orden guardaría (sin IVA, D-9). Orden: nombre de producto y posición. `search` en código o
  nombre (ILIKE escapado). `pageSize` 50 por defecto, tope 200.
- Errores: 404 `suppliers.supplier.not_found`; 422 `purchases.order.supplier_not_linked` (el proveedor no es un mayorista en
  QEP); 403 `wholesale.relationship.inactive` (sin relación, `Blocked`, `Invited`, tenant mayorista no `Active` o sin módulo
  `wholesale`).

### Distribuidor — orden de compra (cambios sobre `/api/v1/tenants/{tenantId}/purchases/orders`)

- `POST /`, `PUT /{id}`, `POST /preview` con proveedor vinculado: `lines: [{ variantId, quantity }]`, donde `variantId` es una
  variante **del mayorista**; `currency` y `unitCost` se ignoran (D-11). Con proveedor externo, igual que fase 1 (`unitCost`
  obligatorio: `errors["Lines[0].UnitCost"]`).
- `POST /preview` vinculado responde además, por línea, `listPrice: { basePrice, discountPercent, unitPrice }` y la moneda del
  mayorista en `currency`.
- `POST /{id}/send` vinculado: crea el pedido en el mayorista (precio recalculado) y responde `PurchaseOrderDetail` en `Sent`
  con `linked.orderNumber`. Idempotente (D-8).
- `POST /{id}/cancel` vinculado en `Sent`: anula también el pedido del mayorista (D-12).
- `POST /{id}/confirm` vinculado → 409 `purchases.order.invalid_transition` (D-13).

```
PurchaseOrderSummary += { isLinked }
PurchaseOrderDetail  += { supplier: { …, isLinked },
                          linked: { orderNumber } | null,          // null en externo y en Draft vinculado
                          lines[i]: { variantId: Guid|null,         // del catálogo propio; en vinculada, null hasta confirmar
                                      supplierVariantId: Guid|null, // la del mayorista
                                      quantity: number|null,        // null en una línea Added
                                      lineStatus: 'Pending'|'Unchanged'|'Changed'|'Rejected'|'Added',
                                      listPrice: { basePrice, discountPercent, unitPrice } | null,
                                      createdInCatalog: bool } }
history[i].actorName: «El mayorista» en confirmación y anulación que llegan del mayorista (D-19)
```

- `allowedActions` vinculada: ver D-13. `confirmedWithChanges` como en fase 1, con `Added` contado como cambio.
- Errores nuevos: 403 `wholesale.relationship.inactive`; 422 `wholesale.product.not_visible` (variante fuera de la
  visibilidad, inactiva, de otro tenant o sin precio en la moneda del mayorista; `errors` no aplica, el `detail` nombra el
  código de la variante), `purchases.order.supplier_not_linked`; 409 `purchases.order.not_cancellable` (D-12),
  `purchases.order.wholesaler_rejected` (D-15).

### Mayorista — pedidos (cambios sobre `/api/v1/tenants/{tenantId}/orders`)

| Método y ruta | Cambio |
| --- | --- |
| `GET /` `?source=Wholesale&wholesaleApproval=Pending\|Approved` | Filtro nuevo `wholesaleApproval` (sólo tiene sentido con `Wholesale`; otro valor → 422 `validation.failed`, `errors.WholesaleApproval`). Ítem suma `wholesale: { purchaseOrderNumber, approval } \| null`; `externalReference` = número de la orden |
| `GET /{id}` | Detalle suma `wholesale` (abajo) e `items[].fromPurchaseOrder: bool` (false = la agregó el mayorista) |
| `POST /{id}/approve` | **Nuevo.** Sin cuerpo ni `If-Match` (como `prepare`/`archive`). Permiso `quotations.order.manage` (D-2). Responde lo mismo que las otras acciones de estado. Escribe `quotations.order.wholesale-approved.v1` |
| `PUT /{id}`, `POST /{id}/preview` | `items[]: { variantId, quantity, unitPrice? }`. `unitPrice` (≥ 0, IVA incluido) sólo en `Wholesale` sin aprobar (D-4); ausente conserva el precio guardado o, en una línea nueva, el de la lista (D-5) |
| `POST /{id}/cancel` | Sin cambio de contrato; en `Wholesale` escribe además `quotations.order.wholesale-cancelled.v1` |

```
OrderDetail.wholesale = { buyerTenant: { id, name }, purchaseOrderId, purchaseOrderNumber,
                          approval: 'Pending'|'Approved', approvedAt | null,
                          approvedBy: { memberId, name } | null,
                          cancelledByBuyer: bool } | null              // null fuera de Source = Wholesale
```

- Errores nuevos (422, `QuotationsDomainException`): `order.wholesale.source_invalid` (aprobar algo que no es `Wholesale`),
  `order.wholesale.already_approved`, `order.item.unit_price_not_editable`; un pedido aprobado responde a `PUT` el
  `ExternalOrderErrors.NotEditable` de siempre. Aprobar un anulado: `OrderStateErrors.NotOpen` existente.
- Reportes: `source=Wholesale` en los filtros que aceptan `OrderSourceFilter`.

### Eventos (contrato entre módulos, camelCase, leídos por nombre)

```
quotations.order.wholesale-approved.v1 { tenantId, orderId, orderNumber, buyerTenantId, purchaseOrderId, currency, approvedAt,
  lines: [{ sourcePurchaseOrderLineId | null, variantId, code, name, quantity, unitPrice, taxPercentage }] }   // todas las líneas finales
quotations.order.wholesale-cancelled.v1 { tenantId, orderId, buyerTenantId, purchaseOrderId, cancelledAt, byBuyer }
```

`unitPrice` es IVA incluido; `taxPercentage` es la tasa efectiva de la línea (`OrderItem.TaxPercentage`, que ya aplica si la
empresa cobra IVA). `code`/`name` salen de la foto de la línea, que el camino `Wholesale` siempre captura.

### Códigos nuevos (resumen)

| Status | Códigos |
| --- | --- |
| 403 | `wholesale.relationship.inactive` (en catálogo, alta, preview y envío vinculados) |
| 409 | `purchases.order.not_cancellable`, `purchases.order.wholesaler_rejected` |
| 422 | `wholesale.product.not_visible`, `purchases.order.supplier_not_linked`, `order.wholesale.source_invalid`, `order.wholesale.already_approved`, `order.item.unit_price_not_editable` |

---

## Bloque A — Dominio (sin contenedores)

### Task 1: Purchases — modo vinculado y reconciliación en el agregado

**Files:**
- Modify: `src/Modules/Purchases/Modules.Purchases.Domain/{PurchaseOrder.cs, PurchaseOrderLine.cs, PurchaseOrderLineStatus.cs, PurchaseOrderTotals.cs, PurchasesErrorCodes.cs}`
- Create: `src/Modules/Purchases/Modules.Purchases.Domain/LinkedOrderLines.cs`
- Test: `tests/Modules/Purchases/Modules.Purchases.UnitTests/{LinkedPurchaseOrderTests.cs, WholesaleReconciliationTests.cs, PurchaseOrderTotalsTests.cs}`

**Interfaces:**
- Produces:
  - `PurchaseOrder`: `Guid? SupplierTenantId`, `bool IsLinked => SupplierTenantId is not null`, `Guid? LinkedOrderId`,
    `string? LinkedOrderNumber`. `Create(…, Guid? supplierTenantId, …)` y `Replace(…, Guid? supplierTenantId, …)` (parámetro nuevo
    después de `supplierId`).
  - `PurchaseOrderLineDraft(Guid? VariantId, string Code, string Name, decimal Quantity, decimal UnitCost, decimal TaxRatePercent, LinkedLinePrice? Listing = null)`;
    `sealed record LinkedLinePrice(Guid SupplierVariantId, decimal BasePrice, decimal DiscountPercent, decimal UnitPrice)`.
    Un borrador vinculado exige `Listing` en todas las líneas y `VariantId = null`; uno externo, `VariantId` y sin `Listing`
    (si no, `ArgumentException`).
  - `PurchaseOrderLine`: `Guid? VariantId`, `Guid? SupplierVariantId`, `decimal? Quantity`, `decimal? ListBasePrice`,
    `decimal? ListDiscountPercent`, `decimal? ListUnitPrice`, `bool CreatedInCatalog`. `Status`: `Quantity is null` →
    `Added`; resto como fase 1.
  - `sealed record LinkedLineSnapshot(Guid LineId, Guid SupplierVariantId, string Code, string Name, decimal Quantity, decimal UnitCost, decimal TaxRatePercent, decimal BasePrice, decimal DiscountPercent, decimal UnitPrice)`.
  - `void SendToWholesaler(Guid linkedOrderId, string linkedOrderNumber, IReadOnlyList<LinkedLineSnapshot> lines, Guid memberId, DateTimeOffset now)`:
    sólo vinculada y con `Send` permitido; **reemplaza** las líneas con los ids del snapshot (D-8).
  - `sealed record WholesalerFinalLine(Guid? SourceLineId, Guid SupplierVariantId, string Code, string Name, decimal Quantity, decimal UnitCost, decimal TaxRatePercent)` (costo ya sin IVA).
  - `bool ApplyWholesalerApproval(IReadOnlyList<WholesalerFinalLine> lines, DateTimeOffset now)` — D-21; `false` sin cambios
    fuera de `Sent`. Historia con `Guid.Empty`.
  - `bool ApplyWholesalerCancellation(DateTimeOffset now)` — `Cancelled` desde `Draft`, `Sent` o `Confirmed`; `false` en
    `Received`/`Cancelled`.
  - `IReadOnlyList<PurchaseOrderLine> LinesNeedingCatalog()` — vinculada `Confirmed`, `ConfirmedQuantity > 0`, `VariantId` nulo.
  - `void LinkLineToCatalog(Guid lineId, Guid variantId, bool created)` — no sube `Version` (es efecto del sistema dentro de la
    misma transacción que la confirmación).
  - `AllowedActions()` vinculada según D-13; externa sin cambios.
  - `PurchaseOrderMath.NetOfVat(decimal grossUnitPrice, decimal taxRatePercent)` = `Round(gross × 100 / (100 + rate))`; las
    sumas tratan `Quantity` nula como 0 en `RequestedSubtotal`.
  - `PurchasesErrorCodes`: `NotCancellable = "purchases.order.not_cancellable"`, `SupplierNotLinked = "purchases.order.supplier_not_linked"`,
    `WholesalerRejected = "purchases.order.wholesaler_rejected"`, `ProductNotVisible = "wholesale.product.not_visible"`,
    `RelationshipInactive = "wholesale.relationship.inactive"`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// LinkedPurchaseOrderTests.cs
ALinkedDraftNeedsAListingOnEveryLine                  // ArgumentException
SendToWholesalerReplacesTheLinesWithTheSnapshotIds    // ids del snapshot, Status Sent, LinkedOrderNumber "PED-0042"
SendRetryAfterTheOrderExistsAdoptsTheOrderLines       // Review Focus 1: snapshot con otras cantidades → la orden queda con las del snapshot
ALinkedSentOrderOnlyAllowsCancel                      // AllowedActions == [Cancel]; Confirm → InvalidTransition
ALinkedConfirmedOrderOnlyAllowsReceive
AnExternalOrderKeepsPhaseOneActions                   // Sent → [Confirm, Cancel]
NetOfVatRoundsToTwoDecimals                           // 11900 @19 → 10000; 10000 @19 → 8403.36; 5000 @0 → 5000
// WholesaleReconciliationTests.cs
ApprovalWithSameQuantityAndCostIsUnchanged            // ConfirmedWithChanges false
ApprovalWithAnotherQuantityOrCostIsChanged
ALineThatDoesNotComeBackIsRejectedWithItsCost         // ConfirmedQuantity 0, ConfirmedUnitCost = UnitCost
ALineWithoutSourceIdIsAddedWithNullRequestedQuantity  // Status Added, UnitCost = confirmado
AnUnknownSourceIdIsAdded
ApplyingTheApprovalTwiceChangesNothing                // Review Focus 3: false, Version igual
ApprovalHistoryIsSignedByTheWholesaler                // MemberId Guid.Empty
CancellationAfterReceivedIsIgnored                    // Review Focus 3
LinesNeedingCatalogSkipsRejectedAndAlreadyLinkedLines
LinkLineToCatalogDoesNotBumpTheVersion
// PurchaseOrderTotalsTests.cs
RequestedTotalsIgnoreAddedLines
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~LinkedPurchaseOrder|FullyQualifiedName~WholesaleReconciliation|FullyQualifiedName~PurchaseOrderTotals"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** el modo vinculado en el agregado. `Quantity` nullable en el modelo obliga a ajustar el
  composer y el mapper del PDF de fase 1 (compila con `?? 0` sólo donde la línea ya no puede ser `Added`; en el PDF, una línea
  `Added` muestra «—» como cantidad pedida).

- [ ] **Step 4: Corre y verifica que pasan** (más toda la clase `PurchaseOrderTests` de fase 1, unitaria).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Domain src/Modules/Purchases/Modules.Purchases.Application tests/Modules/Purchases/Modules.Purchases.UnitTests
git commit -m "feat(purchases): linked purchase orders and wholesaler reconciliation"
```

---

### Task 2: Quotations — `OrderSource.Wholesale`, aprobación y precio editable

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/{OrderSource.cs, Order.cs, OrderItem.cs, OrderInputs.cs}`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/WholesaleOrderErrors.cs`
- Modify: `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs`, `src/Bootstrapper/{OrdersReportSource.cs:605-612, MarginReportSource.cs}`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WholesaleOrderTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/OrdersReportSourceTests.cs` (el mapeo de fuente)

**Interfaces:**
- Produces:
  - `OrderSource.Wholesale` (al final del enum); `OrderSourceFilter.Wholesale` (al final) mapeado a `OrderSource.Wholesale`.
  - `OrderLineInput(…, Guid? SourcePurchaseOrderLineId = null)` (parámetro opcional al final); `OrderItem.SourcePurchaseOrderLineId`.
  - `Order`: `Guid? BuyerTenantId`, `Guid? PurchaseOrderId`, `DateTimeOffset? WholesaleApprovedAt`, `MemberId? WholesaleApprovedBy`,
    `bool CancelledByBuyer`, `bool IsAwaitingWholesaleApproval`.
  - `static string WholesaleExternalKey(Guid purchaseOrderId) => $"purchase-order:{purchaseOrderId:D}"`.
  - `static Order CreateFromWholesale(OrderId id, Guid tenantId, string orderNumber, OrderHeader header, QuotationParties parties, QuotationBillingAccount billingAccount, IReadOnlyCollection<OrderLineInput> lines, string? buyerNotes, MemberId createdBy, DateTimeOffset occurredAt, DateOnly orderDate, Guid buyerTenantId, Guid purchaseOrderId, string purchaseOrderNumber)`
    — calca `CreateFromStore`: `ExternalReference = purchaseOrderNumber`, `ExternalKey = WholesaleExternalKey(…)`,
    `BuyerNotes = buyerNotes`, pago pendiente, `ShippingDetails.None`, cada línea con su `SourcePurchaseOrderLineId` y foto.
  - `void ApproveWholesale(MemberId approvedBy, DateTimeOffset occurredAt)` — `source_invalid`, `NotOpen`, `already_approved`;
    sube `Version`.
  - `IsEditable` = lo de hoy `&& !(Source == Wholesale && WholesaleApprovedAt is not null)` (D-3).
  - `bool CancelByWholesaleBuyer(Guid buyerTenantId, Guid purchaseOrderId, string reason, DateTimeOffset occurredAt)` —
    `false` si ya estaba anulado; lanza `order.wholesale.not_cancellable_by_buyer` (código interno, no viaja por HTTP) si los ids
    no calzan, si está aprobado, preparado, facturado o archivado. Pone `CancelledByBuyer = true`, `CancelledBy = MemberId(Guid.Empty)`.
  - `void ChangeItemUnitPrice(OrderItemId itemId, decimal unitPrice, MemberId updatedBy, DateTimeOffset occurredAt)` — sólo
    `Wholesale` (`order.item.unit_price_not_editable`), editable, `unitPrice >= 0`; reprecia la línea con `ChargesVat`,
    `Currency` y `PriceRounding` como `UpdateQuantity`.
  - `WholesaleOrderErrors`: `SourceInvalid`, `AlreadyApproved`, `NotCancellableByBuyer`, `UnitPriceNotEditable` con los códigos
    del contrato.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleOrderTests.cs
CreateFromWholesaleCarriesTheLinkAndTheSourceLineIds   // ExternalReference "OC-0007", ExternalKey "purchase-order:…", IsAwaitingWholesaleApproval
ApproveMarksTheOrderAndBumpsTheVersion
ApprovingTwiceIsAlreadyApproved
ApprovingANonWholesaleOrderIsSourceInvalid             // Manual
ApprovingACancelledOrderIsNotOpen
AnApprovedWholesaleOrderIsNotEditable                  // AddItem → ExternalOrderErrors.NotEditable
BeforeApprovalTheWholeOrderIsEditable                  // AddItem, UpdateItemQuantity, RemoveItem, ChangeItemUnitPrice
BuyerCancelBeforeApprovalCancels                       // CancelledByBuyer, true
BuyerCancelAfterApprovalIsRejected                     // Review Focus 2
BuyerCancelOfAPreparedOrInvoicedOrderIsRejected
BuyerCancelWithAnotherPurchaseOrderIdIsRejected
BuyerCancelTwiceReturnsFalse
ChangeItemUnitPriceOnAManualOrderIsNotEditable         // unit_price_not_editable
ChangeItemUnitPriceRecomputesSubtotalAndTax            // 11900 IVA incluido @19 → Subtotal 10000, TaxAmount 1900
// OrdersReportSourceTests.cs
WholesaleFilterMapsToTheWholesaleSource
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~WholesaleOrderTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** el dominio y el mapeo de reportes. La columna `source` guarda el nombre: el `CHECK` y la
  migración van en Task 7.

- [ ] **Step 4: Corre y verifica que pasan**, más
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~OrdersReportSource"` y
  `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --filter "FullyQualifiedName~Filter"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Domain src/Modules/Reporting src/Bootstrapper/OrdersReportSource.cs src/Bootstrapper/MarginReportSource.cs tests/Modules/Quotations tests/Bootstrapper
git commit -m "feat(orders): wholesale order source with approval and editable line price"
```

---

### Task 3: Catalog — costo y precio opcionales en `IExternalProductWriter`

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/{ProductPricing.cs, ProductVariant.cs:201-210}`,
  `…Application/{IExternalProductWriter.cs, ExternalProductWriter.cs}`, `src/Bootstrapper/ShopifyCatalogWriter.cs` (sólo compila)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/{ProductVariantPricingTests.cs, ExternalProductWriterTests.cs}`

**Interfaces:**
- Produces:
  - `ProductPricing.AllowsNoPrice { get; init; }` — con `true` y `Prices` vacío, `ApplyPricing` no lanza `price_required` (D-16).
  - `ExternalVariantDraft(string Key, string Code, string? Barcode, IReadOnlyList<string> OptionValues, decimal? Price, decimal? Cost = null)`.
    `Price` nulo sólo en `CreateAsync` (en `UpdateAsync` → `ArgumentException`); `Cost` > 0 se guarda en `ProductVariantInput.Costs`
    con la moneda del borrador; nulo o ≤ 0, sin costo.
  - `ExternalProductDraft.Source` → `string?` (D-17).
  - `IExternalProductWriter.FindVariantIdByCodeAsync(Guid tenantId, string code, CancellationToken ct) : Task<Guid?>` — código
    recortado, exacto, cualquier estado del producto y la variante.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// ProductVariantPricingTests.cs
ASystemProductWithoutPriceIsAccepted        // Review Focus 5: AllowsNoPrice + Prices vacío
TheFormStillRequiresAPrice                  // Review Focus 5: sin AllowsNoPrice → catalog.product.price_required
// ExternalProductWriterTests.cs
CreateWithCostStoresItInTheDraftCurrency    // Cost 8403.36 COP → CostIn("COP") == 8403.36
CreateWithoutPriceLeavesNoPrice
CreateWithANullSourceIsNotExternallyManaged // ExternalSource null
ACostOfZeroIsNotStored
UpdateWithANullPriceThrows
FindVariantIdByCodeTrimsAndMatchesInactiveVariants   // " ABC-1 " encuentra la variante inactiva "ABC-1"; "abc-1" no
CreatedProductHasNoTaxRate                  // TaxRateId null → producto sin tasa (si el resolver impone una por defecto, se salta con TaxRateId null explícito sólo en este camino)
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductVariantPricing|FullyQualifiedName~ExternalProductWriter"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa.** Shopify sigue mandando `Price` y `Source` como hoy.

- [ ] **Step 4: Corre y verifica que pasan**, más las pruebas unitarias de Shopify que usan el escritor:
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyCatalogWriter"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Catalog src/Bootstrapper/ShopifyCatalogWriter.cs tests/Modules/Catalog tests/Bootstrapper
git commit -m "feat(catalog): optional cost and price for system-created products"
```

**Revisión del bloque A.**

---

## Bloque B — Application

### Task 4: Wholesale — `WholesaleOffers` (relación, visibilidad y precio de lista)

**Files:**
- Create: `src/Modules/Wholesale/Modules.Wholesale.Application/WholesaleOffers.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/Ports.cs`, `src/Bootstrapper/WholesaleCatalogReader.cs`,
  `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro scoped)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleOffersTests.cs`; integración (escrita, pendiente):
  `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/WholesaleAdaptersTests.cs` (lector)

**Interfaces:**
- Consumes: `IDistributorRepository`, `IPriceListRepository`, `ITenantDefaultCurrency`, `ITenantPriceRounding`,
  `WholesalePricing.ListPrice`, el chequeo de tenant y módulo de la aceptación (rulings W14/W16 de fase 2).
- Produces:
  - `IDistributorRepository.FindByTenantsAsync(Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken ct) : Task<Distributor?>`.
  - `sealed record WholesaleSellableVariant(Guid VariantId, string Code, string Name, decimal BasePrice, decimal TaxRatePercent, IReadOnlyList<Guid> CategoryPath)`.
  - `IWholesaleCatalogReader.FindSellableAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken ct) : Task<IReadOnlyDictionary<Guid, WholesaleSellableVariant>>`
    (activas, producto activo, con precio en `currency`; `CategoryPath` = la categoría y sus ancestros) y
    `SearchSellableAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid>? categoryIds, string? search, int page, int pageSize, CancellationToken ct) : Task<(IReadOnlyList<WholesaleSellableVariant> Items, int Total)>`.
  - `sealed record WholesaleTerms(Guid DistributorId, Guid CustomerId, Guid AdvisorMemberId, Guid BillingCompanyId, string Currency, decimal DiscountPercent, IReadOnlyCollection<Guid>? VisibleCategoryIds)`.
  - `sealed record WholesaleOffer(Guid VariantId, string Code, string Name, decimal BasePrice, decimal DiscountPercent, decimal ListPrice, decimal TaxRatePercent)`.
  - `public sealed class WholesaleOffers`:
    - `Task<WholesaleTerms> ResolveTermsAsync(Guid wholesalerTenantId, Guid buyerTenantId, CancellationToken ct)` — sin
      relación, no `Active`, tenant mayorista no `Active` o sin `wholesale` → `RequestForbiddenException("wholesale.relationship.inactive", …)`.
    - `Task<(string Currency, IReadOnlyList<WholesaleOffer> Items, int Total)> SearchAsync(Guid wholesalerTenantId, Guid buyerTenantId, string? search, int page, int pageSize, CancellationToken ct)`.
    - `Task<(WholesaleTerms Terms, IReadOnlyDictionary<Guid, WholesaleOffer> Offers)> PriceVisibleAsync(Guid wholesalerTenantId, Guid buyerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)`
      — un id que no vuelve o queda fuera de la visibilidad → `WholesaleDomainException("wholesale.product.not_visible", …)`
      con el código de la variante cuando se conoce.
    - `Task<IReadOnlyDictionary<Guid, decimal>> ListPricesForOrderAsync(Guid wholesalerTenantId, Guid buyerTenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)`
      — D-5: sin mirar estado de la relación ni visibilidad; sin relación, diccionario vacío.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleOffersTests.cs (fakes del módulo)
WithoutRelationshipIsRelationshipInactive          // Lectura entre tenants
ABlockedRelationshipIsRelationshipInactive
AnInvitedRelationshipIsRelationshipInactive
AWholesalerWithoutTheModuleIsRelationshipInactive
AProductOutsideTheCategoriesIsNotVisible           // lista Categories [Hogar]; variante en Cocina → not_visible
ADescendantCategoryIsVisible                       // Hogar > Baño
AllActiveSeesEveryPricedVariant
ListPriceUsesTheWholesalerRounding                 // COP sin decimales: base 10000, 15 % → 8500; base 9999, 12.5 % → 8749
SearchReturnsBaseDiscountListAndTax
OrderPricesIgnoreBlockedAndVisibility              // relación Blocked, variante fuera de visibilidad → precio de lista igual
OrderPricesWithoutRelationshipAreEmpty
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~WholesaleOffersTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** `WholesaleOffers`, los métodos nuevos del repositorio y del lector (el filtro por descendientes ya
  existe en `SampleAsync`; reutilízalo) y registra el servicio. Escribe en `WholesaleAdaptersTests` (integración, pendiente)
  `FindSellableSkipsInactiveAndUnpricedVariants` y `SearchSellableFiltersByCategoryAndText`.

- [ ] **Step 4: Corre y verifica que pasan**, más `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` y
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WholesaleLayerTests"` → PASS.
  Pendiente para el owner: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~WholesaleAdaptersTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wholesale src/Bootstrapper/WholesaleCatalogReader.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Wholesale
git commit -m "feat(wholesale): distributor offers with relationship and visibility guards"
```

---

### Task 5: Quotations — gateway del pedido mayorista, aprobación y edición

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/{IWholesaleOrderGateway.cs, IWholesaleLinePricing.cs, ApproveWholesaleOrder.cs}`
- Modify: `…Application/{OrderItemEdits.cs, SaveOrderEdits.cs, PreviewOrderEdits.cs, CancelOrder.cs, OrdersDtos.cs, OrderResponseComposer.cs, ListOrders.cs, OrderListing.cs, IOrderRepository.cs, IQuotationLifecycleEventPublisher.cs, OrderPricingRecalculation.cs:27}`,
  `…Api/OrderEndpoints.cs` (acción `approve` con `MapStateAction`), `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/{WholesaleOrderGatewayTests.cs, ApproveWholesaleOrderTests.cs, WholesaleOrderEditsTests.cs}`

**Interfaces:**
- Consumes: Task 2; `OrderDraftBuilder`, `KeyedOrderCommit`, `IQuotationCustomerLookup`, `IQuotationCompanyLookup`.
- Produces:
  - `sealed record WholesaleOrderLine(Guid PurchaseOrderLineId, Guid VariantId, decimal Quantity, decimal UnitPrice)`.
  - `sealed record WholesaleOrderDraft(Guid TenantId, Guid BuyerTenantId, Guid PurchaseOrderId, string PurchaseOrderNumber, Guid ClientId, Guid AdvisorMembershipId, Guid BillingCompanyId, string Currency, string? Notes, IReadOnlyList<WholesaleOrderLine> Lines)`.
  - `sealed record WholesaleOrderResultLine(Guid PurchaseOrderLineId, Guid VariantId, string Code, string Name, decimal Quantity, decimal UnitPrice, int TaxPercentage)`.
  - `sealed record WholesaleOrderResult(Guid OrderId, string OrderNumber, string Currency, IReadOnlyList<WholesaleOrderResultLine> Lines, bool AlreadyExisted)`.
  - `interface IWholesaleOrderGateway { Task<WholesaleOrderResult?> FindAsync(Guid tenantId, Guid purchaseOrderId, CancellationToken ct); Task<WholesaleOrderResult> CreateAsync(WholesaleOrderDraft draft, CancellationToken ct); Task<bool> CancelByBuyerAsync(Guid tenantId, Guid orderId, Guid buyerTenantId, Guid purchaseOrderId, string purchaseOrderNumber, CancellationToken ct); }`
    — `CreateAsync` precia con el `UnitPrice` dado (tasa y foto del catálogo), `isRetail = false`, sin escalas, empresa
    facturadora `BillingCompanyId` con su cuenta por defecto, cliente y asesor dados; publica `PublishOrderCreated`; con la clave
    ya tomada devuelve `AlreadyExisted = true`. `CancelByBuyerAsync` escribe `PublishOrderCancelled` y `PublishWholesaleCancelled`,
    audita con actor `Guid.Empty` y motivo `"Cancelada por el distribuidor ({purchaseOrderNumber})."`. Implementación `WholesaleOrderGateway` (calca `StoreOrderGateway`).
  - `interface IWholesaleLinePricing { Task<IReadOnlyDictionary<Guid, decimal>> ListPricesAsync(Guid tenantId, Guid buyerTenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); }`.
  - `IQuotationLifecycleEventPublisher.PublishWholesaleApproved(Order order, DateTimeOffset at)` y `PublishWholesaleCancelled(Order order, bool byBuyer, DateTimeOffset at)` (contrato de eventos arriba).
  - `ApproveWholesaleOrderCommand(Guid TenantId, Guid OrderId)` con el tipo de respuesta de `PrepareOrderCommand`; handler:
    autorizar `OrderManage` → cargar o 404 → `ApproveWholesale` → `PublishWholesaleApproved` → auditar
    `quotations.order.wholesale_approved` → guardar.
  - `OrderEditItemRequest(Guid VariantId, decimal Quantity, decimal? UnitPrice = null)`; `OrderItemAddition(Guid VariantId, decimal Quantity, decimal? UnitPrice = null)`.
  - `OrderItemEdits.ApplyAsync`: con `order.Source == Wholesale` usa una rama propia (D-5: precio de `IWholesaleLinePricing`
    para nuevas, si falta, precio base del catálogo; cantidad sin repreciar; `unitPrice` presente → `ChangeItemUnitPrice`); en
    otro origen un `UnitPrice` presente → `order.item.unit_price_not_editable`.
  - `CancelOrder`: en `Wholesale` publica además `PublishWholesaleCancelled(order, byBuyer: false, …)`.
  - DTOs: `OrderWholesaleDto(OrderTenantRefDto BuyerTenant, Guid PurchaseOrderId, string PurchaseOrderNumber, string Approval, DateTimeOffset? ApprovedAt, OrderMemberRefDto? ApprovedBy, bool CancelledByBuyer)` en el detalle;
    `OrderListWholesaleDto(string PurchaseOrderNumber, string Approval)` en la lista; `items[].fromPurchaseOrder`. El nombre del
    tenant comprador sale de `ITenantDirectory` (ya inyectado en `StoreOrderGateway`).
  - `ListOrdersQuery(…, string? WholesaleApproval = null)` y `IOrderRepository` filtra por `wholesale_approved_at` nulo/no nulo.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleOrderGatewayTests.cs
CreateUsesTheGivenPricesWithoutScales                 // producto con escala 10 % desde 5 u.; 6 u. a 8500 → UnitPrice 8500, Discount 0
CreateStoresTheSourceLineIdsAndThePurchaseOrderNumber
CreateWithATakenKeyReturnsTheExistingOrder            // AlreadyExisted, mismas líneas guardadas
CreatePublishesOrderCreated
CancelByBuyerPublishesBothCancellationEvents          // order.cancelled + wholesale-cancelled byBuyer true
CancelByBuyerOfAnApprovedOrderThrowsNotCancellableByBuyer
// ApproveWholesaleOrderTests.cs
ApprovePublishesEveryFinalLineWithItsSourceId         // línea agregada → sourcePurchaseOrderLineId null
ApproveNeedsOrderManage                               // 403 authorization.denied
ApproveAnotherTenantsOrderIs404
// WholesaleOrderEditsTests.cs
AnAddedLineTakesTheDistributorListPrice               // IWholesaleLinePricing devuelve 8500
AnAddedLineWithoutListPriceTakesTheBasePrice
AQuantityChangeKeepsTheUnitPrice
AnExplicitUnitPriceOverridesIt
AUnitPriceOnAManualOrderIs422
AnApprovedWholesaleOrderCannotBeEdited                // NotEditable
CancellingAWholesaleOrderPublishesWholesaleCancelled  // byBuyer false
ListFiltersByWholesaleApproval                         // Pending / Approved / valor inválido → validation.failed "WholesaleApproval"
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~WholesaleOrderGateway|FullyQualifiedName~ApproveWholesaleOrder|FullyQualifiedName~WholesaleOrderEdits"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** y registra el handler, el gateway y el comando (`MapStateAction(collection, "approve", OrdersPermissions.OrderManage, …)`).
  `OrderPricingRecalculation`: `Wholesale` se suma a los orígenes con la regla del tenant (D-6).

- [ ] **Step 4: Corre y verifica que pasan**, más
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered|FullyQualifiedName~QuotationsLayerTests"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations
git commit -m "feat(orders): wholesale order gateway, approval and whole-order edits"
```

---

### Task 6: Purchases — casos de uso vinculados y aplicador de eventos

**Files:**
- Create: `src/Modules/Purchases/Modules.Purchases.Application/{WholesalePorts.cs, ListSupplierCatalog.cs, WholesaleOrderEventApplier.cs}`
- Modify: `…Application/{Ports.cs (PurchasesSupplierRef), PurchaseOrderLineBuilder.cs, CreatePurchaseOrder.cs, UpdatePurchaseOrder.cs, PreviewPurchaseOrder.cs, TransitionPurchaseOrder.cs, ConfirmPurchaseOrder.cs, PurchaseOrderWriteRules.cs, PurchasesDtos.cs, PurchaseOrderComposer.cs, PurchaseOrderPdfMapper.cs}`,
  `src/Bootstrapper/{PurchasesSupplierLookup.cs, QepServiceCollectionExtensions.cs}`
- Test: `tests/Modules/Purchases/Modules.Purchases.UnitTests/{LinkedPurchaseOrderHandlersTests.cs, SupplierCatalogTests.cs, WholesaleOrderEventApplierTests.cs, PurchaseOrderComposerTests.cs, PurchaseOrderWriteRulesTests.cs}`

**Interfaces:**
- Consumes: Task 1.
- Produces (`WholesalePorts.cs`):
  - `PurchasesSupplierRef(…, Guid? LinkedTenantId)` (parámetro nuevo al final; el adaptador lo lee de `Supplier.LinkedTenantId`).
  - `sealed record WholesaleCatalogItem(Guid VariantId, string Code, string Name, decimal BasePrice, decimal DiscountPercent, decimal ListPrice, decimal TaxRatePercent)`.
  - `interface IWholesaleCatalog { Task<(string Currency, IReadOnlyList<WholesaleCatalogItem> Items, int Total)> SearchAsync(Guid buyerTenantId, Guid wholesalerTenantId, string? search, int page, int pageSize, CancellationToken ct); Task<(string Currency, IReadOnlyDictionary<Guid, WholesaleCatalogItem> Items)> PriceAsync(Guid buyerTenantId, Guid wholesalerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); }`
    — el adaptador traduce `wholesale.product.not_visible` a `PurchasesDomainException(PurchasesErrorCodes.ProductNotVisible, …)`;
    el 403 ya sale como `RequestForbiddenException`.
  - `sealed record WholesaleIntakeLine(Guid PurchaseOrderLineId, Guid VariantId, decimal Quantity)`;
    `sealed record WholesaleIntakeRequest(Guid BuyerTenantId, Guid WholesalerTenantId, Guid PurchaseOrderId, string PurchaseOrderNumber, string? Notes, IReadOnlyList<WholesaleIntakeLine> Lines)`;
    `sealed record WholesaleIntakeResult(Guid OrderId, string OrderNumber, string Currency, IReadOnlyList<LinkedLineSnapshot> Lines, bool AlreadyExisted)`.
  - `interface IWholesaleOrderIntake { Task<WholesaleIntakeResult> SubmitAsync(WholesaleIntakeRequest request, CancellationToken ct); Task CancelAsync(Guid buyerTenantId, Guid wholesalerTenantId, Guid purchaseOrderId, string purchaseOrderNumber, Guid orderId, CancellationToken ct); }`
    — `CancelAsync` lanza `ResourceConflictException(PurchasesErrorCodes.NotCancellable, …)` y es idempotente si ya está anulado.
  - `sealed record PurchasesCatalogEntry(Guid VariantId, bool Created)`;
    `interface IPurchasesCatalogWriter { Task<PurchasesCatalogEntry> EnsureVariantAsync(Guid tenantId, string code, string name, string currency, decimal? unitCost, DateTimeOffset now, CancellationToken ct); }`
    — exige la transacción de Purchases abierta (Task 7).
  - `ListSupplierCatalogQuery(Guid TenantId, Guid SupplierId, string? Search, int Page, int PageSize) : IQuery<SupplierCatalogPageDto>` y su handler:
    autoriza `purchases.order.manage`; proveedor ajeno → 404 `suppliers.supplier.not_found`; no vinculado → 422
    `supplier_not_linked`; `unitCost` = `PurchaseOrderMath.NetOfVat(listPrice, taxRatePercent)`; `pageSize` 50 por defecto, tope 200.
  - DTOs: `SupplierCatalogItemDto(Guid VariantId, string Code, string Name, decimal BasePrice, decimal DiscountPercent, decimal ListPrice, decimal TaxRatePercent, decimal UnitCost)`,
    `SupplierCatalogPageDto(string Currency, IReadOnlyList<SupplierCatalogItemDto> Items, int Total, int Page, int PageSize)`.
  - `PurchaseOrderLineBuilder`: proveedor con `LinkedTenantId` → `IWholesaleCatalog.PriceAsync` y borradores con `Listing`; sin él,
    fase 1 (`unitCost` nulo → `ValidationException` en `Lines[i].UnitCost`). Devuelve también la moneda efectiva.
  - Send: vinculada → `IWholesaleOrderIntake.SubmitAsync` → `SendToWholesaler` → audita `purchases.order.sent` → guarda.
    Cancel: vinculada en `Sent` → `CancelAsync` antes de `Cancel`. Confirm: vinculada → 409 `invalid_transition` (por
    `AllowedActions`).
  - `public sealed record WholesaleApprovedLine(Guid? SourcePurchaseOrderLineId, Guid VariantId, string Code, string Name, decimal Quantity, decimal UnitPrice, decimal TaxPercentage)`;
    `public sealed record WholesaleApproval(Guid WholesalerTenantId, Guid OrderId, Guid BuyerTenantId, Guid PurchaseOrderId, IReadOnlyList<WholesaleApprovedLine> Lines)`;
    `public sealed record WholesaleCancellation(Guid WholesalerTenantId, Guid OrderId, Guid BuyerTenantId, Guid PurchaseOrderId)`.
  - `public sealed class WholesaleOrderEventApplier { Task ApplyApprovedAsync(WholesaleApproval approval, DateTimeOffset now, CancellationToken ct); Task ApplyCancelledAsync(WholesaleCancellation cancellation, DateTimeOffset now, CancellationToken ct); }`
    — D-20 (no calza → `return` y log por `ILogger`), convierte con `NetOfVat`, `ApplyWholesalerApproval`, luego por cada
    `LinesNeedingCatalog()` → `EnsureVariantAsync(code.Trim(), name, order.Currency, ConfirmedUnitCost)` → `LinkLineToCatalog`;
    audita (D-22) y llama `IPurchasesUnitOfWork.SaveChangesAsync`. **No abre transacción**: la abre el procesador (Task 7).
  - Composer: campos del contrato; `actorName` «El mayorista» con `Guid.Empty` en una orden vinculada (D-19).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// LinkedPurchaseOrderHandlersTests.cs
CreateWithALinkedSupplierPricesFromTheWholesaleList    // moneda del mayorista USD aunque el cuerpo diga COP; unitCost del cuerpo ignorado
CreateWithAnInvisibleVariantIs422NotVisible
CreateWithABlockedRelationshipIs403
PreviewOfALinkedOrderCarriesTheListPrice
AnExternalLineWithoutUnitCostFailsOnTheField           // "Lines[0].UnitCost"
SendCallsTheIntakeOnceAndFreezesItsLines
SendRetryAfterTheOrderExistsAdoptsTheOrderLines        // Review Focus 1 (intake falso con AlreadyExisted)
CancelOfASentLinkedOrderCancelsTheWholesaleOrderFirst
CancelAfterApprovalIs409NotCancellable                 // intake falso lanza; la orden sigue Sent con su versión
ConfirmOfALinkedOrderIs409
APhaseTwoDraftToALinkedSupplierStaysExternal           // SupplierTenantId nulo → send manual, sin intake (D-10)
// SupplierCatalogTests.cs
CatalogOfANonLinkedSupplierIs422
CatalogItemsCarryTheNetUnitCost                        // 11900 @19 → unitCost 10000
// WholesaleOrderEventApplierTests.cs
ApprovalConfirmsAndCreatesMissingProducts              // writer falso: código nuevo → Created, línea CreatedInCatalog
AnInactiveVariantWithTheCodeIsReusedUntouched          // Review Focus 4: writer falso devuelve existente → CreatedInCatalog false, VariantId = existente
RejectedLinesDoNotTouchTheCatalog
RedeliveredApprovalCreatesProductsOnce                 // Review Focus 3: segunda llamada no llama al writer
AnEventForAnotherOrderIsIgnored                        // D-20
CancellationOfAConfirmedOrderCancelsIt
// PurchaseOrderComposerTests.cs
LinkedDetailShowsListPriceAndAddedLines                // quantity null, lineStatus "Added"
WholesalerHistoryEntriesReadElMayorista
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~LinkedPurchaseOrderHandlers|FullyQualifiedName~SupplierCatalog|FullyQualifiedName~WholesaleOrderEventApplier|FullyQualifiedName~PurchaseOrderComposer|FullyQualifiedName~PurchaseOrderWriteRules"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** y registra el handler nuevo y `WholesaleOrderEventApplier` (scoped). Los adaptadores de
  `IWholesaleCatalog`, `IWholesaleOrderIntake` e `IPurchasesCatalogWriter` se registran en Task 7, donde corre
  `CompositionRootTests`; en esta tarea sólo se exige `EveryCommandAndQueryHasItsHandlerRegistered`.

- [ ] **Step 4: Corre y verifica que pasan**, más la clase entera `PurchaseOrderTests` (fase 1) y
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PurchasesLayerTests|FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Application src/Bootstrapper/PurchasesSupplierLookup.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Purchases/Modules.Purchases.UnitTests
git commit -m "feat(purchases): linked orders to a wholesaler and approval reconciliation"
```

**Revisión del bloque B.**

---

## Bloque C — Infrastructure, API y documentación

### Task 7: Persistencia, inbox de Purchases y adaptadores

**Files:**
- Modify: `src/Modules/Purchases/Modules.Purchases.Infrastructure/{Persistence/PurchasesDbContext.cs, PurchasesInfrastructureExtensions.cs}`
- Create: `…Infrastructure/Persistence/{PurchasesInbox.cs, Migrations/*_AddLinkedPurchaseOrders.cs}`,
  `…Infrastructure/Messaging/{ClaimedOutboxConsumer.cs, WholesaleOrderEventPayloads.cs, WholesaleOrderEventsProcessor.cs, WholesaleOrderEventsWorker.cs}`
  (copias de `Modules.Pipeline.Infrastructure/Messaging/*` y `Persistence/PipelineInbox.cs`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/{QuotationsDbContext.cs:599 (CHECK), QuotationLifecycleEventPublisher.cs, OrderRepository.cs}`; Create: `…/Migrations/*_AddWholesaleOrderLink.cs`
- Create: `src/Bootstrapper/{PurchasesWholesaleCatalog.cs, PurchasesWholesaleOrderIntake.cs, PurchasesCatalogWriter.cs, QuotationsWholesaleLinePricing.cs}`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`
- Test: `tests/Modules/Purchases/Modules.Purchases.UnitTests/WholesaleOrderEventPayloadsTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/PurchasesWholesaleOrderIntakeTests.cs`
  (el publicador de Quotations es `internal` y no tiene pruebas unitarias: su forma la cubre `WholesaleOrderEventsTests`, Task 8)

**Interfaces:**
- Consumes: puertos de Tasks 4-6.
- Produces:
  - Migración Purchases (aditiva): `purchase_orders.supplier_tenant_id uuid null`, `linked_order_id uuid null`,
    `linked_order_number varchar(64) null`; `purchase_order_lines.variant_id` y `quantity` pasan a nullable;
    `supplier_variant_id uuid null`, `list_base_price/list_discount_percent/list_unit_price numeric null`,
    `created_in_catalog boolean not null default false`; índice parcial `(tenant_id, linked_order_id)` donde no es nulo; tabla
    `purchases.inbox_messages (consumer, message_id PK, processed_at, claimed_until, attempts)`.
  - Migración Quotations (aditiva): `orders.buyer_tenant_id`, `purchase_order_id`, `wholesale_approved_at`,
    `wholesale_approved_by`, `cancelled_by_buyer boolean default false`; `order_items.source_purchase_order_line_id`;
    `CK_orders_source` recreado con `'Wholesale'`.
  - `WholesaleOrderEventsProcessor` (consumidor `"purchases.wholesale-order-events"`, eventos
    `quotations.order.wholesale-approved.v1` y `…-cancelled.v1`, curva 5 s / 30 s / 5 min como Pipeline): por mensaje abre la
    transacción del `PurchasesDbContext`, llama al aplicador, agrega la fila de inbox procesada, guarda y confirma. Payload
    malformado → procesado sin efecto y log (como Pipeline).
  - `PurchasesCatalogWriter : IPurchasesCatalogWriter` — calca `ShopifyCatalogWriter.InTransactionAsync` sobre
    `PurchasesDbContext.Database.CurrentTransaction` (sin ella, `InvalidOperationException`); `FindVariantIdByCodeAsync` y, si no
    hay, `CreateAsync` con D-17/D-18 (`Source = null`, `Price = null`, `Cost = unitCost`, `ActorId = Guid.Empty`, sin opciones,
    `Key = "purchase-order-line"`).
  - `PurchasesWholesaleCatalog : IWholesaleCatalog` → `WholesaleOffers`.
  - `PurchasesWholesaleOrderIntake : IWholesaleOrderIntake` — scope propio por llamada (como `StoreOrdersAdapter`):
    `FindAsync` primero (D-8, sin revalidar); si no, `ResolveTermsAsync` + `PriceVisibleAsync` + asesor (D-14) +
    `CreateAsync`; `QuotationsDomainException` → `ResourceConflictException(WholesalerRejected)` con log del código (D-15).
    Arma `LinkedLineSnapshot` con `NetOfVat(unitPrice, taxPercentage)`; con `AlreadyExisted`, base y descuento salen de la
    oferta vigente sin visibilidad (`ListPricesForOrderAsync`) y, si no hay, base = precio y descuento 0.
  - `QuotationsWholesaleLinePricing : IWholesaleLinePricing` → `ListPricesForOrderAsync` (el pedido sabe su `BuyerTenantId`).
  - `MultiReplicaInventory`: `Modules.Purchases.Infrastructure.Messaging.WholesaleOrderEventsWorker`, `WorkerClassification.Claimed`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleOrderEventPayloadsTests.cs
ReadsAnApprovalWithANullSourceLineId
AMalformedPayloadIsNotReadable
// PurchasesWholesaleOrderIntakeTests.cs (fakes de los dos servicios)
AnExistingOrderIsReturnedWithoutRevalidating           // relación ya Blocked → igual devuelve el pedido (D-8)
AnInactiveAdvisorFallsBackToTheOwner                   // D-14
AnOrderEngineRejectionIsWholesalerRejected             // D-15: el código quotation.* no viaja
SnapshotLinesUseTheNetUnitCost                         // 11900 @19 → 10000
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~WholesaleOrderEventPayloads"` y
`dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PurchasesWholesaleOrderIntake"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa**, genera las dos migraciones con el factory de diseño
  (`dotnet ef migrations add AddLinkedPurchaseOrders --project src/Modules/Purchases/Modules.Purchases.Infrastructure --context PurchasesDbContext -o Persistence/Migrations`,
  ídem `AddWholesaleOrderLink` en Quotations) y revisa que sólo agreguen o ensanchen. Registra adaptadores, aplicador y worker.

- [ ] **Step 4: Corre y verifica que pasan**, más
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplica|FullyQualifiedName~CompositionRoot|FullyQualifiedName~LayerTests"` y `dotnet build src/Api` → PASS, 0 errores.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Infrastructure src/Modules/Quotations/Modules.Quotations.Infrastructure src/Bootstrapper tests
git commit -m "feat(purchases): wholesale order events inbox and cross-tenant adapters"
```

---

### Task 8: Endpoints y pruebas de integración (escritas, pendientes)

**Files:**
- Modify: `src/Modules/Purchases/Modules.Purchases.Api/PurchaseOrderEndpoints.cs` (ruta del catálogo del proveedor),
  `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (query `wholesaleApproval`)
- Create: `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/Linked/{LinkedPurchasesHarness.cs, WholesaleCrossTenantGuardTests.cs, LinkedPurchaseOrderApiTests.cs, WholesaleOrderEventsTests.cs}`

**Interfaces:**
- Consumes: todo lo anterior. El harness arma dos tenants con la relación por el camino real de fase 2 (invitar + aceptar) o por
  los repositorios si la aceptación necesita Google; corre el procesador con `ProcessBatchAsync` (precedente de Pipeline).
- Produces: `GET …/purchases/suppliers/{supplierId:guid}/catalog` con `purchases.order.manage`, `Produces<SupplierCatalogPageDto>`,
  403/404/422.

- [ ] **Step 1: Escribe las pruebas** (todas de integración)

```csharp
// WholesaleCrossTenantGuardTests.cs — «Lectura entre tenants»
CatalogWithoutRelationshipIs403
CatalogWithABlockedRelationshipIs403
PreviewAndSendWithAnInvisibleProductAre422
SendWithABlockedRelationshipIs403AndCreatesNoOrder
AnotherBuyerCannotReadThroughTheSupplierOfATenant      // supplierId de otro tenant → 404 suppliers.supplier.not_found
// LinkedPurchaseOrderApiTests.cs
SendingToAWholesalerCreatesTheOrderWithTheRecalculatedPrice   // la lista cambia entre el borrador y el envío → precio nuevo en los dos
SendingTwiceCreatesOneWholesaleOrder                          // Review Focus 1
CancelBeforeApprovalCancelsBothSides
CancelAfterApprovalIs409AndTheEventStillConfirms              // Review Focus 2
TheWholesalerSeesThePurchaseOrderNumber                       // externalReference y wholesale.purchaseOrderNumber
// WholesaleOrderEventsTests.cs
ApprovedPayloadCarriesEveryFinalLineInCamelCase               // lee platform.outbox_messages: sourcePurchaseOrderLineId null en la agregada
ApprovedWithChangesComesBackAsConfirmedWithChanges            // cantidad, precio, línea quitada y agregada
MissingProductsAreCreatedWithCostAndNoPrice
ExistingProductsStayByteForByte                               // Review Focus 4: misma fila y versión antes y después
WholesalerCancelAfterApprovalCancelsUnlessReceived
```

- [ ] **Step 2: RED de compilación**: `dotnet build tests/Modules/Purchases/Modules.Purchases.IntegrationTests` → FAIL (ruta y
  harness inexistentes).

- [ ] **Step 3: Implementa** las rutas y el harness.

- [ ] **Step 4: GREEN de compilación**: el mismo `dotnet build` → 0 errores. **Pendiente para el owner:**
  `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~Linked"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Api src/Modules/Quotations/Modules.Quotations.Api tests/Modules/Purchases/Modules.Purchases.IntegrationTests
git commit -m "feat(purchases): wholesaler catalog route and linked order integration tests"
```

---

### Task 9: Documentación y cierre

**Files:** `README.md` (módulos y tabla de endpoints: catálogo del proveedor, `approve`), `CLAUDE.md` (gotchas).

- [ ] **Step 1:** README: filas nuevas de endpoints. CLAUDE.md, gotchas: (1) «Por aprobar» de un pedido `Wholesale` es
  `WholesaleApprovedAt`, no un estado, y aprobado deja de ser editable; (2) la orden vinculada guarda costo sin IVA y congela la
  lista; (3) los eventos de vuelta los consume Purchases con inbox propio y la creación de productos va en la misma
  transacción, con Catalog enrolado; (4) un producto creado por Compras no tiene precio de venta (`AllowsNoPrice`) ni origen
  externo.
- [ ] **Step 2:** `dotnet build src/Api` → 0 errores, 0 avisos; `dotnet format --verify-no-changes --include <archivos tocados>`.
- [ ] **Step 3:** Lista para el owner (no se corre): `Modules.Purchases.IntegrationTests` (`Linked`, más las de fase 1 que tocan
  `Quantity` nullable), `Modules.Quotations.IntegrationTests` (`--filter "FullyQualifiedName~Order"`: migración y `CHECK`),
  `Modules.Wholesale.IntegrationTests` (`WholesaleAdaptersTests`), `Modules.Catalog.IntegrationTests`
  (`--filter "FullyQualifiedName~ExternalProduct"`), `Modules.Reporting.IntegrationTests` (`--filter "FullyQualifiedName~Source"`).
- [ ] **Step 4: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs: linked purchase orders between tenants"
```

**Revisión del bloque C** y revisión fuerte de toda la rama (escritura entre tenants, idempotencia, transacción compartida).

---

## Resoluciones del orquestador (2026-11-18, antes de ejecutar)

El owner pidió terminar la fase 3 sin él (2026-11-18). Las decisiones marcadas (P) se toman así y quedan para su revisión:
D-1, D-2, D-4, D-7, D-9, D-12 y D-16 se aceptan tal como están escritas. Motivos: D-1/D-3 es lo único que permite que el
evento de vuelta salga una sola vez con las líneas finales; D-2 evita darle al asesor el permiso de «marcar pagado»; D-4 es lo
mínimo que pide P-6; D-7 no cruza datos que el mayorista no viera ya en Distribuidores; D-9 sigue la regla de costos sin IVA de
la fase 1; D-12 sigue el spec («mientras el mayorista no apruebe»); D-16 es la única forma de cumplir «sin precio de venta» sin
romper la validación del formulario. P-4 y P-5 quedan como están.

### Adenda al «Contrato HTTP» (2026-11-18, tras el plan del frontend; manda sobre el contrato y entra en los bloques B y C)

- **`PurchaseOrderDetail` y `PurchaseOrderSummary`** suman `mode: "External" | "Linked"` (el modo fijado al guardar, D-10); la
  pantalla decide por `mode`, nunca por `supplier.isLinked`.
- **`PurchaseOrderDetail`** suma `changes: { changedLines, addedLines, rejectedLines, createdProducts }` (conteos calculados en el
  servidor, para el banner «2 líneas cambiaron… · 2 productos no estaban en tu catálogo»).
- **Respuesta del catálogo del mayorista** suma `priceList: { name, discountPercent }` (la tarjeta «Tarifa: Distribuidor estándar»).
- **Proveedores** (lista y detalle) suman `linkedRelationshipStatus: "Active" | "Blocked" | "Invited" | null` (null si no está
  vinculado): con `Blocked` la pantalla no pide el catálogo y explica por qué.
- **Historial del pedido `Wholesale`**: el evento de aprobación tiene su tipo (`WholesaleApproved`), con el miembro que aprobó.
- **Pedidos**: el listado suma `pendingWholesaleApprovals` (conteo con los filtros vigentes salvo el de estado) para el aviso «N
  pedidos de distribuidores esperan tu aprobación».
- **Fuera:** motivo de rechazo por línea (no existe en el dominio: el mayorista edita el pedido) y precio con IVA por línea confirmada
  (los totales con IVA ya llegan del servidor; D-9 se queda). El aviso del mockup «El precio ya quedó fijo» se reemplaza: con D-4
  el mayorista puede cambiar precios antes de aprobar.

## Pendiente del owner

- **D-1/D-2.** «Por aprobar» como marca con `POST /orders/{id}/approve` y permiso `quotations.order.manage`. Alternativa:
  `quotations.order.approve` (hoy «marcar pagado», el asesor no lo tiene).
- **D-4.** Precio por línea editable sólo en pedidos `Wholesale` (nadie más puede cambiar precio a mano en un pedido).
- **D-7 (P-2).** El mayorista ve el número `OC-NNNN` y el nombre del tenant del distribuidor.
- **D-9.** Costo sin IVA en la orden vinculada: los totales de la orden y del pedido pueden diferir en centavos.
- **D-12.** Si el mayorista preparó o facturó sin aprobar, el distribuidor ya no puede cancelar.
- **D-16.** Un producto puede existir sin precio de venta (sólo si lo crea Compras). No aparece en tienda, POS ni cotizaciones
  hasta que el distribuidor le ponga precio.
- **P-4** (correo al mayorista por pedido nuevo) y **P-5** (sólo `Code`) siguen como están: sin correo y sin `Barcode`.
- Correr las pruebas de integración listadas en Task 9; ninguna se corrió en esta rama.

## Puntos del spec que chocan con el código

- «Entra Por aprobar» y «Aprobar tal cual es `/approve`»: el `Order` no tiene estado de aprobación ni `/approve` desde el modelo
  de cuatro ejes (spec 2026-11-10) → D-1.
- «Aprobar con cambios es el Editar pedido como borrador que ya existe»: no hay borrador; un pedido se edita mientras
  `IsEditable`, y la edición no cambia precios → D-3, D-4.
- «Las escalas por cantidad no aplican a distribuidores»: la edición de hoy reprecia con escalas → D-5.
- «Sin precio de venta»: el catálogo exige al menos un precio por variante → D-16.
- «Se usa `IExternalProductWriter`»: siempre escribe `ExternalSource`, que bloquea el producto → D-17.
- «Precios con IVA incluido» frente a la orden de compra de fase 1, con costo sin IVA → D-9.
- `purchases.order.not_cancellable` lo había reemplazado fase 1 por `invalid_transition`; vuelve sólo para la cancelación
  vinculada que el mayorista ya no permite → D-12.

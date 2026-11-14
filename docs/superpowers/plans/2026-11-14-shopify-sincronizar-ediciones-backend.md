# Shopify: sincronizar ediciones del pedido (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.
>
> Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: **un implementador por bloque**, no por tarea; cada tarea conserva su commit; una revisión por bloque y una revisión fuerte de toda la rama al final.

**Goal:** Que una edición de Shopify sobre un pedido ya importado llegue a QEP como un cambio que se aplica solo o espera aceptación (sin pisar lo que el asesor corrigió y sin tocar un pedido preparado o facturado), y que la corrección del asesor —dirección de envío, ciudad, nota del cliente y atributos— viaje a Shopify en segundo plano.

**Architecture:** La fila del espejo gana la versión de Shopify aplicada (`applied_payload`) y lo que QEP escribió a partir de ella (`applied_projection`, leída del pedido después de escribir). La detección se cuelga de `ShopifyOrderIngestion.ApplyAsync` (después del espejo, antes de la reacción §6.3): un diff puro por grupo entre la versión aplicada y la nueva, y un planificador que decide por grupo aplicar, conflicto (el pedido ya no es la proyección) o eco (el valor nuevo ya es el del pedido), y por cambio `Pending`/`Blocked`/`Applied` según la tienda. Aplicar baja por `IShopifyOrderWriter` → `IExternalOrderWriter.ApplyEditAsync` → `Order.ApplyExternalEdit`. La corrección del asesor es un endpoint de Shopify que escribe el pedido con el `QuotationsDbContext` enrolado en la transacción de Shopify (como `CatalogProductDeletionCleanup`) y encola en `shopify.order_pushes`; un worker manda `orderUpdate` por GraphQL.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`, Shopify Admin GraphQL `2026-10`.

**Spec:** [`docs/superpowers/specs/2026-11-14-shopify-sincronizar-ediciones-design.md`](../specs/2026-11-14-shopify-sincronizar-ediciones-design.md) (extiende §6.3 y §14 de [`2026-10-15-integracion-shopify-pedidos-design.md`](../specs/2026-10-15-integracion-shopify-pedidos-design.md)).

## Antes de empezar

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/shopify-sincronizar-ediciones`, rama `feature/shopify-sincronizar-ediciones` desde `develop`. Rutas absolutas siempre: el cwd del shell vuelve al checkout principal. Antes de **cada** commit, `git -C $W branch --show-current` tiene que decir esa rama.
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- **RED de compilación:** la evidencia es la salida de `dotnet build <proyecto de pruebas>`, sin contenedores.
- **Integración** sólo en GREEN, con `$env:Seed__Enabled = "false"` en la sesión, filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. La clase completa, una vez al final del bloque. Pruebas siempre en primer plano. Antes de una corrida: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra (de otra sesión) y vas a correr una clase completa, espera; nunca la detengas.
- **Host de pruebas:** Quotations usa `SharedQuotationsHost` (un host por clase) salvo las excepciones que su comentario lista. Shopify no tiene host compartido: sus clases siguen el `Flow`/`ShopifyWorld` de `ShopifyImportFlowTests` (crearlo no es de este plan).
- Atajos: `$W` = la ruta del worktree; `$SU` = `$W/tests/Modules/Shopify/Modules.Shopify.UnitTests`; `$SI` = `$W/tests/Modules/Shopify/Modules.Shopify.IntegrationTests`; `$QU` = `$W/tests/Modules/Quotations/Modules.Quotations.UnitTests`; `$QI` = `$W/tests/Modules/Quotations/Modules.Quotations.IntegrationTests`; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`; `$AT` = `$W/tests/ArchitectureTests/ArchitectureTests`; `$IU`/`$II` = los de `Modules/Integrations`.
- Migraciones con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations
  ```
  La última de Shopify hoy es `20261114165149_AddStoreHistoryDays`. Quotations **no** necesita migración (`buyer_notes` existe desde `AddOrderBuyerNotes`).
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Columnas nuevas de `shopify.orders`: `applied_payload jsonb NULL`, `applied_projection jsonb NULL`, `qep_attributes jsonb NULL`. Ninguna con backfill.
- Tabla `shopify.order_edits` (spec: «order_changes», ver D1): `id uuid PK` (GUID v7), `tenant_id uuid`, `connection_id uuid`, `mirror_order_id uuid FK → shopify.orders(id) ON DELETE CASCADE`, `qep_order_id uuid`, `base_updated_at timestamptz NULL`, `snapshot_updated_at timestamptz`, `payload jsonb`, `groups jsonb`, `status varchar(16)`, `status_reason varchar(64) NULL`, `created_at timestamptz`, `decided_at timestamptz NULL`, `decided_by uuid NULL`, `version bigint` (token de concurrencia). Índices `IX_order_edits_mirror_status (mirror_order_id, status)` y `IX_order_edits_tenant_status_created (tenant_id, status, created_at)`.
- Tabla `shopify.order_pushes`: `id uuid PK`, `tenant_id`, `connection_id`, `mirror_order_id uuid FK → shopify.orders(id) ON DELETE CASCADE`, `qep_order_id`, `groups text[]`, `status varchar(16)`, `attempts int`, `lease_until timestamptz NULL`, `failure_code varchar(64) NULL`, `created_at`, `updated_at`, `sent_at timestamptz NULL`, `version bigint`. Índice único parcial `IX_order_pushes_mirror_pending (mirror_order_id) WHERE status = 'Pending'`, traducido **por nombre** en `ShopifyUnitOfWork` a `RequestConcurrencyException("concurrency.conflict")`.
- `shopify.stores`: `edit_sync varchar(16) NOT NULL DEFAULT 'Manual'`, `edit_payment_statuses text[] NOT NULL DEFAULT '{FullPaymentReceived,PartialPaymentReceived,PaymentPending,PendingVerification}'`.
- Enums por nombre en la API: estado del cambio `Pending | Applied | Blocked | Rejected | Reviewed | Superseded`; grupo `Lines | Shipping | ShippingAddress | Buyer | Note | Attributes`; resultado del grupo `Apply | Conflict | Informative`; envío `Pending | Sent | Failed`; grupos de envío `ShippingAddress | Note | Attributes`; `editSync` `Manual | Automatic`; estados de pago `FullPaymentReceived | PartialPaymentReceived | PaymentPending | PendingVerification`.
- Motivos (snake_case, contrato con el diccionario del frontend): bloqueo `order_not_editable`, `payment_status_not_allowed`, `product_unmatched`; conflicto de grupo `qep_changed`, `customer_changed`, `city_unresolved`, `party_incomplete`, `store_pickup`, `store_pickup_changed`, más los motivos de envío del importador (`freight_tax_rate_missing`, …) tal cual; fallo de envío `access_denied`, `user_error`, `order_not_found`, `connection_missing`, `rejected`, `retries_exhausted`.
- Códigos de error nuevos: `shopify.edit.not_found` (404), `shopify.edit.not_open` (409), `shopify.edit.not_blocked` (422), `shopify.edit.order_not_editable` (422), `shopify.edit.payment_status_not_allowed` (422), `shopify.edit.product_unmatched` (422), `shopify.order.not_imported` (422), `shopify.order.address_not_supported` (422). `shopify.order.not_found` (404) ya existe.
- Permisos (todos existen, con política; no se registra ninguna nueva): leer cambios y el panel del pedido `quotations.order.read` (`ShopifyPermissions.OrderRead`); aceptar, rechazar, revisar y corregir `quotations.order.manage` (`ShopifyPermissions.OrderManage`); configurar la tienda `integrations.connection.manage` (`ShopifyPermissions.ConnectionManage`). El handler revalida con `ShopifyAuthorization.EnsureAsync`: 403, nunca 404 por tenant.
- `Shopify:App:Scopes` = `read_orders,write_orders,read_customers` en `ShopifyAppOptions.DefaultScopes`, `src/Api/appsettings.json`, `src/Api/appsettings.example.json`, `k8s/prod-configMap.yaml` y el README.
- Precios de un cambio aplicado: los de Shopify con `ShopifyPricing` (como la importación), **sin** descuentos de catálogo de QEP.
- Historial del pedido por un cambio de Shopify: sin autor (`memberId: null`), una entrada por grupo aplicado, con el evento de la edición equivalente (`ItemsChanged`, `PartiesChanged`, `NotesChanged`); auditoría `PublishByIntegration(..., "quotation.order.external_edit_applied", ...)`.
- `Modules.Shopify.*` no referencia módulos de negocio (`ShopifyLayerTests`): todo lo de Quotations entra por puertos con adaptador en `src/Bootstrapper/`. `Modules.Shopify.Application` no referencia EF Core ni Npgsql.
- `Order.BuyerNotes`: la escriben `CreateFromStore`, la importación y la sincronización de Shopify, y el asesor **sólo** en pedidos `Source = Shopify`. `Notes` sigue con «Shopify #N» y la dirección cruda, y no viaja a Shopify. Sin backfill.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni ninguna atribución de IA** (aunque el harness lo sugiera).

## Decisiones del plan (lo que el spec deja al implementador y lo que el código obligó)

Todas son **decisión mía (planner)**; el owner las puede revertir. Las marcadas **(hueco)** van también en la Autorrevisión.

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | La entidad es `ShopifyOrderEdit` y la tabla `shopify.order_edits`; la API dice «changes» como el spec. | `ShopifyOrderChange` (record) y `ShopifyOrderChanges` (reacción §6.3) ya existen: un tercer «change» confunde la revisión. |
| D2 | `ShopifyLine.Quantity` = vigente (`current_quantity`/`currentQuantity`); si la llave **falta**, `quantity`. `OriginalQuantity` = `quantity`. Una línea quitada es `Quantity == 0 && OriginalQuantity > 0`; la importación la salta. El descuento en porcentaje se calcula sobre `OriginalQuantity ?? Quantity`. | Las fixtures no traen `current_quantity` (Review Focus 1); `discount_allocations` reparte el descuento de la línea **original** (Review Focus 2). |
| D3 | La corrección del asesor (`PUT …/corrections`) **no** toca `applied_projection`; las correcciones de ciudad del sistema (`ShopifyCityCorrectionProcessor`) **sí** la mueven. Así el grupo que corrigió el asesor queda distinto de la proyección y una edición posterior de Shopify sobre él es conflicto, también después del eco. | Es lo que dice el spec §5 (corregido por el owner el 2026-11-14) con la regla del §3 (conflicto = pedido ≠ proyección). Cumple el anti-objetivo 1. |
| D4 | La proyección es el pedido **leído después de escribir** (`IShopifyOrderWriter.ReadStateAsync`), no el borrador; las comparaciones de conflicto y eco son en forma QEP (líneas como multiconjunto de variante, cantidad, precio y descuento; parte de envío con ciudad resuelta; textos recortados). | El dominio normaliza al guardar (parte, notas, redondeo): comparar con el borrador daría conflictos falsos. |
| D5 | Al planear, **todas** las líneas vigentes se vuelven a emparejar; si alguna no empareja, el cambio entero queda `Blocked` con `product_unmatched`. | Las líneas de QEP no guardan el id de la línea de Shopify. Una línea igual cuyo producto se desactivó también bloquea: es raro y es seguro. |
| D6 | Fila `Imported` importada antes de este cambio (sin `applied_payload` o sin `applied_projection`): la base es el `payload` guardado antes de `ApplySnapshot`, y la proyección se inicializa con los valores **actuales** del pedido de QEP (`ShopifyOrderProjection.From(order, …)`). Para esos pedidos nada cuenta como «lo tocó QEP». En particular, su `BuyerNotes` es `null` (sin backfill), la nota de la proyección también, y una nota que cambia en Shopify se aplica a `BuyerNotes` sin conflicto. | Pedido del coordinador (2026-11-14): evita el conflicto falso en todos los pedidos viejos. El costo: una corrección a mano hecha antes del despliegue no se protege, y la primera edición de Shopify sobre ese grupo la pisa. |
| D7 | Un snapshot con `cancelled_at` no calcula cambio: manda la cancelación (§6.3). | Aplicar una edición a un pedido que se anula en el mismo aviso no sirve a nadie. |
| D8 **(hueco)** | Pedido de recogida: el grupo Dirección queda en conflicto `store_pickup`; un grupo Envío que pasa de recogida a envío o al revés queda en conflicto `store_pickup_changed`. | El pedido no cambia de recogida después de creado (P5, `SaveOrderEdits`) y el dominio rechaza una parte de envío con recogida. El spec no dice qué hacer. |
| D9 **(hueco)** | `Reviewed` adopta el snapshot como versión aplicada, igual que `Rejected`. | Sin adoptarlo, el siguiente aviso de Shopify recalcula el mismo diff y vuelve a bloquear. |
| D10 | La concurrencia de los cambios la da la `Version` de la fila del espejo (crear o reemplazar un cambio siempre toca la fila); `order_edits` no tiene índice único. Los envíos sí: un solo `Pending` por pedido, con índice único parcial, y encolar fusiona grupos. | Reemplazar = `UPDATE` + `INSERT` en un `SaveChanges`, y EF no garantiza el orden (gotcha de `CLAUDE.md`); la ingesta ya reintenta `concurrency.conflict` de la fila. |
| D11 **(hueco)** | Rutas que el spec no nombra y la pantalla necesita: `GET …/shopify/changes` (la sección «Cambios por revisar», paginada), `GET …/shopify/qep-orders/{qepOrderId}` (el panel de Shopify en el detalle del pedido) y `PUT …/shopify/orders/{id}/corrections` (envío, nota y atributos en un solo guardado, `If-Match` = versión del pedido de QEP). | BFF: la bandeja pagina y el detalle del pedido de QEP no conoce el id de la fila del espejo. Un solo `PUT` deja pedido, atributos y envío en una transacción. |
| D12 | `orderUpdate` manda `firstName` = nombre de QEP y `lastName` = `""`; `address1` = dirección de QEP y `address2` = `""`; `company` y `countryCode` del último snapshot (`CO` si falta). | `MailingAddressInput` no tiene `name`; el eco compara `name` y `address1 + address2` de Shopify con los de QEP. `orderUpdate` reemplaza la dirección entera (Review Focus 3, V-12). |
| D13 **(hueco)** | «Notas del cliente» (`buyer_notes`) nace con `DefaultVisible: false`: oculta también sin layout guardado. | Mismo criterio que `coordinadora_city`, `variant` y `withholding_*`; un tenant sin layout también puede tener un ERP leyendo la hoja por defecto. El spec sólo habló de los layouts guardados. |
| D14 | Política del envío: 8 intentos, esperas `10 s, 1 min, 5 min, 15 min, 30 min, 1 h, 2 h, 4 h`; worker cada 10 s, lotes de 50. 429, 5xx, red, 401/402 y token vencido (`AccessToken = null`) reintentan; `ACCESS_DENIED`/403 → `access_denied`; `userErrors` → `user_error`; pedido inexistente → `order_not_found`; otro 4xx → `rejected`; agotado → `retries_exhausted`. | El spec deja la frecuencia y la espera al implementador; el molde es `ShopifyCityCorrectionWorker`. |
| D15 | `PUT /stores/{id}`: `editSync` y `editPaymentStatuses` son opcionales; `null` conserva lo guardado. `editPaymentStatuses` no puede venir vacío. | Un frontend viejo sigue guardando la tienda sin borrar la configuración nueva. |
| D16 | Aceptar vuelve a planear contra el pedido de **ahora**. Un `Pending` que ya no se puede aplicar pasa a `Blocked` con su motivo, se guarda y responde 422. | El asesor pudo editar el pedido entre el aviso y el clic. |
| D17 | El grupo Comprador es `Informative` y nunca toca el pedido ni el cliente; un cambio sólo de Comprador igual crea su fila (con `Manual`, `Pending`). | Lo pide el spec («se muestra en el cambio»). |
| D18 **(hueco)** | Un cambio de líneas o envío no recalcula el estado de pago del pedido de Shopify: el pago lo sigue llevando la plataforma (§6.3). | El spec no lo dice; recalcular dejaría un pedido pagado en Shopify como abono en QEP. |
| D19 | En las filas del diff, el dinero viaja como **número crudo** y el DTO del cambio trae `currency` (ISO del pedido); no hay strings preformateados. `sku` es nullable en el objeto de línea. | El frontend ya formatea montos por moneda; un string armado en el backend fijaría el formato regional en dos lugares. |
| D20 | `ShopifyOrderSyncDto.editBlockedReason` ∈ `NotImported | Cancelled | Archived | Invoiced | Prepared`, en ese orden de precedencia; `null` ⇔ `canEdit`. Para armarlo, `ExternalOrderEditState`/`ShopifyOrderState` suman `IsPrepared`, `IsInvoiced` y `Status`. | Son los estados que distingue `Order.IsEditable` (`Open && !IsPrepared && !IsInvoiced`) más la fila del espejo. La recogida no bloquea toda la edición: sale en `shippingAddressSupported`. |
| D21 | La parte de envío sólo se corrige en Colombia: `PUT …/corrections` con `shippingParty` sobre un pedido cuya parte actual tiene país ≠ `CO` (o, sin parte, cuyo último `shipping_address.country_code` ≠ `CO`) → 422 `shopify.order.address_not_supported`. El cuerpo no lleva país. | Revisado: el modelo de partes **sí** admite otros países (`QuotationPartyDetails.Country` + `CityName`, `AddForeignPartyPlaces`), pero el camino de Shopify es sólo Colombia: `ExternalShippingParty` y `ShopifyShippingFingerprint` no tienen país; `ResolveCityAsync` y `ColombiaProvinceCodes` son de DIVIPOLA; el importador deja una dirección extranjera cruda en `Notes`. Abrirlo es otra pieza (país, ciudad en texto, `provinceCode` de otro país). |
| D22 | `buyerNotes` `""`, sólo espacios o `null` se guardan `null` (`NormalizeNotes`); el handler compara ya normalizado, así que mandar `""` sobre un `null` no encola nada. | Evita un envío a Shopify sin cambio real. |

## Review Focus

1. **Un payload sin `current_quantity` deja todas las líneas en 0** y cada pedido nuevo entra vacío o rechazado (las fixtures no traen esa llave). Tarea 1: `ALineWithoutCurrentQuantityKeepsItsQuantity` (REST y GraphQL); el fin del Bloque 1 corre `ShopifyImportFlowTests` completa.
2. **El descuento se recalcula sobre la cantidad vigente** y una línea a la que le quitaron unidades sale con otro precio neto. Tarea 1: `DiscountPercentageUsesTheOriginalQuantity`.
3. **`orderUpdate` reemplaza la dirección entera** y borra de Shopify la «Empresa» (donde muchas tiendas guardan el documento) y el país. Tarea 11: `PushKeepsCompanyAndCountryFromTheLatestSnapshot`.
4. **El envío manda grupos que nadie corrigió** y pisa la nota de Shopify con un `BuyerNotes` vacío (pedidos importados antes, sin backfill). Tarea 11: `APushOnlySendsTheGroupsItWasEnqueuedFor`.
5. **Después del eco, una edición de Shopify pisa lo que corrigió el asesor** (la proyección se movió con la corrección). Tarea 8: `AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict`; Tarea 10: `ACorrectionDoesNotMoveTheProjection`.

---

## API contract (para el plan del frontend)

Base `/api/v1/tenants/{tenantId:guid}/shopify`. JSON camelCase; enums por nombre; fechas ISO 8601.

**Tienda** (cambian, mismo `If-Match`/`ETag` de hoy):
- `GET /stores/{connectionId}` y `PUT /stores/{connectionId}` → `ShopifyStoreDto` suma `"editSync": "Manual"` y `"editPaymentStatuses": ["FullPaymentReceived","PartialPaymentReceived","PaymentPending","PendingVerification"]`. El cuerpo del `PUT` suma `editSync` y `editPaymentStatuses`, opcionales (D15). 422 `validation.failed` con estas claves exactas de `errors` (camelCase con `OverridePropertyName`, como el resto de `UpdateShopifyStoreValidator`): `editSync` (no es `Manual` ni `Automatic`, ordinal); `editPaymentStatuses` (lista vacía o con un nombre repetido); `editPaymentStatuses[i]` (el elemento `i`, base 0, no es uno de los cuatro nombres). Permiso `integrations.connection.manage`.

**Resumen** (cambia): `GET /orders/summary` → suma `"changesToReview": 3` (filas `Pending` + `Blocked`), siempre presente.

**Cambios** (nuevas):
- `GET /changes?status=Pending&status=Blocked&connectionId={guid}&page=1&pageSize=50` → `{ "items": [ShopifyOrderChangeDto], "total": 1, "page": 1, "pageSize": 50 }`. `status` repetido o separado por comas; por defecto `Pending,Blocked`; `pageSize` ≤ 50. Más nuevos primero. `quotations.order.read`.
- `GET /orders/{id}/changes` (`id` = fila del espejo) → `[ShopifyOrderChangeDto]`, más nuevos primero, todos los estados. 404 `shopify.order.not_found`. `quotations.order.read`.
- `POST /changes/{changeId}/accept` | `/reject` | `/review` → `ShopifyOrderChangeDto`. `quotations.order.manage`. Errores: 404 `shopify.edit.not_found`; 409 `shopify.edit.not_open` (ya no es `Pending` ni `Blocked`); `review` sobre `Pending` → 422 `shopify.edit.not_blocked`; `accept` sin poder aplicar → 422 `shopify.edit.order_not_editable` | `shopify.edit.payment_status_not_allowed` | `shopify.edit.product_unmatched` (y la fila queda `Blocked`, D16); el pedido cambió entre el cálculo y la escritura → 412 `concurrency.conflict` (la fila no cambia).

`ShopifyOrderChangeDto`:
```json
{
  "id": "…", "mirrorOrderId": "…", "connectionId": "…", "storeName": "Mi tienda",
  "orderName": "#1001", "qepOrderId": "…", "qepOrderNumber": "PED-0042", "currency": "COP",
  "status": "Pending", "statusReason": null,
  "baseUpdatedAt": "2026-11-14T15:00:00Z", "snapshotUpdatedAt": "2026-11-14T16:00:00Z",
  "createdAt": "…", "decidedAt": null, "decidedByName": null,
  "groups": [
    { "kind": "Lines", "outcome": "Apply", "reason": null,
      "rows": [ { "field": "line", "label": "Camiseta (CAM-001)",
                  "before": { "sku": "CAM-001", "title": "Camiseta", "quantity": 2, "unitPrice": 50000, "discountAmount": 0 },
                  "after":  { "sku": "CAM-001", "title": "Camiseta", "quantity": 1, "unitPrice": 50000, "discountAmount": 0 } } ] },
    { "kind": "ShippingAddress", "outcome": "Conflict", "reason": "qep_changed",
      "rows": [ { "field": "city", "label": null, "before": "Medellin", "after": "Envigado" } ] },
    { "kind": "Buyer", "outcome": "Informative", "reason": null,
      "rows": [ { "field": "email", "label": null, "before": "a@x.co", "after": "b@x.co" } ] }
  ]
}
```
`rows[].field` por grupo: `Lines` → `line` (`label` = `título (SKU)`; `before` `null` = línea nueva, `after` `null` = quitada); `Shipping` → `title`, `amount`, `storePickup`, `cashOnDelivery`; `ShippingAddress` → `name`, `phone`, `address1`, `address2`, `city`, `province`; `Buyer` → `document`, `email`, `phone`; `Note` → `note`; `Attributes` → `attribute` (`label` = clave; `before`/`after` = valor o `null`). Valores ya listos para pintar: texto, número o booleano, sin enums de QEP. Sólo vienen los grupos que cambiaron en Shopify y no son eco.

Tipos exactos de `before`/`after` (D19):
- **Dinero, número JSON crudo** (sin formato, sin ceros de escala), en la moneda de `currency`: `amount` (fila de `Shipping`), y `unitPrice` y `discountAmount` dentro del objeto de línea. El frontend los formatea con `currency`.
- `currency` es el código ISO 4217 del pedido de QEP. Sale de `shopify.orders.currency`, que la importación exige igual a la de la cuenta de cobro del pedido (`currency_mismatch`); siempre presente.
- `quantity`: número.
- `storePickup` y `cashOnDelivery`: booleano.
- El resto: texto o `null`.
- Objeto de línea: `{ "sku": string|null, "title": string|null, "quantity": number, "unitPrice": number, "discountAmount": number }`. `sku` es `null` en una línea sin SKU (las personalizadas, por ejemplo), y entonces `label` es sólo el título.

**Panel del pedido de QEP** (nuevas):
- `GET /qep-orders/{qepOrderId}` → `ShopifyOrderSyncDto`; 404 `shopify.order.not_found` si el pedido no tiene fila del espejo. `quotations.order.read`.
- `PUT /orders/{id}/corrections` con `If-Match: "<versión del pedido de QEP>"` (la del `OrderDto.version`) → `ShopifyOrderSyncDto`. Sin `If-Match` 428 `precondition.if_match_required`; versión vieja 412 `concurrency.conflict`; fila no importada 422 `shopify.order.not_imported`; pedido no editable 422 `order.order.not_editable`; recogida con dirección 422 `order.order.store_pickup`; dirección fuera de Colombia 422 `shopify.order.address_not_supported` (D21). `quotations.order.manage`.

Validación: 422 `validation.failed` con estas claves exactas de `errors` (camelCase con `OverridePropertyName`; `i` base 0):

| Clave | Cuándo |
| --- | --- |
| `shippingParty.name` | vacío o más de 255 |
| `shippingParty.phone` | vacío o más de 32 |
| `shippingParty.email` | vacío, más de 254 o sin forma de correo |
| `shippingParty.address` | vacío o más de 255 |
| `shippingParty.departmentId` | ausente o `Guid.Empty` |
| `shippingParty.cityId` | ausente o `Guid.Empty` |
| `buyerNotes` | más de 2000 tras recortar |
| `attributes` | ausente (`null`), más de 50, o dos claves iguales (ordinal, tras recortar) |
| `attributes[i].key` | vacía o más de 255 |
| `attributes[i].value` | más de 1000 (`null` vale `""`) |

`buyerNotes`: `""`, sólo espacios y `null` se aceptan igual y se guardan como `null`. Lo normaliza el backend, con el `NormalizeNotes` del dominio (D22).

Cuerpo:
```json
{ "shippingParty": { "name": "Laura Gómez", "phone": "3001112233", "email": "laura@x.co", "address": "Cra 1 # 2-3",
                     "departmentId": "…", "cityId": "…" },
  "buyerNotes": "Dejar en portería",
  "attributes": [ { "key": "Documento", "value": "1036000111" } ] }
```
`shippingParty` `null` = no se toca (obligatorio `null` en recogida). `buyerNotes` y `attributes` van siempre (estado completo del formulario). Sólo los grupos que cambiaron se escriben y se encolan.

`ShopifyOrderSyncDto`:
```json
{ "mirrorOrderId": "…", "connectionId": "…", "orderName": "#1001", "orderVersion": 7, "canEdit": true,
  "editBlockedReason": null, "shippingAddressSupported": true,
  "attributes": [ { "key": "Documento", "value": "1036000111" } ], "attributesEditedInQep": false,
  "push": { "status": "Failed", "groups": ["ShippingAddress"], "failureCode": "access_denied",
            "attempts": 8, "lastAttemptAt": "…", "sentAt": null },
  "openChange": null }
```
`canEdit` = fila `Imported` y pedido editable.

`editBlockedReason` (D20) es `null` si y sólo si `canEdit` es `true`. Si no, es el primer motivo que aplique, en este orden: `NotImported` (la fila no está `Imported` o no tiene pedido), `Cancelled`, `Archived`, `Invoiced`, `Prepared`. Son los estados que el dominio distingue en `Order.IsEditable` (`Status` `Open|Archived|Cancelled`, `IsInvoiced`, `IsPrepared`). La recogida no bloquea: nota y atributos siguen editables. BFF: la pantalla dice por qué no se puede editar sin conocer la regla de Quotations; el motivo va escrito en el comentario del DTO.

`shippingAddressSupported` es `false` cuando la dirección no se puede corregir aunque el pedido sea editable:
- recogida en tienda;
- dirección de envío fuera de Colombia (D21): el país de la parte actual distinto de `CO`, o, sin parte, el `countryCode` del último snapshot distinto de `CO`.

Con `false`, la pantalla manda `shippingParty: null`.

`attributes` = los de QEP si se corrigieron, si no los del último Shopify aplicado. `push` = el último envío (`null` si nunca hubo). `openChange` = el cambio `Pending` o `Blocked` (mismo `ShopifyOrderChangeDto`) o `null`.

**Pedido de QEP** (cambia): `OrderDto` suma `"buyerNotes": "…"` (o `null`) al final; lo leen el detalle y la lista.

**Excel de pedidos** (cambia): columna de catálogo `buyer_notes`, encabezado «Notas del cliente», oculta por defecto (D13).

---

## Estructura de archivos

| Archivo | Qué cambia |
| --- | --- |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderSnapshot.cs` | `ShopifyLine.Id`, `OriginalQuantity`, `IsRemoved`; `ShopifyOrderSnapshot.BillingAddress` |
| `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/RestOrderParser.cs`, `GraphQlOrderParser.cs`, `ShopifyJson.cs` | cantidad vigente, id de línea, `billing_address` |
| `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyGraphQlClient.cs` | `LineFields` y `OrderQuery` piden `id currentQuantity` y `billingAddress` |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyDocumentSources.cs` | `billing_address.company` |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs` | salta líneas quitadas, `BuyerNotes`, escribe `applied_*`; delega en los resolvedores extraídos |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyLineResolver.cs`, `ShopifyShippingResolver.cs` (nuevos) | emparejar líneas y resolver envío, extraídos del importador |
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` | `CreateExternal(... buyerNotes)`, `UpdateExternalBuyerNotes`, `ApplyExternalEdit`, `EditExternalShippingParty` |
| `src/Modules/Quotations/Modules.Quotations.Domain/ExternalOrderEdit.cs` (nuevo) | `ExternalOrderEdit`, `ExternalOrderEditResult` |
| `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs` | `BuyerNotes` en el borrador; `FindEditStateAsync`, `ApplyEditAsync`, `CorrectByMemberAsync` |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs`, `OrderMapping.cs`, `OrderChangeSummary.cs` | `OrderDto.BuyerNotes`; textos del historial |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs`, `Application/OrdersExportProcessor.cs`, `Infrastructure/Seed/QuotationsSeeder.cs` | columna `buyer_notes` |
| `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStore.cs`, `ShopifyEditSync.cs` y `ShopifyPaymentStatuses.cs` (nuevos) | configuración de la tienda |
| `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyOrderMirror.cs` | `AppliedPayload`, `AppliedProjection`, `QepAttributes` y sus métodos |
| `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyOrderEdit.cs`, `ShopifyOrderPush.cs`, `ShopifyEditReasons.cs` (nuevos) | entidades y motivos |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderProjection.cs`, `ShopifyOrderDiff.cs`, `ShopifyEditPlanner.cs`, `ShopifyOrderEdits.cs`, `ShopifyOrderEditDecisions.cs`, `ShopifyOrderCorrections.cs`, `ShopifyOrderPushes.cs` (nuevos) | proyección, diff, planificador, detector, aceptar/rechazar/revisar, corrección y envíos |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` | `ReadStateAsync`, `ApplyEditAsync`, `IShopifyOrderCorrectionWriter`, `IShopifyOrderUpdater`, `DescribeCityAsync` y sus tipos |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderIngestion.cs` | llama al detector |
| `src/Modules/Shopify/Modules.Shopify.Application/ShopifyCityCorrections.cs` | la corrección de ciudad mueve la proyección y encola |
| `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/*` | mapeos, repositorios, traducción del índice, migraciones `AddStoreEditSync`, `AddOrderEdits`, `AddOrderPushes` |
| `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyOrderUpdateClient.cs`, `Workers/ShopifyOrderPushWorker.cs` (nuevos) | `orderUpdate` y el worker |
| `src/Modules/Shopify/Modules.Shopify.Api/ShopifyOrderEditEndpoints.cs` (nuevo), `ShopifyEndpoints.cs` | rutas |
| `src/Bootstrapper/ShopifyOrderWriter.cs`, `ShopifyOrderCorrectionWriter.cs` (nuevo), `ShopifyGeographyLookup.cs`, `QepServiceCollectionExtensions.cs` | adaptadores y registro |
| `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAppOptions.cs`, `src/Api/appsettings*.json`, `k8s/prod-configMap.yaml` | `write_orders` |
| `README.md`, `CLAUDE.md`, el spec | documentación |

---

## Bloque 1 — lo que se lee de Shopify, la nota del cliente, el Excel y la tienda (Tareas 1–4, un implementador)

### Tarea 1: cantidad vigente, id de línea y `billing_address` en los parsers

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderSnapshot.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/RestOrderParser.cs`, `GraphQlOrderParser.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyGraphQlClient.cs` (`LineFields`, `OrderQuery`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyDocumentSources.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs` (`ResolveLinesAsync`)
- Test: `$SU/OrderParsersTests.cs`, `$SU/ShopifyGraphQlClientTests.cs` (`ShopifyGraphQlQueryContractTests`), `$SU/ShopifyDocumentSourcesTests.cs`, `$SU/ShopifyCustomLineImportTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record ShopifyLine(
    string? Sku, decimal Quantity, decimal OriginalUnitPrice, decimal DiscountAmount,
    string? Title = null, bool IsCustom = false, long? Id = null, decimal? OriginalQuantity = null)
{
    /// Quitada en una edición de Shopify: vigente 0 con original > 0. Un payload viejo (OriginalQuantity null) nunca lo es.
    public bool IsRemoved => Quantity == 0m && OriginalQuantity is > 0m;
}

public sealed record ShopifyOrderSnapshot(/* … los 19 de hoy … */, ShopifyAddress? BillingAddress = null);

// ShopifyDocumentSources
public const string BillingAddressCompany = "billing_address.company";
public static IReadOnlyList<string> AddressPaths { get; } = [ShippingAddressCompany, BillingAddressCompany];
// Read: "billing_address.company" → snapshot.BillingAddress?.Company
```
- REST: `Id = OptionalId(line, "id", …)`; `OriginalQuantity = quantity`; `Quantity = current_quantity` si la llave existe, si no `quantity` (D2). `BillingAddress` con los mismos campos que `Address` desde `billing_address`.
- GraphQL: `Id` = número del gid `gid://shopify/LineItem/{n}`; `Quantity = currentQuantity` si existe, si no `quantity`; `billingAddress` igual que `shippingAddress`.
- `LineFields` = `"id sku title quantity currentQuantity variant { id } product { id } originalUnitPriceSet { … } discountAllocations { … }"`; `OrderQuery` suma `billingAddress { name address1 address2 city province provinceCode countryCodeV2 phone company }`.
- Importador: las líneas `IsRemoved` no entran al borrador ni a `wait_detail`; el descuento usa `ShopifyPricing.DiscountPercentage(line.DiscountAmount, line.OriginalUnitPrice, line.OriginalQuantity ?? line.Quantity)`.

- [ ] **RED.** En `OrderParsersTests` (JSON inline con `Json(...)`):
  ```csharp
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void ACurrentQuantityIsTheLineQuantity(string format)
  // REST: {"id":9001,"sku":"CAM-001","quantity":3,"current_quantity":1,…}; GraphQL: {"id":"gid://shopify/LineItem/9001","quantity":3,"currentQuantity":1,…}
  // Assert.Equal((9001L, 1m, 3m), (line.Id, line.Quantity, line.OriginalQuantity)); Assert.False(line.IsRemoved)
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void ALineWithCurrentQuantityZeroIsRemoved(string format)   // current 0, quantity 2 → IsRemoved
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void ALineWithoutCurrentQuantityKeepsItsQuantity(string format)   // Review Focus 1
  // Fixture(format, "taxes-included") → cada línea: Quantity == OriginalQuantity, IsRemoved == false
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void BillingAddressIsRead(string format)   // billing_address.company "1036000111" → snapshot.BillingAddress!.Company
  [Fact] public void AnOldPayloadWithoutTheNewFieldsReadsDefaults()
  // WritePayload de un snapshot, quitando "id", "originalQuantity" de las líneas y "billingAddress" con JsonNode → ReadPayload: Id null, OriginalQuantity null, BillingAddress null
  ```
  En `ShopifyGraphQlQueryContractTests`: `Leaf` devuelve `"gid://shopify/LineItem/1"` para `("id","lineItems")` y `1` para `currentQuantity`; `OrderQueryRequestsEveryFieldTheParserReads` sigue verde con `billingAddress` (el analizador exige que todo campo leído se pida). En `ShopifyDocumentSourcesTests`: `OptionsStartWithBothAddressPaths` → `Options([])` == `["shipping_address.company","billing_address.company"]`; `ReadBillingCompany`. En `ShopifyCustomLineImportTests` (dobles de `ShopifyImporterTestDoubles`):
  ```csharp
  [Fact] public async Task ARemovedLineIsNotImported()            // dos líneas, una current 0 → draft.Lines.Count == 1
  [Fact] public async Task DiscountPercentageUsesTheOriginalQuantity()   // Review Focus 2
  // quantity 4, current 2, price 10000, discount 4000 → draft línea: Quantity 2, DiscountPercentage 10.0000m (4000 / (10000 × 4))
  ```
- [ ] Corre `dotnet build $SU` → `CS1739`/`CS0117` (`ShopifyLine` sin `Id`, `ShopifyDocumentSources` sin `BillingAddressCompany`).
- [ ] **GREEN.** Implementa. Actualiza el comentario de `ShopifyLine` (D2) y el de `ShopifyDocumentSourcesDto` (la opción nueva).
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~OrderParsersTests|FullyQualifiedName~ShopifyGraphQl|FullyQualifiedName~ShopifyDocumentSourcesTests|FullyQualifiedName~ShopifyCustomLineImportTests"` → verde.
- [ ] Commit: `feat(shopify): read current quantities, line ids and billing address`

### Tarea 2: la nota del comprador va a `BuyerNotes`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`CreateExternal`, nuevo `UpdateExternalBuyerNotes`, comentario de `BuyerNotes`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs` (`ExternalOrderDraft.BuyerNotes`, `CreateAsync`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs`, `OrderMapping.cs` (`OrderDto.BuyerNotes`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` (`ShopifyOrderDraft.BuyerNotes`), `ShopifyOrderImporter.cs` (`Notes`, `Draft`)
- Modify: `src/Bootstrapper/ShopifyOrderWriter.cs` (`ToExternal`)
- Modify: `CLAUDE.md` (regla de `BuyerNotes`)
- Test: `$QU/OrderTests.cs`, `$QU/ExternalOrderServiceTests.cs`, `$SU/ShopifyCustomLineImportTests.cs`, `$SI/ShopifyImportFlowTests.cs`

**Interfaces — Produces:**
```csharp
// Order
public static Order CreateExternal(/* … los de hoy … */, OrderLineCosts costs, string? buyerNotes = null);   // BuyerNotes = NormalizeNotes(buyerNotes)
/// Sólo Source = Shopify (IsExternal) y editable. true si cambió; sube Version.
public bool UpdateExternalBuyerNotes(string? buyerNotes, DateTimeOffset occurredAt);
// throws QuotationsDomainException ExternalOrderErrors.SourceInvalid (tienda, manual, cotización) | ExternalOrderErrors.NotEditable

public sealed record ExternalOrderDraft(/* … */, string? ExternalShippingTitle = null, string? BuyerNotes = null);
public sealed record ShopifyOrderDraft(/* … */, string? ExternalShippingTitle, string? BuyerNotes = null);
public sealed record OrderDto(/* … */, decimal Balance, string? BuyerNotes = null);
```
- Importador: `Notes(snapshot, …)` ya no incluye `snapshot.Note` («Shopify #N» y, si aplica, la dirección cruda); `BuyerNotes = Blank(snapshot.Note)`.

- [ ] **RED.**
  ```csharp
  // $QU/OrderTests.cs
  [Fact] public void CreateExternalKeepsTheBuyerNotesApartFromNotes()   // notes "Shopify #1001", buyerNotes "  Dejar en portería " → Notes "Shopify #1001", BuyerNotes "Dejar en portería"
  [Fact] public void UpdateExternalBuyerNotesChangesOnlyBuyerNotesAndBumpsVersion()   // true; Version + 1; Notes intacta
  [Fact] public void UpdateExternalBuyerNotesWithTheSameTextChangesNothing()          // false; Version igual
  [Fact] public void UpdateExternalBuyerNotesOnAStoreOrderIsSourceInvalid()          // CreateFromStore → Code == "order.order.source_invalid"
  [Fact] public void UpdateExternalBuyerNotesOnAPreparedOrderIsNotEditable()         // Code == "order.order.not_editable"
  // $SU/ShopifyCustomLineImportTests.cs
  [Fact] public async Task TheBuyerNoteGoesToBuyerNotesNotToNotes()   // snapshot.Note "Sin cebolla" → draft.Notes == "Shopify #1001", draft.BuyerNotes == "Sin cebolla"
  // $SI/ShopifyImportFlowTests.cs
  [Fact] public async Task ImportPutsTheBuyerNoteInBuyerNotes()
  // fixture con "note":"Dejar en portería" → SELECT notes, buyer_notes FROM quotations.orders → ("Shopify #1001", "Dejar en portería"); GET /orders/{id} → order.buyerNotes == "Dejar en portería"
  ```
  Antes de escribir, busca en `$SU` y `$SI` las aserciones que esperan la nota dentro de `Notes` (`rg "Shopify #" $SU $SI`) y corrígelas en este mismo RED.
- [ ] Corre `dotnet build $QU` y `dotnet build $SU` → `CS1061` (`UpdateExternalBuyerNotes`) / `CS1739` (`buyerNotes`).
- [ ] **GREEN.** Implementa. En `CLAUDE.md`, reescribe la entrada «La nota del comprador es `Order.BuyerNotes`…»: la escriben `CreateFromStore`, la importación y la sincronización de Shopify y el asesor sólo en pedidos de Shopify (`UpdateExternalBuyerNotes`, `EditExternalShippingParty` no la toca); en un pedido de la tienda pública sigue intocable; los pedidos de Shopify importados antes la tienen en `NULL` y su nota sigue dentro de `Notes`.
- [ ] Corre `dotnet test $QU --filter "FullyQualifiedName~OrderTests"`, `dotnet test $SU --filter "FullyQualifiedName~ShopifyCustomLineImportTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~ImportPutsTheBuyerNoteInBuyerNotes" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(orders): keep the shopify buyer note in buyer notes`

### Tarea 3: columna «Notas del cliente» en el Excel de pedidos

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` (al final)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (la celda)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Seed/QuotationsSeeder.cs` (lista de ocultas de «MIGRACION 1»)
- Modify: `README.md` § «Columnas del Excel de pedidos por tenant (homologación)»
- Test: `$QU/OrdersExportColumnCatalogTests.cs`, `$QU/OrdersExportProcessorTests.cs`, `$QU/OrdersExportLayoutEffectiveTests.cs`

**Interfaces — Produces:** `new("buyer_notes", "Notas del cliente", 40, DefaultVisible: false)` como **última** columna del catálogo; la celda es `ExportCell` de texto con `order.BuyerNotes ?? string.Empty`. «Observaciones» (`notes`) no cambia.

- [ ] **RED.**
  ```csharp
  // OrdersExportColumnCatalogTests: el conteo de hoy pasa de 83 a 84 (Assert.Equal(84, …Columns.Count))
  [Fact] public void BuyerNotesIsTheLastColumnAndHidden()   // Columns[^1] == new("buyer_notes", "Notas del cliente", 40, DefaultVisible: false)
  // OrdersExportProcessorTests
  [Fact] public void BuyerNotesColumnReadsBuyerNotesAndObservacionesKeepsNotes()
  // pedido con Notes "Shopify #1001" y BuyerNotes "Sin cebolla", layout con notes y buyer_notes visibles → celdas "Shopify #1001" y "Sin cebolla"
  // OrdersExportLayoutEffectiveTests
  [Fact] public void ASavedLayoutGetsBuyerNotesHidden()   // layout guardado sin buyer_notes → Effective la agrega al final con Visible == false
  ```
- [ ] Corre `dotnet test $QU --filter "FullyQualifiedName~OrdersExportColumnCatalogTests|FullyQualifiedName~OrdersExportProcessorTests|FullyQualifiedName~OrdersExportLayoutEffectiveTests"` → rojo (84 ≠ 83, `buyer_notes` desconocida).
- [ ] **GREEN.** Implementa; agrega `"buyer_notes"` al final de la lista de ocultas del seeder, con su comentario. README: un párrafo «Notas del cliente» (2026-11-14): sale de `Order.BuyerNotes` (la nota que el comprador escribió en la tienda pública o en Shopify), nace oculta (D13), «Observaciones» sigue saliendo de `Notes`; revisaste la regla de choque con «MIGRACION 1» (no hay encabezado igual).
- [ ] Corre el mismo filtro → verde.
- [ ] Commit: `feat(orders): add the customer notes column to the orders export`

### Tarea 4: `editSync` y estados de pago en la tienda, y el scope `write_orders`

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyEditSync.cs`, `ShopifyPaymentStatuses.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStore.cs` (`Configure`, propiedades)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs` (`ConfigureStore`); migración `AddStoreEditSync`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyStores.cs` (comando, validador, handler, `ToDto`), `ShopifyDtos.cs`, `src/Modules/Shopify/Modules.Shopify.Api/ShopifyEndpoints.cs` (`UpdateShopifyStoreRequest`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAppOptions.cs`, `src/Api/appsettings.json`, `src/Api/appsettings.example.json`, `k8s/prod-configMap.yaml`, `README.md` (tabla de configuración de Shopify)
- Test: `$SU/ShopifyStoreTests.cs`, `$SU/ShopifyStoreHandlersTests.cs`, `$SI/ShopifyStoresApiTests.cs`, `$BU/ShopifyPaymentStatusesParityTests.cs` (nuevo), `$IU/ShopifyAppOptionsValidatorTests.cs`, `$II/ShopifyInstallApiTests.cs`, `$II/FakeShopifyHandler.cs`

**Interfaces — Produces:**
```csharp
public enum ShopifyEditSync { Manual, Automatic }

public static class ShopifyPaymentStatuses
{
    /// Copia de los nombres de Quotations.OrderPaymentStatus (Shopify no lo ve); la amarra ShopifyPaymentStatusesParityTests.
    public static IReadOnlyList<string> All { get; } = ["FullPaymentReceived", "PartialPaymentReceived", "PaymentPending", "PendingVerification"];
    public static bool IsKnown(string value);   // ordinal
}

// ShopifyStore
public ShopifyEditSync EditSync { get; private set; } = ShopifyEditSync.Manual;
public IReadOnlyList<string> EditPaymentStatuses { get; private set; } = ShopifyPaymentStatuses.All;
public void Configure(/* … los de hoy hasta historyDays … */, ShopifyEditSync editSync, IReadOnlyCollection<string> editPaymentStatuses, Guid updatedBy, DateTimeOffset now);
// editPaymentStatuses vacío, repetido o desconocido → ArgumentException

public sealed record UpdateShopifyStoreCommand(/* … */, int? HistoryDays, string? EditSync, IReadOnlyList<string>? EditPaymentStatuses, long ExpectedVersion);
public sealed record ShopifyStoreDto(/* … */, int? HistoryDays, string EditSync, IReadOnlyList<string> EditPaymentStatuses, DateTimeOffset? LastReconciledAt, DateTimeOffset UpdatedAt, long Version);
public sealed record UpdateShopifyStoreRequest(/* … */, int? HistoryDays, string? EditSync, IReadOnlyList<string>? EditPaymentStatuses);

public const string DefaultScopes = "read_orders,write_orders,read_customers";   // ShopifyAppOptions
```
Validador: `RuleFor(EditSync).Must(…).OverridePropertyName("editSync")`; `RuleFor(EditPaymentStatuses).NotEmpty().Must(sin repetidos).OverridePropertyName("editPaymentStatuses")` cuando no es `null`; `RuleForEach(EditPaymentStatuses).Must(ShopifyPaymentStatuses.IsKnown).OverridePropertyName("editPaymentStatuses")` (FluentValidation agrega `[i]`).
Mapeo: `edit_sync` con `HasConversion<string>()` y `HasMaxLength(16)`, default `'Manual'`; `edit_payment_statuses` `text[]` con el default de las Global Constraints. El handler pasa a `Configure` lo guardado cuando el comando trae `null` (D15).

- [ ] **RED.**
  ```csharp
  // ShopifyStoreTests
  [Fact] public void ANewStoreIsManualWithEveryPaymentStatus()
  [Fact] public void ConfigureRejectsAnEmptyOrUnknownPaymentStatusList()   // [] y ["Paid"] → ArgumentException
  // ShopifyStoreHandlersTests
  [Fact] public async Task UpdateWithoutEditFieldsKeepsTheSavedOnes()   // guardada Automatic + ["PaymentPending"]; comando con nulls → siguen
  [Theory, InlineData("automatic"), InlineData("Auto")] public async Task AnUnknownEditSyncIsAValidationError(string value)   // errors["editSync"]
  [Fact] public async Task ARepeatedPaymentStatusIsAValidationError()   // errors["editPaymentStatuses"]
  [Fact] public void StoreValidationErrorKeysAreTheContractOnes()
  // UpdateShopifyStoreValidator sobre editSync "x", editPaymentStatuses ["PaymentPending","Paid"] → PropertyName de los errores == {"editSync", "editPaymentStatuses[1]"};
  // editPaymentStatuses [] → {"editPaymentStatuses"}; ["PaymentPending","PaymentPending"] → {"editPaymentStatuses"}
  // ShopifyStoresApiTests
  [Fact] public async Task PutAndGetRoundTripEditSyncAndPaymentStatuses()
  // PUT con "editSync":"Automatic","editPaymentStatuses":["PaymentPending","PendingVerification"] → GET los devuelve iguales; SELECT edit_sync FROM shopify.stores == "Automatic"
  // $BU/ShopifyPaymentStatusesParityTests
  [Fact] public void TheNamesMatchOrderPaymentStatus()
  // Assert.Equal(Enum.GetNames<OrderPaymentStatus>().Order(StringComparer.Ordinal), ShopifyPaymentStatuses.All.Order(StringComparer.Ordinal))
  // $IU/ShopifyAppOptionsValidatorTests
  [Fact] public void DefaultScopesAskForWriteOrders()   // ShopifyAppOptions.DefaultScopes == "read_orders,write_orders,read_customers"
  ```
  En `$II`: la URL esperada de `ShopifyInstallApiTests` pasa a `scope=read_orders%2Cwrite_orders%2Cread_customers`; el `scope` por defecto de `FakeShopifyHandler` y el `scopes = …` que la prueba lee de la base pasan a `read_orders,write_orders,read_customers` (con `read_orders,read_customers` el canje ahora falla con `scopes_missing`, que es lo correcto).
- [ ] Corre `dotnet build $SU`, `dotnet build $BU` → `CS0246 ShopifyEditSync`/`ShopifyPaymentStatuses`.
- [ ] **GREEN.** Implementa y genera `AddStoreEditSync`; revísala (sólo dos columnas con default). Actualiza appsettings, ConfigMap y README (tabla `Shopify:App:Scopes`, más una nota: las tiendas que ya existen tienen que conceder `write_orders` —V-13—; un canje sin él falla con `scopes_missing`).
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyStoreTests|FullyQualifiedName~ShopifyStoreHandlersTests"`, `dotnet test $BU --filter "FullyQualifiedName~ShopifyPaymentStatusesParityTests"`, `dotnet test $IU --filter "FullyQualifiedName~ShopifyAppOptionsValidatorTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~PutAndGetRoundTripEditSyncAndPaymentStatuses" -- xUnit.MaxParallelThreads=4` y `dotnet test $II --filter "FullyQualifiedName~ShopifyInstallApiTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): configure edit sync per store and request write_orders`
- [ ] **Fin del Bloque 1:** `dotnet test $SU`; `dotnet test $SI --filter "FullyQualifiedName~ShopifyImportFlowTests|FullyQualifiedName~ShopifyStoresApiTests|FullyQualifiedName~ShopifyPersistenceTests" -- xUnit.MaxParallelThreads=4` (Review Focus 1); `dotnet test $QU --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~OrdersExport"`; `dotnet test $AT`. Revisión del bloque.

---

## Bloque 2 — las ediciones que llegan de Shopify (Tareas 5–9, un implementador)

### Tarea 5: `Order.ApplyExternalEdit` y su camino en Quotations y Bootstrapper

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ExternalOrderEdit.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs`, `OrderChangeSummary.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs`, `src/Bootstrapper/ShopifyOrderWriter.cs`
- Modify: `$SU/ShopifyImporterTestDoubles.cs` (el doble de `IShopifyOrderWriter`)
- Test: `$QU/OrderTests.cs`, `$QU/ExternalOrderServiceTests.cs`, `$BU/ShopifyAdaptersTests.cs`

**Interfaces — Produces:**
```csharp
// Domain
public sealed record ExternalOrderEdit(
    IReadOnlyCollection<OrderLineInput>? Lines,          // null = no se tocan; si viene, reemplaza todas
    ShippingDetails? Shipping,                           // null = no se toca
    QuotationPartyDetails? ShippingParty,                // null = no se toca; sin parte la crea
    bool ReplaceBuyerNotes, string? BuyerNotes,
    string NotesLinePrefix);                             // la dirección cruda que se quita de Notes al crear la parte
public sealed record ExternalOrderEditResult(bool LinesChanged, bool ShippingChanged, bool PartyChanged, bool BuyerNotesChanged)
{ public bool Any => LinesChanged || ShippingChanged || PartyChanged || BuyerNotesChanged; }

// Order: EnsureEditable; IsExternal (si no, SourceInvalid); parte con recogida → StorePickup; parte incompleta → ShippingPartyIncomplete;
// líneas vacías → order.order.items_required; envío por ShippingDetails.Validated(IsStorePickup, Currency, "order.order").
// Un solo Version++ si Any; sin cambios no toca nada. Con pago completo congela el costo de las líneas nuevas (costs). No recalcula el pago (D18).
public ExternalOrderEditResult ApplyExternalEdit(ExternalOrderEdit edit, OrderLineCosts costs, DateTimeOffset occurredAt);

// Application
public sealed record ExternalOrderLineState(Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);
public sealed record ExternalOrderEditState(
    Guid OrderId, long Version, OrderStatus Status, bool IsEditable, bool IsPrepared, bool IsInvoiced, OrderPaymentStatus PaymentStatus,
    Guid ClientId, string Currency, bool IsStorePickup, IReadOnlyList<ExternalOrderLineState> Lines, ShippingDetails Shipping,
    ExternalShippingParty? ShippingParty, string? ShippingCountry, string? BuyerNotes, string? Notes);
// ShippingCountry: OrderParty.Country de la parte de envío (null = Colombia, como en el dominio); null también sin parte (D21).
public sealed record ExternalOrderEditDraft(
    Guid TenantId, Guid OrderId, long ExpectedVersion, OrderSource Source,
    IReadOnlyList<ExternalOrderLine>? Lines, ShippingDetails? Shipping, ExternalShippingParty? ShippingParty,
    bool ReplaceBuyerNotes, string? BuyerNotes, string NotesLinePrefix, DateTimeOffset OccurredAt);
// IExternalOrderWriter
Task<ExternalOrderEditState?> FindEditStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
/// Version distinta → RequestConcurrencyException("concurrency.conflict"). Historial sin autor por grupo y auditoría por integración.
Task<ExternalOrderEditState> ApplyEditAsync(ExternalOrderEditDraft draft, CancellationToken cancellationToken);

// OrderChangeSummary
public static string EditedFromExternal(OrderSource source, ExternalOrderEditGroup group);
// Lines → "{source} editó los productos." · Shipping → "{source} editó el envío." · Party → "{source} editó la dirección de envío." · BuyerNotes → "{source} editó la nota del cliente."

// Shopify.Application
public sealed record ShopifyOrderLineState(Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);
public sealed record ShopifyOrderState(
    Guid OrderId, long Version, string Status, bool IsEditable, bool IsPrepared, bool IsInvoiced, string PaymentStatus, Guid ClientId,
    string Currency, bool IsStorePickup, IReadOnlyList<ShopifyOrderLineState> Lines, ShopifyShippingDraft Shipping,
    ShopifyShippingFingerprint? ShippingParty, string? ShippingCountry, string? BuyerNotes);
// Status por nombre (Open, Archived, Cancelled), como PaymentStatus; los usa editBlockedReason (D20).
public sealed record ShopifyOrderEditDraft(
    Guid TenantId, Guid OrderId, long ExpectedVersion,
    IReadOnlyList<ShopifyOrderLineDraft>? Lines, ShopifyShippingDraft? Shipping, ShopifyPartyDraft? ShippingParty,
    bool ReplaceBuyerNotes, string? BuyerNotes, DateTimeOffset OccurredAt);
/// State con el pedido después de escribir; RejectedCode con not_editable, store_pickup, items_required…
public sealed record ShopifyOrderEditOutcome(ShopifyOrderState? State, string? RejectedCode);
// IShopifyOrderWriter (cada uno en su scope, como los demás)
Task<ShopifyOrderState?> ReadStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
Task<ShopifyOrderEditOutcome> ApplyEditAsync(ShopifyOrderEditDraft draft, CancellationToken cancellationToken);   // concurrency.conflict se propaga
```
El adaptador manda `NotesLinePrefix = ShopifyOrderImporter.RawAddressLabel`, `Source = OrderSource.Shopify`, y traduce `QuotationsDomainException`/`ResourceNotFoundException` a `RejectedCode`; `PaymentStatus` viaja por nombre (`order.PaymentStatus.ToString()`).

- [ ] **RED.**
  ```csharp
  // $QU/OrderTests.cs
  [Fact] public void ApplyExternalEditReplacesTheLinesAndBumpsVersionOnce()   // 2 líneas → 1 línea qty 1; Items.Count 1; Total recalculado; Version + 1
  [Fact] public void ApplyExternalEditWithTheSameValuesChangesNothing()       // Any == false; Version igual
  [Fact] public void ApplyExternalEditCreatesThePartyAndDropsTheRawAddressLine()
  // Notes "Shopify #1001\nDirección de envío de Shopify: Calle 1" → Notes "Shopify #1001"; Shipping completo
  [Fact] public void ApplyExternalEditKeepsThePlatformUnitPrice()   // UnitPrice 47619.05m entra igual; DiscountPercentage 10m igual
  [Fact] public void ApplyExternalEditOnAPreparedOrderIsNotEditable()
  [Fact] public void ApplyExternalEditOnAManualOrderIsSourceInvalid()
  [Fact] public void ApplyExternalEditWithAPartyOnAPickupOrderIsStorePickup()
  // $QU/ExternalOrderServiceTests.cs
  [Fact] public async Task ApplyEditWithAStaleVersionIsConcurrencyConflict()   // ExpectedVersion = Version - 1 → RequestConcurrencyException Code "concurrency.conflict"; nada guardado
  [Fact] public async Task ApplyEditWritesOneHistoryEntryPerGroupWithoutAuthorAndAudits()
  // líneas y nota → entradas ItemsChanged "Shopify editó los productos." y NotesChanged "Shopify editó la nota del cliente.", MemberId null; audit "quotation.order.external_edit_applied"
  [Fact] public async Task FindEditStateReturnsLinesShippingPartyAndBuyerNotes()
  // $BU/ShopifyAdaptersTests.cs
  [Fact] public async Task ApplyEditMapsNotEditableToRejected()   // RejectedCode == "order.order.not_editable"
  [Fact] public async Task ReadStateMapsThePaymentStatusByName()   // "PaymentPending"
  [Fact] public async Task ReadStateCarriesStatusPreparedInvoicedCurrencyAndShippingCountry()
  // pedido preparado en COP con parte en Colombia → ("Open", IsPrepared true, IsInvoiced false, "COP", ShippingCountry null)
  ```
- [ ] Corre `dotnet build $QU`, `dotnet build $BU` → `CS0246 ExternalOrderEdit` / `CS0535` (el adaptador no implementa los métodos nuevos).
- [ ] **GREEN.** Implementa. Las líneas se reemplazan por el mismo camino interno que `AddItemCore`/`RemoveItem`, sin repreciar (llegan preciadas); la foto de la variante sale de `productLookup.FindManyAsync`, como en `CreateAsync`. Actualiza el doble de `$SU/ShopifyImporterTestDoubles.cs` (lee y escribe un estado en memoria).
- [ ] Corre `dotnet test $QU --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExternalOrderServiceTests"`, `dotnet test $BU --filter "FullyQualifiedName~ShopifyAdaptersTests"`, `dotnet build $SU` → verde.
- [ ] Commit: `feat(orders): apply external edits to a shopify order`

### Tarea 6: versión aplicada, proyección y tabla de cambios

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyOrderMirror.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyOrderEdit.cs`, `ShopifyEditReasons.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderProjection.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyRepositories.cs` (`IShopifyOrderEditRepository`, `IShopifyOrderMirrorRepository.FindByQepOrderIdAsync`, `CountToReviewAsync`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs`, `ShopifyRepositories.cs`, `ShopifyInfrastructureExtensions.cs`; migración `AddOrderEdits`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs` (después de `MarkImported`)
- Test: `$SU/ShopifyOrderMirrorTests.cs`, `$SU/ShopifyOrderEditTests.cs` (nuevo), `$SU/ShopifyOrderProjectionTests.cs` (nuevo), `$SI/ShopifyPersistenceTests.cs`, `$SI/ShopifyImportFlowTests.cs`

**Interfaces — Produces:**
```csharp
// ShopifyOrderMirror
public string? AppliedPayload { get; private set; }
public string? AppliedProjection { get; private set; }
public string? QepAttributes { get; private set; }
public void RecordApplied(string payloadJson, string projectionJson);   // Touch
public void AdoptPayload(string payloadJson);                           // sólo applied_payload; Touch (rechazar, revisar, eco)
public void RecordProjection(string projectionJson);                    // Touch (corrección del sistema)
public void SetQepAttributes(string attributesJson);                    // Touch

// Domain
public enum ShopifyOrderEditStatus { Pending, Applied, Blocked, Rejected, Reviewed, Superseded }
public static class ShopifyEditReasons
{
    public const string OrderNotEditable = "order_not_editable", PaymentStatusNotAllowed = "payment_status_not_allowed", ProductUnmatched = "product_unmatched";
    public const string QepChanged = "qep_changed", CustomerChanged = "customer_changed", CityUnresolved = "city_unresolved",
        PartyIncomplete = "party_incomplete", StorePickup = "store_pickup", StorePickupChanged = "store_pickup_changed";
}
public sealed class ShopifyOrderEdit
{
    public const int StatusReasonMaxLength = 64;
    // Id, TenantId, ConnectionId, MirrorOrderId, QepOrderId, BaseUpdatedAt, SnapshotUpdatedAt, Payload, Groups, Status, StatusReason,
    // CreatedAt, DecidedAt, DecidedBy, Version
    public static ShopifyOrderEdit Create(Guid tenantId, Guid connectionId, Guid mirrorOrderId, Guid qepOrderId, DateTimeOffset? baseUpdatedAt,
        DateTimeOffset snapshotUpdatedAt, string payloadJson, string groupsJson, ShopifyOrderEditStatus status, string? reason, DateTimeOffset now);
    public bool IsOpen => Status is ShopifyOrderEditStatus.Pending or ShopifyOrderEditStatus.Blocked;
    public void MarkApplied(string groupsJson, Guid? decidedBy, DateTimeOffset now);   // desde Pending o Blocked
    public void Block(string reason, string groupsJson, DateTimeOffset now);            // desde Pending o Blocked
    public void Reject(Guid decidedBy, DateTimeOffset now);                             // desde Pending o Blocked
    public void Review(Guid decidedBy, DateTimeOffset now);                             // sólo desde Blocked
    public void Supersede(DateTimeOffset now);                                          // desde Pending o Blocked
    // Fuera de esos estados: ShopifyDomainException con ShopifyErrorCodes.EditNotOpen ("shopify.edit.not_open") o EditNotBlocked
}

// Application
public sealed record ShopifyOrderProjection(
    IReadOnlyList<ShopifyOrderLineState> Lines, ShopifyShippingDraft Shipping, ShopifyShippingFingerprint? ShippingParty,
    string? BuyerNotes, ShopifyBuyerDocument? Document, IReadOnlyDictionary<string, string> Attributes)
{
    public static ShopifyOrderProjection From(ShopifyOrderState order, ShopifyBuyerDocument? document, IReadOnlyDictionary<string, string> attributes);
    public static string Write(ShopifyOrderProjection projection);   // camelCase, como WritePayload
    public static ShopifyOrderProjection Read(string json);
    public static bool SameLines(IReadOnlyList<ShopifyOrderLineState> a, IReadOnlyList<ShopifyOrderLineState> b);   // multiconjunto; decimales sin escala
    public static bool SameParty(ShopifyShippingFingerprint? a, ShopifyShippingFingerprint? b);   // textos recortados, teléfono sólo dígitos, ids iguales; el correo no cuenta
}

public interface IShopifyOrderEditRepository
{
    Task<ShopifyOrderEdit?> FindOpenAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken);
    Task<ShopifyOrderEdit?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShopifyOrderEdit>> ListByMirrorAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken);
    Task<(IReadOnlyList<ShopifyOrderEdit> Items, int Total)> SearchAsync(
        Guid tenantId, IReadOnlyCollection<ShopifyOrderEditStatus> statuses, Guid? connectionId, int page, int pageSize, CancellationToken cancellationToken);
    Task<int> CountToReviewAsync(Guid tenantId, CancellationToken cancellationToken);   // Pending + Blocked
    void Add(ShopifyOrderEdit edit);
}
// IShopifyOrderMirrorRepository
Task<ShopifyOrderMirror?> FindByQepOrderIdAsync(Guid tenantId, Guid qepOrderId, CancellationToken cancellationToken);
```
Importador: tras `MarkImported` (también con `AlreadyExisted`), `orders.ReadStateAsync(tenantId, result.OrderId)`; si existe, `row.RecordApplied(ShopifyOrderIngestion.WritePayload(snapshot), ShopifyOrderProjection.Write(ShopifyOrderProjection.From(state, ShopifyCustomerResolver.DocumentOf(store, snapshot), snapshot.Attributes)))`.

- [ ] **RED.**
  ```csharp
  // ShopifyOrderEditTests
  [Fact] public void ReviewOnlyFromBlocked()          // Pending.Review → Code "shopify.edit.not_blocked"
  [Fact] public void AClosedEditCannotBeDecided()     // Applied.Reject → Code "shopify.edit.not_open"
  [Fact] public void SupersedeClosesAnOpenEdit()
  // ShopifyOrderMirrorTests
  [Fact] public void RecordAppliedKeepsBothJsonsAndBumpsVersion()
  // ShopifyOrderProjectionTests
  [Fact] public void SameLinesIgnoresOrderAndDecimalScale()        // [(v1, 2.0m, 50000.00m, 0m)] vs [(v1, 2m, 50000m, 0.0000m)] → true
  [Fact] public void SamePartyIgnoresPhoneFormattingAndEmail()     // "+57 300 111 2233" vs "573001112233" → true
  [Fact] public void WriteAndReadRoundTrip()
  // ShopifyPersistenceTests
  [Fact] public async Task OrderEditRoundTripsWithTheContractColumns()
  [Fact] public async Task DeletingTheMirrorRowDeletesItsEdits()   // DELETE FROM shopify.orders → 0 filas en shopify.order_edits
  // ShopifyImportFlowTests
  [Fact] public async Task ImportRecordsTheAppliedPayloadAndProjection()
  // applied_payload ->> 'name' == "#1001"; jsonb_array_length(applied_projection -> 'lines') == 1; applied_projection ->> 'buyerNotes' igual a buyer_notes del pedido
  ```
- [ ] Corre `dotnet build $SU` → `CS0246 ShopifyOrderEdit`.
- [ ] **GREEN.** Implementa y genera `AddOrderEdits` (tres columnas en `orders`, la tabla con su FK `ON DELETE CASCADE` y los dos índices). `ShopifyErrorCodes` suma los ocho códigos de las Global Constraints.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyOrderEditTests|FullyQualifiedName~ShopifyOrderMirrorTests|FullyQualifiedName~ShopifyOrderProjectionTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~OrderEditRoundTrips|FullyQualifiedName~DeletingTheMirrorRowDeletesItsEdits|FullyQualifiedName~ImportRecordsTheAppliedPayloadAndProjection" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): store the applied version and projection of imported orders`

### Tarea 7: el diff por grupo

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderDiff.cs`
- Test: `$SU/ShopifyOrderDiffTests.cs` (nuevo)

**Interfaces — Produces:**
```csharp
public enum ShopifyEditGroupKind { Lines, Shipping, ShippingAddress, Buyer, Note, Attributes }
public sealed record ShopifyEditRow(string Field, string? Label, JsonNode? Before, JsonNode? After);
public sealed record ShopifyGroupDiff(ShopifyEditGroupKind Kind, IReadOnlyList<ShopifyEditRow> Rows);
public static class ShopifyOrderDiff
{
    /// Sólo los grupos con al menos una fila, en el orden del enum. No mira pago, cancelación ni updated_at (§6.3).
    public static IReadOnlyList<ShopifyGroupDiff> Compute(ShopifyOrderSnapshot before, ShopifyOrderSnapshot after, ShopifyStore store);
}
```
- Líneas: se emparejan por `Id`; una línea sin `Id` (payload viejo) por `"#{posición}"`. Fila si cambió cantidad vigente, precio, descuento, SKU o título; `Before`/`After` = `{sku,title,quantity,unitPrice,discountAmount}`; quitada (`IsRemoved` o ausente) → `After = null`; nueva → `Before = null`. Montos como `JsonValue` numérico crudo, sin ceros de escala (como `Normalized` del importador), nunca como texto (D19); `sku` y `title` pueden ser `null`; sin SKU, `Label` es sólo el título.
- Envío: `title` (`ShopifyShippingMapper.CarrierTitle`), `amount` (`Amount`), `storePickup` (`IsStorePickup`), `cashOnDelivery` (`ShopifyCashOnDeliveryGateways.Matches(PaymentGateways)`).
- Dirección: `name`, `phone`, `address1`, `address2`, `city`, `province` (`Province ?? ProvinceCode`), recortados; vacío = `null`.
- Comprador: `document` (`DocumentOf(store, snapshot)` como `"CC 1036000111"`), `email`, `phone`.
- Nota: `note`. Atributos: `attribute` con `Label` = clave, sólo las claves que cambiaron.

- [ ] **RED.** En `ShopifyOrderDiffTests` (snapshots armados con un builder local):
  ```csharp
  [Fact] public void TheSameSnapshotHasNoDiff()
  [Fact] public void OnlyPaymentCancellationAndUpdatedAtAreNoDiff()
  [Fact] public void AQuantityChangeIsOneLineRowPairedById()   // Id 9001 qty 2→1 → Rows == [("line","Camiseta (CAM-001)", {quantity:2…}, {quantity:1…})]
  [Fact] public void ARemovedLineHasAfterNullAndANewLineHasBeforeNull()
  [Fact] public void ACustomLineIsLabelledByItsTitle()          // IsCustom, sin SKU → Label "Garantía🚛"
  [Fact] public void LinesWithoutIdsPairByPosition()
  [Fact] public void AShippingAmountChangeIsAShippingRow()
  [Fact] public void ACityChangeIsAShippingAddressRow()         // ("city", null, "Medellin", "Envigado")
  [Fact] public void TheDocumentComesFromTheConfiguredSource()  // store.DocumentAttributeKey "billing_address.company"
  [Fact] public void ANoteChangeIsANoteRow()
  [Fact] public void AttributesListOnlyTheChangedKeys()         // agregada, quitada y cambiada; la igual no sale
  [Fact] public void MoneyValuesAreRawNumbers()                 // fila amount: After.GetValueKind() == JsonValueKind.Number y 15000 (no "15000.00"); unitPrice de la línea igual
  [Fact] public void ALineWithoutSkuHasANullSku()               // After["sku"] es null (JSON null), Label == título
  ```
- [ ] Corre `dotnet build $SU` → `CS0103 ShopifyOrderDiff`.
- [ ] **GREEN.** Implementa.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyOrderDiffTests"` → verde.
- [ ] Commit: `feat(shopify): diff two shopify order versions by group`

### Tarea 8: el planificador y el detector en la ingesta

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyLineResolver.cs`, `ShopifyShippingResolver.cs` (extraídos de `ShopifyOrderImporter`, sin cambio de conducta)
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyEditPlanner.cs`, `ShopifyOrderEdits.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs`, `ShopifyOrderIngestion.cs`, `ShopifyPorts.cs` (`IShopifyOrderEditDetector`, `NoShopifyOrderEditDetector`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/ShopifyInfrastructureExtensions.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (mismo patrón de registro que `IShopifyOrderChangeReaction`)
- Test: `$SU/ShopifyEditPlannerTests.cs` (nuevo), `$SU/ShopifyOrderEditsTests.cs` (nuevo), `$BU/CompositionRootTests.cs`, `$SI/ShopifyEditSyncFlowTests.cs` (nuevo)

**Interfaces — Produces:**
```csharp
public sealed class ShopifyLineResolver(IShopifyProductLookup products, IShopifyCustomLineMappingRepository customLines)
{   // el cuerpo de hoy de ResolveLinesAsync; ignora las líneas IsRemoved
    public Task<(IReadOnlyList<ShopifyOrderLineDraft> Lines, IReadOnlyList<string?> UnmatchedSkus, IReadOnlyList<string> UnmatchedTitles)> ResolveAsync(
        Guid tenantId, Guid connectionId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken);
}
public sealed class ShopifyShippingResolver(IShopifyShippingLookup shipping)
{ public Task<ShopifyShippingResult> ResolveAsync(Guid tenantId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken); }

public enum ShopifyEditGroupOutcome { Apply, Conflict, Informative }
public sealed record ShopifyEditGroup(ShopifyEditGroupKind Kind, ShopifyEditGroupOutcome Outcome, string? Reason, IReadOnlyList<ShopifyEditRow> Rows);
public sealed record ShopifyEditPlan(
    IReadOnlyList<ShopifyEditGroup> Groups,          // sin los grupos eco
    IReadOnlyList<ShopifyEditGroupKind> Echoes,
    string? BlockReason,                             // order_not_editable | payment_status_not_allowed | product_unmatched
    ShopifyOrderEditDraft? Draft)                    // null si no hay grupos Apply o hay BlockReason
{ public bool IsEmpty => Groups.Count == 0; }

public sealed class ShopifyEditPlanner(
    ShopifyLineResolver lines, ShopifyShippingResolver shipping, ShopifyCustomerResolver customers,
    IShopifyCustomerDirectory customerDirectory, IShopifyUnresolvedCityReporter unresolvedCities, IClock clock)
{
    public Task<ShopifyEditPlan> PlanAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot applied, ShopifyOrderProjection projection,
        ShopifyOrderSnapshot incoming, ShopifyOrderState order, string? qepAttributesJson, CancellationToken cancellationToken);
}

public interface IShopifyOrderEditDetector
{   /// previousPayload = row.Payload antes de ApplySnapshot.
    Task DetectAsync(ShopifyOrderChange change, string previousPayload, CancellationToken cancellationToken);
}
public sealed class ShopifyOrderEdits(
    ShopifyEditPlanner planner, IShopifyOrderWriter orders, IShopifyOrderEditRepository edits, IClock clock) : IShopifyOrderEditDetector;
```
**Algoritmo del planificador** (por cada grupo de `ShopifyOrderDiff.Compute(applied, incoming, store)`; `current` = valor del pedido de `order`, `projected` = el de `projection`, `candidate` = el que saldría de `incoming`):
1. Comprador: `Informative`; si el documento nuevo resuelve (`customerDirectory.FindByDocumentAsync`) a un cliente `≠ order.ClientId` → `Conflict` `customer_changed`.
2. Atributos: `currentQep = qepAttributesJson ?? projected`. `incoming == currentQep` → eco; `currentQep ≠ projected` → `Conflict` `qep_changed`; si no `Apply` (no toca el pedido).
3. Nota: `candidate = Blank(incoming.Note)`; `== order.BuyerNotes` → eco; `order.BuyerNotes ≠ projected` → `Conflict` `qep_changed`; si no `Apply`.
4. Dirección: `order.IsStorePickup` → `Conflict` `store_pickup` (D8). Ciudad con `customers.ResolveCityAsync`; sin ciudad → `Conflict` `city_unresolved` y, si hay departamento, `unresolvedCities.ReportAsync` como `RecordShippingCityAsync` del importador (`PartyCityOnly: true`, sin cliente); campos incompletos (`ShopifyOrderImporter.ShippingPartyFields` con el correo de la parte actual si la hay) → `Conflict` `party_incomplete`. Luego `SameParty(candidate, current)` → eco; `!SameParty(current, projected)` → `Conflict` `qep_changed`; si no `Apply`.
5. Envío: `shipping.ResolveAsync`; un `WaitReason` → `Conflict` con ese motivo; `candidate.IsStorePickup ≠ order.IsStorePickup` → `Conflict` `store_pickup_changed`; `candidate == current` → eco; `current ≠ projected` → `Conflict` `qep_changed`; si no `Apply`.
6. Líneas: `lines.ResolveAsync(incoming)`; algo sin emparejar → `BlockReason = product_unmatched` (D5). `SameLines(candidate, current)` → eco; `!SameLines(current, projected)` → `Conflict` `qep_changed`; si no `Apply`.
7. `BlockReason`, en este orden: `!order.IsEditable` → `order_not_editable`; `store.EditPaymentStatuses` no contiene `order.PaymentStatus` → `payment_status_not_allowed`; el `product_unmatched` del paso 6.
8. `Draft` con sólo los grupos `Apply` que tocan el pedido (Líneas, Envío, Dirección, Nota), `ExpectedVersion = order.Version`, `OccurredAt = clock.UtcNow`.

**Algoritmo del detector** (`DetectAsync`), sólo si `change.Previous.ImportState == Imported`, `row.QepOrderId` existe y `snapshot.CancelledAt is null` (D7):
1. `open = edits.FindOpenAsync`; si `open?.SnapshotUpdatedAt == snapshot.UpdatedAt` → nada (misma entrega otra vez).
2. `applied = ReadPayload(row.AppliedPayload ?? previousPayload)`; `order = orders.ReadStateAsync` (`null` → nada). Proyección: si la fila tiene `AppliedPayload` **y** `AppliedProjection`, `Read(row.AppliedProjection)`. Si le falta cualquiera de las dos (pedido importado antes, D6), `ShopifyOrderProjection.From(order, DocumentOf(store, applied), applied.Attributes)`: los valores **actuales** del pedido, así ningún grupo cuenta como tocado por QEP.
3. `plan = PlanAsync(…)`. Vacío (todo eco o nada): `open?.Supersede(now)`; `row.AdoptPayload(WritePayload(snapshot))`; fin.
4. `open?.Supersede(now)`; estado inicial: `BlockReason` → `Blocked` con el motivo; `store.EditSync == Automatic` → intenta aplicar; si no `Pending`. Agrega el `ShopifyOrderEdit` con `groups` = JSON de `plan.Groups`.
5. Aplicar: si `plan.Draft` es `null` → `MarkApplied`; si no `orders.ApplyEditAsync(draft)`: `State` → `MarkApplied` y `row.RecordApplied(WritePayload(snapshot), Write(merge))`, donde `merge` toma de `State` los grupos aplicados y de `projection` los demás; `RejectedCode == "order.order.not_editable"` → `Block(order_not_editable)`; otro código → queda `Pending`; `RequestConcurrencyException` con `concurrency.conflict` **de Quotations** se atrapa acá y el cambio queda `Pending` (no sube al bucle de la ingesta).

Ingesta: guarda `var previousPayload = row.Payload;` antes de `ApplySnapshot` y llama `await editDetector.DetectAsync(change, previousPayload, …)` **antes** de `reaction.ReactAsync`, con el mismo `ShopifyOrderChange`.

- [ ] **RED.** En `ShopifyEditPlannerTests` (dobles de `ShopifyImporterTestDoubles`):
  ```csharp
  [Fact] public async Task NoGroupChangedIsAnEmptyPlan()
  [Theory, InlineData(ShopifyEditGroupKind.Lines), InlineData(ShopifyEditGroupKind.Shipping), InlineData(ShopifyEditGroupKind.ShippingAddress),
           InlineData(ShopifyEditGroupKind.Note), InlineData(ShopifyEditGroupKind.Attributes)]
  public async Task EachGroupAloneIsApplied(ShopifyEditGroupKind kind)   // un solo grupo, Outcome Apply
  [Fact] public async Task AGroupTheAdvisorChangedIsAConflictAndTheRestApplies()   // nota de QEP ≠ proyección: Note Conflict qep_changed; Lines Apply
  [Fact] public async Task TheEchoOfTheQepValueIsNotAGroup()                        // ciudad nueva de Shopify == ciudad del pedido → Echoes [ShippingAddress], Groups []
  [Fact] public async Task ACustomLineEditMatchesThroughItsMapping()
  [Fact] public async Task AnUnmatchedLineBlocksTheWholeChange()                    // BlockReason "product_unmatched"; Draft null
  [Fact] public async Task ADocumentOfAnotherCustomerIsCustomerChanged()
  [Fact] public async Task AnUnresolvedCityIsAConflictAndIsReported()               // Conflict city_unresolved; ReportAsync llamado una vez con PartyCityOnly
  [Fact] public async Task ACityThroughASynonymApplies()
  [Fact] public async Task ThePricesAreShopifysWithoutCatalogDiscounts()            // draft.Lines[0].UnitPrice == ShopifyPricing.UnitPriceWithVat(…); DiscountPercentage del descuento de Shopify
  [Fact] public async Task AnAddressOnAPickupOrderIsStorePickup()                   // D8
  ```
  En `ShopifyOrderEditsTests`:
  ```csharp
  [Theory]
  [InlineData(false, true, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Blocked, "order_not_editable")]
  [InlineData(true, false, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Blocked, "payment_status_not_allowed")]
  [InlineData(true, true, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Applied, null)]
  [InlineData(true, true, ShopifyEditSync.Manual, ShopifyOrderEditStatus.Pending, null)]
  public async Task TheStateTableOfTheSpec(bool editable, bool paymentAllowed, ShopifyEditSync sync, ShopifyOrderEditStatus expected, string? reason)
  [Fact] public async Task ASecondSnapshotSupersedesThePendingChange()      // primero Pending; otro updated_at → el primero Superseded, uno nuevo Pending
  [Fact] public async Task TheSameSnapshotTwiceCreatesOneChange()           // idempotencia
  [Fact] public async Task AutomaticWithAStaleOrderVersionStaysPending()    // el doble lanza RequestConcurrencyException("concurrency.conflict") → Pending; DetectAsync no lanza
  [Fact] public async Task ALegacyRowUsesThePreviousPayloadAsBase()         // AppliedPayload null; diferencias viejas no salen
  [Fact] public async Task ALegacyRowTakesTheProjectionFromTheCurrentOrder()
  // AppliedPayload y AppliedProjection null; dirección del pedido ≠ la que Shopify mandó al importar; Shopify cambia la ciudad → ShippingAddress Apply (no Conflict)
  [Fact] public async Task AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict()
  // fila sin AppliedProjection; pedido con BuyerNotes null y la nota vieja dentro de Notes; Shopify cambia note "A" → "B" → Note Apply; con Automatic, BuyerNotes == "B" y Notes intacta
  [Fact] public async Task ACancelledSnapshotComputesNothing()
  [Fact] public async Task AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict()   // Review Focus 5
  // proyección con nota "A"; pedido con "B" (asesor); snapshot 1 trae "B" (eco) → sin cambio y applied_payload adopta; snapshot 2 trae "C" → Note Conflict qep_changed
  [Fact] public async Task AnAppliedChangeMovesTheAppliedPayloadAndTheProjection()
  ```
  En `CompositionRootTests`: `TheEditDetectorIsTheRealOne` (resuelve `IShopifyOrderEditDetector` → `ShopifyOrderEdits`). En `ShopifyEditSyncFlowTests` (nuevo, `Flow` como `ShopifyImportFlowTests`):
  ```csharp
  [Fact] public async Task AQuantityEditInShopifyCreatesAPendingChange()
  // orders/create, luego orders/updated con current_quantity 1 y updated_at nuevo → una fila en shopify.order_edits Pending; quotations.order_items sin cambio
  [Fact] public async Task AnAutomaticStoreAppliesTheEdit()
  // PUT tienda editSync Automatic → orders/updated → order_items.quantity == 1; order_history ItemsChanged con member_id NULL; fila Applied
  [Fact] public async Task DeliveringTheSameUpdateTwiceCreatesOneChange()   // dos webhook ids, mismo cuerpo → count == 1
  ```
- [ ] Corre `dotnet build $SU` → `CS0246 ShopifyEditPlanner`.
- [ ] **GREEN.** Extrae los dos resolvedores (el importador los usa; sin cambio de conducta), implementa el planificador, el detector y el registro. Los textos `rows` salen del diff de la Tarea 7.
  En `ShopifyEditSyncFlowTests` además:
  ```csharp
  [Fact] public async Task AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict()
  // tienda Automatic; tras importar, UPDATE shopify.orders SET applied_payload = NULL, applied_projection = NULL y quotations.orders SET buyer_notes = NULL
  // (como un pedido viejo); orders/updated con otra "note" → order_edits Applied, sin grupo Conflict; buyer_notes == la nota nueva
  ```
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyEditPlannerTests|FullyQualifiedName~ShopifyOrderEditsTests|FullyQualifiedName~ShopifyCustomLineImportTests"`, `dotnet test $BU --filter "FullyQualifiedName~CompositionRootTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~ShopifyEditSyncFlowTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): detect shopify order edits as changes`

### Tarea 9: aceptar, rechazar, revisar y la API de la bandeja

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderEditDecisions.cs` (consultas, comandos, handlers, mapper)
- Create: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyOrderEditEndpoints.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyDtos.cs`, `ShopifyOrders.cs` (resumen), `ShopifyAuditActions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (mapear las rutas; una línea al final del bloque de Shopify)
- Test: `$SU/ShopifyOrderEditDecisionsTests.cs` (nuevo), `$SI/ShopifyEditSyncFlowTests.cs`, `$SI/ShopifyInboxApiTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record ShopifyOrderChangeDto(
    Guid Id, Guid MirrorOrderId, Guid ConnectionId, string? StoreName, string OrderName, Guid? QepOrderId, string? QepOrderNumber,
    string Currency,   // ISO del pedido = shopify.orders.currency (la importación exige que coincidan); siempre presente (D19)
    string Status, string? StatusReason, DateTimeOffset? BaseUpdatedAt, DateTimeOffset SnapshotUpdatedAt,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, string? DecidedByName, JsonElement Groups);
public sealed record ShopifyOrderChangePageDto(IReadOnlyList<ShopifyOrderChangeDto> Items, int Total, int Page, int PageSize);
public sealed record ShopifyOrdersSummaryDto(int Waiting, int WithWarnings, int CancelledAfterInvoice, int ImportedToday, int ChangesToReview);

public sealed record ListShopifyChangesQuery(Guid TenantId, IReadOnlyList<string>? Statuses, Guid? ConnectionId, int? Page, int? PageSize) : IQuery<ShopifyOrderChangePageDto>;
public sealed record ListShopifyOrderChangesQuery(Guid TenantId, Guid MirrorOrderId) : IQuery<IReadOnlyList<ShopifyOrderChangeDto>>;
public sealed record AcceptShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;
public sealed record RejectShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;
public sealed record ReviewShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;

// ShopifyAuditActions
public const string OrderEditAccepted = "shopify.order_edit.accepted", OrderEditRejected = "shopify.order_edit.rejected", OrderEditReviewed = "shopify.order_edit.reviewed";
```
- Aceptar (D16): `FindAsync` (404 `shopify.edit.not_found`); `!IsOpen` → 409 `shopify.edit.not_open`; relee fila, tienda y `ReadStateAsync`; vuelve a planear con `applied = row.AppliedPayload`, `incoming = edit.Payload`. Con `BlockReason` → `edit.Block(reason, groups)`, guarda y lanza `ShopifyDomainException("shopify.edit.<motivo>")` (422). Sin él → `ApplyEditAsync` (412 se propaga, la fila no cambia) y, con éxito, `MarkApplied(groups, membershipId)` + `row.RecordApplied(edit.Payload, merge)` + auditoría, en un guardado.
- Rechazar: `Reject` + `row.AdoptPayload(edit.Payload)` + auditoría. Revisar: `Review` (sólo `Blocked`, D9) + `AdoptPayload` + auditoría.
- `decided_by` = membresía activa del actor (como `shopify.stores.updated_by`); sin membresía activa → 403.
- Rutas en `ShopifyOrderEditEndpoints` con las políticas del contrato; `status` como en `ListOrdersAsync` (repetido o con comas); `pageSize` ≤ 50 con el validador de la lista de pedidos.

- [ ] **RED.**
  ```csharp
  // ShopifyOrderEditDecisionsTests
  [Fact] public async Task AcceptABlockedChangeOnceTheOrderIsEditableAgainApplies()
  [Fact] public async Task AcceptWhileStillNotEditableIs422AndStaysBlocked()     // Code "shopify.edit.order_not_editable"; Status Blocked
  [Fact] public async Task AcceptAPendingThatCannotApplyAnymoreBlocksIt()        // D16
  [Fact] public async Task AcceptWithAStaleOrderIs412AndLeavesTheChangePending()
  [Fact] public async Task RejectAdoptsTheSnapshotSoTheNextDiffDoesNotProposeIt()
  [Fact] public async Task ReviewAPendingIsNotBlocked()                          // Code "shopify.edit.not_blocked"
  [Fact] public async Task DecidingAClosedChangeIsNotOpen()                      // 409
  // ShopifyEditSyncFlowTests
  [Fact] public async Task APreparedOrderBlocksAndAcceptAfterUnprepareApplies()
  // prepare → orders/updated → Blocked order_not_editable; accept → 422; POST /orders/{id}/unprepare; accept → 200 Applied; order_items cambiado
  [Fact] public async Task ListChangesAndSummaryCountPendingAndBlocked()         // GET /shopify/changes total 2; GET /orders/summary changesToReview == 2
  [Fact] public async Task ChangesOfAnOrderComeNewestFirst()
  [Fact] public async Task TheChangeCarriesTheOrderCurrencyAndRawAmounts()
  // GET /shopify/changes → items[0].currency == "COP"; la fila de línea trae before.unitPrice como número JSON (ValueKind Number)
  // ShopifyInboxApiTests
  [Fact] public async Task SummaryAlwaysCarriesChangesToReview()                 // tenant sin cambios → "changesToReview": 0
  ```
- [ ] Corre `dotnet build $SU` → `CS0246 AcceptShopifyChangeCommand`.
- [ ] **GREEN.** Implementa. Documenta cada DTO con su comentario BFF (por qué `rows` viaja armado: el frontend no conoce el diff ni los enums de QEP).
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyOrderEditDecisionsTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~APreparedOrderBlocksAndAcceptAfterUnprepareApplies|FullyQualifiedName~ListChangesAndSummary|FullyQualifiedName~ChangesOfAnOrderComeNewestFirst|FullyQualifiedName~SummaryAlwaysCarriesChangesToReview" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): accept, reject and review shopify order changes`
- [ ] **Fin del Bloque 2:** `dotnet test $SU`; `dotnet test $BU --filter "FullyQualifiedName~Shopify|FullyQualifiedName~CompositionRootTests"`; `dotnet test $SI --filter "FullyQualifiedName~ShopifyEditSyncFlowTests|FullyQualifiedName~ShopifyImportFlowTests|FullyQualifiedName~ShopifyWebhookWorkerTests|FullyQualifiedName~ShopifyReconcileWorkerTests|FullyQualifiedName~ShopifyInboxApiTests" -- xUnit.MaxParallelThreads=4`; `dotnet test $QU --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExternalOrderServiceTests"`; `dotnet test $AT`. Revisión del bloque.

---

## Bloque 3 — las correcciones del asesor viajan a Shopify (Tareas 10–13, un implementador)

### Tarea 10: corregir envío, nota y atributos, y encolar el envío

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`EditExternalShippingParty`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs` (`CorrectByMemberAsync`), `OrderChangeSummary.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyOrderPush.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderPushes.cs`, `ShopifyOrderCorrections.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs`, `ShopifyRepositories.cs`, `ShopifyDtos.cs`, `ShopifyAuditActions.cs`
- Create: `src/Bootstrapper/ShopifyOrderCorrectionWriter.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs`, `ShopifyRepositories.cs`, `ShopifyUnitOfWork.cs`; migración `AddOrderPushes`
- Modify: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyOrderEditEndpoints.cs`
- Test: `$QU/OrderTests.cs`, `$QU/ExternalOrderServiceTests.cs`, `$SU/ShopifyOrderPushTests.cs` (nuevo), `$SU/ShopifyOrderCorrectionsTests.cs` (nuevo), `$SI/ShopifyOrderCorrectionApiTests.cs` (nuevo)

**Interfaces — Produces:**
```csharp
// Order: sólo Source = Shopify, editable, sin recogida, parte completa. Crea la parte (quitando de Notes las líneas con el prefijo) o la reemplaza.
public bool EditExternalShippingParty(QuotationPartyDetails party, string notesLinePrefix, DateTimeOffset occurredAt);

public sealed record ExternalOrderCorrection(
    Guid TenantId, Guid OrderId, long ExpectedVersion, ExternalShippingParty? ShippingParty, bool ReplaceBuyerNotes, string? BuyerNotes,
    Guid MemberId, Guid SubjectId, string NotesLinePrefix, DateTimeOffset OccurredAt);
/// Corre en la transacción que ya tenga el contexto (la de Shopify, enrolada); guarda con SaveChangesAsync.
/// Historial con autor: PartiesChanged "Editó la dirección de envío." · NotesChanged "Editó la nota del cliente.";
/// auditoría Publish(subjectId, "quotation.order.shipping_party_changed" | "quotation.order.buyer_notes_changed").
Task<ExternalOrderEditState> CorrectByMemberAsync(ExternalOrderCorrection correction, CancellationToken cancellationToken);

// Shopify.Domain
public enum ShopifyPushGroup { ShippingAddress, Note, Attributes }
public enum ShopifyOrderPushStatus { Pending, Sent, Failed }
public sealed class ShopifyOrderPush
{
    public const int FailureCodeMaxLength = 64;
    // Id, TenantId, ConnectionId, MirrorOrderId, QepOrderId, Groups (IReadOnlyList<ShopifyPushGroup>), Status, Attempts, LeaseUntil,
    // FailureCode, CreatedAt, UpdatedAt, SentAt, Version
    public static ShopifyOrderPush Create(ShopifyOrderMirror row, IReadOnlyCollection<ShopifyPushGroup> groups, DateTimeOffset now);
    public void Merge(IReadOnlyCollection<ShopifyPushGroup> groups, DateTimeOffset now);   // unión, sólo Pending
    public void MarkSent(DateTimeOffset now);
    public void MarkFailed(string code, DateTimeOffset now);
}

// Shopify.Application
public sealed class ShopifyOrderPushes(IShopifyOrderPushRepository pushes)
{   /// Fusiona con el Pending de la fila o agrega uno; el commit es del llamador.
    public Task EnqueueAsync(ShopifyOrderMirror row, IReadOnlyCollection<ShopifyPushGroup> groups, DateTimeOffset now, CancellationToken cancellationToken);
}
public interface IShopifyOrderPushRepository
{
    Task<ShopifyOrderPush?> FindPendingAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken);
    Task<ShopifyOrderPush?> FindLatestAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken);
    void Add(ShopifyOrderPush push);
}

public sealed record ShopifyOrderCorrection(
    Guid TenantId, Guid OrderId, long ExpectedVersion, ShopifyPartyDraft? ShippingParty, bool ReplaceBuyerNotes, string? BuyerNotes,
    Guid MemberId, Guid SubjectId);
/// Adaptador en Bootstrapper, scoped, SIN scope propio: enrola el QuotationsDbContext del request en la transacción abierta del
/// ShopifyDbContext (como CatalogProductDeletionCleanup); sin transacción abierta lanza InvalidOperationException.
public interface IShopifyOrderCorrectionWriter
{   /// QuotationsDomainException y RequestConcurrencyException se propagan (422 / 412).
    Task<ShopifyOrderState> CorrectAsync(ShopifyOrderCorrection correction, CancellationToken cancellationToken);
}

public sealed record ShopifyAttributeInput(string? Key, string? Value);
public sealed record ShopifyPartyInput(string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId);
public sealed record CorrectShopifyOrderCommand(
    Guid TenantId, Guid MirrorOrderId, long ExpectedOrderVersion, ShopifyPartyInput? ShippingParty, string? BuyerNotes,
    IReadOnlyList<ShopifyAttributeInput>? Attributes) : ICommand<ShopifyOrderSyncDto>;
public sealed record GetShopifyOrderSyncQuery(Guid TenantId, Guid QepOrderId) : IQuery<ShopifyOrderSyncDto>;
public sealed record ShopifyAttributeDto(string Key, string Value);
public sealed record ShopifyOrderPushDto(string Status, IReadOnlyList<string> Groups, string? FailureCode, int Attempts, DateTimeOffset? LastAttemptAt, DateTimeOffset? SentAt);
public sealed record ShopifyOrderSyncDto(
    Guid MirrorOrderId, Guid ConnectionId, string OrderName, long OrderVersion, bool CanEdit,
    string? EditBlockedReason,        // NotImported | Cancelled | Archived | Invoiced | Prepared; null ⇔ CanEdit (D20)
    bool ShippingAddressSupported,    // false con recogida o dirección fuera de Colombia (D21)
    IReadOnlyList<ShopifyAttributeDto> Attributes, bool AttributesEditedInQep, ShopifyOrderPushDto? Push, ShopifyOrderChangeDto? OpenChange);
public const string OrderCorrected = "shopify.order.corrected";   // ShopifyAuditActions; detalle { groups: [...] }
```
- Validador (`CorrectShopifyOrderValidator`): exactamente la tabla de claves del contrato.
  - Cada regla usa `OverridePropertyName` en camelCase (`shippingParty.name` …), y `RuleForEach(Attributes).ChildRules(...)` con `OverridePropertyName("attributes")`, que da `attributes[i].key` y `attributes[i].value`.
  - Los topes de la parte (255, 32, 254, 255) son constantes de `ShopifyPartyLimits` (Shopify no ve Quotations), copiadas de `QuotationPartyDetails.*MaxLength`. Las amarra `$BU/ShopifyPartyLimitsParityTests.TheLimitsMatchQuotationPartyDetails`, para que un ancho del dominio nunca salga como 422 sin mapa `errors`.
- `buyerNotes` se recorta y `""` queda `null` antes de comparar y de escribir (D22).
- Dirección fuera de Colombia (D21): con `shippingParty` no nulo, si `order.ShippingCountry` no es `null` ni `CO`, o el pedido no tiene parte y el `ShippingAddress.CountryCode` del último `row.Payload` no es `CO` (vacío cuenta como `CO`), el handler lanza `ShopifyDomainException("shopify.order.address_not_supported")` (422) antes de abrir la transacción.
- `EditBlockedReason` sale de la fila y de `ShopifyOrderState` en el orden de D20. `ShippingAddressSupported = !order.IsStorePickup &&` la misma regla de país.
- Handler: `ShopifyAuthorization.EnsureAsync` con `OrderManage`; fila `Imported` con `QepOrderId` (si no 422 `shopify.order.not_imported`); `ReadStateAsync`; decide qué grupos cambiaron (parte con `SameParty`, nota recortada, atributos contra `QepAttributes ?? proyección`); ninguno → devuelve el DTO sin escribir. Si no: `BeginTransactionAsync` de Shopify → `CorrectAsync` (si cambió parte o nota) → `row.SetQepAttributes` (si cambiaron atributos) → `EnqueueAsync` con esos grupos → auditoría `shopify.order.corrected` → `SaveChangesAsync` → `CommitAsync`. **No** toca `applied_projection` (D3).
- `GET /qep-orders/{qepOrderId}` lee la fila con `FindByQepOrderIdAsync`, el estado del pedido, el último envío y el cambio abierto.
- `ShopifyUnitOfWork`: `IX_order_pushes_mirror_pending` → `RequestConcurrencyException("concurrency.conflict")`, por nombre, antes de los demás `catch`.

- [ ] **RED.**
  ```csharp
  // $QU/OrderTests.cs
  [Fact] public void EditExternalShippingPartyCreatesItAndDropsTheRawAddressLine()
  [Fact] public void EditExternalShippingPartyReplacesTheExistingOne()        // ciudad y dirección nuevas; Version + 1
  [Fact] public void EditExternalShippingPartyOnAStoreOrderIsSourceInvalid()
  // $QU/ExternalOrderServiceTests.cs
  [Fact] public async Task CorrectByMemberWritesHistoryWithTheMemberAndAudits()
  // $SU/ShopifyOrderPushTests.cs
  [Fact] public void MergeUnitesTheGroups()                                   // [Note] + [ShippingAddress, Note] → [ShippingAddress, Note]
  // $SU/ShopifyOrderCorrectionsTests.cs
  [Fact] public async Task OnlyTheChangedGroupsAreEnqueued()                  // sólo nota distinta → push Groups == [Note]; CorrectAsync con ShippingParty null
  [Fact] public async Task ASecondCorrectionMergesIntoThePendingPush()
  [Fact] public async Task NothingChangedWritesNothing()
  [Fact] public async Task ACorrectionDoesNotMoveTheProjection()              // Review Focus 5: AppliedProjection igual antes y después
  [Fact] public async Task ANotImportedRowIsNotImported()                     // Code "shopify.order.not_imported"
  [Fact] public void CorrectionValidationErrorKeysAreTheContractOnes()
  // party con name "", phone de 33, email "x", address "", departmentId Guid.Empty, cityId null; attributes [{key:""}, {key:"A", value: 1001×"v"}, {key:"A"}]; buyerNotes 2001×"n"
  // → PropertyName de los errores == {"shippingParty.name","shippingParty.phone","shippingParty.email","shippingParty.address","shippingParty.departmentId",
  //    "shippingParty.cityId","attributes[0].key","attributes[1].value","attributes","buyerNotes"}; attributes null → {"attributes"}
  [Theory, InlineData(""), InlineData("   "), InlineData(null)]
  public async Task ABlankBuyerNoteIsNull(string? notes)                      // pedido con BuyerNotes "A" → CorrectAsync con BuyerNotes null; pedido con null → nada encolado
  [Fact] public async Task ANonColombianAddressIsNotSupported()               // ShippingCountry "VE" → Code "shopify.order.address_not_supported"; nada escrito
  [Theory]
  [InlineData("NotImported"), InlineData("Cancelled"), InlineData("Archived"), InlineData("Invoiced"), InlineData("Prepared")]
  public async Task EditBlockedReasonFollowsTheOrderState(string expected)    // CanEdit false y EditBlockedReason == expected; editable → (true, null)
  [Fact] public async Task AStorePickupOrderHasNoShippingAddressSupport()     // CanEdit true, ShippingAddressSupported false
  // $BU/ShopifyPartyLimitsParityTests
  [Fact] public void TheLimitsMatchQuotationPartyDetails()
  // $SI/ShopifyOrderCorrectionApiTests.cs
  [Fact] public async Task CorrectingTheCityWritesTheOrderAndEnqueuesAPush()
  // PUT /shopify/orders/{id}/corrections con If-Match → order_parties.city_id nuevo; shopify.order_pushes: 1 fila Pending, groups {ShippingAddress}
  [Fact] public async Task AShopifyFailureAfterTheOrderWriteRollsBackBoth()
  // IShopifyAuditRecorder reemplazado por uno que lanza → 500; order_parties sin cambio; 0 filas en order_pushes
  [Fact] public async Task CorrectingANotEditableOrderIs422AndEnqueuesNothing()   // prepare antes → 422 order.order.not_editable; 0 pushes
  [Fact] public async Task CorrectingWithoutIfMatchIs428()
  [Fact] public async Task AnInvalidCorrectionReturnsTheContractErrorKeys()   // 422 validation.failed; errors tiene "shippingParty.name" y "attributes[0].key" 
  [Fact] public async Task GetSyncShowsAttributesAndThePendingPush()             // GET /shopify/qep-orders/{qepOrderId} → push.status "Pending", attributesEditedInQep true
  ```
- [ ] Corre `dotnet build $QU`, `dotnet build $SU` → `CS1061 EditExternalShippingParty` / `CS0246 ShopifyOrderPush`.
- [ ] **GREEN.** Implementa y genera `AddOrderPushes`. Registra `ShopifyOrderCorrectionWriter` como scoped en Bootstrapper (una línea al final del bloque de Shopify) y documenta en su comentario el enrolamiento y que el `QuotationsDbContext` del scope no se vuelve a usar después del commit.
- [ ] Corre `dotnet test $QU --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExternalOrderServiceTests"`, `dotnet test $SU --filter "FullyQualifiedName~ShopifyOrderPushTests|FullyQualifiedName~ShopifyOrderCorrectionsTests"`, `dotnet test $BU --filter "FullyQualifiedName~ShopifyPartyLimitsParityTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~ShopifyOrderCorrectionApiTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): correct shopify orders and enqueue the push in the same transaction`

### Tarea 11: `orderUpdate` y el worker de envíos

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyOrderUpdateClient.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderPushProcessor.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Workers/ShopifyOrderPushWorker.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` (`IShopifyOrderUpdater`, `IShopifyGeographyLookup.DescribeCityAsync`), `ColombiaProvinceCodes.cs` (`CodeFor`), `ShopifyRepositories.cs` (`IShopifyOrderPushQueue`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyRepositories.cs`, `ShopifyInfrastructureExtensions.cs`; `src/Bootstrapper/ShopifyGeographyLookup.cs`
- Test: `$SU/ShopifyOrderPushProcessorTests.cs` (nuevo), `$SU/ShopifyOrderUpdateClientTests.cs` (nuevo), `$SU/ColombiaProvinceCodesTests.cs`, `$SI/ShopifySecretLeakTests.cs`, `$SI/ShopifyOrderPushWorkerTests.cs` (nuevo)

**Interfaces — Produces:**
```csharp
public sealed record ShopifyAddressUpdate(
    string FirstName, string LastName, string Address1, string Address2, string City, string? ProvinceCode, string CountryCode, string Phone, string? Company);
public sealed record ShopifyOrderUpdate(
    long ShopifyOrderId, ShopifyAddressUpdate? ShippingAddress, bool SetNote, string? Note, IReadOnlyDictionary<string, string>? Attributes);
public enum ShopifyOrderUpdateKind { Updated, Retry, Failed }
public sealed record ShopifyOrderUpdateResult(ShopifyOrderUpdateKind Kind, string? FailureCode, TimeSpan? RetryAfter);
public interface IShopifyOrderUpdater
{   /// Sólo manda los campos no nulos (SetNote para la nota). Nunca registra el token, el cuerpo ni el mensaje de userErrors.
    Task<ShopifyOrderUpdateResult> UpdateAsync(ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken);
}
// IShopifyGeographyLookup
Task<ShopifyCityName?> DescribeCityAsync(Guid departmentId, Guid cityId, CancellationToken cancellationToken);
public sealed record ShopifyCityName(string City, string Department);
// ColombiaProvinceCodes
public static string? CodeFor(string departmentName);   // inverso de DepartmentNameFor: "ANTIOQUIA" → "ANT"

public sealed class ShopifyOrderPushProcessor(
    IShopifyConnectionDirectory connections, IShopifyOrderWriter orders, IShopifyOrderMirrorRepository mirrors,
    IShopifyGeographyLookup geography, IShopifyOrderUpdater updater, IShopifyUnitOfWork unitOfWork, IClock clock)
{ public Task<ShopifyOrderUpdateResult> ProcessAsync(ShopifyOrderPush push, CancellationToken cancellationToken); }
public interface IShopifyOrderPushQueue
{
    Task<IReadOnlyList<Guid>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);   // UPDATE … attempts + 1, un solo ganador
}
```
- Mutación (sin alias ni fragmentos): `mutation OrderUpdate($input: OrderInput!) { orderUpdate(input: $input) { order { id updatedAt } userErrors { field message } } }`; `input.id = "gid://shopify/Order/{id}"`, `shippingAddress`, `note`, `customAttributes: [{ key, value }]` sólo si vienen.
- Clasificación (D14): 2xx sin `errors` ni `userErrors` y con `order` → `Updated`; 429, `THROTTLED`, 5xx, red, timeout, 401, 402 → `Retry` (con `Retry-After` si llega); 403 o `ACCESS_DENIED` → `Failed access_denied`; `userErrors` → `Failed user_error` (al log sólo `field`); `order` nulo → `Failed order_not_found`; otro 4xx → `Failed rejected`.
- Procesador: conexión ausente → `Failed connection_missing`; `AccessToken` nulo → `Retry` sin llamar a Shopify; pedido ausente → `Failed order_not_found`. Arma sólo los grupos de `push.Groups` (Review Focus 4): Dirección = parte de QEP con `FirstName` = nombre, `LastName` = `""`, `Address1` = dirección, `Address2` = `""`, `City`/`ProvinceCode` desde `DescribeCityAsync` + `CodeFor`, `CountryCode` y `Company` del `ShippingAddress` del **último** `row.Payload` (`CO` y `null` si no hay) (Review Focus 3, D12); Nota = `order.BuyerNotes` con `SetNote: true`; Atributos = `QepAttributes ?? snapshot.Attributes`. `Updated` → `MarkSent`; `Failed` → `MarkFailed(code)`.
- Worker: copia de `ShopifyCityCorrectionWorker` con `BatchSize = 50`, `PollInterval = 10 s`, `MaxAttempts = 8`, `Leases = [10 s, 1 min, 5 min, 15 min, 30 min, 1 h, 2 h, 4 h]`; en el último intento un `Retry` o una excepción cierran con `MarkFailed("retries_exhausted")`. Usa el `HttpClient` `shopify.admin` y su handler sin redirecciones.

- [ ] **RED.**
  ```csharp
  // ShopifyOrderPushProcessorTests
  [Fact] public async Task APushOnlySendsTheGroupsItWasEnqueuedFor()        // Review Focus 4: Groups [ShippingAddress] → update.SetNote false, Attributes null
  [Fact] public async Task PushKeepsCompanyAndCountryFromTheLatestSnapshot() // Review Focus 3: payload con company "1036000111", country "CO" → Company "1036000111", CountryCode "CO"
  [Fact] public async Task TheCityGoesWithTheQepNameAndTheProvinceCode()    // ("MEDELLÍN", "ANTIOQUIA") → City "MEDELLÍN", ProvinceCode "ANT"
  [Fact] public async Task TheNameAndAddressGoInFirstNameAndAddress1()      // LastName "", Address2 ""
  [Fact] public async Task AnExpiredTokenRetriesWithoutCallingShopify()
  [Fact] public async Task AccessDeniedFailsWithItsCode()                   // push.Status Failed, FailureCode "access_denied"
  [Fact] public async Task AMissingConnectionFails()                        // "connection_missing"
  // ShopifyOrderUpdateClientTests (handler HTTP falso)
  [Fact] public async Task TheMutationCarriesOnlyTheGivenFields()           // sin nota → el cuerpo no tiene "note"
  [Fact] public async Task UserErrorsAreUserError()
  [Theory, InlineData(429), InlineData(500), InlineData(401)] public async Task TransientStatusesRetry(int status)
  [Fact] public async Task AccessDeniedIsFailed()
  // ColombiaProvinceCodesTests
  [Fact] public void CodeForIsTheInverseOfDepartmentNameFor()               // para cada código de All
  // ShopifySecretLeakTests
  [Fact] public async Task ThePushNeverLogsTheTokenOrTheUserErrorMessage()
  // ShopifyOrderPushWorkerTests (host con IShopifyOrderUpdater falso)
  [Fact] public async Task ThePushIsSentAndMarkedSent()
  [Fact] public async Task AfterTheLastAttemptThePushIsRetriesExhausted()
  ```
- [ ] Corre `dotnet build $SU` → `CS0246 IShopifyOrderUpdater`.
- [ ] **GREEN.** Implementa y registra el worker como hosted service y `IShopifyOrderUpdater` en Infrastructure.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyOrderPushProcessorTests|FullyQualifiedName~ShopifyOrderUpdateClientTests|FullyQualifiedName~ColombiaProvinceCodesTests"`; integración `dotnet test $SI --filter "FullyQualifiedName~ShopifyOrderPushWorkerTests|FullyQualifiedName~ThePushNeverLogsTheTokenOrTheUserErrorMessage" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): push order corrections to shopify with orderUpdate`

### Tarea 12: las correcciones de ciudad del sistema también viajan, y el eco

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyCityCorrections.cs` (`ShopifyCityCorrectionProcessor`)
- Test: `$SU/ShopifyCityCorrectionProcessorTests.cs`, `$SI/CitySynonymEndToEndTests.cs`, `$SI/ShopifyEditSyncFlowTests.cs`

**Interfaces — Consumes:** `ShopifyOrderPushes.EnqueueAsync`, `ShopifyOrderMirror.RecordProjection`, `ShopifyOrderProjection.Read/Write`. **Produces:** en el guardado final con éxito de un trabajo de destino `Order` (`Applied` o `AlreadyApplied`), en el mismo `SaveChangesAsync` de la huella: la fila del espejo (`job.MirrorOrderId ?? correction.MirrorOrderId`), si tiene `AppliedProjection`, la recibe con `ShippingParty = result.Party` (`RecordProjection`: es corrección del sistema, no del asesor); y se encola `[ShippingAddress]`. Un trabajo `Skipped` o de destino `Customer` no encola nada.

- [ ] **RED.**
  ```csharp
  // ShopifyCityCorrectionProcessorTests
  [Fact] public async Task AnAppliedOrderCorrectionMovesTheProjectionAndEnqueuesAPush()
  [Fact] public async Task ACustomerCorrectionEnqueuesNothing()
  [Fact] public async Task ASkippedJobEnqueuesNothing()       // shipping_changed (el asesor ya editó la parte) → 0 pushes; gana el asesor
  // CitySynonymEndToEndTests
  [Fact] public async Task ConfirmingASynonymPushesTheCityToShopify()   // shopify.order_pushes: 1 Pending con groups {ShippingAddress}
  // ShopifyEditSyncFlowTests
  [Fact] public async Task TheEchoOfAPushedCorrectionCreatesNoChange()
  // PUT corrections (ciudad Envigado) → orders/updated con city "ENVIGADO" y province_code "ANT" → 0 filas en order_edits; applied_payload adopta el snapshot
  ```
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyCityCorrectionProcessorTests"` → rojo (0 pushes).
- [ ] **GREEN.** Implementa.
- [ ] Corre el mismo filtro; integración `dotnet test $SI --filter "FullyQualifiedName~ConfirmingASynonymPushesTheCityToShopify|FullyQualifiedName~TheEchoOfAPushedCorrectionCreatesNoChange" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): push system city corrections to shopify`

### Tarea 13: documentación y cierre

**Files:**
- Modify: `README.md` § «Integración con Shopify»
- Modify: `CLAUDE.md` (gotchas)
- Modify: `docs/superpowers/specs/2026-11-14-shopify-sincronizar-ediciones-design.md` (Estado)

- [ ] README, en § «Integración con Shopify»: un apartado **«Ediciones del pedido»** después de «Flujo de un pedido» (qué se lee —cantidad vigente, `billing_address`—, versión aplicada y proyección, la tabla de estados, eco y conflicto con las decisiones D3–D9, `editSync`/`editPaymentStatuses`, las rutas del contrato con sus permisos y códigos); en «La bandeja», «Cambios por revisar» y `changesToReview`; en «Configuración», los dos campos y `write_orders`; en «Despliegue», el worker de envíos, las migraciones `AddStoreEditSync`, `AddOrderEdits`, `AddOrderPushes` y las verificaciones V-11, V-12 y V-13 pendientes con cómo cerrarlas.
- [ ] `CLAUDE.md`, gotchas nuevos (prescriptivos, con su prueba amarrada):
  - La corrección del asesor no mueve `applied_projection`; la corrección de ciudad del sistema sí (D3, spec §5): una corrección del asesor que la mueva deja que la siguiente edición de Shopify pise lo del asesor; lo amarran `ACorrectionDoesNotMoveTheProjection` y `AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict`. Una fila sin `applied_projection` (importada antes) toma la proyección del pedido actual (D6): lo amarra `AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict`.
  - `orderUpdate` reemplaza la dirección entera: `company` y `countryCode` salen del último snapshot; un envío sólo manda los grupos encolados (`PushKeepsCompanyAndCountryFromTheLatestSnapshot`, `APushOnlySendsTheGroupsItWasEnqueuedFor`).
  - `current_quantity` ausente = `quantity`, y el descuento va sobre la cantidad original (D2).
  - El `PUT …/corrections` enrola el `QuotationsDbContext` en la transacción de Shopify (`ShopifyOrderCorrectionWriter`): nada en ese scope vuelve a usarlo después del commit; un camino nuevo que corrija un pedido de Shopify y encole pasa por ahí.
  - Detección: el detector corre en `ShopifyOrderIngestion.ApplyAsync` antes de la reacción §6.3 y atrapa el `concurrency.conflict` de Quotations (el cambio queda `Pending`); uno nuevo que dispare ediciones fuera de la ingesta rompe la regla del «único camino».
  - Actualiza la entrada de `ShopifyOrderIngestion.ApplyAsync` (ahora también detecta ediciones) y la de `BuyerNotes` si la Tarea 2 la dejó corta.
- [ ] Spec: la línea de Estado pasa a «aprobado por el owner (2026-11-14); backend implementado en `feature/shopify-sincronizar-ediciones` (plan `2026-11-14-shopify-sincronizar-ediciones-backend.md`); V-11, V-12 y V-13 pendientes», y una sección corta «Decisiones de la implementación» que remite a la tabla D1–D18 del plan.
- [ ] Antes del merge (suite limitada a lo tocado, una corrida a la vez): `dotnet test $SU`; `dotnet test $SI -- xUnit.MaxParallelThreads=4`; `dotnet test $QU`; `dotnet test $QI -- xUnit.MaxParallelThreads=4`; `dotnet test $BU`; `dotnet test $AT`; `dotnet test $IU`; `dotnet test $II --filter "FullyQualifiedName~Shopify" -- xUnit.MaxParallelThreads=4`. Guarda la salida literal como evidencia.
- [ ] Commit: `docs(shopify): document two-way sync of order edits`
- [ ] **Fin del Bloque 3:** revisión del bloque y la revisión fuerte de toda la rama.

---

## Autorrevisión

**Cobertura del spec**

| Spec | Tarea |
| --- | --- |
| §1 cantidades vigentes, línea quitada, importación inicial con lo vigente | 1 |
| §1 `billing_address` y `billing_address.company` como fuente del documento | 1 |
| §1 atributos en el snapshot | ya existe (`Attributes`); 7 y 10 los comparan y editan |
| §2 `applied_payload`, `applied_projection`, base y proyección de una fila vieja (valores actuales del pedido, D6) | 6 (columnas, importación), 8 (D6) |
| §3 diff por grupo, conflicto, eco, un cambio por pedido (`Superseded`) | 7, 8 |
| §4 `editSync`, `editPaymentStatuses` | 4 |
| §4 tabla de estados, aplicar (`Order.ApplyExternalEdit`, historial, auditoría, 412) | 5, 8 |
| §4 ciudad y documento con las reglas de la importación; comprador informativo y `customer_changed`; `product_unmatched`; pedido sin parte | 8 |
| §4 `Blocked` aceptable al volver a ser editable, `Reviewed`, `Rejected` | 9 |
| §5 qué se edita en QEP (parte de envío, nota, atributos), sólo Shopify y editable | 10 |
| §5 notas internas y del cliente, sin backfill, regla de `CLAUDE.md` | 2 |
| §5 columna «Notas del cliente» oculta | 3 |
| §5 atributos en el módulo Shopify (`qep_attributes`) | 6, 10 |
| §5 correcciones del sistema viajan; el trabajo de sinónimos salta lo del asesor | 12 |
| §5 encolado en la misma transacción, worker `orderUpdate`, reintentos, `failed` con código | 10, 11 |
| §5 estado del envío en el detalle; eco sin aviso | 10 (`ShopifyOrderSyncDto`), 12 |
| §6 bandeja, contador, rutas de cambios | 9 |
| §6 pantalla de la tienda; detalle del pedido | 4, 10 |
| §7 permisos | Global Constraints; 4, 9, 10 |
| §8 pruebas | en cada tarea |
| §9 V-11, V-12, V-13 | 13 (README y Estado; se cierran a mano con la tienda de desarrollo) |
| Restricción `write_orders` en `Shopify:App:Scopes` | 4 |

**Huecos del spec (decisión pendiente del owner; el plan sigue con lo marcado):**

1. **Pedidos de recogida (D8).** El spec no dice qué pasa con una dirección nueva sobre un pedido de recogida ni con un cambio de recogida ↔ envío, que el pedido no admite. El plan los deja en conflicto.
2. **`Reviewed` (D9).** El spec no dice si revisar adopta el snapshot. El plan lo adopta, como rechazar.
3. **Rutas que la pantalla necesita y el spec no nombra (D11):** `GET /shopify/changes`, `GET /shopify/qep-orders/{qepOrderId}` y `PUT /shopify/orders/{id}/corrections` (un endpoint de Shopify en vez de tocar el `PUT` del pedido).
4. **«Notas del cliente» sin layout guardado (D13).** El spec la oculta en los layouts guardados; el plan la oculta también en la hoja por defecto, como las columnas anteriores.
5. **Estado de pago tras editar líneas (D18).** No se recalcula; el pago de un pedido de Shopify lo sigue llevando la plataforma.
6. **V-12 sin cerrar:** si `orderUpdate` conserva `zip` u otros campos que QEP no lee cuando se manda `shippingAddress`. El plan preserva `company` y `countryCode`; el resto se confirma en la prueba manual.

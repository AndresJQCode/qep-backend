# Pedidos del comprador: historial y anulación desde la tienda — diseño (backend)

Fecha: 2026-11-09 · Repo: `qep-backend` · Spec hermano: `qep-storefront/docs/superpowers/specs/2026-11-09-pedidos-del-comprador-design.md`.
Este documento manda el contrato HTTP.
Construye sobre `2026-11-09-pedidos-tienda-directos-design.md` (el checkout crea un `Order` real) y revierte el
no objetivo «historial de pedidos» de `2026-11-07-cuentas-de-comprador-design.md`.

Estado: decisiones del owner de §3 tomadas el 2026-11-09. Lo marcado **[decisión del diseñador]** se puede
discutir, pero no bloquea la construcción.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan
campos, estados, rutas, permisos ni códigos de error —lo que no existe en el código se marca como pendiente—;
ante discrepancia entre este documento y el código, gana el código y se corrige el documento.

## 1. Intención y porqué

Con el spec de pedidos directos, un checkout de `{slug}.lulocrm.com` crea un `Order` (`Source = Store`,
`PaymentPending`) y el visitante recibe su número. Si además tiene una cuenta de comprador (módulo `Buyers`),
hoy no puede volver a ver ese pedido ni arrepentirse: la única forma es escribirle al asesor. El owner decidió
que el comprador con sesión vea **sus pedidos en esa tienda** y pueda **anular los que siguen pendientes**.

El pedido no sabe hoy de qué cuenta de comprador vino: `Order` no tiene ningún campo que apunte a `Buyers`, y
`Buyers` no tiene `TenantId`. El trabajo central es crear ese vínculo sin cruzar módulos.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- `Order` gana `BuyerAccountId` (nullable), sellado al crear el pedido cuando llega una sesión válida, y por
  el alta diferida de `createAccount`.
- Rutas bajo `/api/v1/public/stores/{host}/buyer/orders`: listado paginado, detalle y anulación.
- La anulación del comprador reutiliza `Order.Cancel`, el historial y el evento `quotations.order.cancelled.v1`.
- El comprador ve sólo pedidos del tenant de la tienda donde inició sesión.

**No objetivos**

- Seguimiento de envío, pagos en línea, repetir pedido, editar un pedido.
- Backfill de pedidos anteriores (D3).
- Anular desde la tienda un pedido `Approved` o uno con pago registrado (D5): eso lo hace el asesor.
- Una cuenta de comprador asociada a `Customer`: siguen separadas, como en el spec de cuentas.

**Anti-objetivos**

- Que un comprador vea o anule un pedido de otro comprador, de otro tenant o uno que puso como invitado.
- Que la ruta revele si un `orderId` existe en otro tenant (siempre el mismo 404).
- Que `Modules.Store.*` o `Modules.Buyers.*` referencien `Modules.Quotations.*`, o Quotations a Buyers
  (`StoreLayerTests`, `BuyersLayerTests`, `QuotationsLayerTests` lo verifican).
- Que una falla al vincular la cuenta haga fallar el pedido.

## 3. Decisiones del owner (2026-11-09)

| # | Decisión |
| --- | --- |
| D1 | El comprador con sesión ve los pedidos que puso en esa tienda y puede anular los que siguen pendientes. |
| D2 | Construye sobre el spec de pedidos directos: lo que se lista es un `Order` real. No hay `StoreOrderRequest`. |
| D3 | Los pedidos de la tienda que existen hoy son datos de prueba: no hay backfill. Sólo aparecen los pedidos creados cuando ya existe el vínculo con la cuenta. |
| D4 | Este trabajo corre **en paralelo** con el de pedidos directos, en worktrees separados. Todo salvo el sellado de `BuyerAccountId` en el checkout (y su enlace con el alta diferida de `createAccount`) es independiente; ese paso se hace **al final**, después de que la rama de pedidos directos entre a `develop` y ésta se rebase sobre ella. Orden de merge: pedidos directos → esta feature, en los dos repos, backend primero. |
| D5 | El comprador puede anular sólo si `Order.Status == Pending` **y** `Order.PaymentStatus == PaymentPending` **y** el pedido no tiene ningún comprobante de pago registrado (`PaymentProofs` vacío). El tercer requisito no es redundante: el asesor puede registrar un comprobante por `POST …/orders/{id}/proofs` dejando `paymentStatus: PaymentPending` (por ejemplo, «recibido, aún verificando»), y ese pedido ya tiene dinero registrado. Un pedido pendiente con pago parcial o total, o con cualquier comprobante, ya no se anula desde la tienda: lo resuelve el asesor. |

## 4. Diseño del backend

### 4.1 Punto de partida verificado en el código

- `Order` (`Modules.Quotations.Domain/Order.cs`): `OrderNumber`, `Source`, `ExternalKey`, `Currency`, `Total`,
  `Status` (`OrderStatus`: `Pending`, `Approved`, `Cancelled`, `Invoiced`), `PaymentStatus` (`OrderPaymentStatus`:
  `FullPaymentReceived`, `PartialPaymentReceived`, `PaymentPending`), `CreatedAt`, `CancelledAt`,
  `CancelledBy` (`MemberId?`), `CancellationReason`, `Items` (`OrderItem`: `ProductName`, `Quantity`,
  `UnitPrice`, `Subtotal`, `TaxAmount`). **No existe ningún campo que apunte a una cuenta de comprador.**
- `Order.Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)` (`Order.cs:907`): rechaza
  `Invoiced` (`order.order.already_invoiced`) y `Cancelled` (`order.order.already_cancelled`); **no rechaza
  `Approved`** (la consola anula aprobados). `MemberId` es un `readonly record struct` no nulo: hoy no hay
  camino para un actor que no sea miembro.
- `CancelOrderHandler` (`Modules.Quotations.Application/CancelOrder.cs`): permiso `OrdersPermissions.OrderCancel`,
  `QuotationAdvisorResolver`, historial `OrderHistoryEntry.Create(…, cancelledBy, OrderChangeSummary.Cancelled(reason), now)`,
  auditoría `quotation.order.cancelled` con `executionContext.SubjectId`, `lifecycleEvents.PublishOrderCancelled(order, now)`.
  Atado a la consola (`IExecutionContext`): no sirve tal cual desde una ruta pública.
- `OrderHistoryEntry.MemberId` ya es `MemberId?` y `OrderResponseComposer.cs:64` tolera `null`
  (`entry.MemberId is { } member ? … : null`). El spec de pedidos directos (§3.1, §4.8) ya usa `null` para
  «Creado desde la tienda»; el builder verifica el renderizador de la consola una sola vez para los dos casos.
- `OrderChangeSummary.Cancelled(reason)` → `"Anulado: {reason}"` (`OrderChangeSummary.cs:80`).
- `IQuotationAuditPublisher.PublishByIntegration(tenantId, action, resourceId, outcome, occurredAt)` existe
  (`IQuotationAuditPublisher.cs:14`): es el actor «integración», sin usuario.
- Sesión de comprador: `BuyerSessions.RequireAsync(host, rawToken)` → `BuyerSession` con `AccountId` y
  `StoreHost`; cualquier sesión inválida es `RequestUnauthorizedException(store.buyer.session_invalid)` (401).
- `IBuyerStorefronts.ResolveAsync(host)` → `BuyerStorefront(Host, Name)` (`Ports.cs:113`), adaptador
  `Bootstrapper/BuyerStorefronts.cs` sobre `StorefrontResolver`. **No devuelve el `TenantId`**; hay que agregarlo.
- Rutas de comprador: `PublicBuyerEndpoints.cs`, grupo `/api/v1/public/stores/{host}/buyer` con `AllowAnonymous`,
  `StorefrontKeyFilter`, `RequestSizeLimitAttribute(65_536)`, rate limit `readPolicy`; token en
  `X-Qep-Buyer-Session`; `RequireVisitorIp` → `store.public.visitor_ip_required`. `BuyerNoStoreMiddleware`
  pone `Cache-Control: no-store` a todo `/buyer` y `/buyer/…`.
- Checkout: `SubmitOrderRequestHandler` recibe `command.BuyerSessionToken` (`PublicStoreEndpoints.cs:128`,
  header `StorefrontClient.BuyerSessionHeader`) y **sólo lo usa con `updateProfile`**
  (`IStoreBuyerAccounts.ReplaceProfileAsync`). Con `createAccount`, el evento
  `store.buyer-signup-requested.v1` lo consume `BuyerSignupRequestedWorker` (Buyers.Infrastructure), que llama
  `BuyerSignup.SignUpAsync(request, ct, beforeSave)`; `SignUpAsync` devuelve `Task` **sin el id de la cuenta**,
  y si el correo ya tiene cuenta verificada no crea nada (sólo el aviso «ya tienes cuenta»).
- Capas: `Modules.Buyers.Application/Infrastructure` no referencian ningún otro `Modules.*`
  (`BuyersLayerTests.ApplicationAndInfrastructureReferenceNoOtherBusinessModule`); `Modules.Buyers.Api` sólo
  puede ver `Modules.Store.Api`; `Modules.Quotations.Application` sólo Tenancy; ninguna capa de Store ve
  Quotations ni Buyers. Los cruces van por puertos declarados en el módulo que los necesita y adaptadores en
  `src/Bootstrapper` (`StoreBuyerAccounts.cs`, `StoreOrdersAdapter.cs`, `BuyerStorefronts.cs`).
- Mapeo de status (`src/Api/ApiExceptionHandler.cs`): `ResourceNotFoundException` → 404,
  `RequestUnauthorizedException` → 401, `ResourceConflictException` → 409, `DomainException` → 422.
- Índice de pedidos por clave: `(TenantId, Source, ExternalKey)` (`QuotationsDbContext.cs:645`).

### 4.2 El vínculo: `Order.BuyerAccountId` **[decisión del diseñador]**

- `Order` gana `Guid? BuyerAccountId { get; private set; }`, sin FK (los módulos no se cruzan por FK; Buyers
  además borra cuentas y el pedido no se toca, decisión 9 del spec de cuentas). Se guarda **sólo el id**: nunca
  el correo ni el nombre del comprador fuera del pedido.
- Dos formas de llegar:
  1. **En la creación.** `StoreOrderDraft` gana `Guid? BuyerAccountId`; `Order.CreateFromStore` lo recibe y lo
     asigna. Entra por el handler del checkout: si llega `X-Qep-Buyer-Session`, el handler resuelve la cuenta
     **antes** de crear el pedido por un método nuevo del puerto `IStoreBuyerAccounts`, p. ej.
     `Task<Guid?> ResolveAccountIdAsync(host, sessionToken, ct)` (adaptador en `StoreBuyerAccounts.cs` sobre
     `BuyerSessions.TryResolveAsync`). Una sesión que no sirve devuelve `null` y el pedido sigue como invitado
     (regla del spec de cuentas: la cuenta nunca hace fallar el pedido). Hoy el token sólo se mira con
     `updateProfile`; pasa a mirarse siempre. **Este punto depende del spec de pedidos directos (D4):** se
     implementa al final, sobre el handler ya reescrito.
  2. **En el alta diferida.** `BuyerSignupRequestedWorker` ya trabaja por `OrderId` (nombre nuevo del campo
     según el spec de pedidos directos §4.7; hoy `OrderRequestId`). Cuando `SignUpAsync` **crea o reactiva**
     una cuenta (ramas `account is null` y «sin verificar» de `SignUpOnceAsync`), el worker vincula el pedido.
     Para eso: `SignUpAsync` pasa a devolver el `Guid?` de la cuenta afectada (`null` en la rama «ya tienes
     cuenta», donde no se toca nada), y Buyers declara un puerto `IBuyerOrderLinker`
     (`Task LinkAsync(Guid tenantId, Guid orderId, Guid accountId, ct)`) cuyo adaptador en Bootstrapper llama a
     un servicio nuevo de Quotations.Application (`LinkOrderToBuyerAccount`: carga el pedido por tenant e id,
     asigna `BuyerAccountId` sólo si está vacío, guarda). El `TenantId` ya viaja en `BuyerSignupRequestedMessage`.
     El vínculo se hace **después** del `SaveChanges` del alta, fuera de su transacción: si falla, queda un
     `Warning` y el pedido sigue sin vínculo (no se reintenta el alta por eso) **[decisión del diseñador]**.
     Un pedido anulado o aprobado entre medias se vincula igual: el vínculo no cambia el estado.
     **También va en el paso posterior al rebase (D4)**: hoy `BuyerSignupRequestedMessage.OrderRequestId` es
     el id de una solicitud, no de un pedido, hasta que pedidos directos lo renombre a `OrderId`
     **[decisión del diseñador]**. Nota: `feat/store-orders-direct` en `qep-backend` tiene hoy sólo el commit
     de documentación; mientras tanto, las pruebas de integración de esta rama siembran pedidos por el camino
     existente (`StoreOrderGateway.CreateAsync` / «Tomar») y fijan `BuyerAccountId` directamente.
- Con sesión **y** `createAccount` a la vez, el validador del checkout ya decide hoy qué pasa; la regla acá es
  que (1) gana porque ocurre primero, y (2) no pisa un id ya puesto.
- Índice `IX_orders_tenant_buyer_created (TenantId, BuyerAccountId, CreatedAt)`, filtrado a
  `buyer_account_id IS NOT NULL` **[decisión del diseñador]**. Migración `AddOrderBuyerAccount` en Quotations
  (nombre al builder): `ALTER TABLE quotations.orders ADD buyer_account_id uuid NULL` más el índice.
- D3: no hay `UPDATE` histórico. Un pedido anterior al despliegue nunca aparece en el listado.

### 4.3 Dónde vive la lectura y la anulación

Las rutas van en `PublicBuyerEndpoints` (Buyers.Api), que ya tiene el grupo, el filtro, el límite y el header.
Buyers.Application no puede ver Quotations, así que declara el puerto `IBuyerOrders` (nombre al builder):

```text
Task<BuyerOrderPage>   ListAsync(Guid tenantId, Guid accountId, int page, int pageSize, ct)
Task<BuyerOrderDetail?> FindAsync(Guid tenantId, Guid accountId, Guid orderId, ct)
Task<BuyerOrderDetailDto?> CancelAsync(Guid tenantId, Guid accountId, Guid orderId, ct)
```

El adaptador `Bootstrapper/BuyerOrdersAdapter.cs` resuelve en un scope propio (como `StoreOrdersAdapter`) un
servicio nuevo de Quotations.Application, `BuyerOrderGateway`, hermano de `StoreOrderGateway`. Toda consulta
filtra por `TenantId` **y** `BuyerAccountId`: un pedido ajeno es simplemente «no encontrado».

Los handlers de Buyers (`ListBuyerOrdersQuery`, `GetBuyerOrderQuery`, `CancelBuyerOrderCommand`) hacen lo mismo
que `GetBuyerSessionHandler`: `IBuyerStorefronts.ResolveAsync(host)` → `BuyerSessions.RequireAsync(storefront.Host, token)`
→ puerto. Para eso `BuyerStorefront` gana `Guid TenantId` (el adaptador ya tiene `store.TenantId`).

### 4.4 Anulación por el comprador

- Sólo `OrderStatus.Pending`, `OrderPaymentStatus.PaymentPending` **y** sin comprobantes registrados (`PaymentProofs.Count == 0`) (D1, D5). `Approved` ya fue revisado
  por una persona y un pago registrado (`PartialPaymentReceived` / `FullPaymentReceived`) es dinero que el
  asesor tendría que devolver: ninguno de los dos se deshace desde la tienda (más estricto que `Order.Cancel`).
  La regla vive en el dominio (`CancelByBuyer`, abajo) y se expone como `Order.CanBeCancelledByBuyer` para
  que el gateway y el DTO la lean sin repetirla. Con `Approved`, `Cancelled`, `Invoiced` o cualquier
  `PaymentStatus` distinto de `PaymentPending`, o con un comprobante registrado, responde `409` (la `ResourceConflictException` la lanza el `BuyerOrderGateway` de Application) con un código nuevo
  `order.order.not_cancellable_by_buyer` (409) **[código nuevo; no existe hoy]**. No se reutiliza
  `order.order.not_pending`: hoy es `QuotationsDomainException` (422) y lo lanzan las ediciones; cambiarle el
  status tocaría la consola.
- Actor: el comprador, que no es miembro. `Order.Cancel` exige `MemberId`; se agrega
  `CancelByBuyer(Guid buyerAccountId, DateTimeOffset occurredAt)` **[decisión del diseñador]** que exige
  `CanBeCancelledByBuyer` (`Status == Pending && PaymentStatus == PaymentPending && PaymentProofs.Count == 0`; si no, el dominio lanza `QuotationsDomainException` con `order.order.not_cancellable_by_buyer` y Application lo traduce al 409 de
  arriba: el Domain no puede lanzar `ResourceConflictException`), deja `CancelledBy = null`, `CancelledAt`, `CancellationReason` con el texto fijo
  «Cancelado por el comprador desde la tienda» y sube `Version`. Exige `BuyerAccountId == buyerAccountId`
  (defensa en profundidad; el gateway ya filtró). Sin motivo libre: no hay campo de texto para el visitante.
- Historial: `OrderHistoryEntry.Create(…, OrderHistoryEventType.Cancelled, memberId: null,
  OrderChangeSummary.Cancelled(order.CancellationReason!), now)`. En la consola se lee «Anulado: Cancelado por
  el comprador desde la tienda», sin nombre de miembro (mismo caso que «Creado desde la tienda»).
- Auditoría: `PublishByIntegration(tenantId, "quotation.order.cancelled", orderId, "success", now)`.
- Evento: `lifecycleEvents.PublishOrderCancelled(order, now)` tal cual (Pipeline reabre el negocio si lo hubiera).
- Misma unidad de trabajo que `CancelOrderHandler`. Una aprobación o un comprobante concurrentes del asesor
  hacen que `QuotationsUnitOfWork.SaveChangesAsync` lance `RequestConcurrencyException`
  (`QuotationsUnitOfWork.cs:93`; 412 `concurrency.conflict` en la consola). El gateway **la atrapa, descarta lo
  pendiente, relee el pedido y responde el 409 `order.order.not_cancellable_by_buyer`**: para el comprador el
  resultado es el mismo («ya no se puede anular») y un 412 no tiene copy en el storefront **[decisión del
  diseñador: 409, no 412]**. Si al releer el pedido sigue anulable (una edición concurrente que no cambió
  estado ni pago), se reintenta una sola vez; si vuelve a perder, 409.

### 4.5 Contrato HTTP

Grupo existente `/api/v1/public/stores/{host}/buyer`, mismos filtros (`StorefrontKeyFilter`, `readPolicy`,
tope de cuerpo, `no-store`). Todas exigen `X-Qep-Visitor-Ip` (`RequireVisitorIp`) y `X-Qep-Buyer-Session`.

| Ruta | Respuesta | Errores |
| --- | --- | --- |
| `GET /orders?page=1&pageSize=20` | `200 BuyerOrderPageDto` | 401 `store.buyer.session_invalid`; 404 `store.public.not_found`; 422 `validation.failed` si `page < 1` o `pageSize` fuera de `1..50` |
| `GET /orders/{orderId:guid}` | `200 BuyerOrderDetailDto` | 401; 404 `store.public.not_found` (host); 404 `order.order.not_found` (pedido ajeno, de otro tenant o inexistente: un solo código) |
| `POST /orders/{orderId:guid}/cancel` | `200 BuyerOrderDetailDto` ya anulado | los del detalle; 409 `order.order.not_cancellable_by_buyer` (no `Pending`, pago registrado, o carrera perdida) |

`BuyerOrderPageDto(items, page, pageSize, total)`, más reciente primero (`CreatedAt DESC, OrderNumber DESC`).
`BuyerOrderSummaryDto(id, number, createdAt, status, paymentStatus, total, currency, lineCount, firstLineName, canCancel)`
(`canCancel` también en el ítem del listado, con la misma regla D5 que el detalle: la lista pinta el botón sin pedir el
detalle; **[decisión del diseñador, 2026-11-09]**)
con `status` y `paymentStatus` como los nombres del enum (`"Pending"`, `"PaymentPending"`), igual que `OrderDto`.
`BuyerOrderDetailDto` = summary + `lines[]` (`name`, `quantity`, `unitPrice`, `lineTotal = Subtotal + TaxAmount`),
`cancelledAt`, `canCancel` (= `Order.CanBeCancelledByBuyer`: `status == Pending && paymentStatus == PaymentPending`,
D5; el storefront no recalcula la regla, la lee). Sólo campos que existen en `Order`/`OrderItem`; nada del
cliente, del asesor ni de las partes. El detalle es parte del alcance: el storefront lo usa para la
confirmación de anulación **[decisión del diseñador]**. `POST …/cancel` es idempotente sólo en el sentido de
que un segundo intento responde 409: no hay reintento silencioso.

### 4.6 Pruebas (TDD)

Cada ítem va RED antes que GREEN, con la salida literal de las dos corridas en el plan.

Unitarias:
- `OrderTests` (Quotations.Domain): `CreateFromStore` guarda `BuyerAccountId`; `CancelByBuyer` desde
  `Pending` + `PaymentPending` deja `CancelledBy = null`, motivo fijo y `Version + 1`; desde
  `Approved`/`Cancelled`/`Invoiced` lanza; desde `Pending` con `PartialPaymentReceived` o
  `FullPaymentReceived` lanza (D5); con otro `buyerAccountId` lanza; `CanBeCancelledByBuyer` cubre la tabla
  completa de estado × pago.
- `BuyerOrderGatewayTests` (Quotations.Application): lista sólo `(tenant, cuenta)`; orden; detalle ajeno →
  `null`; `canCancel` falso con pago registrado; cancel de `Approved` y de `Pending` con pago →
  `ResourceConflictException` con el código nuevo; `RequestConcurrencyException` al guardar → relee y 409;
  historial con `memberId = null`; `PublishByIntegration` y `PublishOrderCancelled` llamados una vez;
  `LinkOrderToBuyerAccount` no pisa un id.
- Buyers.Application: los tres handlers exigen sesión del host (`session_invalid`) y pasan `TenantId` del
  storefront; `BuyerSignup.SignUpAsync` devuelve el id en las ramas que crean o reactivan, `null` en «ya tienes
  cuenta»; `BuyerSignupRequestedWorkerTests`: llama `IBuyerOrderLinker` con `(TenantId, OrderId, accountId)` y
  una falla del linker no revierte el alta.
- Store.Application (al final, D4): el handler del checkout resuelve la cuenta con sesión válida y la pone en
  el draft; sesión inválida → `null` y el pedido sigue.
Integración (Testcontainers; un proyecto a la vez, `-- xUnit.MaxParallelThreads=4`):
- `PublicBuyerOrdersApiTests` (en `Modules.Store.IntegrationTests`, que es quien tiene la API real con la tienda): 401 sin token; 404 `order.order.not_found` para pedido de otra cuenta y
  para pedido del mismo comprador en otro tenant; listado paginado; cancel → 200 con `status = "Cancelled"`,
  fila `order_history` con `member_id NULL`, evento `quotations.order.cancelled.v1` en el outbox; segundo
  cancel → 409; pedido `Pending` con comprobante (`POST …/proofs` de la consola) → 409 y sin cambios. Hasta el
  rebase (D4) los pedidos se siembran con `StoreOrderGateway.CreateAsync` y `BuyerAccountId` fijado directamente.
- `OrdersMigrationTests` (Quotations.IntegrationTests): columna `buyer_account_id` e índice; la migración aplica sobre la anterior.
- `ArchitectureTests`: `BuyersLayerTests` y `QuotationsLayerTests` siguen en verde (Buyers no gana referencias
  a Quotations ni al revés); agregar una aserción nombrada de que `Modules.Buyers.*` no referencia
  `Modules.Quotations.*` **[decisión del diseñador]**.
- `ApiExceptionHandler`/`StoreErrorMappingApiTests`: `order.order.not_cancellable_by_buyer` → 409.

### 4.7 Despliegue

Backend primero (trae la migración de Quotations). Orden de merge (D4): pedidos directos → esta feature, en los
dos repos. Esta rama se construye en su propio worktree con la columna y la migración de §4.2, más §4.3, §4.4 y
§4.5; **los dos caminos del vínculo** —el sellado en el checkout, §4.2(1), y el enlace del alta diferida,
§4.2(2)— se escriben después del rebase sobre `develop` con pedidos directos ya mergeado, porque tocan el
handler que ese spec reescribe y el mensaje cuyo campo renombra. El storefront viejo ignora las rutas nuevas. `docs/estado.md` se actualiza en
`develop` al mergear, no en la rama.

## 5. Storefront (resumen; detalle en el spec hermano)

Página `/cuenta/pedidos` con guard de sesión, enlace «Mis pedidos» en el header, BFF
`GET /api/cuenta/pedidos` y `POST /api/cuenta/pedidos/[id]/cancelar` sobre el patrón de `eliminar.ts`, isla de
anulación con confirmación (botón sólo con `canCancel`), mapeo de los dos códigos nuevos en
`errors.ts`/`copy.ts`, stub de e2e. Además, `src/pages/api/order-requests.ts` pasa a reenviar
`X-Qep-Buyer-Session` siempre que haya cookie (hoy sólo con `updateProfile`), para que el sellado de §4.2(1)
reciba el token.

## 6. Qué queda al builder

- Nombres de puertos, adaptadores, gateway, DTOs, handlers y migración.
- Si `SignUpAsync` devuelve `Guid?` o un record con la rama tomada; si el linker corre en el worker o en
  `BuyerSignup` por callback.
- Forma exacta de `CancelByBuyer` (sobrecarga vs. método aparte) y dónde vive el texto fijo del motivo.
- Pasar el `TenantId` en `BuyerStorefront` o en un segundo método del puerto.
- Confirmar con una corrida que la consola pinta el historial con `member_id NULL` (una sola vez, junto con
  «Creado desde la tienda» del spec de pedidos directos).

## 7. Riesgos

- **R1 — Pedido sin vínculo.** Si el alta diferida crea la cuenta pero el linker falla, el pedido queda como
  invitado para siempre (no hay reproceso). Aceptado: queda un `Warning` con `OrderId`; el asesor lo ve igual.
- **R2 — Correo con cuenta verificada en `createAccount`.** Por decisión 6 del spec de cuentas no se toca esa
  cuenta, así que el pedido **no** se vincula: el comprador debió iniciar sesión antes del checkout. Es
  coherente con no vincular un pedido a una cuenta cuya contraseña no se demostró.
- **R3 — Carrera con el asesor.** Aprobar, registrar un pago y anular a la vez: `xmin` decide; el comprador
  que pierde recibe 409 (la `RequestConcurrencyException` se traduce en el gateway, §4.4) y la consola su
  `412 concurrency.conflict` de siempre.
- **R4 — Rebase sobre pedidos directos (D4).** El handler del checkout y `StoreOrderDraft` cambian en los dos
  specs; el sellado se escribe después del rebase para no resolver dos veces el mismo conflicto.
- **R5 — Cuenta eliminada.** `BuyerAccountId` queda apuntando a una cuenta que ya no existe; nadie puede
  iniciar sesión con ella, así que nada se expone. Es sólo un id huérfano, sin dato personal.

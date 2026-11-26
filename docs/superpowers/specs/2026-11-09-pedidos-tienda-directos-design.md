# Pedidos de la tienda directos, sin bandeja intermedia — diseño (backend)

Fecha: 2026-11-09 · Repo: `qep-backend` · Specs hermanos: `qep-frontend/docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`
y `qep-storefront/docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`. Este documento manda el contrato HTTP.
Reemplaza §4.4, §5.1 y §5.2 del spec `2026-11-03-tienda-publica-design.md`.

Estado: revisado con el owner el 2026-11-09; sin decisiones pendientes. Las decisiones de §3 son del owner.
Lo marcado **[decisión del diseñador]** se tomó al escribir o al cerrar la revisión (§3.1) y se puede
discutir, pero no bloquea la construcción.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan
campos, estados, rutas, permisos ni códigos de error —lo que no existe en el código se marca como pendiente—;
ante discrepancia entre este documento y el código, gana el código y se corrige el documento.

## 1. Intención y porqué

Hoy un visitante de `{slug}.lulocrm.com` no crea un pedido: crea una **solicitud** (`store.order_requests`,
numerada `T-0001`) que espera en la bandeja «Desde la tienda» (`/orders/store`) hasta que un asesor la
**toma**. «Tomar» (`TakeOrderRequestHandler`) resuelve el cliente, completa su libreta de direcciones y recién
entonces crea el `Order` con `OrderSource.Store` y `PaymentPending`.

Ese paso intermedio no agrega valor: el asesor siempre termina tomando la solicitud, y mientras tanto el pedido
no existe para el resto del sistema (listado, reportes, mensajería). El owner decidió que el checkout de la
tienda **cree el pedido real de inmediato**, con el mismo resultado que hoy da «Tomar», y que la bandeja, sus
tablas, sus endpoints y su numeración desaparezcan.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- `POST /api/v1/public/stores/{host}/order-requests` crea un `Order` (`Source = Store`, `PaymentPending`) y
  devuelve su número real al visitante.
- El cliente del pedido se resuelve por tipo y número de documento con las reglas que hoy aplica «Tomar»,
  sin intervención humana y sin pisar datos existentes.
- El pedido nace con asesor: el configurado en `/settings/store` o, si no hay, el owner del tenant.
- Borrar la bandeja completa: entidad, tablas, endpoints, handlers, DTOs, UI y numeración `T-xxxx`.
- El reintento del endpoint público sigue siendo idempotente.

**No objetivos**

- Un estado «borrador» en `Order`. El owner lo consideró y lo rechazó (§3, D1).
- Cambiar el motor de precios, el flujo del pedido después de creado, el editor de pedidos o el envío por
  WhatsApp.
- Notificar al asesor del pedido nuevo (sigue sin push, como el resto de listados).
- Migrar las solicitudes existentes: se descartan (§3, D6).

**Anti-objetivos**

- Que la tienda deje un pedido a medias (sin líneas, sin cliente o sin cuenta de cobro).
- Que el endpoint público rechace un pedido por falta de asesor.
- Que un dato del cliente que ya existía se sobrescriba con lo que escribió el visitante.
- Que `Modules.Store.Application` referencie Customers, Quotations, Geography o Buyers
  (`StoreLayerTests` lo verifica hoy y lo sigue verificando).

## 3. Decisiones del owner (2026-11-09)

| # | Decisión |
| --- | --- |
| D1 | **Se elimina la bandeja intermedia por completo.** Un checkout crea un `Order` real directamente (`OrderSource.Store`, `OrderPaymentStatus.PaymentPending`), con la misma forma que hoy produce «Tomar». No se agrega un estado «borrador» a `Order`: sería la bandeja con otro nombre y toca a todos los consumidores de `Order`. |
| D2 | **Asesor.** Un ajuste nuevo en `/settings/store`: «asesor asignado a los pedidos de la tienda» (una membresía del tenant). Si no está configurado, se usa la membresía owner del tenant (`Tenant.OwnerMembershipId`). La tienda nunca rechaza un pedido por falta de asesor. |
| D3 | **Cliente.** Se resuelve por tipo y número de identificación. Si existe, se usa; si está incompleto, se completa con los datos del pedido. Si no existe, se crea; si Customers rechaza el perfil completo, se crea **incompleto** (patrón Shopify, `ShopifyCustomerResolver.CreateAsync`) y el pedido se crea igual. Direcciones: si la de facturación o la de envío difiere de las que el cliente ya tiene, se agrega una nueva (comportamiento de `EnsureAddressesAsync`). Nunca se sobrescriben datos existentes del cliente. |
| D4 | **Falla.** Si el pedido genuinamente no se puede crear (la tienda ya no vende la moneda, variante inactiva o inexistente), el endpoint público devuelve un error y el checkout se lo muestra al visitante como cualquier otro error de validación del carrito. No se persiste nada a medias. |
| D5 | **Spam.** Consecuencia aceptada: un pedido basura se anula como cualquier pedido. |
| D6 | **Migración.** Las filas de `store.order_requests` se descartan; ningún tenant usa la tienda en producción. Se borran las tablas, la entidad, los endpoints, la UI y la numeración `T-xxxx`. La confirmación del visitante muestra el número real del pedido. |
| D7 | **Idempotencia.** Se conserva la idempotencia por `ExternalKey` para los reintentos del endpoint público. Clave fijada: `store-checkout:{clientRequestId}` (§4.2). |
| D8 | **Cliente inactivo o fusionado con el documento del pedido** (revisión 2026-11-09): se crea un cliente incompleto nuevo, sin documento, y el pedido sigue (comportamiento de Shopify). El asesor lo fusiona después. |
| D9 | **Consentimiento del invitado** (revisión 2026-11-09): se conserva en una tabla mínima `store.order_consents` con clave por id de pedido, escrita en la misma transacción de Store que la auditoría. |

### 3.1 Decisiones cerradas tras revisión (2026-11-09)

Dudas abiertas del borrador que quedan cerradas como **decisión del diseñador**:

- **Atomicidad entre módulos (R1):** no hay transacción ambiental entre Store, Customers y Quotations. Ante
  una falla parcial el comportamiento es el mismo que hoy tiene «Tomar»: un cliente o una dirección creados
  antes de una falla de infraestructura al crear el pedido se quedan; el reintento los reutiliza por documento
  y por contenido, así que no se duplican.
- **Clave de idempotencia (D7):** `store-checkout:{clientRequestId}`, sin alternativas.
- **`Order.ExternalReference`:** `null` para los pedidos de la tienda. La consola pinta `OrderSourceBadge`
  como `${label} ${externalReference}`, así que un UUID ahí se vería en todos los listados. El
  `clientRequestId` vive sólo en `ExternalKey`.
- **Historial del pedido:** la entrada `Created` lleva `memberId = null` (sistema), coherente con la
  auditoría anónima de Store. El builder verifica que el renderizador del historial de la consola tolere
  `null`.
- **Tope diario:** `CountCreatedSinceAsync` cuenta **todos** los pedidos `Source = Store` del tenant en la
  ventana, anulados incluidos.
- **Teléfono de la parte de facturación:** se queda crudo, tal como lo escribió el visitante; no se expone
  E.164 en este cambio. Seguimiento si «Escribir por WhatsApp» falla con números sin normalizar (spec de
  `qep-frontend`, §4.4).

## 4. Diseño del backend

### 4.1 Punto de partida verificado en el código

- Endpoint público: `src/Modules/Store/Modules.Store.Api/PublicStoreEndpoints.cs`, grupo
  `/api/v1/public/stores/{host}` con `StorefrontKeyFilter`, `AllowAnonymous`, `RequestSizeLimitAttribute` y
  rate limit; `MapPost("/order-requests", SubmitOrderRequestAsync)` responde `201` con
  `PublicOrderRequestReceiptDto(Id, Number, Total, Currency, BuyerSessionRejected)`. La IP del visitante
  sale de `StorefrontClient.TryGetVisitorIp`; sin ella, `store.public.visitor_ip_required`.
- Handler: `Modules.Store.Application/SubmitOrderRequest.cs`. Orden actual: resolver tienda → validar cuerpo
  (`PublicOrderRequestBodyValidator`) → `RequireCurrency` → consentimiento → **reintento por
  `ClientRequestId`** (`FindByClientRequestIdAsync`) → lugares y teléfonos → Turnstile → tope diario
  (`CountReceivedSinceAsync`, `StoreOptions.DailyRequestCap = 500` en 24 h) → preciar
  (`PublicCartPricing.PriceWithOriginsAsync`) → transacción: consecutivo `T-xxxx`
  (`IStoreRequestNumberGenerator`), `StoreOrderRequest.Create`, auditoría anónima
  `store.order_request.received`, y el evento `store.buyer-signup-requested.v1` al outbox cuando viene
  `createAccount`. Después del commit, `updateProfile` por `IStoreBuyerAccounts.ReplaceProfileAsync`.
- Entidad: `Modules.Store.Domain/StoreOrderRequest.cs` (+ `StoreOrderRequestLine`, `StoreOrderRequestStatus`
  `New/Converted/Discarded`, `StoreDiscardReason`, `StoreConsent`, `StoreRequestBilling`,
  `StoreRequestShipping`, `StorePlace`). Tablas en `StoreDbContext.cs`: `store.order_requests`,
  `store.order_request_lines`, `store.request_counters` (el consecutivo).
- Bandeja: `Modules.Store.Api/StoreEndpoints.cs`, grupo `/api/v1/tenants/{tenantId}/store/order-requests`
  con `GET /` (vista `Untaken|TakenByMe|All`), `GET /summary`, `GET /{requestId}`,
  `GET /{requestId}/customer-candidates`, `POST /{requestId}/take`, `POST /{requestId}/discard`. Handlers
  `ListOrderRequests.cs`, `GetOrderRequestSummary.cs`, `GetOrderRequest.cs`, `GetCustomerCandidates.cs`,
  `TakeOrderRequest.cs`, `DiscardOrderRequest.cs`, `OrderRequestRowComposer.cs`, `StoreInboxDtos.cs`.
- Lo que «Tomar» hace hoy y **se reutiliza tal cual** (`TakeOrderRequest.cs`):
  - `store.BillingAccountFor(request.Currency)` → sin cuenta, `store.order_request.currency_not_in_store`.
  - `IStoreCustomerDirectory.CreateAsync(tenantId, actorUserId, StoreNewCustomer)`: crea completo; si el
    documento ya era de otro cliente devuelve ése con `Created = false`. `CompleteAsync` llena sólo lo que
    está en blanco (`CustomerProvisioning.CompleteFromStoreAsync`); documento ajeno → 409
    `store.order_request.customer_document_taken`. `EnsureAddressesAsync` es idempotente: reutiliza o agrega
    (`Reused | Added`). El adaptador es `src/Bootstrapper/StoreCustomerDirectory.cs`.
  - `StoreOrderDraft` → `IStoreOrders.CreateAsync` (`src/Bootstrapper/StoreOrdersAdapter.cs`, un scope de DI
    por llamada) → `StoreOrderGateway.CreateAsync` (`Modules.Quotations.Application/IStoreOrderGateway.cs`):
    `FindByExternalKeyAsync(tenantId, OrderSource.Store, key)`, `OrderCustomerEligibility.Ensure(…,
    allowIncomplete: true)` (**un cliente incompleto ya es válido para un pedido de la tienda**),
    `KeyedOrderCommit.CommitAsync` con `Order.CreateFromStore(…)`, historial
    `OrderChangeSummary.CreatedFromStore(requestNumber)`, auditoría `quotation.order.created` firmada por
    `TakerUserId`, y `lifecycleEvents.PublishOrderCreated`. La clave hoy es
    `StoreOrderGateway.ExternalKeyFor(requestId) = "store-request:{requestId}"`; el índice es
    `(TenantId, Source, ExternalKey)` (`QuotationsDbContext.cs:645`).
  - `Order.CreateFromStore` (`Modules.Quotations.Domain/Order.cs:474`) exige `requestNumber` no vacío y lo
    guarda en `ExternalReference`; `ShippingDetails.None`, `PaymentPending`, sin comprobantes.
- Patrón Shopify de referencia: `Modules.Shopify.Application/ShopifyCustomerResolver.cs` →
  `CreateCompleteAsync` y, ante `ShopifyCustomerNotCompletableException`, `CreateIncompleteAsync(tenantId,
  name, phone, email, country)`. `ICustomerProvisioning.CreateIncompleteAsync` existe con esa firma
  (`Modules.Customers.Application/CustomerProvisioning.cs:75`); su documentación dice literalmente que el
  llamador «cae a `CreateIncompleteAsync`» cuando el alta completa falla por `ValidationException` o por
  `customers.customer.classification_not_found` / `customers.customer.city_not_found`.
- Owner del tenant: `Tenant.OwnerMembershipId` (`Modules.Tenancy.Domain/Tenant.cs:112`) y
  `Tenant.IsOwner(membershipId)`. Ningún puerto de `Modules.Tenancy.Application` lo expone hoy
  (`ITenantDirectory`, `IMembershipDirectory` no lo tienen); `StoreMemberDirectory.cs` (Bootstrapper) ya lee
  `TenancyDbContext.Memberships` directamente.
- Store ↔ otros módulos, hoy: `OrderStoreRequestLookup.cs` (Bootstrapper) alimenta
  `OrderDetailResponse.StoreRequest` (`OrdersDtos.cs:310-323`) con el teléfono, correo y total mostrado de
  la solicitud; `CatalogProductDeletionCleanup.cs` borra líneas de `store.order_request_lines` por SQL;
  Buyers consume `store.buyer-signup-requested.v1` idempotente por `OrderRequestId`
  (`BuyerSignupRequestedWorker.cs`, tabla `buyers.processed_signups`); `BuyerParityTests` ata el payload.
- Códigos `store.*` en `Modules.Store.Domain/StoreErrorCodes.cs`; el status lo decide
  `src/Api/ApiExceptionHandler.cs` por tipo (`DomainException` → 422, `ResourceConflictException` → 409,
  `RequestRateLimitedException` → 429).

### 4.2 Contrato HTTP

**Ruta y cuerpo: sin cambios.** `POST /api/v1/public/stores/{host}/order-requests` conserva nombre, cuerpo
(`PublicOrderRequestBody`: `clientRequestId`, `turnstileToken`, `currency`, `billing`, `shipping`, `note`,
`acceptsDataPolicy`, `lines`, `createAccount`, `updateProfile`), filtros y rate limit. Renombrar la ruta a
`/orders` sería más honesto, pero obliga a desplegar storefront y backend a la vez; se deja como está
**[decisión del diseñador]**.

**Respuesta.** `201` con `PublicOrderRequestReceiptDto`, misma forma, semántica nueva:

| Campo | Antes | Ahora |
| --- | --- | --- |
| `id` | id de la solicitud | **id del `Order`** |
| `number` | `T-0001` | **`Order.OrderNumber`** (el consecutivo de pedidos del tenant, con su formato de numeración) |
| `total` | total de la solicitud (cotización anónima) | **`Order.Total`** (ver riesgo R2) |
| `currency` | moneda de la solicitud | moneda del pedido (la de la cuenta de cobro) |
| `buyerSessionRejected` | igual | igual |

El `201` se devuelve también en el reintento idempotente (hoy ya es así: «el visitante no necesita
distinguirlos»).

**Idempotencia (D7).** La clave del pedido es `"store-checkout:{clientRequestId}"`, con el
`clientRequestId` que el storefront ya genera y ata al contenido del formulario
(`qep-storefront/src/features/checkout/client-request-id.ts`: UUID por `crypto.randomUUID()`, rota si cambia
el pedido). Razones: ya existe, ya viaja, ya está validado (`NotEqual(Guid.Empty)`), y el índice
`(TenantId, Source, ExternalKey)` lo hace único por tenant sin tabla nueva. Un hash del cuerpo no sirve:
el token de Turnstile cambia en cada intento. El prefijo cambia de `store-request:` a `store-checkout:` para
que un builder que lea la base no confunda una clave con el id de una solicitud borrada **[decisión del
diseñador, cerrada]**. `ExternalKeyFor` pasa a recibir el `clientRequestId`, que no va a ningún otro campo
del pedido (`ExternalReference` queda `null`, §3.1).

### 4.3 Flujo del handler, paso a paso

`SubmitOrderRequestHandler` se reescribe (mismo nombre o `CreateStoreOrderHandler`; el nombre queda al
builder). Todo lo que puede rechazar va **antes** de la primera escritura, como hoy:

1. `StorefrontResolver.ResolveAsync(host)` → tienda publicada y tenant activo, o `store.public.not_found`.
2. `PublicOrderRequestBodyValidator` → 422 `validation.failed` con el mapa `errors`.
3. `StorefrontResolver.RequireCurrency(store, body.Currency)` → `store.public.currency_invalid`.
4. `acceptsDataPolicy` → `store.order_request.consent_required`.
5. **Reintento**: `IStoreOrders.FindExistingAsync(tenantId, externalKey)` (hoy recibe `requestId`; pasa a
   recibir la clave o el `clientRequestId`). Si hay pedido, se devuelve su recibo y termina: sin captcha, sin
   cuenta, sin perfil, igual que hoy.
6. Lugares (`BuildPlaceAsync`: `country_not_sold`, `city_invalid`) y teléfonos (`phone_invalid`), sin cambios.
7. Turnstile → `store.order_request.captcha_invalid` / `store.captcha.unavailable`, sin cambios.
8. **Tope diario.** `CountReceivedSinceAsync` contaba solicitudes; esa tabla desaparece. Se cuenta sobre
   pedidos `Source = Store` del tenant en la ventana, con un método nuevo en `IStoreOrders`
   (p. ej. `CountCreatedSinceAsync(tenantId, since)`), contando **todos** los pedidos `Store` de la ventana,
   anulados incluidos: un pedido basura anulado sigue siendo un envío que la tienda recibió
   **[decisión del diseñador]**. Mismo código `store.order_request.daily_cap` (429).
9. **Cuenta de cobro**: `store.BillingAccountFor(currency)`. Sin cuenta →
   `store.order_request.currency_not_in_store` (422). Es el caso «la tienda ya no vende la moneda» de D4;
   el visitante lo ve como un error del carrito.
10. **Preciar**: `PublicCartPricing.PriceWithOriginsAsync`; una línea no disponible →
    `store.order_request.line_unavailable` (422). Es el caso «variante inactiva o inexistente» de D4. Esta
    cotización ya no se persiste: sólo decide si se sigue (y sirve para el consentimiento, §4.6).
11. **Asesor**: `ResolveAdvisorAsync(store)` (§4.4). Nunca falla por falta de asesor (D2).
12. **Cliente** (§4.5): existente → completar si incompleto; inexistente → crear completo o, si Customers lo
    rechaza, incompleto. Después, `EnsureAddressesAsync` con la dirección de facturación y, si vino distinta,
    la de envío (misma construcción que `TakeOrderRequestHandler.AddressInputs`).
13. **Pedido**: `StoreOrderDraft` → `IStoreOrders.CreateAsync`. El draft pierde `RequestId` y
    `RequestNumber` y gana la clave (o el `clientRequestId`); `AdvisorMembershipId` es el de (11);
    `TakerUserId` deja de existir (§4.8). `IsRetail = store.PricingMode == RetailOnly`, partes y líneas como
    hoy (`BillingParty`, `ShippingParty`, `StoreOrderLineInput`).
14. **Después del pedido**, en una transacción de Store: auditoría anónima (§4.8), la fila de
    `store.order_consents` (§4.6) y el evento `store.buyer-signup-requested.v1` si vino `createAccount`
    (§4.7). `updateProfile` fuera de la transacción, como hoy.
15. Recibo con `OrderId`, `OrderNumber`, `Total`, `Currency`.

Si el adaptador de pedidos pierde la carrera de la clave (dos envíos simultáneos con el mismo
`clientRequestId`), `StoreOrderGateway.CreateAsync` ya devuelve el ganador con `AlreadyExisted = true`; el
handler responde ese recibo.

### 4.4 Asesor: ajuste nuevo y fallback al owner

**Entidad.** `OnlineStore` gana `Guid? AdvisorMembershipId` (nullable). Entra por `StoreSettingsInput`
(`AdvisorMembershipId`), se asigna en `Configure`, participa en `StateKey()` (para que un cambio suba
`Version` y audite) y sale en `StoreSettingsDto` como `advisorMembershipId` **más** el nombre visible para
pintarlo (`advisorName`, resuelto por `IStoreMemberDirectory.NamesAsync`, que ya existe)
**[decisión del diseñador]**. `StoreSettingsRequest` gana `Guid? AdvisorMembershipId`. No es bloqueo de
publicación (`PublishBlockers` no cambia): sin ajuste, hay owner.

**Validación al guardar** (`UpdateStoreSettingsHandler`): si viene, tiene que ser una membresía **activa del
tenant**. Se verifica por un método nuevo del puerto `IStoreMemberDirectory`
(p. ej. `IsActiveMemberAsync(tenantId, membershipId)`); si no lo es, 422 con un código nuevo
`store.settings.advisor_not_found` **[código nuevo; no existe hoy]**. No se exige un permiso concreto al
asesor elegido: el pedido lo crea el sistema, no esa persona **[decisión del diseñador]**.

**Resolución al crear el pedido** (`ResolveAdvisorAsync`):

1. Si `store.AdvisorMembershipId` tiene valor **y** esa membresía sigue activa en el tenant → ésa.
2. Si no (nunca se configuró, o la persona fue suspendida o quitada después) → la membresía owner del
   tenant, por un método nuevo del mismo puerto (p. ej. `FindOwnerMembershipIdAsync(tenantId)`), cuyo
   adaptador en Bootstrapper lee `Tenant.OwnerMembershipId` de `TenancyDbContext`. El owner no se puede quitar
   ni suspender mientras sea owner (`Membership.cs:524, 605`), así que este paso siempre devuelve una
   membresía. Cuando se cae al owner por un asesor configurado que ya no está activo, se deja un `Warning`
   en el log (sin datos del visitante) **[decisión del diseñador]**.

Nada de esto referencia `Modules.Tenancy.Domain` desde `Modules.Store.Application`: el puerto vive en Store
y el adaptador en Bootstrapper, como `StoreMemberDirectory` hoy.

**Migración** (`AddStoreAdvisor`, nombre al builder): `ALTER TABLE store.stores ADD advisor_membership_id uuid
NULL`. Sin FK a `tenancy` (los módulos no se cruzan por FK; la coherencia la da la validación al guardar y el
fallback al crear).

### 4.5 Cliente: resolución sin intervención humana (D3)

Con el bloque de facturación del cuerpo (`StoreNewCustomer`, construido como en
`TakeOrderRequestHandler.BillingData`, con `ClassificationId = null`):

1. **Buscar por documento.** `IStoreCustomerDirectory` gana `FindByDocumentAsync(tenantId, type, number)`
   (hoy sólo hay `FindCandidatesAsync` por teléfono **o** documento, pensado para el diálogo). El adaptador
   reutiliza la búsqueda por documento que ya usa `StoreCustomerDirectory.FindCandidatesAsync` (a lo sumo un
   cliente activo y sin fusionar por documento). El teléfono **no** decide: un teléfono compartido entre dos
   personas no puede colgar el pedido del cliente equivocado sin que nadie lo mire **[decisión del
   diseñador]**.
2. **Existe y está completo** → se usa. No se toca ningún dato maestro.
3. **Existe e incompleto** → `CompleteAsync` (sólo llena lo que está en blanco). Si Customers responde que no
   se puede completar (p. ej. ciudad DIVIPOLA que ya no existe), **el pedido se crea igual con el cliente
   incompleto**: `OrderCustomerEligibility.Ensure(…, allowIncomplete: true)` ya lo permite. El 409
   `customer_document_taken` de `CompleteAsync` no puede darse acá porque el cliente se encontró por ese
   mismo documento; si se diera por una carrera, se relee por documento y se usa el ganador.
4. **No existe** → `CreateAsync` (completo, con `actorUserId = null`: la bitácora lo registra como
   integración, igual que Shopify; `source = "store"`). Si Customers lo rechaza por los motivos documentados
   en `ICustomerProvisioning.CreateCompleteAsync` (`ValidationException`, `classification_not_found`,
   `city_not_found`) → `CreateIncompleteAsync(tenantId, name, phone, email, country)` y el pedido sigue
   (patrón `ShopifyCustomerResolver`). Esto exige que `IStoreCustomerDirectory` gane
   `CreateIncompleteAsync` o que `CreateAsync` devuelva el incompleto; el adaptador decide qué excepciones
   de Customers significan «no completable» y cuáles son transitorias (se propagan y el visitante recibe
   `store.unavailable`). Si `CreateAsync` devuelve `Created = false` (otro alta ganó el documento entre el
   paso 1 y éste), se sigue con ése, completándolo si está incompleto, como hoy.
5. **Cliente inactivo o fusionado con ese documento** (D8, decisión del owner). Hoy `CreateAsync` responde
   `store.order_request.customer_not_found` (422) y «Tomar» se lo muestra al asesor. Desde la tienda no hay
   quién lo resuelva, así que se crea un cliente **incompleto nuevo, sin documento**
   (`CreateIncompleteAsync` con nombre, teléfono, correo y país) y el pedido sigue; el asesor lo fusiona
   después con la ficha existente. Es lo que hace Shopify y respeta D2/D4. El código
   `store.order_request.customer_not_found` deja de usarse en el camino público.
6. **Libreta.** `EnsureAddressesAsync(customerId, [facturación, envío si vino])`. Idempotente y
   nunca pisa: sólo agrega lo que no está. Para un cliente incompleto, Customers decide si acepta direcciones
   (hoy «Tomar» sólo las agrega después de completar; `Customer.cs:700-707` no edita un incompleto por
   partes). Si Customers las rechaza para un incompleto, se registra y el pedido sigue: la dirección de
   envío ya viaja en la parte `Shipping` del pedido, así que nada se pierde **[decisión del diseñador]**.
7. `WithRetention` y `VatSurplus` del cliente resuelto van a la parte de facturación, como hoy.

El actor humano desaparece: `IStoreCustomerDirectory.CreateAsync/CompleteAsync` reciben `Guid? actorUserId`
(`null` desde la tienda). `ICustomerProvisioning.CreateCompleteAsync` ya lo acepta así (`Guid? actorUserId =
null`); `CompleteFromStoreAsync` hoy exige `Guid actorUserId` (`CustomerProvisioning.cs:140`) y pasa a
`Guid?`, con la misma regla que el alta: sin actor, la bitácora lo registra como integración.

### 4.6 Consentimiento de datos

Hoy `StoreConsent(now, visitorIp, store.DataPolicyUrl, store.DataPolicySetAt)` vive en la fila de la
solicitud. Al borrarla, el consentimiento del comprador **invitado** no tendría dónde quedar (el de una
cuenta sí: `buyers.consents`). **Decisión del owner (D9):** se conserva en una tabla mínima
`store.order_consents (order_id PK, tenant_id, accepted_at, visitor_ip, policy_url NULL, policy_set_at NULL)`,
escrita en la misma transacción de Store que la auditoría del pedido (paso 14 de §4.3), después de que
Quotations confirmó el pedido. `StoreConsent` se conserva como value object de esa fila. La tabla no se lee
desde ninguna pantalla en este cambio; el plazo de conservación sigue pendiente, como en el spec de cuentas
de comprador. Va en la misma migración que agrega `advisor_membership_id` o en una propia (al builder).

### 4.7 Cuenta de comprador y perfil

- `StoreBuyerSignupRequestedPayload.OrderRequestId` → `OrderId` (y `BuyerSignupRequestedMessage`,
  `BuyerInboxMessage.OrderRequestId`, `BuyerProcessedSignup.OrderRequestId`). La idempotencia del worker
  pasa a ser por `OrderId`. Columna `buyers.processed_signups.order_request_id` → `order_id` con migración en
  Buyers **[decisión del diseñador; el builder puede conservar el nombre físico si prefiere no migrar, pero
  entonces el campo del record igual se renombra]**. `BuyerParityTests` se ajusta.
- El evento sigue saliendo en la **misma transacción de Store** que la auditoría del pedido, **después** de
  que `IStoreOrders.CreateAsync` confirmó el pedido. Ventana: si Store falla al confirmar su transacción
  después de que Quotations confirmó la suya, el pedido existe y la cuenta no se crea; el visitante ve un
  error y reintenta; el reintento encuentra el pedido por la clave y **hoy no vuelve a emitir el alta**
  (paso 5 de §4.3). Es la misma ventana que hoy existe entre pedido y solicitud en «Tomar», ahora en el
  camino público. Se acepta y se documenta (R4); el builder puede emitir el evento en el reintento si el
  pedido existe y la cuenta no, pero no es obligatorio en este cambio **[decisión del diseñador]**.
- `updateProfile` no cambia.

### 4.8 Auditoría, historial y eventos

- Store: `StoreAuditActions.RequestReceived` (`store.order_request.received`, recurso `order_request`) se
  reemplaza por `store.order.created` sobre el recurso `order` con el `OrderId`, publicado con
  `PublishAnonymous` (no hay actor) **[acción nueva]**. `RequestConverted` y `RequestDiscarded` se borran.
- Quotations: `StoreOrderGateway.CreateAsync` hoy audita `quotation.order.created` con `draft.TakerUserId`
  como actor. Sin tomador, el actor es integración: `IQuotationAuditPublisher.PublishByIntegration` ya
  existe (`IQuotationAuditPublisher.cs:14`) y es lo que usa el gateway desde la tienda. El historial
  `OrderChangeSummary.CreatedFromStore(requestNumber)` pierde el número de solicitud; su texto pasa a algo
  como «Creado desde la tienda» (copy exacta al builder). La entrada `OrderHistoryEntry.Create(…)` lleva
  `memberId = null` (sistema): hoy el gateway pasa `advisor`, pero el asesor no hizo nada. El compositor ya
  tolera `null` (`entry.MemberId is { } member ? … : null`, `OrderResponseComposer.cs:64`); el builder
  verifica el renderizador de la consola **[decisión del diseñador]**.
- `Order.CreateFromStore`: `requestNumber` deja de existir y `ExternalReference` queda **`null`**
  (`NormalizeExternalReference` admite null). No se guarda ahí el `clientRequestId`: la consola pinta
  `OrderSourceBadge` como `${label} ${externalReference}` y un UUID aparecería en cada listado. Se quita
  `ArgumentException.ThrowIfNullOrWhiteSpace(requestNumber)`. `OrderStoreFactoryTests` se ajusta.
- `lifecycleEvents.PublishOrderCreated` sigue igual: el pedido entra a reportes y mensajería como cualquiera.

### 4.9 Detalle del pedido (`OrderDetailResponse.StoreRequest`)

`OrderStoreRequestResponse` y `IOrderStoreRequestLookup` / `OrderStoreRequestLookup` se eliminan. Lo que la
consola usaba de ahí ya está en el pedido: nombre, teléfono y correo en la parte `Billing`; el total, en el
pedido. Lo único que se pierde es `TotalDifference` (total mostrado al visitante vs. total del pedido); ver
R2. El frontend adapta «Escribir por WhatsApp» del detalle (spec hermano).

### 4.10 Eliminaciones

Store (Domain): `StoreOrderRequest`, `StoreOrderRequestLine`, `StoreOrderRequestStatus`,
`StoreDiscardReason`. `StoreConsent` se conserva para `store.order_consents` (§4.6). Se conservan `StoreRequestBilling`,
`StoreRequestShipping`, `StorePlace` si el handler nuevo los sigue usando para construir partes y
direcciones; el builder decide si los aplana.
Store (Application): `IStoreOrderRequestRepository`, `IStoreRequestNumberGenerator`, `ListOrderRequests`,
`GetOrderRequestSummary`, `GetOrderRequest`, `GetCustomerCandidates`, `TakeOrderRequest`,
`DiscardOrderRequest`, `OrderRequestRowComposer`, `StoreInboxDtos`, `StoreReadModels` (lo que sea de la
bandeja), los códigos `not_found`, `not_new`, `being_taken`, `conflict`, `duplicate_client_request`,
`customer_not_found` (D8 lo deja sin uso), `customer_document_taken` de `StoreErrorCodes` si quedan sin uso, y los
métodos de `IStoreCustomerDirectory` que sólo servían al diálogo (`FindCandidatesAsync`, `KnownAsync`,
`FindEligibleAsync`) si nadie más los usa.
Store (Infrastructure): `StoreOrderRequestRepository`, `StoreRequestNumberGenerator`, configuración de las
tres tablas en `StoreDbContext`, y la migración `DropStoreOrderRequests` (nombre al builder) que hace
`DROP TABLE store.order_request_lines, store.order_requests, store.request_counters` (D6: sin copia) y crea
`store.order_consents` (D9).
Store (Api): el grupo `inbox` de `StoreEndpoints.cs` completo. `StorePermissions.OrderManage`
(`quotations.order.manage`, copia del de Quotations para la bandeja) y `StoreAuthorization.EnsureAsync` con
ese permiso quedan sin uso y se borran; `SettingsManage` se conserva.
Bootstrapper: `OrderStoreRequestLookup`, registros DI de los handlers borrados
(`QepServiceCollectionExtensions.cs:1384-1431`), y el bloque de `store.order_request_lines` en
`CatalogProductDeletionCleanup.cs` (el pedido ya es un `Order`, que `DeletedVariantLineStore` ya cubre).
Quotations: `IOrderStoreRequestLookup`, `OrderStoreRequestRef`, `OrderStoreRequestResponse`,
`OrderDetailResponse.StoreRequest`.
`StoreOptions.ConversionLease` se borra (era el lease de «Tomar»); `DailyRequestCap` se conserva.

### 4.11 Códigos de error

Existen y se conservan en el endpoint público: `validation.failed`, `store.public.not_found`,
`store.public.currency_invalid`, `store.public.visitor_ip_required`, `store.public.body_too_large`,
`store.public.body_invalid`, `store.order_request.consent_required`, `captcha_invalid`, `country_not_sold`,
`city_invalid`, `document_type_not_allowed`, `phone_invalid`, `line_unavailable`, `daily_cap`,
`store.captcha.unavailable`, y pasa a ser público `store.order_request.currency_not_in_store` (hoy sólo lo
veía el asesor). El storefront ya mapea todos menos `currency_not_in_store` (spec hermano).

Nuevos: `store.settings.advisor_not_found` (422, §4.4). Ninguno más.
No se cambia el prefijo `store.order_request.*` de los códigos existentes aunque ya no haya «solicitud»:
renombrarlos obliga a desplegar storefront y backend juntos **[decisión del diseñador]**.

Los errores del motor de pedidos (`quotation.item.product_inactive`, `product_price_unavailable`,
`order.billing.account_required`) **no** llegan al visitante: `StoreOrdersAdapter.CreateAsync` hoy los deja
subir porque los leía un asesor; pasa a traducirlos como `QuoteAsync` (`store.order_request.line_unavailable`
con el original en el log) o a dejar que caigan en el 503 genérico. Lo primero es lo coherente con D4
**[decisión del diseñador]**.

### 4.12 Pruebas (TDD)

Cada ítem va RED antes que GREEN, con la salida literal de las dos corridas en el plan.

Unitarias (Store):
- `OnlineStoreTests`: `AdvisorMembershipId` entra en `Configure`, cambia `StateKey` y sube `Version`.
- Handler del checkout (reemplaza `SubmitOrderRequestValidatorTests` + nuevas): orden de rechazos antes de
  escribir; reintento por clave devuelve el recibo del pedido sin captcha; asesor configurado activo →
  ése; configurado inactivo → owner con warning; sin configurar → owner; cliente existente completo no se
  toca; incompleto se completa; no existe → completo; Customers rechaza → incompleto y el pedido se crea;
  direcciones: facturación y envío distinta → dos entradas; igual → una; `currency_not_in_store` y
  `line_unavailable` antes de tocar cliente; documento de un cliente inactivo/fusionado → incompleto nuevo
  sin documento y pedido creado (D8); tope diario sobre pedidos `Store`, anulados incluidos; la fila de
  `store.order_consents` lleva fecha, IP y la política vigente (D9).
- `UpdateStoreSettings`: `advisor_not_found` con membresía inactiva o ajena.
Integración (Store, Testcontainers; un proyecto a la vez, `-- xUnit.MaxParallelThreads=4`):
- `PublicOrderRequestApiTests` reescrita: `201` con número de pedido real; el pedido existe en
  `quotations.orders` con `source = Store`, `PaymentPending`, asesor y cliente esperados; reintento con el
  mismo `clientRequestId` devuelve el mismo `id`; `createAccount` emite el evento con `OrderId`; moneda sin
  cuenta → 422 `currency_not_in_store`; variante inactiva → 422 `line_unavailable` y **cero filas nuevas**
  en `customers.customers` y `quotations.orders`.
  Nota de implementación (2026-11-10): el caso «moneda sin cuenta → 422 `currency_not_in_store`» es inalcanzable por
  la ruta pública, porque `RequireCurrency` rechaza antes la moneda que la tienda no vende; la comprobación queda como
  defensa y sólo la cubre la fila existente de `StoreErrorMappingApiTests`.
- `StoreSettingsApiTests`: guardar y leer `advisorMembershipId` + `advisorName`.
- `StoreInboxApiTests`, `StoreTakeApiTests`, `StoreInboxRig`, `StoreOrderDetailApiTests` (la parte de
  `storeRequest`), `DiscardOrderRequest*Tests`, `StoreInboxFilterTests`, `StoreOrderRequestTests`,
  `ProductDeletionStoreCleanupTests`: se borran o se reducen a lo que sobrevive.
- `StorePersistenceTests`: el modelo no tiene las tres tablas; la migración aplica sobre una base con la
  migración anterior.
Otros módulos:
- Quotations `StoreOrderGatewayTests` (unit e integración) y `OrderStoreFactoryTests`: clave por
  `clientRequestId`, `ExternalReference` nulo, `PublishByIntegration` en auditoría, historial `Created` con
  `memberId = null`.
- `StorePersistenceTests`: `store.order_consents` existe con su PK por `order_id`.
- Buyers `BuyerSignupRequestedWorkerTests` y Bootstrapper `BuyerParityTests`: `OrderId`.
- Bootstrapper `StoreOrdersAdapterTests`: traducción de errores del motor en `CreateAsync`;
  `StoreCustomerDirectoryDocumentTests`: `FindByDocumentAsync`, caída a incompleto.
- `ArchitectureTests/StoreLayerTests`: siguen en verde sin cambios (Store no gana referencias). Agregar una
  aserción de que `Modules.Store.Application` no referencia `Modules.Tenancy.Domain`
  (`Tenant.OwnerMembershipId` entra por puerto) **[decisión del diseñador]**.
- `StoreErrorMappingApiTests`: `advisor_not_found` → 422.

### 4.13 Despliegue

Backend primero (trae las migraciones de Store y Buyers). El storefront viejo sigue funcionando contra el
backend nuevo (misma ruta y misma forma del recibo; el `number` cambia de `T-0001` a un número de pedido,
que la confirmación muestra sin interpretarlo). El frontend viejo rompe en `/orders/store` (404 de los
endpoints de la bandeja) hasta que se despliegue el suyo: ventana aceptable porque no hay tenants en
producción (D6). `docs/estado.md` se actualiza en `develop` al mergear, no en la rama.

## 5. Frontend (resumen; detalle en el spec hermano de `qep-frontend`)

- `/settings/store`: sección nueva «Pedidos de la tienda» con el selector «Asesor asignado a los pedidos de
  la tienda» (membresías activas del tenant) y la leyenda de que sin selección los recibe el owner.
- Se borran la ruta `/orders/store`, `store-inbox-page`, `TakeStoreRequestDialog`,
  `DiscardStoreRequestDialog`, `store-inbox-table`, `store-request-card`, hooks y tipos de la bandeja, y el
  enlace «Desde la tienda» con su contador en `orders-list-page.tsx`.
- El detalle de un pedido `source = 'Store'` deja de leer `storeRequest`; «Escribir por WhatsApp» toma el
  cliente y el teléfono del pedido.

## 6. Storefront (resumen; detalle en el spec hermano de `qep-storefront`)

- `src/pages/api/order-requests.ts` y `submitOrderRequest` no cambian de contrato; el recibo trae el número
  del pedido.
- `confirmation.astro`: «Número de solicitud» → «Número de pedido» y la copy deja de hablar de solicitud.
- `copy.ts` gana `store.order_request.currency_not_in_store`.
- `e2e/support/stub-backend.ts`: `STUB_RECEIPT_NUMBER` deja de ser `T-0001`; `smoke.spec.ts` se ajusta.

## 7. Qué queda al builder

- Nombre del handler y de las migraciones; si `StoreRequestBilling`/`StoreRequestShipping`/`StorePlace`
  se conservan como value objects del handler o se aplanan.
- Forma exacta de los métodos nuevos de los puertos: `IStoreOrders.FindExistingAsync` por clave,
  `CountCreatedSinceAsync`; `IStoreMemberDirectory.IsActiveMemberAsync` y `FindOwnerMembershipIdAsync`;
  `IStoreCustomerDirectory.FindByDocumentAsync` y `CreateIncompleteAsync`.
- Qué excepciones de Customers se traducen a «no completable» en el adaptador (las tres documentadas en
  `ICustomerProvisioning.CreateCompleteAsync`) y cuáles se propagan.
- Texto del historial «Creado desde la tienda» y verificación de que la consola tolera `memberId = null`.
- Si el reintento idempotente vuelve a emitir el alta de cuenta cuando el pedido existe y la cuenta no (§4.7).
- Renombrar o no la columna física `buyers.processed_signups.order_request_id`.
- Si `store.order_consents` va en la misma migración que `advisor_membership_id` o en una propia.

## 8. Riesgos

- **R1 — Atomicidad entre módulos.** Store, Customers y Quotations tienen unidades de trabajo distintas y los
  adaptadores abren su propio scope. «No se persiste nada a medias» (D4) se cumple para todo lo que se
  valida antes de escribir (moneda, líneas, lugares, captcha) y para el pedido (o entra completo o no entra).
  Pero un cliente creado o una dirección agregada en el paso 12 se quedan si el paso 13 falla por una causa
  de infraestructura; el reintento los reutiliza por documento y por contenido, así que no hay duplicados
  (es exactamente lo que hoy documenta `TakeOrderRequestHandler`). **Cerrado (§3.1):** no se agrega una
  transacción ambiental entre contextos; se acepta el mismo comportamiento que «Tomar».
- **R2 — Total mostrado vs. total del pedido.** El carrito se precia para un comprador anónimo; el pedido,
  con el cliente real (retención, excedente de IVA). Hoy la diferencia quedaba en `TotalDifference` y el
  asesor la veía en la bandeja y en el detalle. Al desaparecer la solicitud, nadie la ve, y el recibo devuelve
  `Order.Total`, que puede no ser el total que el visitante acaba de ver. La confirmación del storefront hoy
  no muestra el total, así que no hay contradicción en pantalla; el asesor lo explica al contactar, como ya
  hacía. Alternativa: guardar la diferencia en el historial del pedido **[decisión del diseñador: no, en
  este cambio]**.
- **R3 — Spam genera pedidos y consume consecutivos** (D5, aceptado). Turnstile, rate limit y el tope diario
  siguen; un pedido basura se anula. Los números de pedido anulados quedan en la secuencia.
- **R4 — Pedido sin cuenta de comprador** cuando Store falla después de que Quotations confirmó (§4.7).
- **R5 — Documento de un cliente inactivo/fusionado** → incompleto nuevo sin documento (D8). Riesgo residual:
  dos fichas para la misma persona hasta que el asesor las fusione.
- **R6 — Consentimiento del invitado** → `store.order_consents` (D9). Riesgo residual: si Quotations confirmó
  y Store falló al escribir, el pedido queda sin fila de consentimiento (misma ventana que R4).
- **R7 — Asesor que pierde acceso.** El fallback al owner cubre el caso; los pedidos ya creados no cambian de
  asesor (como cualquier pedido).

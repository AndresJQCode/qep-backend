# Integración con Shopify: pedidos de la tienda en QEP

**Fecha:** 2026-10-15
**Módulos:** Shopify (nuevo: Domain, Application, Infrastructure, Api), Integrations (proveedor, OAuth e
instalación), Quotations (origen del pedido, servicio para pedidos externos, filtros), Customers (alta de
cliente desde el sistema), Tenancy (clave de módulo `shopify` y activación por instalación), Reporting
(excluir cotizaciones de origen externo), Api/Bootstrapper (webhook, adaptadores, políticas). **Frontend:**
`qep-frontend`, rutas públicas de instalación, `features/integrations`, `features/orders`, `features/quotes`.
**Estado:** alcance, enfoque y las cuatro secciones del diseño aprobados por el owner en conversación el
2026-10-15. Pendiente de su lectura final y del plan de implementación.
**Depende de:** el spec de **flete en cotizaciones** (pendiente de escribir). Sin él, los pedidos de Shopify
con costo de envío quedan en espera (§6.2, motivo `freight_unsupported`) y la integración no se entrega a un
tenant real.
**Ramas:** `feature/shopify-pedidos` desde `develop` en `qep-backend`; la homónima desde `develop` en
`qep-frontend`.

## 1. Contexto y alcance

Los tenants que venden por Shopify hoy copian a mano cada pedido de la tienda a QEP para que salga en el
Excel de pedidos que importa su ERP. El owner quiere que el pedido de Shopify **entre solo**, se liste junto a
los pedidos que nacen de una cotización, se apruebe, facture o anule desde la misma pantalla y se exporte
con las mismas columnas.

La integración nace **desde Shopify**: el comercio instala la app de QEP en su tienda, y al instalarla
enlaza la tienda con su cuenta de QEP, creándola si no la tiene. QEP recibe los pedidos por webhook, los
concilia por sondeo, los traduce a una cotización convertida más su pedido, y deja en una bandeja los que
no puede traducir todavía con el motivo y la forma de resolverlo.

**Dentro:**

- Proveedor `shopify` en Integrations con onboarding por instalación y OAuth (§4).
- Enlace tienda ↔ tenant, con registro de tenant nuevo cuando la persona no tiene cuenta (§4.3).
- Módulo `shopify` por tenant, activado al enlazar (§4.4).
- Webhook firmado, espejo de pedidos y conciliación periódica (§5).
- Traducción a `Quotation` + `Order` con cliente completo, SKU = código de producto, asesor y cuenta de cobro
  de la tienda (§6).
- Bandeja de pedidos en espera, con reintento manual y automático (§6.4, §8).
- Origen del pedido en la lista de pedidos y filtro; cotizaciones de origen externo fuera de la lista y de
  los reportes de cotizaciones por defecto (§6.5).
- Sincronización de estado de pago y de cancelación desde Shopify (§6.3).
- Webhooks de cumplimiento exigidos a una app pública (§5.5).

**Fuera** (§14): flete en cotizaciones (spec aparte), historial anterior al enlace, reembolsos y ediciones
de líneas, sincronización hacia Shopify (fulfillment, inventario), app embebida en el admin de Shopify,
WooCommerce u otras plataformas.

## 2. Hechos verificados en el código

Lo que el diseño asume, con el lugar donde se confirmó. Ante discrepancia manda el código.

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | Un `Order` es 1:1 con una `Quotation` y no repite cliente, productos ni totales; nace `Pending` con `OrderPaymentStatus` y comprobantes opcionales, y se anula desde `Pending` o `Approved`, nunca desde `Invoiced`. No tiene origen ni referencia externa. | `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:118-132,160` |
| H2 | Convertir exige `Draft` o `Sent`, al menos una línea, `ValidUntil`, cuenta de cobro y partes completas; se puede convertir desde `Draft` sin enviar. Antes, el handler revalida el cliente con `QuotationCustomerEligibility`, que rechaza `Incomplete` (`client_incomplete`), sin CUC e inactivo. | `Quotation.cs:853-940`; `src/Modules/Quotations/Modules.Quotations.Application/QuotationCustomerEligibility.cs`; `ConvertQuotationToOrder.cs:84` |
| H3 | `QuotationItem.UnitPrice` es **con IVA** (`UnitPriceWithoutTax = UnitPrice × 100 / (100 + TaxPercentage)`); el subtotal de la cotización es sin IVA y `Total = Subtotal + TaxAmount`. La cotización no tiene concepto de flete. | `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:113-142`; `Quotation.cs` (`RecalculateTotals`) |
| H4 | El asesor de una cotización es la membresía activa de quien la crea (`QuotationAdvisorResolver`), y los handlers autorizan contra `IExecutionContext` con tenant y permiso: ninguno sirve para un actor del sistema. | `QuotationAdvisorResolver.cs:17-30`; `QuotationsAuthorization.cs:11-21`; `CreateQuotation.cs` |
| H5 | `Order.PaymentStatus` sólo cambia por comprobantes (`RecalculatePaymentStatus(amountDue)`); no hay forma de fijarlo desde afuera. | `Order.cs:334` |
| H6 | La lista de pedidos filtra por cliente, asesor, estado, pago, fechas, CUC y número; la de cotizaciones por cliente, asesor, estado, fechas, NIT y número. Ninguna por origen. | `ListOrders.cs:14-30`; `ListQuotations.cs:7-19` |
| H7 | Un cliente completo exige nombre, tipo y número de documento (`NIT`, `CC`, `CE`, `PASAPORTE`), dirección, país, ciudad DIVIPOLA si es colombiano, y clasificación; el CUC lo emite `ICucGenerator` con el prefijo de la clasificación. `CreateCustomerHandler` autoriza por sesión. El único alta desde el sistema que existe crea clientes `Incomplete` (`CreateIncomplete`, para Messaging). | `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:9-80`; `Modules.Customers.Domain/IdentificationType.cs`; `Customer.cs:325-350` |
| H8 | Customers busca por documento con `FindExistingIdentificationsAsync(tenantId, [(Type, Number)])`. | `ICustomerRepository.cs:87-90` |
| H9 | Geography resuelve ciudad **por nombre dentro de un departamento** (`ICityRepository.FindByNameAsync(departmentId, name)`) y departamento por nombre (`IDepartmentRepository.FindByNameAsync`); `Department` tiene `DivipolaCode` y `Name`, no código ISO 3166-2. | `src/Modules/Geography/Modules.Geography.Application/ICityRepository.cs:25-34`; `IDepartmentRepository.cs:28`; `Modules.Geography.Domain/Department.cs:21-25` |
| H10 | Catalog busca un producto por código exacto: `IProductRepository.FindByCodeAsync(tenantId, code)`; Pos ya lo consume por `PosProductLookup` en Bootstrapper. | `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs:79`; `src/Bootstrapper/PosProductLookup.cs:22-26` |
| H11 | Integrations tiene catálogo en código (`IntegrationProviders.All`), categorías `Messaging`, `Shipping`, `Ai`, onboarding `Form`, `MetaEmbeddedSignup`, `InstagramLogin`, campos públicos, internos y secretos, tope por proveedor, `connection_routes` (`provider_key`, `external_id` único) para enrutar webhooks y los puertos `IIntegrationConnections` e `IConnectionHealthReporter`. Un proveedor nuevo es su entrada, su probador, sus pruebas y la migración del `CHECK`. | `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs`; `IntegrationCatalog.cs:6-30`; `IntegrationConnectionRoute.cs`; `Modules.Integrations.Application/IIntegrationConnections.cs` |
| H12 | El precedente de OAuth por redirección es Instagram Login: el SPA abre la URL de autorización con `state`, vuelve a `/settings/integrations/instagram/callback`, y `ConnectInstagramHandler` canjea el `code` con el secreto global `Meta:Instagram:AppSecret`, exige que el `redirectUri` sea de un origen permitido, crea la conexión y la ruta. Las opciones globales se validan con `ValidateOnStart` y en `Production` son obligatorias. | `ConnectInstagram.cs`; `Modules.Integrations.Infrastructure/Meta/MetaInstagramOptions.cs`; spec `2026-10-14-mensajeria-instagram-design.md` §8.1 |
| H13 | `ConnectionEvents` publica `connection-paused`, `connection-deleted` y `connection-needs-attention`; **no hay evento de conexión creada**. | `Modules.Integrations.Application/ConnectionEvents.cs:8-14` |
| H14 | El webhook de Meta es una ruta anónima bajo `/api/webhooks/`, con el limitador `webhook`, exenta de CSRF por prefijo, cuerpo acotado, firma HMAC verificada en el handler y encolado en `messaging.webhook_deliveries` (`ON CONFLICT (body_sha256) DO NOTHING`); un `BackgroundService` con leases y 8 intentos procesa las entregas. | `src/Modules/Messaging/Modules.Messaging.Api/WhatsAppWebhookEndpoints.cs`; `src/Bootstrapper/WebhookPaths.cs`; `Csrf/RequireCsrfHeaderMiddleware.cs:50`; `Modules.Messaging.Infrastructure/Persistence/WebhookDeliveries.cs`; `Webhook/WebhookDeliveryWorker.cs:23-42`; `src/Api/Program.cs:183-185` |
| H15 | Las claves de módulo son una lista cerrada con `CHECK` en la tabla; `DefaultForNewTenants` son seis; `pos`, `messaging` y `pipeline` se prenden por tenant con `ChangeTenantModulesHandler` (fuente y nota), que no publica eventos. El enforcement es `TenantModuleGuard.EnsureEnabledAsync` → 403 `tenancy.module_not_enabled`. | `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:35-58`; `Modules.Tenancy.Application/ChangeTenantModules.cs:43`; `TenantModuleGuard.cs:12-16` |
| H16 | El registro público de tenant es `POST /api/v1/auth/register-tenant` con bearer de Google, detrás de `Registration:PublicTenantSignupEnabled`; aprovisiona el owner, crea el tenant y deja la sesión emitida. `/register` es una ruta pública del SPA. `GET /auth/me` devuelve `activeTenantIds` (cero, uno o muchos). | `src/Api/RegistrationEndpoints.cs:33-125`; `qep-frontend` `src/routes/register.tsx`; `src/features/auth/services/auth.api.ts:42-47` (`develop`) |
| H17 | Pipeline consume `quotations.quotation.converted.v1` y, sin negocio abierto del cliente, **crea uno y lo gana** a nombre del asesor del evento, con actor del sistema. La auditoría de un worker va con `bySystem` y actor vacío. | `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs:110-175` |
| H18 | Un módulo consumidor nunca referencia `Modules.Integrations.Application`, `Modules.Customers.Application` ni `Modules.Catalog.Application`: declara su puerto y el adaptador vive en Bootstrapper (`MessagingCustomerDirectory`, `PosProductLookup`, `PipelineConversationDirectory`). | `src/Bootstrapper/*.cs`; `tests/ArchitectureTests/ArchitectureTests/*LayerTests.cs` |
| H19 | `AuditActorType.Integration` existe y lo usa `ConnectionHealthReporter`. | `Modules.Integrations.Application/IConnectionHealthReporter.cs` |

## 3. Decisiones del owner (2026-10-15)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿Qué hace el tenant con los pedidos de Shopify en QEP? | **Que se listen en Pedidos junto a los de cotización y se exporten** por el mismo Excel. |
| 2 | ¿Cómo se resuelve el cliente, si Shopify casi nunca trae documento? | **Cliente completo o pedido en espera.** QEP crea la ficha completa con el documento que traiga el checkout; sin documento, el pedido espera a que alguien complete al cliente en QEP. Se descartó consumidor final por defecto y el cliente genérico por tienda. |
| 3 | ¿Cómo se emparejan los productos? | **SKU de Shopify = `Product.Code` de QEP.** Un SKU desconocido deja el pedido en espera. Se descartó la tabla de homologación y el alta automática de productos. |
| 4 | ¿De dónde salen el asesor y la cuenta de cobro? | **Configurados por tienda** al enlazar. Si el asesor queda inactivo, los pedidos nuevos esperan. |
| 5 | ¿Qué pasa con el costo de envío? | **Agregar flete a la cotización**, en un spec aparte que se implementa antes. Mientras no esté, los pedidos con envío esperan (§6.2). Se descartó la línea «Envío» con un producto del catálogo y se descartó ignorarlo. |
| 6 | ¿Un spec o dos? | **Dos specs.** Éste declara el flete como dependencia; el de flete se diseña en otra sesión y se implementa primero. |
| 7 | ¿Cómo llegan los pedidos? | **Webhooks + conciliación periódica.** |
| 8 | ¿Cómo conecta el tenant su tienda? | **App pública de QCode con OAuth**, no app personalizada por tienda. Precisión del owner: la instalación empieza **en Shopify**; al instalar, la tienda se enlaza con la cuenta de QEP, y se crea la cuenta si no existe. |
| 9 | ¿Qué pedidos entran y qué se sincroniza después? | **Entran al crearse, con su estado de pago** (`PaymentPending`, `PartialPaymentReceived`, `FullPaymentReceived`); el pago se actualiza cuando Shopify avisa; la cancelación se refleja. |
| 10 | ¿Dónde se ven los pedidos en espera? | **Bandeja propia en Pedidos, con reintento automático** y botón «Reintentar». Permiso `quotations.order.manage`. |
| 11 | ¿Módulo vendible? | **Módulo `shopify` propio**, fuera de los seis por defecto, **prendido al instalar**; el operador puede apagarlo. |
| 12 | Enfoque de implementación | **A: módulo nuevo `Shopify`**; Integrations guarda la conexión y hace el OAuth; Quotations gana lo mínimo para aceptar un pedido externo. |

## 4. Instalación, OAuth y enlace con el tenant

### 4.1 Proveedor en Integrations

```text
IntegrationProviders.Shopify
  Key               "shopify"
  DisplayName       "Shopify"
  Category          Commerce                       (valor nuevo de IntegrationCategory)
  ConsumingModules  [TenantModuleKeys.Shopify]
  Onboarding        ShopifyInstall                 (valor nuevo de ProviderOnboarding)
  MaxConnections    5
  Fields
    shopDomain      Text, público, sólo lectura    "mi-tienda.myshopify.com"
    shopName        Text, público, sólo lectura    nombre de la tienda según Shopify
    shopId          Text, interno                  id numérico de la tienda
    scopes          Text, interno                  scopes concedidos, separados por coma
    accessToken     Secret, interno                token offline de la Admin API
```

- `connection_routes`: una fila `("shopify", shopDomain, accountId: null, tenantId, connectionId)`. El
  índice único de `external_id` por proveedor impide que una tienda quede enlazada a dos tenants
  (`integrations.shopify.shop_already_linked`) y es la clave con la que el webhook enruta por
  `X-Shopify-Shop-Domain`.
- Probador (`ShopifyConnectionTester`): `POST https://{shopDomain}/admin/api/{version}/graphql.json` con
  `{ shop { id name myshopifyDomain } }` y el header `X-Shopify-Access-Token`. 200 refresca `shopName`;
  401 y 402 (tienda congelada) dejan `credentials_rejected`; red o 5xx, `provider_unreachable`.
- Eliminar la conexión no llama a Shopify (la app sigue instalada en la tienda). Pausarla detiene la
  conciliación y la importación; los webhooks que lleguen mientras tanto se guardan en el espejo y se
  importan al reanudar.
- Desinstalar la app en Shopify llega como webhook `app/uninstalled` (§5.2): la conexión pasa a
  `NeedsAttention` con el código `app_uninstalled` (nuevo en `ConnectionFailureCodes`). Volver a instalar
  la app en esa tienda repite el flujo de §4.2 y, si el tenant es el mismo, **reutiliza** la conexión:
  token nuevo, `Active` otra vez; si es otro tenant, `shop_already_linked`.
- Spec 2026-10-08 D6: lo que es del consumidor no es campo de la conexión. Asesor, cuenta de cobro y
  demás ajustes de la tienda viven en `shopify.stores` (§6.1), no en `fields`.

### 4.2 Configuración global de la app

| Clave | Dónde | Qué |
| --- | --- | --- |
| `Shopify:App:ClientId` | ConfigMap | Client ID de la app en el Dev Dashboard de Shopify |
| `Shopify:App:ClientSecret` | Secret (`SHOPIFY_APP_CLIENT_SECRET`) | Firma los webhooks y canjea el `code` |
| `Shopify:App:ApiVersion` | ConfigMap, `2026-07` por defecto en `appsettings.json` | Versión de la Admin API; forma `^\d{4}-(01\|04\|07\|10)$` |
| `Shopify:App:Scopes` | ConfigMap, `read_orders,read_customers` por defecto | Lo que se pide en la autorización; §12 exige que lo concedido los contenga |
| `Shopify:App:AllowedRedirectOrigins:N` | ConfigMap | Orígenes permitidos para el `redirect_uri` del SPA, como `Meta:Instagram:AllowedRedirectOrigins` |
| `Shopify:Reconcile:IntervalMinutes` | ConfigMap, `10` | Periodo de la conciliación (§5.3) |
| `Shopify:Webhook:MaxBodyBytes` | `appsettings.json`, `1048576` | Tope del cuerpo del webhook |

`ShopifyAppOptionsValidator` con `ValidateOnStart`: en `Production` exige `ClientId`, `ClientSecret` y al
menos un origen; en cualquier ambiente valida la forma de `ApiVersion` y de los orígenes. Sin las claves,
el pod no arranca: a propósito, como con Meta.

### 4.3 Flujo de instalación y enlace

```text
Shopify ──(1)──► https://qep.qcode.co/shopify/install?shop=…&hmac=…&timestamp=…&host=…
SPA ─────(2)──► POST /api/v1/integrations/shopify/install/start { shop, hmac, timestamp, host, redirectUri }
                ◄── { authorizeUrl }
SPA ─────(3)──► window.location = https://{shop}/admin/oauth/authorize?client_id&scope&redirect_uri&state
Shopify ──────► https://qep.qcode.co/shopify/callback?code=…&shop=…&state=…&hmac=…&timestamp=…
SPA ─────(4)──► POST /api/v1/integrations/shopify/install/complete { code, shop, state, hmac, timestamp, redirectUri }
                ◄── { installId, shopName, shopDomain, linkedTenantId? }
SPA ─────(5)──► sin sesión: /login o /register con returnTo=/shopify/link/{installId}
                con sesión: /shopify/link/{installId}  (GET /api/v1/integrations/shopify/installs/{installId})
SPA ─────(6)──► POST /api/v1/tenants/{tenantId}/integrations/shopify/link { installId, name }
                ◄── 201 ConnectionResponse  →  /settings/integrations/shopify/{connectionId}
```

1. **App URL.** Shopify abre la URL de la app con `shop`, `hmac`, `timestamp` y `host`. Es una ruta
   **pública** del SPA (como `/register`); no exige sesión porque la persona puede no tener cuenta.
2. **Inicio.** `StartShopifyInstallHandler` (Integrations): verifica el HMAC de la query con el client
   secret (todos los parámetros menos `hmac`, ordenados, `k=v` unidos con `&`, HMAC-SHA256 en hex, comparación
   en tiempo constante); valida la forma del dominio (`^[a-z0-9][a-z0-9-]*\.myshopify\.com$`); exige que el
   `redirectUri` sea de un origen permitido (H12); genera un `state` aleatorio de 32 bytes; guarda una fila
   en `integrations.shopify_installs` (§9) que vence a los **15 minutos**; devuelve la URL de autorización
   con `client_id`, `scope`, `redirect_uri` y `state`. No se pide `grant_options[]=per-user`: el token tiene
   que ser **offline** para que los workers funcionen sin nadie conectado.
3. **Autorización.** Redirección de nivel superior (no popup, no iframe): la app no es embebida.
4. **Canje.** `CompleteShopifyInstallHandler`: verifica el HMAC de la query del callback, encuentra la fila
   por `state` (una sola vez: el `state` se consume), comprueba que `shop` coincide, canjea el `code` con
   `POST https://{shop}/admin/oauth/access_token { client_id, client_secret, code }`, exige que `scope`
   concedido contenga los configurados (`integrations.shopify.scopes_missing`), lee `shop { id name }` con el
   token, y guarda en la fila el token **cifrado** con `ISecretProtector` (AAD: `installId` + `accessToken`),
   `shopId`, `shopName` y `scopes`. La fila pasa a vencer a la **hora**: es el tiempo que tiene la persona
   para entrar o registrarse. Responde `installId`, `shopName`, `shopDomain` y, si la ruta
   `("shopify", shopDomain)` ya existe, `linkedTenantId` para que el SPA diga «esta tienda ya está enlazada
   a *Nombre del tenant*» y ofrezca ir a ese tenant.
5. **Sesión.** El SPA guarda `installId` en la ruta (`/shopify/link/$installId`) y, sin sesión, manda a
   `/login` o a `/register` con `returnTo`. El registro es el de hoy (H16); al terminar, vuelve al enlace.
   `GET /api/v1/integrations/shopify/installs/{installId}` (sólo autenticación) devuelve la tienda y los
   tenants activos del usuario donde tiene `integrations.connection.manage`, con nombre, para que elija.
   Nunca devuelve el token.
6. **Enlace.** `LinkShopifyStoreHandler` (`POST /api/v1/tenants/{tenantId}/integrations/shopify/link`),
   permiso `integrations.connection.manage`, en una transacción de Integrations:
   - encuentra la instalación (vigente, con token) o `integrations.shopify.install_expired`;
   - **activa el módulo** `shopify` en el tenant con el servicio nuevo de Tenancy
     `ITenantModuleActivation.EnableAsync(tenantId, TenantModuleKeys.Shopify, source: "shopify_install",
     note: shopDomain)`, que reutiliza la lógica de `ChangeTenantModulesHandler` (H15) sin autorización HTTP;
     si ya está activo, no hace nada;
   - comprueba la visibilidad del proveedor (ahora sí visible) y el tope de 5;
   - si existe una conexión `shopify` del **mismo tenant** con ese `shopDomain` (reinstalación), la
     reactiva con el token nuevo; si no, crea la conexión (nombre por defecto `shopName`, editable) y la ruta;
   - borra la fila de instalación; audita `onboarding:shopify_install`;
   - responde `201` con la conexión. El SPA navega a la configuración de la tienda (§8.2).

   Las dos unidades de trabajo (Tenancy e Integrations) no comparten transacción. Orden elegido: módulo
   primero, conexión después. Si falla la conexión, el tenant queda con el módulo prendido y sin tienda:
   es inocuo (el proveedor aparece en el catálogo con la instrucción de instalar) y la próxima instalación
   lo encuentra prendido. El orden inverso dejaría una conexión invisible.

### 4.4 Módulo `shopify` en Tenancy

- `TenantModuleKeys.Shopify = Define("shopify")`, último de `All`, fuera de `DefaultForNewTenants`, sin
  dependencias declaradas más allá de `orders` (sin pedidos no hay dónde listar). Migración del `CHECK`.
- `ITenantModuleActivation` es un servicio de `Modules.Tenancy.Application` nuevo, usado por Integrations
  (que ya referencia Tenancy.Application). Escribe `tenant_modules`, `TenantChange` y auditoría igual que el
  handler del operador, con `source = "shopify_install"` (cabe en `SourceMaxLength = 16`).
- El operador puede apagarlo desde la consola: la conexión queda oculta (regla del catálogo filtrado), los
  workers saltan la tienda y el webhook sigue guardando el espejo sin importar nada. Reactivarlo retoma.

## 5. Ingesta, espejo y conciliación

### 5.1 Webhook

`POST /api/webhooks/shopify` en `Modules.Shopify.Api`, mapeado desde `Program.cs` como los de Meta:
anónimo, `RateLimiterPolicies.Webhook`, exento de CSRF por el prefijo `/api/webhooks/`, cuerpo acotado por
`Shopify:Webhook:MaxBodyBytes` (413 si excede).

Headers que importan: `X-Shopify-Hmac-Sha256` (HMAC-SHA256 del cuerpo crudo con el client secret, en
**base64**), `X-Shopify-Topic`, `X-Shopify-Shop-Domain`, `X-Shopify-Webhook-Id`, `X-Shopify-API-Version`,
`X-Shopify-Event-Id`. Firma ausente o inválida → 401 sin encolar; el verificador compara en tiempo constante
y usa sólo `Shopify:App:ClientSecret`. Cuerpo válido → `INSERT … ON CONFLICT (webhook_id) DO NOTHING` en
`shopify.webhook_deliveries` y 200 **antes** de procesar: Shopify espera respuesta en 5 segundos y, sin
ella, reintenta hasta 19 veces en 48 horas y después puede retirar la suscripción.

Topics suscritos **a nivel de app** (los declara el owner en la configuración de la app; no hay registro por
tienda): `orders/create`, `orders/updated`, `orders/paid`, `orders/cancelled`, `app/uninstalled`,
`customers/data_request`, `customers/redact`, `shop/redact`.

### 5.2 Worker de entregas

`ShopifyWebhookDeliveryWorker` (`BackgroundService`), lotes de 50, leases y 8 intentos como
`WebhookDeliveryWorker` de Messaging (H14). Por entrega:

1. Resuelve la conexión por `shop_domain` con `IShopifyConnectionDirectory.FindByShopDomainAsync`
   (puerto del módulo; adaptador en Bootstrapper sobre `connection_routes` + `IIntegrationConnections`).
   Sin ruta → procesada e ignorada (`ignored_reason = "unknown_shop"`). Con ruta pero conexión `Paused` o
   `NeedsAttention` → **se aplica al espejo** igual (§5.4) y no se importa.
2. `app/uninstalled` → `IConnectionHealthReporter.ReportCredentialsRejectedAsync(…, "app_uninstalled")`.
3. `customers/data_request`, `customers/redact`, `shop/redact` → §5.5.
4. `orders/*` → upsert en el espejo (§5.4) y, si la conexión está `Active` y el módulo prendido, intento de
   importación (§6).

El payload de un webhook de pedidos es el REST del recurso `Order`; la conciliación trae el mismo pedido por
GraphQL. El módulo normaliza los dos a un solo modelo interno `ShopifyOrderSnapshot` (§6.2) con dos
parsers, cada uno con sus pruebas; el resto del módulo no sabe de dónde vino.

### 5.3 Conciliación

`ShopifyReconcileWorker` corre cada `Shopify:Reconcile:IntervalMinutes` sobre las conexiones `Active` de
tenants con el módulo prendido (`IShopifyConnectionDirectory.ListActiveAsync`). Por tienda:

- `orders(first: 50, after: cursor, sortKey: UPDATED_AT, query: "updated_at:>={desde}")`, con `desde` =
  `last_reconciled_at − 5 min` de `shopify.stores`. Al terminar, `last_reconciled_at` = ahora. Al enlazar,
  `last_reconciled_at` = momento del enlace: **no se trae historial** (D-S4).
- Cada pedido pasa por el mismo upsert del espejo que el webhook (§5.4) e intento de importación.
- Después, reintenta los `Waiting` de la tienda (§6.4), con un tope de 200 por ciclo, los más viejos primero.
- `THROTTLED` en GraphQL o 429: espera `Retry-After` (o 2 s) y sigue; tras 3 seguidos, la tienda se deja
  para el próximo ciclo. 401 o 402 → `ReportCredentialsRejectedAsync` con `credentials_rejected`; la tienda
  sale del ciclo hasta que la conexión vuelva a `Active` (reinstalación, §4.1).
- Un ciclo no corre dos veces en paralelo para la misma tienda: `shopify.stores.reconcile_lease_until`.

### 5.4 Espejo: `shopify.orders`

Una fila por pedido de Shopify y conexión, única por `(connection_id, shopify_order_id)`:

| Columna | Qué |
| --- | --- |
| `id`, `tenant_id`, `connection_id` | identidad |
| `shopify_order_id` (bigint), `order_name` (`#1001`) | el pedido en Shopify |
| `shopify_created_at`, `shopify_updated_at`, `cancelled_at`, `financial_status`, `currency`, `total_price`, `taxes_included` | lo que la bandeja muestra sin abrir el payload |
| `payload` (jsonb) | el último snapshot normalizado |
| `import_state` | `Waiting` · `Imported` · `Cancelled` · `Ignored` |
| `wait_reason`, `wait_detail` | §6.2; `wait_detail` es jsonb (lista de SKUs, diferencia de total, etc.) |
| `attempts`, `last_attempt_at`, `imported_at` | historia de la importación |
| `qep_quotation_id`, `qep_order_id` | referencia blanda al pedido de QEP |
| `total_difference` | `total_price − Total de QEP` tras importar (§6.2) |

Reglas del upsert:

- Un snapshot con `shopify_updated_at` **anterior** al guardado no pisa nada: los webhooks llegan
  desordenados y la conciliación trae lo mismo con retraso.
- Una fila `Imported` no vuelve a `Waiting`: los cambios posteriores sólo tocan pago y cancelación (§6.3).
- `Ignored` es para pedidos que QEP no va a traer nunca: `test = true` (pedidos de prueba de Shopify) y los
  cancelados antes de haberse importado. Se guardan igual para que la bandeja pueda mostrarlos con filtro.

### 5.5 Webhooks de cumplimiento

Una app pública **debe** responder 200 a los tres topics de privacidad, y Shopify los verifica en la
revisión. QEP no borra ni anonimiza clientes ni pedidos por un aviso de la plataforma: son registros
contables del tenant, el responsable del dato es el comercio, y QEP actúa como su encargado.

- `customers/data_request`: se audita (`shopify.compliance.data_request`, con `shop_domain`, id del cliente
  en Shopify y los ids de pedidos del aviso) para que el owner atienda la solicitud en el plazo de Shopify
  (30 días). No se envía nada automático.
- `customers/redact`: se audita igual (`shopify.compliance.customer_redact`). El tenant decide qué hacer con
  la ficha desde Customers.
- `shop/redact` (48 horas después de desinstalar): borra `shopify.orders` y `shopify.webhook_deliveries` de
  esa tienda; `shopify.stores` y la conexión quedan como estén. Los pedidos de QEP no se tocan.

## 6. De pedido de Shopify a pedido de QEP

### 6.1 Configuración de la tienda: `shopify.stores`

Una fila por conexión, creada por el `PUT` de la pantalla de configuración (§7) o, vacía, por el primer
intento de importación. Hasta que no tenga asesor y cuenta de cobro, todo pedido espera con
`store_not_configured`.

| Campo | Qué |
| --- | --- |
| `connection_id` (PK), `tenant_id` | identidad |
| `advisor_membership_id` | membresía **activa** del asesor de la tienda (`shopify.store.advisor_not_member` al guardar si no lo es) |
| `billing_company_id`, `billing_bank_name`, `billing_account_number`, `billing_currency` | la cuenta de cobro, copiada como hace `QuotationBillingAccount`; al guardar se valida contra Companies (`billing_account_not_found`) |
| `default_classification_id` | clasificación del cliente nuevo (`classification_not_found`) |
| `document_attribute_key`, `document_type_attribute_key` | claves en `customAttributes` del pedido; por defecto `Documento` y `Tipo de documento` |
| `default_identification_type` | `CC` por defecto; se usa cuando el pedido no trae tipo |
| `last_reconciled_at`, `reconcile_lease_until` | §5.3 |
| `version`, `updated_at`, `updated_by` | concurrencia optimista con `If-Match` |

### 6.2 Importación

`ShopifyOrderImporter` (Application del módulo) toma un `ShopifyOrderSnapshot`, lo evalúa y, si todo está,
llama a **un** método del puerto `IExternalOrderWriter.CreateAsync(ExternalOrderDraft)` cuyo adaptador en
Bootstrapper invoca un servicio de aplicación nuevo de Quotations (`ExternalOrderService`) que crea
cotización y pedido en **una transacción** del `QuotationsDbContext`. Ninguna de las dos partes pasa por los
handlers HTTP (H4): no hay sujeto con sesión. La auditoría de Quotations va con `AuditActorType.Integration`
(H19) y referencia la conexión.

**Precondiciones, en este orden.** La primera que falla deja la fila `Waiting` con su motivo y detiene la
evaluación; los motivos viajan al frontend con su nombre, que tiene el diccionario de textos:

| `wait_reason` | Cuándo | `wait_detail` | Cómo se resuelve |
| --- | --- | --- | --- |
| `store_not_configured` | sin asesor o sin cuenta de cobro | — | configurar la tienda (§8.2) |
| `advisor_inactive` | la membresía del asesor no está activa | — | cambiar el asesor |
| `billing_account_missing` | la cuenta ya no existe en la empresa | — | elegir otra cuenta |
| `customer_document_missing` | sin documento en los atributos y sin cliente existente por email o teléfono (§6.2.1) | `{ email, phone }` | crear o completar el cliente y reintentar |
| `customer_inactive` | el cliente encontrado está inactivo | `{ customerId }` | reactivarlo |
| `city_unresolved` | dirección colombiana cuya ciudad no se resuelve (§6.2.1) | `{ province, provinceCode, city }` | crear el cliente a mano con la ciudad correcta |
| `product_unmatched` | algún SKU no es un `Product.Code` activo del tenant, o la línea no trae SKU | `{ skus: [...] }` | crear o corregir el producto |
| `freight_unsupported` | el pedido trae `shipping_lines` con monto > 0 y el flete no está implementado | `{ shippingTitle, amount }` | se levanta con el spec de flete |
| `currency_mismatch` | la moneda del pedido no es la de la cuenta de cobro de la tienda | `{ orderCurrency, billingCurrency }` | elegir una cuenta en esa moneda |
| `quotation_rejected` | `EnsureConvertibleToOrder` u otra regla de Quotations tiró un código que esta lista no previó | `{ code }` | corregir el dato que el código señala y reintentar |

#### 6.2.1 Cliente

1. **Documento.** Se lee `customAttributes[document_attribute_key]`; el tipo, de
   `customAttributes[document_type_attribute_key]` si existe y se parsea con `IdentificationTypeParser`, o el
   `default_identification_type` de la tienda. El número se recorta y se limita a 32 caracteres (H7).
2. **Búsqueda.** Con documento: `IShopifyCustomerDirectory.FindByIdentificationAsync(tenantId, type, number)`.
   Sin documento: `FindByEmailOrPhoneAsync` (email exacto sin mayúsculas, teléfono E.164); si encuentra un
   cliente **completo**, se usa; si encuentra uno incompleto o ninguno → `customer_document_missing`. Esto
   permite que el segundo pedido de un comprador que ya tiene ficha entre sin documento en el checkout.
3. **Alta.** Si no existe, `IShopifyCustomerDirectory.CreateCompleteAsync` → servicio nuevo de Customers
   `ICustomerProvisioning.CreateCompleteAsync` (Application, sin autorización HTTP, auditoría con actor
   `Integration`): nombre = `firstName lastName` del cliente de Shopify o de la dirección de envío, documento,
   teléfono, email, dirección (`address1` + `address2`), país (`countryCodeV2`), ciudad resuelta, clasificación
   por defecto, `WithRetention = false`, `VatSurplus = false`. Emite CUC como el handler. Si el documento
   quedó tomado entre la búsqueda y el alta (índice único), se vuelve a buscar y se usa el existente.
4. **Ciudad.** Sólo si el país es `CO`. Shopify entrega `province` (nombre) y `provinceCode` (ISO 3166-2
   sin el prefijo `CO-`: `ANT`, `DC`, `CUN`…). Geography no tiene código ISO (H9), así que el módulo trae
   una tabla **en código** `ColombiaProvinceCodes` (`"DC" → "Bogotá, D.C."`, 33 entradas) que mapea el
   código al nombre de departamento, y luego `FindByNameAsync(departmentId, city)` por el puerto
   `IShopifyGeographyLookup`. Sin match por código, se intenta `province` por nombre. Sin ciudad →
   `city_unresolved`. Fuera de Colombia no se exige ciudad y la dirección va con `CityId = null`, como admite
   `CreateCustomerCommand`.

#### 6.2.2 Productos y precios

- Cada línea: `sku` → `IShopifyProductLookup.FindByCodeAsync(tenantId, sku)` (adaptador sobre
  `IProductRepository.FindByCodeAsync`, H10) que devuelve id, nombre, código, `IsActive` y `TaxPercentage`.
  Inactivo o inexistente → `product_unmatched`. Se resuelven **todas** las líneas antes de fallar para que
  `wait_detail.skus` traiga la lista completa.
- Precio unitario con IVA (H3): si `taxes_included`, `originalUnitPrice`; si no,
  `originalUnitPrice × (100 + TaxPercentage) / 100`. La tasa es la del **producto de QEP**, no la de
  Shopify: es la que el ERP del tenant espera.
- Descuento: `Σ discountAllocations.amount` de la línea sobre `originalUnitPrice × quantity`, como porcentaje
  con 4 decimales; `AddItem` lo recibe ya resuelto (el dominio no consulta escalas para esto).
- Flete: ver `freight_unsupported`. El spec de flete define el mapeo de `shipping_lines` y retira el motivo.

#### 6.2.3 Cotización y pedido (dentro de `ExternalOrderService`)

1. `Quotation.Create` con `Origin = Shopify` (campo nuevo, §6.5), cliente, asesor de la tienda,
   `validUntil` = fecha del pedido en la zona del tenant, `paymentMethod` = `gateway` de Shopify recortado,
   notas = `Shopify #1001` + la nota del comprador (tope 500), partes: `Shipping` con la dirección de envío
   (nombre, teléfono, dirección, departamento y ciudad resueltos) o `IsStorePickup = true` si el método de
   envío es de recogida (`shipping_lines[].code` empieza por `pickup` o `deliveryMethod.methodType = PICK_UP`);
   `Billing` vacío (factura a los datos del cliente); cuenta de cobro de la tienda; moneda del pedido (si
   difiere de `billing_currency` → `Waiting` con `currency_mismatch`, motivo adicional de la tabla);
   `customerWithRetention` y `customerVatSurplus` del cliente.
2. `AddItem` por línea, en el orden de Shopify.
3. `ConvertToOrder` (H2: `Draft` convierte sin enviar). `EnsureConvertibleToOrder` corre igual; si tira,
   el motivo se guarda como `Waiting` con `quotation_rejected` y el código de dominio en `wait_detail`: es la
   red para lo que esta lista no previó.
4. `Order.Create` con el consecutivo del tenant (`PED-…`), `Source = Shopify`, `ExternalReference = "#1001"`,
   `PaymentStatus` desde `financial_status` (`paid` → `FullPaymentReceived`; `partially_paid` →
   `PartialPaymentReceived`; `pending`, `authorized` y cualquier otro → `PaymentPending`), sin comprobantes,
   `convertedBy` = asesor de la tienda.
5. Historial de la cotización (`Created`, `Converted`) y auditoría con actor `Integration`.
6. Eventos como siempre: `quotations.quotation.converted.v1` sale al outbox y Pipeline, si está prendido,
   gana el negocio del cliente a nombre del asesor de la tienda (H17). No se emite `sent`.
7. De vuelta en el módulo, la fila del espejo pasa a `Imported` con `qep_quotation_id`, `qep_order_id` y
   `total_difference` = `total_price` de Shopify − `Total` de QEP. Si el valor absoluto supera el 1 % del
   total, la bandeja lo muestra con aviso; la importación no se frena por eso (D-S6).

### 6.3 Cambios posteriores

- **Pago.** Un snapshot nuevo con otro `financial_status` sobre una fila `Imported` llama a
  `IExternalOrderWriter.SyncPaymentStatusAsync(orderId, status)`, que usa el método nuevo
  `Order.SyncExternalPaymentStatus` (H5): sólo cambia si el pedido **no tiene comprobantes** propios; con
  comprobantes, el estado lo gobiernan ellos y el aviso se ignora con auditoría. `refunded`,
  `partially_refunded` y `voided` no cambian nada: quedan en el espejo (fuera de alcance).
- **Cancelación.** `cancelled_at` no nulo sobre una fila `Imported`: pedido `Pending` o `Approved` →
  `IExternalOrderWriter.CancelAsync(orderId, "Cancelado en Shopify: {cancel_reason}")` a nombre del asesor
  de la tienda (el dominio exige `MemberId`); `Invoiced` → la fila pasa a `Cancelled` con
  `wait_detail = { reason: "invoiced_before_cancel" }` y la bandeja la muestra para que alguien revierta la
  facturación y anule a mano; ya `Cancelled` en QEP → nada. Sobre una fila `Waiting` → `Ignored`.
- **Líneas, dirección y cliente** editados en Shopify después de importar: no se sincronizan; el snapshot
  queda en el espejo.

### 6.4 Reintentos

- **Automático:** cada ciclo de conciliación (§5.3) reintenta los `Waiting` de la tienda. Así, completar
  una ficha o crear un producto surte efecto en menos de `IntervalMinutes` sin suscribir el módulo a
  eventos de Customers ni de Catalog (D-S5).
- **Manual:** `POST …/shopify/orders/{id}/retry` (§7) corre la importación en el request y devuelve la fila.
  Sobre una fila que no está `Waiting` → 422 `shopify.order.not_waiting`.
- `attempts` y `last_attempt_at` se actualizan en los dos casos; ningún tope: un pedido en espera no se
  abandona solo.

### 6.5 Cambios en Quotations y Reporting

- `Quotation.Origin` (`Manual` | `Shopify`) y `Order.Source` (`Quotation` | `Shopify`) +
  `Order.ExternalReference` (hasta 64 caracteres). Migración con backfill `Manual` / `Quotation`. En los DTO
  viajan como texto con el nombre del enum (regla del BFF).
- `ListOrdersQuery.Source` (opcional; sin él, todos) y `ListQuotationsQuery.Origin` (opcional; **sin él,
  sólo `Manual`**): una cotización que nace convertida en la lista de cotizaciones confundiría al asesor y
  la pantalla no la puede editar ni enviar. El detalle por id sigue respondiendo cualquiera.
- `OrderListItemDto`, `OrderDto` y `QuotationDto` ganan `source` / `origin` / `externalReference`. El motivo
  va en el DTO: la pantalla dibuja la insignia y el `#1001` con eso.
- Reporting: `QuotationsReportSource` filtra `Origin = Manual` en los tres reportes de cotizaciones
  (listado, resumen, mensual); `OrdersReportSource` no filtra (los pedidos de la tienda son ventas). El
  Excel de pedidos no cambia de columnas; `order_number` sigue siendo el `PED-…` (D-S2).
- `ExternalOrderService` y `Order.SyncExternalPaymentStatus` son los únicos puntos de entrada nuevos;
  `ConvertQuotationToOrderHandler` no cambia.

## 7. API (BFF)

Sin permisos nuevos: configurar la tienda es administrar integraciones y la bandeja es gestión de pedidos.
Las rutas de `/shopify/*` pasan por `TenantModuleGuard` (`shopify`); las de instalación no, porque el
módulo todavía no existe en el tenant.

| Ruta | Permiso | Para qué |
| --- | --- | --- |
| `POST /api/v1/integrations/shopify/install/start` | anónimo, limitador `public` | §4.3 paso 2. Cuerpo `{ shop, hmac, timestamp, host, redirectUri }` → `{ authorizeUrl }` |
| `POST /api/v1/integrations/shopify/install/complete` | anónimo, limitador `public` | §4.3 paso 4. Cuerpo `{ code, shop, state, hmac, timestamp, redirectUri }` → `{ installId, shopDomain, shopName, linkedTenantId, linkedTenantName }` |
| `GET /api/v1/integrations/shopify/installs/{installId}` | sólo autenticación | `{ shopDomain, shopName, expiresAt, tenants: [{ tenantId, name, canLink }] }`. `canLink` es tener `integrations.connection.manage` ahí; viajan todos los activos para que la pantalla explique por qué uno no se puede elegir |
| `POST /api/v1/tenants/{tenantId}/integrations/shopify/link` | `integrations.connection.manage` | `{ installId, name? }` → 201 `ConnectionResponse` |
| `GET /api/v1/tenants/{tenantId}/shopify/stores` | `integrations.connection.read` | Tiendas del tenant con `connectionId`, `name`, `shopDomain`, `status`, `isConfigured`, `waitingCount` |
| `GET /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}` | `integrations.connection.read` | La configuración (§6.1) con `advisorName`, `billingCompanyName`, `classificationName`, `version` (ETag) |
| `PUT /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}` | `integrations.connection.manage`, `If-Match` | Guarda la configuración; 428/412 como el resto del repo; valida asesor, cuenta y clasificación |
| `GET /api/v1/tenants/{tenantId}/shopify/orders?state=&connectionId=&page=&pageSize=` | `quotations.order.read` | Espejo paginado (tope 50): `id`, `orderName`, `storeName`, `shopifyCreatedAt`, `financialStatus`, `totalPrice`, `currency`, `importState`, `waitReason`, `waitDetail`, `attempts`, `lastAttemptAt`, `qepOrderId`, `qepOrderNumber`, `totalDifference`, `customerName`, `customerEmail`. `state` por defecto `Waiting`; acepta varios separados por coma |
| `GET /api/v1/tenants/{tenantId}/shopify/orders/summary` | `quotations.order.read` | `{ waiting, cancelledAfterInvoice, importedToday }` por estado, **completo incluso en cero**, para la insignia de la pestaña |
| `POST /api/v1/tenants/{tenantId}/shopify/orders/{id}/retry` | `quotations.order.manage` | Reintento inmediato; devuelve la fila actualizada |
| `POST /api/webhooks/shopify` | anónimo, limitador `webhook` | §5.1 |

Cambios en rutas existentes: `GET …/orders` acepta `source`; `GET …/quotations` acepta `origin`;
`OrderListItemResponse`, `OrderDto` y `QuotationDto` ganan los campos de §6.5.

### 7.1 Errores

| Código | Status | Cuándo |
| --- | --- | --- |
| `integrations.shopify.hmac_invalid` | 401 | HMAC de la query de instalación o del callback no coincide |
| `integrations.shopify.shop_invalid` | 422 | el dominio no tiene la forma `*.myshopify.com` |
| `integrations.shopify.install_not_found` | 404 | `installId` o `state` desconocido (no es por tenant: no filtra nada) |
| `integrations.shopify.install_expired` | 422 | la fila venció (15 min sin canje, 1 h sin enlace) |
| `integrations.shopify.code_exchange_failed` | 422 | Shopify no canjeó el `code` |
| `integrations.shopify.scopes_missing` | 422 | lo concedido no contiene `Shopify:App:Scopes` |
| `integrations.shopify.shop_already_linked` | 422 | la tienda está enlazada a otro tenant |
| `integrations.connection.limit_reached` | 422 | ya hay 5 tiendas (existente) |
| `shopify.store.advisor_not_member` | 422 | el asesor no es membresía activa del tenant |
| `shopify.store.billing_account_not_found` | 422 | la cuenta no existe en esa empresa del tenant |
| `shopify.store.classification_not_found` | 422 | la clasificación no es del tenant |
| `shopify.order.not_found` | 404 | fila del espejo inexistente en el tenant |
| `shopify.order.not_waiting` | 422 | reintentar una fila que no está `Waiting` |
| `validation.failed` | 422 | campos del `PUT` y de los cuerpos de instalación, con el mapa `errors` |
| `tenancy.module_not_enabled` | 403 | módulo `shopify` apagado (existente) |

Los motivos de espera (§6.2) **no** son errores HTTP.

## 8. Frontend (`qep-frontend`)

| Dónde | Qué |
| --- | --- |
| `routes/shopify/install.tsx` (pública) | Lee `shop`, `hmac`, `timestamp`, `host`; llama `startInstall` con `redirectUri = origin + '/shopify/callback'`; redirige a `authorizeUrl`. Estados: cargando, `hmac_invalid` / `shop_invalid` («Abre QEP desde tu tienda de Shopify»). |
| `routes/shopify/callback.tsx` (pública) | Lee `code`, `shop`, `state`, `hmac`, `timestamp`; llama `completeInstall`; con `linkedTenantId`, muestra «Esta tienda ya está enlazada a *X*» con botón a ese tenant; si no, navega a `/shopify/link/$installId`, pasando por `/login` o `/register` con `returnTo` cuando no hay sesión. |
| `routes/_authenticated/shopify/link.$installId.tsx` | Tienda y lista de tenants con `canLink`; nombre de la conexión editable (por defecto `shopName`); «Enlazar» → `link` → navega a la configuración. Sin tenants: enlace a `/register` con `returnTo`. `install_expired`: «Vuelve a instalar la app desde Shopify». |
| `routes/_authenticated/settings/integrations/shopify.$connectionId.tsx` | Formulario de §6.1: asesor (combo de membresías activas), empresa y cuenta de cobro (combos de Companies), clasificación, claves de atributos, tipo de documento por defecto. `If-Match`; 412 → «Alguien cambió esta configuración, recarga». Aviso arriba mientras `isConfigured === false`: «Los pedidos de esta tienda quedan en espera hasta que completes esto». |
| `features/integrations/` | `onboarding.kind === 'ShopifyInstall'`: la tarjeta del proveedor no abre formulario; muestra «Instala QEP desde el App Store de Shopify» con el enlace de `VITE_SHOPIFY_APP_URL` y, por conexión, «Configurar tienda» hacia la ruta anterior y el `waitingCount`. |
| `features/orders/` | Columna «Origen» (insignia `Shopify` con `#1001` debajo del `PED-…`) y filtro «Origen» (Todos / Cotización / Shopify). Pestaña «Desde Shopify» con la bandeja: tabla del espejo por defecto en `Waiting` + `Cancelled`, motivo en texto desde el diccionario `describe-wait-reason.ts`, acción contextual (motivo de cliente → «Ver cliente» o «Crear cliente» prellenado con nombre, documento, email, teléfono y dirección; `product_unmatched` → lista de SKUs y enlace a Catálogo con búsqueda; `store_not_configured` → «Configurar tienda»), botón «Reintentar» (`quotations.order.manage`), e insignia con `summary.waiting` en la pestaña. |
| `features/quotes/` | Filtro «Origen» oculto por defecto en `Manual`; una cotización `Shopify` se abre en lectura con aviso «Nació de un pedido de Shopify; se gestiona desde Pedidos». |
| `features/auth/` | `/login` y `/register` aceptan `returnTo` interno (misma app, misma ruta relativa) y vuelven ahí al terminar. |

Textos en español con tuteo; sin exponer el token ni el `state` en logs del navegador.

## 9. Base de datos

Esquema nuevo `shopify` (`ShopifyDbContext`, migraciones en
`Modules.Shopify.Infrastructure/Persistence/Migrations`):

- `shopify.stores` (§6.1). PK `connection_id`; índice `(tenant_id)`.
- `shopify.orders` (§5.4). Único `(connection_id, shopify_order_id)`; índices `(tenant_id, import_state,
  shopify_created_at DESC)` y `(tenant_id, qep_order_id)`.
- `shopify.webhook_deliveries`: `id`, `webhook_id` (único), `topic`, `shop_domain`, `api_version`,
  `payload` jsonb, `received_at`, `attempts`, `lease_until`, `processed_at`, `ignored_reason`, `last_error`.
- `shopify.audit_entries`: proyección propia de `audit.entries` para `IShopifyAuditRecorder`, como
  Messaging e Integrations (Integrations no comparte el `IAuditRecorder` ligado a Tenancy).

Integrations:

- `integrations.shopify_installs`: `id`, `shop_domain`, `state` (único), `redirect_uri`, `shop_id`,
  `shop_name`, `scopes`, `access_token_protected` (bytea, null hasta el canje), `key_id`, `created_at`,
  `expires_at`. Un `IHostedService` barre las vencidas cada hora. El worker de re-cifrado de llaves
  (`ConnectionSecretRekeyWorker`) **no** la toca: una fila vive una hora como máximo.
- Migración del `CHECK` de `provider_key` con `shopify`.

Tenancy: migración del `CHECK` de `tenant_modules.module_key` con `shopify`.

Quotations: `quotations.quotations.origin` (`text`, default `Manual`, `CHECK`), `quotations.orders.source`
(`text`, default `Quotation`, `CHECK`), `quotations.orders.external_reference` (`varchar(64)`, null); índice
`(tenant_id, source)` en pedidos.

## 10. Configuración y despliegue

1. **Variables del pipeline** (`Backend-prod`): `SHOPIFY_APP_CLIENT_ID` normal y `SHOPIFY_APP_CLIENT_SECRET`
   **secreta**. `k8s/prod-configMap.yaml`: `Shopify__App__ClientId`, `Shopify__App__ApiVersion`,
   `Shopify__App__Scopes`, `Shopify__App__AllowedRedirectOrigins__0: "https://qep.qcode.co"`,
   `Shopify__Reconcile__IntervalMinutes`. `k8s/prod-secret.yaml`: `Shopify__App__ClientSecret`. Sin ellas el
   pod entra en crash-loop por `ValidateOnStart`: a propósito.
2. **Local:** user-secrets `Shopify:App:ClientId` y `Shopify:App:ClientSecret` (verificar con
   `Measure-Object`, nunca listando el valor); `appsettings.Development.json` con
   `AllowedRedirectOrigins: ["http://localhost:3002"]`. Para recibir webhooks en local hace falta un túnel
   público; sin él, la conciliación trae los pedidos igual.
3. **Orden:** backend, luego frontend. Rollback del backend: el SPA muestra rutas que responden 404; nada se
   corrompe porque ninguna tienda puede enlazarse sin backend.
4. **Frontend:** `VITE_SHOPIFY_APP_URL` con el enlace de instalación de la app.
5. **Handoff al owner** (`docs/superpowers/plans/<fecha>-shopify-handoff.md`): app en el Dev Dashboard de
   Shopify, no embebida, distribución pública (listada o no listada: las dos pasan por revisión), App URL
   `https://qep.qcode.co/shopify/install`, redirect URLs de prod y local, scopes `read_orders` y
   `read_customers`, suscripciones de webhook a nivel de app con los ocho topics y la versión de API,
   solicitud de **acceso a datos protegidos de clientes** (sin ella la API devuelve los pedidos sin nombre,
   email, teléfono ni dirección, y todo caería en `customer_document_missing`), tienda de desarrollo para
   probar, y las verificaciones de §16.

## 11. Dependencia del flete

El spec de flete en cotizaciones (pendiente) tiene que resolver, al menos: si el flete lleva IVA y a qué
tasa, si entra en la base de retención, cómo se edita en el cotizador, cómo sale en el PDF, qué columna del
Excel de pedidos lo lleva, y cómo lo suman los reportes. Cuando exista, este spec recibe un delta que: (a)
retira `freight_unsupported`, (b) define `shipping_lines[].discountedPrice` → flete de la cotización, (c)
decide qué pasa con el IVA del envío que Shopify calculó. Hasta entonces, un tenant cuya tienda cobra envío
vería casi todos sus pedidos en espera: **no se habilita la integración a un tenant real sin el flete**.

## 12. Seguridad

- El token de la Admin API es un secreto interno: nunca viaja en un DTO, un log ni la auditoría; prueba de
  fuga con sentinela como `WhatsAppSecretLeakTests`.
- HMAC de la query (instalación y callback) y del cuerpo (webhook) con comparación en tiempo constante;
  `state` aleatorio, de un solo uso y con vencimiento; `redirect_uri` de origen permitido y exigido idéntico
  en el canje.
- `GET /installs/{installId}` responde 404 para cualquier id desconocido y no lista tenants ajenos: sólo
  los activos del usuario autenticado. El `installId` es un GUID aleatorio, no secuencial.
- El enlace exige `integrations.connection.manage` **en el tenant elegido**, revalidado en el handler
  (doble capa).
- Aislamiento de tenant en todo `shopify.*`: cada repositorio recibe `tenantId`; 403, nunca 404, para un
  recurso de otro tenant.
- El webhook no revela si una tienda existe: 200 siempre que la firma sea válida.
- Limitador `webhook` (concurrencia global con cola) para el webhook y `public` para las dos rutas anónimas
  de instalación.

## 13. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias (`tests/Modules/Shopify/Modules.Shopify.UnitTests`):** parsers REST y GraphQL → el mismo
`ShopifyOrderSnapshot` (fixtures reales de una tienda de desarrollo: `taxes_included` en los dos modos,
descuentos por línea y por pedido, recogida en tienda, pedido de prueba, cancelado); verificación HMAC de
query y de cuerpo con vectores conocidos; `ColombiaProvinceCodes` completa contra la lista de
departamentos de Geography; evaluador de precondiciones (cada motivo, el orden, `wait_detail`); mapeo de
`financial_status`; máquina de estados del espejo (desorden por `updated_at`, `Imported` no retrocede,
cancelación en cada estado); cálculo del precio con IVA y del porcentaje de descuento; `Quotation.Origin`,
`Order.Source`, `Order.SyncExternalPaymentStatus` (con y sin comprobantes).

**Integración (`Modules.Shopify.IntegrationTests`, Testcontainers, `ShopifyApiHarness` que prende el módulo
y crea tienda, asesor, empresa con cuenta, clasificación y productos):** webhook firmado → 200 y fila;
sin firma → 401 sin fila; duplicado por `webhook_id`; tienda desconocida → ignorado; importación completa
que termina en `Quotation` `Converted` + `Order` `Pending` con las líneas, el cliente creado con CUC y los
totales esperados; cada motivo de espera y su reintento manual y automático; cliente existente por
documento y por email; segundo pedido del mismo comprador; cancelación sobre `Pending`, `Approved`,
`Invoiced` y `Waiting`; pago actualizado con y sin comprobantes; conciliación con `HttpMessageHandler`
falso (paginación, `THROTTLED`, 401 → `NeedsAttention`); conexión pausada guarda espejo sin importar;
módulo apagado → 403 en `/shopify/*`; tenant ajeno → 403; `Pipeline` gana el negocio al convertir;
fuga de token con sentinela.

**Integración en Integrations:** `install/start` (HMAC bueno y malo, dominio inválido, origen no
permitido), `install/complete` (`state` desconocido, vencido, reutilizado; canje con gateway falso; scopes
faltantes), `installs/{id}` (404 ajeno, lista de tenants con `canLink`), `link` (activa el módulo, crea
conexión y ruta, reinstalación del mismo tenant, otro tenant → `shop_already_linked`, tope 5), probador.

**Quotations:** barrer las pruebas que arman cuerpos a mano por los campos nuevos antes de correr nada
(CLAUDE.md, gotcha de reglas endurecidas); `ListOrders` por `source`; `ListQuotations` excluye `Shopify`
por defecto; Reporting excluye en cotizaciones y no en pedidos.

**Arquitectura:** `ShopifyLayerTests` (capas; sólo Tenancy y Audit entre los módulos; prohibido referenciar
Integrations, Quotations, Customers, Catalog y Geography en Application); `IntegrationsLayerTests` sigue
prohibiendo que un módulo de negocio referencie `Modules.Integrations.Application`;
`ConfigurationExampleTests` con las claves nuevas.

**Frontend (Vitest + Testing Library):** las cuatro rutas nuevas (carga, error, éxito, `returnTo`),
formulario de tienda (validación, 412), bandeja (cada motivo con su acción, reintento, insignia), columna y
filtro de origen, cotización `Shopify` en lectura.

**Suite por módulo tocado**, no la completa (memoria del repo: el developer la detuvo a mitad).

## 14. Fuera de alcance

- Flete en cotizaciones: spec aparte, previo.
- Historial de pedidos anterior al enlace (D-S4).
- Reembolsos, devoluciones, ediciones de líneas o de dirección después de importar.
- Sincronización hacia Shopify: fulfillment, tracking, inventario, facturas.
- App embebida (App Bridge, token exchange) y la ficha en el App Store (textos, capturas).
- Otras plataformas (WooCommerce, VTEX, Mercado Libre): cada una traería su módulo o un delta a éste.
- Comprobantes de pago para pedidos de la tienda: el pago lo registra Shopify.
- Clientes de Shopify sin pedido (sincronizar la libreta).

## 15. Decisiones tomadas sin el owner (a ratificar)

| ID | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-S1 | Tope de **5 tiendas por tenant**; `state` vence a 15 min; instalación canjeada vence a 1 h; conciliación cada 10 min con 5 min de solape; 200 reintentos por ciclo | Constantes |
| D-S2 | El número del pedido de QEP es el **consecutivo del tenant** (`PED-…`); `#1001` va en `ExternalReference`, en la lista y en las notas. El Excel sigue llevando `order_number` = `PED-…` | Un campo en el Excel o una opción por tienda |
| D-S3 | La **tasa de IVA es la del producto de QEP**, no la de Shopify; la diferencia de total se registra pero no frena | Un motivo de espera más (`tax_mismatch`) |
| D-S4 | **No se importa historial** anterior al enlace | Un botón «Traer los últimos N días» en la configuración de la tienda |
| D-S5 | Reintento automático **por ciclo de conciliación**, sin suscribirse a eventos de Customers ni Catalog | Dos consumidores de outbox |
| D-S6 | Diferencia de total > 1 % se **muestra**, no frena | Un motivo de espera |
| D-S7 | Cliente nuevo nace con `WithRetention = false` y `VatSurplus = false`; facturación a los datos del cliente, nunca a consumidor final | Dos campos más en la configuración de la tienda |
| D-S8 | Sin documento, un cliente **completo** existente se encuentra por email o teléfono; uno incompleto no | Quitar la búsqueda secundaria |
| D-S9 | Los webhooks de cumplimiento **sólo auditan**; `shop/redact` borra espejo y entregas, no pedidos ni clientes | Un flujo de anonimización |
| D-S10 | Módulo primero, conexión después en el enlace; sin compensación si falla la segunda | Un paso de limpieza |
| D-S11 | `ListQuotations` **excluye** `Shopify` por defecto; Reporting de cotizaciones lo excluye siempre | Un flag en el filtro |
| D-S12 | Pedidos con `test = true` y cancelados antes de importar → `Ignored`, visibles con filtro | Importarlos |
| D-S13 | Nombre de la conexión por defecto = `shopName`; la configuración de la tienda vive en `shopify.stores`, no en `fields` (coherente con D6 de Integrations) | Mover campos |
| D-S14 | Sin permiso nuevo: `integrations.connection.*` para la tienda y `quotations.order.*` para la bandeja | Un permiso `shopify.store.manage` con sus cuatro piezas |
| D-S15 | `Billing` vacío en la cotización (factura al cliente); `paymentMethod` = `gateway` de Shopify | Otra parte de facturación |

## 16. Verificaciones pendientes contra Shopify (handoff)

| # | Qué | Cómo se cierra | Si falla |
| --- | --- | --- | --- |
| V-1 | Que una app pública **no embebida** siga admitiendo el *authorization code grant* con redirección y token offline en la versión de API elegida, sin obligar a token exchange | Tienda de desarrollo, primer enlace real | Flujo embebido con App Bridge: otro spec |
| V-2 | Que las suscripciones de webhook **a nivel de app** (configuración de la app) lleguen para toda tienda instalada y se firmen con el client secret | Primer pedido real | Registrar webhooks por tienda al enlazar, con `webhookSubscriptionCreate` |
| V-3 | Que sin la aprobación de **datos protegidos de clientes** el payload llegue sin PII (y por tanto todo caiga en `customer_document_missing`) y que con ella llegue completo | Comparar payloads antes y después | Nada en el código; es un paso del handoff |
| V-4 | La forma exacta de `provinceCode` para Colombia (`ANT`, `DC`…) y que `customAttributes` traiga lo que el checkout del tenant captura como documento | Pedido real de la tienda piloto | Ajustar `ColombiaProvinceCodes` o las claves por tienda |
| V-5 | Que `orders/updated` llegue en cada cambio de `financial_status` además de `orders/paid` | Pagar un pedido pendiente en la tienda de desarrollo | La conciliación lo cubre con retraso |
| V-6 | Códigos de `shipping_lines` para recogida en tienda en el checkout colombiano | Pedido de prueba con recogida | Opción por tienda para marcar el método de recogida |

## 17. Entregables

1. Spec de flete en cotizaciones (sesión aparte) y su implementación en `develop`.
2. Backend: Tenancy (clave, activación), Integrations (proveedor, instalación, enlace, probador), Quotations
   (origen, servicio externo, filtros), Customers (`ICustomerProvisioning`), Reporting (filtro), módulo
   Shopify (webhook, workers, espejo, importador, API), Bootstrapper (adaptadores, políticas, mapeo), README.
3. Frontend: rutas de instalación y enlace, configuración de tienda, bandeja y origen en pedidos, filtro en
   cotizaciones, `returnTo`.
4. Handoff al owner con lo de §10.5 y §16.
5. Plan de implementación (`writing-plans`) tras la lectura de este spec.

## Historial de revisión

- 2026-10-15: decisiones 1–12 tomadas en conversación con el owner; secciones de diseño aprobadas una por
  una; spec escrito. Pendiente la lectura del owner, el spec de flete y el plan de implementación.

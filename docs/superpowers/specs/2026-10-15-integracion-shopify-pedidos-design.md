# Integración con Shopify: pedidos de la tienda en QEP

**Fecha:** 2026-10-15
**Módulos:** Shopify (nuevo: Domain, Application, Infrastructure, Api), Integrations (proveedor, OAuth,
instalación, enlace y registro de webhooks), Quotations (origen del pedido, servicio para pedidos externos,
excepción de elegibilidad del cliente, filtros), Customers (alta de cliente completo o incompleto desde el
sistema), Tenancy (clave de módulo `shopify`), Reporting (excluir cotizaciones de origen externo),
Api/Bootstrapper (webhook, adaptadores, políticas). **Frontend:** `qep-frontend`, rutas públicas de
instalación, `features/integrations`, `features/orders`, `features/quotes`, `features/operator`.
**Estado:** alcance, enfoque y las cuatro secciones del diseño aprobados por el owner en conversación el
2026-10-15; revisado el mismo día con sus correcciones (§3, decisiones 2, 8, 11 y 13 a 15). Pendiente de su
lectura final y del plan de implementación.
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

El módulo lo activa el usuario de plataforma, tenant por tenant, desde la consola de operador. Un tenant con
el módulo activo puede conectar **N tiendas**, cada una una sola vez, y la conexión se puede iniciar desde
dos lugares: desde QEP (Ajustes → Integraciones → «Nueva integración de Shopify») o desde Shopify
(instalar la app). En los dos casos corre el OAuth de Shopify y termina en QEP, donde la tienda se vincula a
una cuenta existente o a una cuenta nueva. Al vincular, QEP registra los webhooks de la tienda. Los
pedidos llegan por webhook, por una sincronización manual o por un cron de conciliación; cada pedido crea
el cliente (completo si trae documento, incompleto si no) y el pedido en QEP, que se mezcla con los demás
en la vista de pedidos. Lo que no puede entrar queda en una bandeja con el motivo.

**Dentro:**

- Proveedor `shopify` en Integrations con onboarding por OAuth desde QEP y desde Shopify (§4).
- Enlace tienda ↔ tenant, con registro de tenant nuevo cuando la persona no tiene cuenta (§4.3).
- Módulo `shopify` por tenant, activado por el operador (§4.4).
- Registro de webhooks por tienda al enlazar; webhook firmado, espejo de pedidos, conciliación periódica y
  sincronización manual (§5).
- Traducción a `Quotation` + `Order` con cliente completo o incompleto, SKU = código de producto, asesor y
  cuenta de cobro de la tienda (§6).
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
| H2 | Convertir exige `Draft` o `Sent`, al menos una línea, `ValidUntil`, cuenta de cobro y partes completas; se puede convertir desde `Draft` sin enviar. Antes, el handler revalida el cliente con `QuotationCustomerEligibility.Ensure`, que rechaza `Incomplete` (`client_incomplete`), sin CUC (`client_cuc_missing`) e inactivo. Sus cuatro llamadores son crear, cambiar cliente, enviar y convertir; aprobar, facturar y anular un pedido no lo llaman. | `Quotation.cs:853-940`; `src/Modules/Quotations/Modules.Quotations.Application/QuotationCustomerEligibility.cs`; `ConvertQuotationToOrder.cs:84`; `CreateQuotation.cs:60`; `ChangeQuotationClient.cs:56`; `SendQuotation.cs:104` |
| H3 | `QuotationItem.UnitPrice` es **con IVA** (`UnitPriceWithoutTax = UnitPrice × 100 / (100 + TaxPercentage)`); el subtotal de la cotización es sin IVA y `Total = Subtotal + TaxAmount`. La cotización no tiene concepto de flete. | `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:113-142`; `Quotation.cs` (`RecalculateTotals`) |
| H4 | El asesor de una cotización es la membresía activa de quien la crea (`QuotationAdvisorResolver`), y los handlers autorizan contra `IExecutionContext` con tenant y permiso: ninguno sirve para un actor del sistema. | `QuotationAdvisorResolver.cs:17-30`; `QuotationsAuthorization.cs:11-21`; `CreateQuotation.cs` |
| H5 | `Order.PaymentStatus` sólo cambia por comprobantes (`RecalculatePaymentStatus(amountDue)`); no hay forma de fijarlo desde afuera. | `Order.cs:334` |
| H6 | La lista de pedidos filtra por cliente, asesor, estado, pago, fechas, CUC y número; la de cotizaciones por cliente, asesor, estado, fechas, NIT y número. Ninguna por origen. | `ListOrders.cs:14-30`; `ListQuotations.cs:7-19` |
| H7 | Un cliente completo exige nombre, tipo y número de documento (`NIT`, `CC`, `CE`, `PASAPORTE`), dirección, país, ciudad DIVIPOLA si es colombiano, y clasificación; el CUC lo emite `ICucGenerator`. `CreateCustomerHandler` autoriza por sesión. Un cliente `Incomplete` lo crea sólo el sistema (`CreateIncomplete` con BSUID para WhatsApp, `CreateIncompleteFromInstagram` con IGSID): nombre, teléfono y país opcionales, sin documento, dirección ni clasificación, con CUC nulo. | `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:9-80`; `Modules.Customers.Domain/IdentificationType.cs`; `Customer.cs:325-350,412-427`; `CustomerCompleteness.cs` |
| H8 | Customers busca por documento con `FindExistingIdentificationsAsync(tenantId, [(Type, Number)])` y por teléfono con `ICustomerPhoneDirectory.MatchAsync`; no hay búsqueda por email. | `ICustomerRepository.cs:87-90`; `src/Bootstrapper/MessagingCustomerDirectory.cs:19` |
| H9 | El Excel de pedidos escribe `Documento` = `customer.Cuc ?? ""` y `Documento de identidad` = número del cliente o vacío: un cliente sin CUC ni documento sale con las dos columnas **vacías**, sin error. | `OrdersExportProcessor.cs:290,300,427-442` |
| H10 | Geography resuelve ciudad **por nombre dentro de un departamento** (`ICityRepository.FindByNameAsync(departmentId, name)`) y departamento por nombre; `Department` tiene `DivipolaCode` y `Name`, no código ISO 3166-2. | `src/Modules/Geography/Modules.Geography.Application/ICityRepository.cs:25-34`; `IDepartmentRepository.cs:28`; `Modules.Geography.Domain/Department.cs:21-25` |
| H11 | Catalog busca un producto por código exacto: `IProductRepository.FindByCodeAsync(tenantId, code)`; Pos ya lo consume por `PosProductLookup` en Bootstrapper. | `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs:79`; `src/Bootstrapper/PosProductLookup.cs:22-26` |
| H12 | Integrations tiene catálogo en código, categorías `Messaging`, `Shipping`, `Ai`, onboarding `Form`, `MetaEmbeddedSignup`, `InstagramLogin`, campos públicos, internos y secretos, tope por proveedor (`DefaultMaxConnections = 20`), `connection_routes` (`provider_key`, `external_id` único) para enrutar webhooks, y los puertos `IIntegrationConnections` e `IConnectionHealthReporter`. El proveedor sólo es visible con alguno de sus `ConsumingModules` activo. | `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs`; `IntegrationCatalog.cs:6-30`; `IntegrationConnectionRoute.cs`; `Modules.Integrations.Application/IIntegrationConnections.cs`; `IntegrationsSupport.cs:36-48` |
| H13 | El precedente de OAuth por redirección es Instagram Login: el SPA abre la URL de autorización con `state`, vuelve a un callback propio, y `ConnectInstagramHandler` canjea el `code` con el secreto global, exige `redirectUri` de un origen permitido, **suscribe el webhook de la cuenta** (`SubscribeAsync`) y crea la conexión y la ruta. Las opciones globales se validan con `ValidateOnStart` y en `Production` son obligatorias. | `ConnectInstagram.cs`; `InstagramLogin.cs` (`WebhookFields`); `Modules.Integrations.Infrastructure/Meta/MetaInstagramOptions.cs` |
| H14 | `ConnectionEvents` publica `connection-paused`, `connection-deleted` y `connection-needs-attention`; no hay evento de conexión creada. | `Modules.Integrations.Application/ConnectionEvents.cs:8-14` |
| H15 | El webhook de Meta es una ruta anónima bajo `/api/webhooks/`, con el limitador `webhook`, exenta de CSRF por prefijo, cuerpo acotado, firma verificada en el handler y encolado en `messaging.webhook_deliveries` con `ON CONFLICT DO NOTHING`; un `BackgroundService` con leases y 8 intentos procesa las entregas. | `src/Modules/Messaging/Modules.Messaging.Api/WhatsAppWebhookEndpoints.cs`; `src/Bootstrapper/WebhookPaths.cs`; `Csrf/RequireCsrfHeaderMiddleware.cs:50`; `Modules.Messaging.Infrastructure/Persistence/WebhookDeliveries.cs`; `Webhook/WebhookDeliveryWorker.cs:23-42`; `src/Api/Program.cs:183-185` |
| H16 | Las claves de módulo son una lista cerrada con `CHECK`; `DefaultForNewTenants` son seis; `pos`, `messaging` y `pipeline` se prenden por tenant desde la consola de operador (`POST /operator/tenants/{id}/modules/changes`, `ChangeTenantModulesHandler`, con fuente y nota). El enforcement es `TenantModuleGuard.EnsureEnabledAsync` → 403 `tenancy.module_not_enabled`. El frontend tiene `features/operator` con `module-change-dialog`. | `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:35-58`; `Modules.Tenancy.Api/OperatorEndpoints.cs:35`; `Modules.Tenancy.Application/ChangeTenantModules.cs:43`; `TenantModuleGuard.cs:12-16` |
| H17 | El registro público de tenant es `POST /api/v1/auth/register-tenant` con bearer de Google, detrás de `Registration:PublicTenantSignupEnabled`; aprovisiona el owner, crea el tenant y deja la sesión emitida. `/register` es una ruta pública del SPA. `GET /auth/me` devuelve `activeTenantIds`. | `src/Api/RegistrationEndpoints.cs:33-125`; `qep-frontend` `src/routes/register.tsx`; `src/features/auth/services/auth.api.ts:42-47` (`develop`) |
| H18 | Pipeline consume `quotations.quotation.converted.v1` y, sin negocio abierto del cliente, crea uno y lo gana a nombre del asesor del evento, con actor del sistema. | `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs:110-175` |
| H19 | Un módulo consumidor nunca referencia `Modules.Integrations.Application`, `Modules.Customers.Application` ni `Modules.Catalog.Application`: declara su puerto y el adaptador vive en Bootstrapper. `AuditActorType.Integration` existe. | `src/Bootstrapper/*.cs`; `tests/ArchitectureTests/ArchitectureTests/*LayerTests.cs`; `IConnectionHealthReporter.cs` |

## 3. Decisiones del owner (2026-10-15)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿Qué hace el tenant con los pedidos de Shopify en QEP? | **Que se listen en Pedidos junto a los de cotización y se exporten** por el mismo Excel. |
| 2 | ¿Cómo se resuelve el cliente, si Shopify casi nunca trae documento? | Primera respuesta: cliente completo o pedido en espera. **Revisado por el owner:** el comprador se crea con lo que traiga Shopify, **completo si trae documento e incompleto si no**, y el pedido **entra igual**. Implica una excepción a la elegibilidad del cliente sólo para origen Shopify (§6.5) y que el Excel salga con `Documento` y `Documento de identidad` vacíos hasta completar la ficha (H9). |
| 3 | ¿Cómo se emparejan los productos? | **SKU de Shopify = `Product.Code` de QEP.** Un SKU desconocido deja el pedido en espera. |
| 4 | ¿De dónde salen el asesor y la cuenta de cobro? | **Configurados por tienda.** Si el asesor queda inactivo, los pedidos nuevos esperan. |
| 5 | ¿Qué pasa con el costo de envío? | **Agregar flete a la cotización**, en un spec aparte que se implementa antes. Mientras no esté, los pedidos con envío esperan (§6.2). |
| 6 | ¿Un spec o dos? | **Dos specs.** Éste declara el flete como dependencia. |
| 7 | ¿Cómo llegan los pedidos? | **Webhooks + conciliación periódica**, y además **sincronización manual** por tienda (revisión del owner). |
| 8 | ¿Cómo conecta el tenant su tienda? | **App pública de QCode con OAuth.** Revisión del owner: el flujo se puede iniciar **desde QEP** (menú Integraciones) **o desde Shopify** (instalar la app); en los dos casos termina en QEP vinculando una cuenta existente o creando una nueva. |
| 9 | ¿Qué pedidos entran y qué se sincroniza después? | **Entran al crearse, con su estado de pago**; el pago se actualiza cuando Shopify avisa; la cancelación se refleja. |
| 10 | ¿Dónde se ven los pedidos en espera? | **Bandeja propia en Pedidos, con reintento automático** y botón «Reintentar». Permiso `quotations.order.manage`. |
| 11 | ¿Módulo vendible? | **Módulo `shopify` propio**, fuera de los seis por defecto. Revisión del owner: lo **activa el usuario de plataforma a mano** por tenant desde la consola de operador; no se activa solo al instalar. |
| 12 | Enfoque de implementación | **A: módulo nuevo `Shopify`**; Integrations guarda la conexión y hace el OAuth; Quotations gana lo mínimo para aceptar un pedido externo. |
| 13 | ¿Cuántas tiendas por tenant? | **N**, cada tienda una sola vez por tenant (y por plataforma: una tienda no puede estar en dos tenants). |
| 14 | ¿Quién crea los webhooks? | **QEP, al vincular la tienda**, por la Admin API. |
| 15 | ¿Y los campos que Shopify no trae? | **Se dejan en blanco**; el cliente puede quedar incompleto. |

## 4. Instalación, OAuth y enlace con el tenant

### 4.1 Proveedor en Integrations

```text
IntegrationProviders.Shopify
  Key               "shopify"
  DisplayName       "Shopify"
  Category          Commerce                       (valor nuevo de IntegrationCategory)
  ConsumingModules  [TenantModuleKeys.Shopify]
  Onboarding        ShopifyOAuth                   (valor nuevo de ProviderOnboarding)
  MaxConnections    DefaultMaxConnections (20)
  Fields
    shopDomain      Text, público, sólo lectura    "mi-tienda.myshopify.com"
    shopName        Text, público, sólo lectura    nombre de la tienda según Shopify
    shopId          Text, interno                  id numérico de la tienda
    scopes          Text, interno                  scopes concedidos, separados por coma
    accessToken     Secret, interno                token offline de la Admin API
```

- `connection_routes`: una fila `("shopify", shopDomain, accountId: null, tenantId, connectionId)`. El
  índice único de `external_id` por proveedor impide que una tienda quede enlazada a dos tenants o dos veces
  al mismo (`integrations.shopify.shop_already_linked`, con el nombre del tenant si es otro) y es la clave con
  la que el webhook enruta por `X-Shopify-Shop-Domain`.
- Probador (`ShopifyConnectionTester`): `POST https://{shopDomain}/admin/api/{version}/graphql.json` con
  `{ shop { id name myshopifyDomain } webhookSubscriptions(first: 20) { … } }` y `X-Shopify-Access-Token`.
  200 refresca `shopName` y comprueba que los topics de §5.1 sigan registrados (si falta alguno, los vuelve a
  crear); 401 y 402 dejan `credentials_rejected`; red o 5xx, `provider_unreachable`.
- Eliminar la conexión no llama a Shopify. Pausarla detiene la conciliación y la importación; los webhooks
  que lleguen se guardan en el espejo y se importan al reanudar.
- Desinstalar la app en Shopify llega como `app/uninstalled` (§5.2): la conexión pasa a `NeedsAttention` con
  `app_uninstalled` (nuevo en `ConnectionFailureCodes`). Volver a enlazar la misma tienda al **mismo** tenant
  reutiliza la conexión: token nuevo, webhooks registrados otra vez, `Active`.
- Spec 2026-10-08 D6: lo que es del consumidor no es campo de la conexión. Asesor, cuenta de cobro y demás
  ajustes viven en `shopify.stores` (§6.1).

### 4.2 Configuración global de la app

| Clave | Dónde | Qué |
| --- | --- | --- |
| `Shopify:App:ClientId` | ConfigMap | Client ID de la app en el Dev Dashboard de Shopify |
| `Shopify:App:ClientSecret` | Secret (`SHOPIFY_APP_CLIENT_SECRET`) | Firma los webhooks y canjea el `code` |
| `Shopify:App:ApiVersion` | ConfigMap, `2026-07` por defecto | Versión de la Admin API; forma `^\d{4}-(01\|04\|07\|10)$` |
| `Shopify:App:Scopes` | ConfigMap, `read_orders,read_customers` | Lo que se pide en la autorización |
| `Shopify:App:AllowedRedirectOrigins:N` | ConfigMap | Orígenes permitidos para el `redirect_uri` del SPA |
| `Shopify:App:WebhookCallbackUrl` | ConfigMap, `https://api.qep.qcode.co/api/webhooks/shopify` | URL pública que QEP registra en cada tienda (§4.3 paso 6) |
| `Shopify:Reconcile:IntervalMinutes` | ConfigMap, `10` | Periodo de la conciliación (§5.3) |
| `Shopify:Webhook:MaxBodyBytes` | `appsettings.json`, `1048576` | Tope del cuerpo del webhook |

`ShopifyAppOptionsValidator` con `ValidateOnStart`: en `Production` exige `ClientId`, `ClientSecret`,
`WebhookCallbackUrl` (https absoluta) y al menos un origen; en cualquier ambiente valida la forma de
`ApiVersion`, de los orígenes y de la URL. Sin las claves, el pod no arranca: a propósito, como con Meta.

### 4.3 Flujo de conexión

Hay dos inicios y un solo final. Las dos variantes comparten `install/complete`, `installs/{id}` y `link`.

```text
Inicio A (desde QEP, con sesión y tenant):
  SPA ─(A1)─► POST /api/v1/tenants/{tenantId}/integrations/shopify/install/start { shop, redirectUri }
              ◄── { authorizeUrl }
Inicio B (desde Shopify, sin sesión):
  Shopify ─(B1)─► https://qep.qcode.co/shopify/install?shop=…&hmac=…&timestamp=…&host=…
  SPA ─────(B2)─► POST /api/v1/integrations/shopify/install/start { shop, hmac, timestamp, host, redirectUri }
                  ◄── { authorizeUrl }
Común:
  SPA ─(3)─► window.location = https://{shop}/admin/oauth/authorize?client_id&scope&redirect_uri&state
  Shopify ──► https://qep.qcode.co/shopify/callback?code=…&shop=…&state=…&hmac=…&timestamp=…
  SPA ─(4)─► POST /api/v1/integrations/shopify/install/complete { code, shop, state, hmac, timestamp, redirectUri }
             ◄── { installId, shopDomain, shopName, initiatedTenantId?, linkedTenant? }
  SPA ─(5)─► con initiatedTenantId y sesión en ese tenant: enlaza solo (paso 6)
             sin sesión: /login o /register con returnTo=/shopify/link/{installId}
             con sesión: /shopify/link/{installId}  (GET /api/v1/integrations/shopify/installs/{installId})
  SPA ─(6)─► POST /api/v1/tenants/{tenantId}/integrations/shopify/link { installId, name }
             ◄── 201 ConnectionResponse  →  /settings/integrations/shopify/{connectionId}
```

- **A1. Desde QEP.** `StartShopifyInstallForTenantHandler`, permiso `integrations.connection.manage`,
  módulo `shopify` activo (si no, 403 `tenancy.module_not_enabled`: la tarjeta ni aparece, por el catálogo
  filtrado). Valida la forma del dominio (`^[a-z0-9][a-z0-9-]*\.myshopify\.com$`), exige `redirectUri` de un
  origen permitido, guarda en `integrations.shopify_installs` una fila con `state` aleatorio de 32 bytes,
  `initiated_tenant_id` e `initiated_by` (membresía), vigente 15 minutos, y devuelve la URL de autorización.
  No se pide `grant_options[]=per-user`: el token tiene que ser **offline** para los workers.
- **B1-B2. Desde Shopify.** La App URL es una ruta **pública** del SPA (como `/register`).
  `StartShopifyInstallHandler` (anónimo, limitador `public`) verifica el HMAC de la query con el client secret
  (todos los parámetros menos `hmac`, ordenados, `k=v` unidos con `&`, HMAC-SHA256 en hex, comparación en
  tiempo constante), valida el dominio y el origen, y guarda la fila sin tenant.
- **3. Autorización.** Redirección de nivel superior: la app no es embebida.
- **4. Canje.** `CompleteShopifyInstallHandler` (anónimo, `public`): verifica el HMAC del callback,
  encuentra la fila por `state` (se consume: un solo uso), comprueba que `shop` coincide, canjea el `code`
  con `POST https://{shop}/admin/oauth/access_token { client_id, client_secret, code }`, exige que `scope`
  concedido contenga los configurados (`scopes_missing`), lee `shop { id name }` con el token y guarda en la
  fila el token **cifrado** con `ISecretProtector` (AAD: `installId` + `accessToken`), `shopId`, `shopName` y
  `scopes`. La fila pasa a vencer a los **7 días** (D-S16): es el tiempo que tiene la persona para entrar,
  registrarse o esperar a que QCode active el módulo. Responde `installId`, tienda, `initiatedTenantId` si
  vino de A, y `linkedTenant { tenantId, name }` si la ruta `("shopify", shopDomain)` ya existe.
- **5. Sesión y elección.** Con `initiatedTenantId` y sesión activa en ese tenant, el SPA enlaza de
  inmediato. Si no, `GET /installs/{installId}` (sólo autenticación) devuelve la tienda y los tenants activos
  del usuario con `canLink` (tiene `integrations.connection.manage`) y `moduleEnabled` (tiene `shopify`
  activo); la pantalla explica cada caso. Sin tenants, enlace a `/register` con `returnTo`; el registro es el
  de hoy (H17). Con el módulo apagado: «Pide a QCode que active la integración con Shopify en *Tenant*; la
  instalación queda guardada hasta el *fecha*».
- **6. Enlace.** `LinkShopifyStoreHandler` (`POST /tenants/{tenantId}/integrations/shopify/link`), permiso
  `integrations.connection.manage`, módulo `shopify` activo (403 si no), en una transacción de Integrations:
  - encuentra la instalación vigente con token o `install_expired`; si la fila tiene `initiated_tenant_id`,
    exige que sea este tenant;
  - tope de 20 y nombre no repetido (`name` por defecto `shopName`);
  - si existe una conexión `shopify` del **mismo tenant** con ese `shopDomain` (reenlace tras desinstalar),
    la reactiva con el token nuevo; si la ruta es de otro tenant, `shop_already_linked`; si no, crea la
    conexión y la ruta;
  - **registra los webhooks** de la tienda con `IShopifyAdminGateway.EnsureWebhooksAsync(shop, token,
    topics, callbackUrl)`: un `webhookSubscriptionCreate` por topic de §5.1 que falte (lee primero los
    existentes de la app en esa tienda), formato JSON, y guarda los ids en `webhook_subscription_ids` de la
    conexión (campo interno nuevo); si Shopify rechaza el registro, la conexión queda `NeedsAttention` con
    `webhooks_not_registered` y el probador lo reintenta;
  - borra la fila de instalación; audita `onboarding:shopify_oauth` con la variante (A o B);
  - responde `201` con la conexión. El SPA navega a la configuración de la tienda (§8).

### 4.4 Módulo `shopify` en Tenancy

- `TenantModuleKeys.Shopify = Define("shopify")`, último de `All`, fuera de `DefaultForNewTenants`, con
  dependencia declarada de `orders`. Migración del `CHECK`.
- Lo activa el usuario de plataforma desde la consola de operador con el flujo que ya existe (H16): `POST
  /operator/tenants/{id}/modules/changes`. No hay código nuevo en Tenancy más allá de la clave; el frontend
  del operador muestra la tarjeta del módulo como las de `pos`, `messaging` y `pipeline`.
- Con el módulo apagado: el proveedor desaparece del catálogo del tenant y sus conexiones se ocultan (regla
  de Integrations), los workers saltan la tienda, el webhook sigue guardando el espejo sin importar nada, y
  `/shopify/*` responde 403. Reactivarlo retoma todo, incluidos los `Waiting` acumulados.

## 5. Ingesta, espejo, conciliación y sincronización manual

### 5.1 Webhook

`POST /api/webhooks/shopify` en `Modules.Shopify.Api`, mapeado desde `Program.cs` como los de Meta:
anónimo, `RateLimiterPolicies.Webhook`, exento de CSRF por el prefijo `/api/webhooks/`, cuerpo acotado por
`Shopify:Webhook:MaxBodyBytes` (413 si excede).

Headers que importan: `X-Shopify-Hmac-Sha256` (HMAC-SHA256 del cuerpo crudo con el client secret, en
**base64**), `X-Shopify-Topic`, `X-Shopify-Shop-Domain`, `X-Shopify-Webhook-Id`, `X-Shopify-API-Version`,
`X-Shopify-Event-Id`. Firma ausente o inválida → 401 sin encolar; comparación en tiempo constante. Cuerpo
válido → `INSERT … ON CONFLICT (webhook_id) DO NOTHING` en `shopify.webhook_deliveries` y 200 **antes** de
procesar: Shopify espera respuesta en 5 segundos y, sin ella, reintenta hasta 19 veces en 48 horas y después
puede retirar la suscripción.

Topics **registrados por tienda** al enlazar (§4.3 paso 6), constantes en `ShopifyWebhookTopics` de
Integrations.Application: `orders/create`, `orders/updated`, `orders/paid`, `orders/cancelled`,
`app/uninstalled`. Los tres de cumplimiento (`customers/data_request`, `customers/redact`, `shop/redact`)
no se registran por API: Shopify sólo los admite en la configuración de la app, y ahí los pone el owner.

### 5.2 Worker de entregas

`ShopifyWebhookDeliveryWorker` (`BackgroundService`), lotes de 50, leases y 8 intentos como
`WebhookDeliveryWorker` de Messaging (H15). Por entrega:

1. Resuelve la conexión por `shop_domain` con `IShopifyConnectionDirectory.FindByShopDomainAsync`
   (puerto del módulo; adaptador en Bootstrapper sobre `connection_routes` + `IIntegrationConnections`).
   Sin ruta → procesada e ignorada (`ignored_reason = "unknown_shop"`). Con ruta pero conexión `Paused`,
   `NeedsAttention` o módulo apagado → **se aplica al espejo** igual (§5.4) y no se importa.
2. `app/uninstalled` → `IConnectionHealthReporter.ReportCredentialsRejectedAsync(…, "app_uninstalled")`.
3. Topics de cumplimiento → §5.5.
4. `orders/*` → upsert en el espejo (§5.4) e intento de importación (§6) si la tienda está operativa.

El payload de un webhook de pedidos es el REST del recurso `Order`; la conciliación trae el mismo pedido por
GraphQL. El módulo normaliza los dos a un solo modelo interno `ShopifyOrderSnapshot` (§6.2) con dos
parsers, cada uno con sus pruebas; el resto del módulo no sabe de dónde vino.

### 5.3 Conciliación (cron) y sincronización manual

`ShopifyReconcileWorker` corre cada `Shopify:Reconcile:IntervalMinutes` sobre las tiendas operativas
(`IShopifyConnectionDirectory.ListActiveAsync`, que ya filtra por módulo). Por tienda, `ReconcileStore`:

- `orders(first: 50, after: cursor, sortKey: UPDATED_AT, query: "updated_at:>={desde}")`, con `desde` =
  `last_reconciled_at − 5 min` de `shopify.stores`. Al terminar, `last_reconciled_at` = ahora. Al enlazar,
  `last_reconciled_at` = momento del enlace: **no se trae historial** (D-S4).
- Cada pedido pasa por el mismo upsert del espejo que el webhook (§5.4) e intento de importación.
- Después, reintenta los `Waiting` de la tienda (§6.4), tope 200 por corrida, los más viejos primero.
- `THROTTLED` o 429: espera `Retry-After` (o 2 s) y sigue; tras 3 seguidos, la tienda se deja para la
  próxima corrida. 401 o 402 → `ReportCredentialsRejectedAsync` con `credentials_rejected`; la tienda sale
  del ciclo hasta que la conexión vuelva a `Active`.
- Una corrida no se solapa con otra de la misma tienda: `shopify.stores.reconcile_lease_until`.

**Sincronización manual.** `POST /tenants/{tenantId}/shopify/stores/{connectionId}/sync` (permiso
`quotations.order.manage`) ejecuta `ReconcileStore` en el request con la misma ventana (desde el último
cursor) y devuelve `{ fetched, imported, waiting, cancelled, ignored, durationMs }`. Si hay una corrida en
curso (lease vigente), responde 409 `shopify.store.sync_in_progress`. Es el botón «Sincronizar ahora» de la
tienda y de la bandeja.

### 5.4 Espejo: `shopify.orders`

Una fila por pedido de Shopify y conexión, única por `(connection_id, shopify_order_id)`:

| Columna | Qué |
| --- | --- |
| `id`, `tenant_id`, `connection_id` | identidad |
| `shopify_order_id` (bigint), `order_name` (`#1001`), `shopify_customer_id` (bigint, null) | el pedido y su comprador en Shopify |
| `shopify_created_at`, `shopify_updated_at`, `cancelled_at`, `financial_status`, `currency`, `total_price`, `taxes_included` | lo que la bandeja muestra sin abrir el payload |
| `payload` (jsonb) | el último snapshot normalizado |
| `import_state` | `Waiting` · `Imported` · `Cancelled` · `Ignored` |
| `wait_reason`, `wait_detail` (jsonb) | §6.2 |
| `warnings` (jsonb) | avisos de una importación que sí entró (§6.2.4): `customer_incomplete`, `shipping_address_unresolved`, `total_difference` |
| `attempts`, `last_attempt_at`, `imported_at` | historia de la importación |
| `qep_customer_id`, `qep_quotation_id`, `qep_order_id` | referencias blandas |
| `total_difference` | `total_price − Total de QEP` tras importar |

Reglas del upsert:

- Un snapshot con `shopify_updated_at` **anterior** al guardado no pisa nada: los webhooks llegan
  desordenados y la conciliación trae lo mismo con retraso.
- Una fila `Imported` no vuelve a `Waiting`: los cambios posteriores sólo tocan pago y cancelación (§6.3).
- `Ignored` es para pedidos que QEP no va a traer: `test = true` y los cancelados antes de importarse. Se
  guardan para que la bandeja los muestre con filtro.

### 5.5 Webhooks de cumplimiento

Una app pública **debe** responder 200 a los tres topics de privacidad, y Shopify lo verifica en la
revisión. QEP no borra ni anonimiza clientes ni pedidos por un aviso de la plataforma: son registros
contables del tenant, que es el responsable del dato; QEP actúa como su encargado.

- `customers/data_request`: se audita (`shopify.compliance.data_request`, con `shop_domain`, id del cliente
  en Shopify y los ids de pedidos) para que el owner atienda la solicitud en el plazo de Shopify (30 días).
- `customers/redact`: se audita igual. El tenant decide qué hacer con la ficha desde Customers.
- `shop/redact` (48 horas después de desinstalar): borra `shopify.orders` y `shopify.webhook_deliveries` de
  esa tienda; `shopify.stores` y la conexión quedan como estén. Los pedidos y clientes de QEP no se tocan.

## 6. De pedido de Shopify a pedido de QEP

### 6.1 Configuración de la tienda: `shopify.stores`

Una fila por conexión, creada vacía al enlazar (la crea el módulo Shopify al recibir el primer webhook o la
primera lectura de la pantalla; no hay evento de conexión creada, H14) y llenada por el `PUT` de §7. Hasta
que no tenga asesor y cuenta de cobro, todo pedido espera con `store_not_configured`.

| Campo | Qué |
| --- | --- |
| `connection_id` (PK), `tenant_id` | identidad |
| `advisor_membership_id` | membresía **activa** del asesor de la tienda (`shopify.store.advisor_not_member` al guardar si no lo es) |
| `billing_company_id`, `billing_bank_name`, `billing_account_number`, `billing_currency` | la cuenta de cobro, copiada como `QuotationBillingAccount`; al guardar se valida contra Companies (`billing_account_not_found`) |
| `default_classification_id` (null) | clasificación con la que nace un cliente **completo**; sin ella, el comprador siempre nace incompleto aunque traiga documento (§6.2.1) |
| `document_attribute_key`, `document_type_attribute_key` | claves en `customAttributes` del pedido; por defecto `Documento` y `Tipo de documento` |
| `default_identification_type` | `CC` por defecto; se usa cuando el pedido no trae tipo |
| `last_reconciled_at`, `reconcile_lease_until` | §5.3 |
| `version`, `updated_at`, `updated_by` | concurrencia optimista con `If-Match` |

### 6.2 Importación

`ShopifyOrderImporter` (Application del módulo) toma un `ShopifyOrderSnapshot`, lo evalúa y llama a **un**
método del puerto `IExternalOrderWriter.CreateAsync(ExternalOrderDraft)` cuyo adaptador en Bootstrapper
invoca un servicio de aplicación nuevo de Quotations (`ExternalOrderService`) que crea cotización y pedido en
**una transacción** del `QuotationsDbContext`. Ninguna de las dos partes pasa por los handlers HTTP (H4).
La auditoría de Quotations va con `AuditActorType.Integration` y referencia la conexión.

**Precondiciones, en este orden.** La primera que falla deja la fila `Waiting` con su motivo y detiene la
evaluación; los motivos viajan al frontend con su nombre, que tiene el diccionario de textos:

| `wait_reason` | Cuándo | `wait_detail` | Cómo se resuelve |
| --- | --- | --- | --- |
| `store_not_configured` | sin asesor o sin cuenta de cobro | — | configurar la tienda (§8) |
| `advisor_inactive` | la membresía del asesor no está activa | — | cambiar el asesor |
| `billing_account_missing` | la cuenta ya no existe en la empresa | — | elegir otra cuenta |
| `currency_mismatch` | la moneda del pedido no es la de la cuenta de cobro | `{ orderCurrency, billingCurrency }` | elegir una cuenta en esa moneda |
| `customer_inactive` | el cliente encontrado (§6.2.1) está inactivo | `{ customerId }` | reactivarlo |
| `product_unmatched` | algún SKU no es un `Product.Code` activo del tenant, o la línea no trae SKU | `{ skus: [...] }` | crear o corregir el producto |
| `freight_unsupported` | el pedido trae `shipping_lines` con monto > 0 y el flete no está implementado | `{ shippingTitle, amount }` | se levanta con el spec de flete |
| `quotation_rejected` | una regla de Quotations tiró un código que esta lista no previó | `{ code }` | corregir lo que el código señala y reintentar |

El cliente **nunca** detiene la importación salvo que exista y esté inactivo: sin documento, sin ciudad o
sin clasificación, nace incompleto (decisión 2 revisada).

#### 6.2.1 Cliente

1. **Documento.** `customAttributes[document_attribute_key]`; el tipo, de
   `customAttributes[document_type_attribute_key]` con `IdentificationTypeParser`, o el
   `default_identification_type` de la tienda. Número recortado, tope 32 (H7).
2. **Búsqueda**, por `IShopifyCustomerDirectory`, en este orden y se queda con el primero: por documento
   (`FindByIdentificationAsync`); por `shopify_customer_id` ya visto en el espejo de esta tienda
   (`qep_customer_id` de un pedido anterior del mismo comprador); por teléfono E.164
   (`ICustomerPhoneDirectory.MatchAsync`); por email exacto sin mayúsculas (método nuevo
   `FindByEmailAsync` en Customers; H8). Un cliente encontrado se usa tal cual, completo o incompleto; si
   está inactivo → `customer_inactive`.
3. **Alta completa** si hay documento **y** la tienda tiene `default_classification_id` **y** la dirección
   de envío se resuelve a ciudad (o el país no es `CO`): `ICustomerProvisioning.CreateCompleteAsync`
   (servicio nuevo de Customers.Application, sin autorización HTTP, auditoría con actor `Integration`):
   nombre `firstName lastName` del comprador o de la dirección, documento, teléfono, email, dirección
   (`address1` + `address2`), país (`countryCodeV2`), ciudad, clasificación, `WithRetention = false`,
   `VatSurplus = false`. Emite CUC. Si el documento quedó tomado entre la búsqueda y el alta (índice único),
   se busca otra vez y se usa el existente.
4. **Alta incompleta** en cualquier otro caso: `Customer.CreateIncompleteFromExternalSale(name, phone, email,
   country)` (factoría nueva en Customers, hermana de las de WhatsApp e Instagram): nombre (o «Cliente de
   Shopify» si no viene), teléfono normalizado, email, país; sin documento, dirección, clasificación ni CUC.
   El documento que sí vino pero no alcanzó para completar (sin clasificación o sin ciudad) queda en
   `warnings.customer_incomplete.document` del espejo para que quien complete la ficha lo tenga a la mano.
5. **Ciudad.** Sólo si el país es `CO`. Shopify entrega `province` y `provinceCode` (ISO 3166-2 sin `CO-`:
   `ANT`, `DC`, `CUN`…). Geography no tiene código ISO (H10): el módulo trae una tabla **en código**
   `ColombiaProvinceCodes` (33 entradas) que mapea código → nombre de departamento, y luego
   `FindByNameAsync(departmentId, city)` por `IShopifyGeographyLookup`. Sin match por código, se intenta
   `province` por nombre. Sin ciudad, no hay parte de envío (§6.2.3) y el cliente nace incompleto.

#### 6.2.2 Productos y precios

- Cada línea: `sku` → `IShopifyProductLookup.FindByCodeAsync(tenantId, sku)` (adaptador sobre
  `IProductRepository.FindByCodeAsync`, H11): id, nombre, código, `IsActive`, `TaxPercentage`. Inactivo o
  inexistente → `product_unmatched`. Se resuelven **todas** las líneas antes de fallar para que
  `wait_detail.skus` traiga la lista completa.
- Precio unitario con IVA (H3): si `taxes_included`, `originalUnitPrice`; si no,
  `originalUnitPrice × (100 + TaxPercentage) / 100`. La tasa es la del **producto de QEP** (D-S3).
- Descuento: `Σ discountAllocations.amount` de la línea sobre `originalUnitPrice × quantity`, como porcentaje
  con 4 decimales; `AddItem` lo recibe resuelto.
- Flete: `freight_unsupported` hasta el spec de flete.

#### 6.2.3 Cotización y pedido (dentro de `ExternalOrderService`)

1. Elegibilidad del cliente con la **excepción de origen externo** (owner, 2026-10-15: «sólo deja pasar el
   pedido incompleto si no viene de cotización»): `QuotationCustomerEligibility.Ensure` gana un parámetro
   `allowIncomplete` que **sólo** `ExternalOrderService` pasa en `true`, y sólo porque la cotización que crea
   nace con `Origin = Shopify`; con él, `client_incomplete` y `client_cuc_missing` no se lanzan,
   `client_not_found` y `client_inactive` sí. Los cuatro llamadores de hoy (H2: crear, cambiar cliente, enviar
   y convertir una cotización manual) **no cambian**: una cotización `Manual` sigue exigiendo cliente completo
   con CUC para convertirse en pedido. La prueba de arquitectura/unitaria verifica que ningún handler HTTP
   pase `allowIncomplete: true`.
2. `Quotation.Create` con `Origin = Shopify` (§6.5), cliente, asesor de la tienda, `validUntil` = fecha del
   pedido en la zona del tenant, `paymentMethod` = `gateway` recortado, notas = `Shopify #1001` + la nota
   del comprador (tope 500), cuenta de cobro de la tienda, moneda del pedido, `customerWithRetention` y
   `customerVatSurplus` del cliente (falsos en uno incompleto). Partes: `Shipping` con la dirección de envío
   **sólo si queda completa** (nombre, teléfono, dirección, departamento y ciudad); si no, sin parte de envío
   y la dirección cruda va a las notas con el aviso `shipping_address_unresolved`; `IsStorePickup = true` si
   el método de envío es de recogida (`shipping_lines[].code` empieza por `pickup`); `Billing` vacío (factura
   a los datos del cliente).
3. `AddItem` por línea, en el orden de Shopify.
4. `ConvertToOrder` (H2: `Draft` convierte sin enviar). Si `EnsureConvertibleToOrder` tira, el motivo se
   guarda como `quotation_rejected`.
5. `Order.Create` con el consecutivo del tenant (`PED-…`), `Source = Shopify`, `ExternalReference = "#1001"`,
   `PaymentStatus` desde `financial_status` (`paid` → `FullPaymentReceived`; `partially_paid` →
   `PartialPaymentReceived`; el resto → `PaymentPending`), sin comprobantes, `convertedBy` = asesor de la
   tienda.
6. Historial de la cotización (`Created`, `Converted`) y auditoría con actor `Integration`.
7. `quotations.quotation.converted.v1` sale al outbox como siempre; Pipeline, si está prendido, gana el
   negocio del cliente a nombre del asesor de la tienda (H18). No se emite `sent`.

#### 6.2.4 Resultado

La fila del espejo pasa a `Imported` con `qep_customer_id`, `qep_quotation_id`, `qep_order_id`,
`total_difference` = `total_price` de Shopify − `Total` de QEP, y `warnings`: `customer_incomplete` (con el
documento si vino), `shipping_address_unresolved`, `total_difference` si supera el 1 % del total (D-S6). La
bandeja muestra los avisos en una pestaña «Con avisos»; ninguno frena la importación.

### 6.3 Cambios posteriores

- **Pago.** Un snapshot con otro `financial_status` sobre una fila `Imported` llama a
  `IExternalOrderWriter.SyncPaymentStatusAsync(orderId, status)` → `Order.SyncExternalPaymentStatus` (método
  nuevo, H5): sólo cambia si el pedido **no tiene comprobantes** propios; con comprobantes, el aviso se audita
  y se ignora. `refunded`, `partially_refunded` y `voided` no cambian nada.
- **Cancelación.** `cancelled_at` no nulo sobre una fila `Imported`: pedido `Pending` o `Approved` →
  `IExternalOrderWriter.CancelAsync(orderId, "Cancelado en Shopify: {cancel_reason}")` a nombre del asesor
  de la tienda; `Invoiced` → la fila pasa a `Cancelled` con `wait_detail = { reason: "invoiced_before_cancel" }`
  y la bandeja la muestra para revertir la facturación y anular a mano; ya `Cancelled` en QEP → nada. Sobre
  una fila `Waiting` → `Ignored`.
- **Líneas, dirección y cliente** editados en Shopify después de importar: no se sincronizan.

### 6.4 Reintentos

- **Automático:** cada corrida de conciliación (§5.3) reintenta los `Waiting` de la tienda. Completar una
  ficha o crear un producto surte efecto en menos de `IntervalMinutes` sin suscribir el módulo a eventos de
  Customers ni de Catalog (D-S5).
- **Manual:** `POST …/shopify/orders/{id}/retry` (§7) corre la importación en el request y devuelve la fila.
  Sobre una fila que no está `Waiting` → 422 `shopify.order.not_waiting`. «Sincronizar ahora» (§5.3) también
  reintenta los `Waiting` de la tienda.
- `attempts` y `last_attempt_at` se actualizan siempre; sin tope: un pedido en espera no se abandona solo.

### 6.5 Cambios en Quotations, Customers y Reporting

- `Quotation.Origin` (`Manual` | `Shopify`) y `Order.Source` (`Quotation` | `Shopify`) +
  `Order.ExternalReference` (hasta 64). Migración con backfill `Manual` / `Quotation`. En los DTO viajan como
  texto con el nombre del enum (regla del BFF).
- `QuotationCustomerEligibility.Ensure(…, allowIncomplete: false)`: parámetro con default; sólo
  `ExternalOrderService` lo pasa en `true`, para cotizaciones con `Origin != Manual`. Un pedido que viene de
  una cotización manual conserva la regla de hoy: cliente completo con CUC o `client_incomplete`.
- `ListOrdersQuery.Source` (opcional; sin él, todos) y `ListQuotationsQuery.Origin` (opcional; **sin él,
  sólo `Manual`**): una cotización que nace convertida confundiría al asesor en la lista y la pantalla no la
  puede editar ni enviar. El detalle por id responde cualquiera.
- `OrderListItemDto`, `OrderDto` y `QuotationDto` ganan `source` / `origin` / `externalReference`.
- Customers: `ICustomerProvisioning` (alta completa desde el sistema), `Customer.CreateIncompleteFromExternalSale`,
  `FindByEmailAsync`. El `PUT` que completa un incompleto ya existe (spec 2026-10-10 §6.2) y sirve igual.
- Reporting: `QuotationsReportSource` filtra `Origin = Manual` en los tres reportes de cotizaciones;
  `OrdersReportSource` no filtra. Los reportes que agrupan por clasificación o ciudad ya filtran
  `Completeness == Complete` (CLAUDE.md), así que un comprador incompleto no los rompe.
- El Excel de pedidos no cambia de columnas; `order_number` sigue siendo el `PED-…` (D-S2); con cliente
  incompleto, `Documento` y `Documento de identidad` salen vacías (H9), que es lo que el owner aceptó.

## 7. API (BFF)

Sin permisos nuevos: configurar la tienda es administrar integraciones; la bandeja y la sincronización son
gestión de pedidos. Las rutas de `/shopify/*` y las de `/integrations/shopify/*` por tenant pasan por
`TenantModuleGuard` (`shopify`); las anónimas de instalación no, porque todavía no hay tenant.

| Ruta | Permiso | Para qué |
| --- | --- | --- |
| `POST /api/v1/tenants/{tenantId}/integrations/shopify/install/start` | `integrations.connection.manage` | Inicio A. `{ shop, redirectUri }` → `{ authorizeUrl }` |
| `POST /api/v1/integrations/shopify/install/start` | anónimo, `public` | Inicio B. `{ shop, hmac, timestamp, host, redirectUri }` → `{ authorizeUrl }` |
| `POST /api/v1/integrations/shopify/install/complete` | anónimo, `public` | Canje. `{ code, shop, state, hmac, timestamp, redirectUri }` → `{ installId, shopDomain, shopName, expiresAt, initiatedTenantId, linkedTenant }` |
| `GET /api/v1/integrations/shopify/installs/{installId}` | sólo autenticación | `{ shopDomain, shopName, expiresAt, initiatedTenantId, tenants: [{ tenantId, name, canLink, moduleEnabled }] }`; viajan todos los tenants activos del usuario para que la pantalla explique cada caso |
| `POST /api/v1/tenants/{tenantId}/integrations/shopify/link` | `integrations.connection.manage` | `{ installId, name? }` → 201 `ConnectionResponse` |
| `GET /api/v1/tenants/{tenantId}/shopify/stores` | `integrations.connection.read` | Tiendas: `connectionId`, `name`, `shopDomain`, `status`, `isConfigured`, `waitingCount`, `lastReconciledAt` |
| `GET /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}` | `integrations.connection.read` | Configuración (§6.1) con `advisorName`, `billingCompanyName`, `classificationName`, `version` (ETag) |
| `PUT /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}` | `integrations.connection.manage`, `If-Match` | Guarda la configuración; 428/412 como el resto del repo |
| `POST /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/sync` | `quotations.order.manage` | Sincronización manual (§5.3) → `{ fetched, imported, waiting, cancelled, ignored, durationMs }`; 409 si ya corre |
| `GET /api/v1/tenants/{tenantId}/shopify/orders?state=&connectionId=&withWarnings=&page=&pageSize=` | `quotations.order.read` | Espejo paginado (tope 50): `id`, `orderName`, `storeName`, `shopifyCreatedAt`, `financialStatus`, `totalPrice`, `currency`, `importState`, `waitReason`, `waitDetail`, `warnings`, `attempts`, `lastAttemptAt`, `qepCustomerId`, `qepOrderId`, `qepOrderNumber`, `totalDifference`, `customerName`, `customerEmail`. `state` por defecto `Waiting`; acepta varios |
| `GET /api/v1/tenants/{tenantId}/shopify/orders/summary` | `quotations.order.read` | `{ waiting, withWarnings, cancelledAfterInvoice, importedToday }`, **completo incluso en cero** |
| `POST /api/v1/tenants/{tenantId}/shopify/orders/{id}/retry` | `quotations.order.manage` | Reintento inmediato; devuelve la fila |
| `POST /api/webhooks/shopify` | anónimo, `webhook` | §5.1 |

Cambios en rutas existentes: `GET …/orders` acepta `source`; `GET …/quotations` acepta `origin`;
`OrderListItemResponse`, `OrderDto` y `QuotationDto` ganan los campos de §6.5.

### 7.1 Errores

| Código | Status | Cuándo |
| --- | --- | --- |
| `integrations.shopify.hmac_invalid` | 401 | HMAC de la query de instalación o del callback no coincide |
| `integrations.shopify.shop_invalid` | 422 | el dominio no tiene la forma `*.myshopify.com` |
| `integrations.shopify.install_not_found` | 404 | `installId` o `state` desconocido |
| `integrations.shopify.install_expired` | 422 | la fila venció (15 min sin canje, 7 días sin enlace) |
| `integrations.shopify.install_tenant_mismatch` | 403 | la instalación se inició desde otro tenant |
| `integrations.shopify.code_exchange_failed` | 422 | Shopify no canjeó el `code` |
| `integrations.shopify.scopes_missing` | 422 | lo concedido no contiene `Shopify:App:Scopes` |
| `integrations.shopify.shop_already_linked` | 422 | la tienda ya está enlazada (a este tenant o a otro; el detalle dice cuál) |
| `integrations.shopify.webhooks_not_registered` | 422 | Shopify rechazó el registro de webhooks al enlazar (la conexión queda `NeedsAttention`) |
| `integrations.connection.limit_reached`, `name_taken` | 422 | existentes |
| `shopify.store.advisor_not_member` | 422 | el asesor no es membresía activa del tenant |
| `shopify.store.billing_account_not_found` | 422 | la cuenta no existe en esa empresa |
| `shopify.store.classification_not_found` | 422 | la clasificación no es del tenant |
| `shopify.store.sync_in_progress` | 409 | ya hay una corrida de esa tienda |
| `shopify.order.not_found` | 404 | fila del espejo inexistente en el tenant |
| `shopify.order.not_waiting` | 422 | reintentar una fila que no está `Waiting` |
| `validation.failed` | 422 | campos del `PUT` y de los cuerpos, con el mapa `errors` |
| `tenancy.module_not_enabled` | 403 | módulo `shopify` apagado (existente) |

Los motivos de espera y los avisos (§6.2) **no** son errores HTTP.

## 8. Frontend (`qep-frontend`)

| Dónde | Qué |
| --- | --- |
| `features/operator/` | Tarjeta del módulo `shopify` como las de `pos`, `messaging` y `pipeline`; sin código nuevo si el catálogo de módulos viene del backend. |
| `features/integrations/` | `onboarding.kind === 'ShopifyOAuth'`: botón «Nueva integración de Shopify» abre un diálogo que pide el dominio (`mi-tienda.myshopify.com`, con ayuda) y llama al inicio A con `redirectUri = origin + '/shopify/callback'`; redirige a `authorizeUrl`. Debajo, «También puedes instalar QEP desde el App Store de Shopify» con `VITE_SHOPIFY_APP_URL`. Por conexión: «Configurar tienda», `waitingCount` y «Sincronizar ahora». |
| `routes/shopify/install.tsx` (pública) | Inicio B: lee `shop`, `hmac`, `timestamp`, `host`; llama al inicio anónimo; redirige. Errores: `hmac_invalid` / `shop_invalid` → «Abre QEP desde tu tienda de Shopify». |
| `routes/shopify/callback.tsx` (pública) | Canje; con `initiatedTenantId` y sesión en ese tenant, enlaza solo y navega a la configuración; con `linkedTenant`, «Esta tienda ya está enlazada a *X*» y botón a ese tenant; si no, `/shopify/link/$installId`, pasando por `/login` o `/register` con `returnTo` cuando no hay sesión. |
| `routes/_authenticated/shopify/link.$installId.tsx` | Tienda, vencimiento y lista de tenants: elegible, «sin permiso para administrar integraciones», o «módulo no activado: pide a QCode que lo active; la instalación queda guardada hasta el *fecha*». Nombre de la conexión editable. Sin tenants: enlace a `/register` con `returnTo`. `install_expired`: «Vuelve a conectar la tienda». |
| `routes/_authenticated/settings/integrations/shopify.$connectionId.tsx` | Formulario de §6.1: asesor (membresías activas), empresa y cuenta de cobro, clasificación por defecto (opcional, con la explicación de qué pasa sin ella), claves de atributos, tipo de documento por defecto. `If-Match`; 412 → «Alguien cambió esta configuración, recarga». Aviso mientras `isConfigured === false`. Botón «Sincronizar ahora» con el resumen que devuelve. |
| `features/orders/` | Columna «Origen» (insignia `Shopify` con `#1001` debajo del `PED-…`) y filtro «Origen». Pestaña «Desde Shopify» con dos vistas: **En espera** (`Waiting` + `Cancelled` tras facturar, motivo en texto desde `describe-wait-reason.ts`, acción contextual: `product_unmatched` → SKUs y enlace a Catálogo; `store_not_configured` → «Configurar tienda»; `customer_inactive` → «Ver cliente»; «Reintentar» con `quotations.order.manage`) y **Con avisos** (`Imported` con `warnings`: «Cliente incompleto» con «Completar ficha» prellenando el documento si vino, «Dirección sin ciudad», «Total distinto al de Shopify»). Insignias con `summary` en la pestaña. |
| `features/quotes/` | Filtro «Origen» oculto por defecto en `Manual`; una cotización `Shopify` se abre en lectura con aviso «Nació de un pedido de Shopify; se gestiona desde Pedidos». |
| `features/customers/` | Un cliente incompleto que viene de Shopify se completa con el `PUT` de hoy; la ficha muestra «Creado desde Shopify» si `warnings` lo dice (dato del espejo, por `qepCustomerId`). |
| `features/auth/` | `/login` y `/register` aceptan `returnTo` interno (misma app, ruta relativa) y vuelven ahí. |

Textos en español con tuteo; sin exponer el token ni el `state` en logs del navegador.

## 9. Base de datos

Esquema nuevo `shopify` (`ShopifyDbContext`, migraciones en
`Modules.Shopify.Infrastructure/Persistence/Migrations`):

- `shopify.stores` (§6.1). PK `connection_id`; índice `(tenant_id)`.
- `shopify.orders` (§5.4). Único `(connection_id, shopify_order_id)`; índices `(tenant_id, import_state,
  shopify_created_at DESC)`, `(tenant_id, qep_order_id)`, `(connection_id, shopify_customer_id)`.
- `shopify.webhook_deliveries`: `id`, `webhook_id` (único), `topic`, `shop_domain`, `api_version`,
  `payload` jsonb, `received_at`, `attempts`, `lease_until`, `processed_at`, `ignored_reason`, `last_error`.
- `shopify.audit_entries`: proyección propia de `audit.entries` para `IShopifyAuditRecorder`, como
  Messaging e Integrations.

Integrations:

- `integrations.shopify_installs`: `id`, `shop_domain`, `state` (único), `redirect_uri`,
  `initiated_tenant_id` (null), `initiated_by` (null), `shop_id`, `shop_name`, `scopes`,
  `access_token_protected` (bytea, null hasta el canje), `key_id`, `created_at`, `expires_at`. Un
  `IHostedService` barre las vencidas cada hora. El worker de re-cifrado de llaves
  (`ConnectionSecretRekeyWorker`) **sí** la incluye: una fila puede vivir 7 días.
- Campo interno `webhookSubscriptionIds` en la conexión (`fields`), separado por coma.
- Migración del `CHECK` de `provider_key` con `shopify`.

Tenancy: migración del `CHECK` de `tenant_modules.module_key` con `shopify`.

Quotations: `quotations.quotations.origin` (`text`, default `Manual`, `CHECK`), `quotations.orders.source`
(`text`, default `Quotation`, `CHECK`), `quotations.orders.external_reference` (`varchar(64)`, null); índice
`(tenant_id, source)`.

Customers: sin columnas nuevas (`FindByEmailAsync` usa `email` con índice `(tenant_id, lower(email))`
nuevo).

## 10. Configuración y despliegue

1. **Variables del pipeline** (`Backend-prod`): `SHOPIFY_APP_CLIENT_ID` normal y `SHOPIFY_APP_CLIENT_SECRET`
   **secreta**. `k8s/prod-configMap.yaml`: `Shopify__App__ClientId`, `Shopify__App__ApiVersion`,
   `Shopify__App__Scopes`, `Shopify__App__AllowedRedirectOrigins__0: "https://qep.qcode.co"`,
   `Shopify__App__WebhookCallbackUrl`, `Shopify__Reconcile__IntervalMinutes`. `k8s/prod-secret.yaml`:
   `Shopify__App__ClientSecret`. Sin ellas el pod entra en crash-loop por `ValidateOnStart`: a propósito.
2. **Local:** user-secrets `Shopify:App:ClientId` y `Shopify:App:ClientSecret` (verificar con
   `Measure-Object`, nunca listando el valor); `appsettings.Development.json` con
   `AllowedRedirectOrigins: ["http://localhost:3002"]` y `WebhookCallbackUrl` del túnel. Sin túnel, la
   conciliación y «Sincronizar ahora» traen los pedidos igual.
3. **Orden:** backend, luego frontend; después, el operador activa el módulo al primer tenant piloto.
   Rollback del backend: el SPA muestra rutas que responden 404; nada se corrompe.
4. **Frontend:** `VITE_SHOPIFY_APP_URL` con el enlace de instalación de la app.
5. **Handoff al owner** (`docs/superpowers/plans/<fecha>-shopify-handoff.md`): app en el Dev Dashboard de
   Shopify, **no embebida**, distribución pública (listada o no listada: las dos pasan por revisión), App URL
   `https://qep.qcode.co/shopify/install`, redirect URLs de prod y local, scopes `read_orders` y
   `read_customers`, los tres webhooks de cumplimiento en la configuración de la app, solicitud de **acceso
   a datos protegidos de clientes** (sin ella el payload llega sin nombre, email, teléfono ni dirección, y
   todo comprador nace incompleto y sin forma de encontrarlo después), tienda de desarrollo para probar, y
   las verificaciones de §16.

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
- Una instalación iniciada desde un tenant sólo se puede enlazar a ese tenant (`install_tenant_mismatch`).
- `GET /installs/{installId}` responde 404 para cualquier id desconocido y lista sólo los tenants activos
  del usuario autenticado. El `installId` es un GUID aleatorio.
- El enlace y la configuración exigen `integrations.connection.manage` **en el tenant elegido**, revalidado
  en el handler; aislamiento de tenant en todo `shopify.*`: 403, nunca 404, para un recurso ajeno.
- El webhook no revela si una tienda existe: 200 siempre que la firma sea válida.
- Limitador `webhook` para el webhook y `public` para las rutas anónimas de instalación.

## 13. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias (`tests/Modules/Shopify/Modules.Shopify.UnitTests`):** parsers REST y GraphQL → el mismo
`ShopifyOrderSnapshot` (fixtures reales de una tienda de desarrollo: `taxes_included` en los dos modos,
descuentos, recogida, pedido de prueba, cancelado, sin documento, sin ciudad); verificación HMAC con
vectores conocidos; `ColombiaProvinceCodes` completa contra Geography; evaluador de precondiciones (orden,
`wait_detail`); decisión completo/incompleto del cliente (documento × clasificación × ciudad); avisos;
mapeo de `financial_status`; máquina de estados del espejo; precio con IVA y descuento;
`Quotation.Origin`, `Order.Source`, `Order.SyncExternalPaymentStatus`;
`QuotationCustomerEligibility.Ensure` con y sin `allowIncomplete`.

**Integración (`Modules.Shopify.IntegrationTests`, Testcontainers, `ShopifyApiHarness` que prende el módulo
y crea tienda configurada, asesor, empresa con cuenta, clasificación y productos):** webhook firmado → 200 y
fila; sin firma → 401; duplicado; tienda desconocida; importación con documento → cliente completo con
CUC; sin documento → cliente incompleto y pedido `Pending` igual, con `warnings`; segundo pedido del mismo
comprador reutiliza el cliente (por `shopify_customer_id`, teléfono y email); cliente inactivo → espera;
cada motivo de espera y su reintento manual y automático; cancelación sobre `Pending`, `Approved`,
`Invoiced` y `Waiting`; pago con y sin comprobantes; conciliación con `HttpMessageHandler` falso
(paginación, `THROTTLED`, 401); «Sincronizar ahora» y su 409; conexión pausada y módulo apagado; tenant
ajeno 403; Pipeline gana el negocio; Excel de pedidos con cliente incompleto sale con las dos columnas
vacías; fuga de token con sentinela.

**Integración en Integrations:** inicio A (permiso, módulo apagado 403, dominio inválido), inicio B (HMAC
bueno y malo, origen no permitido), canje (`state` desconocido, vencido, reutilizado; gateway falso; scopes
faltantes), `installs/{id}` (404 ajeno, `canLink` y `moduleEnabled`), enlace (crea conexión, ruta y
**webhooks** con gateway falso; registro rechazado → `NeedsAttention`; reenlace del mismo tenant; otro
tenant → `shop_already_linked`; `install_tenant_mismatch`; tope 20), probador que repone webhooks.

**Quotations y Customers:** barrer las pruebas que arman cuerpos a mano por los campos nuevos antes de
correr nada; `ListOrders` por `source`; `ListQuotations` excluye `Shopify` por defecto; Reporting excluye en
cotizaciones y no en pedidos; `ICustomerProvisioning` y `CreateIncompleteFromExternalSale`.

**Arquitectura:** `ShopifyLayerTests` (capas; sólo Tenancy y Audit entre los módulos; prohibido referenciar
Integrations, Quotations, Customers, Catalog y Geography); `IntegrationsLayerTests` sigue prohibiendo que
un módulo de negocio referencie `Modules.Integrations.Application`; `ConfigurationExampleTests`.

**Frontend (Vitest + Testing Library):** las rutas nuevas (carga, error, éxito, `returnTo`, enlace
automático con `initiatedTenantId`, módulo apagado), diálogo de dominio, formulario de tienda (validación,
412), bandeja en sus dos vistas con cada motivo y aviso, «Sincronizar ahora», columna y filtro de origen,
cotización `Shopify` en lectura.

**Suite por módulo tocado**, no la completa (memoria del repo).

## 14. Fuera de alcance

- Flete en cotizaciones: spec aparte, previo.
- Historial de pedidos anterior al enlace (D-S4).
- Reembolsos, devoluciones, ediciones de líneas o de dirección después de importar.
- Sincronización hacia Shopify: fulfillment, tracking, inventario, facturas.
- App embebida (App Bridge, token exchange) y la ficha en el App Store.
- Otras plataformas (WooCommerce, VTEX, Mercado Libre).
- Comprobantes de pago para pedidos de la tienda: el pago lo registra Shopify.
- Clientes de Shopify sin pedido (sincronizar la libreta).
- Activación automática del módulo al instalar: el owner la descartó por ahora; si se quiere después, es un
  servicio de Tenancy que reutilice `ChangeTenantModulesHandler` y una llamada en el enlace.

## 15. Decisiones tomadas sin el owner (a ratificar)

| ID | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-S1 | Tope de **20 tiendas por tenant** (el genérico); `state` vence a 15 min; conciliación cada 10 min con 5 min de solape; 200 reintentos por corrida | Constantes |
| D-S2 | El número del pedido de QEP es el **consecutivo del tenant** (`PED-…`); `#1001` va en `ExternalReference`, en la lista y en las notas | Un campo en el Excel o una opción por tienda |
| D-S3 | La **tasa de IVA es la del producto de QEP**; la diferencia de total se avisa, no frena | Un motivo de espera (`tax_mismatch`) |
| D-S4 | **No se importa historial** anterior al enlace | Un botón «Traer los últimos N días» |
| D-S5 | Reintento automático **por corrida de conciliación**, sin suscribirse a eventos de Customers ni Catalog | Dos consumidores de outbox |
| D-S6 | Diferencia de total > 1 % se **avisa**, no frena | Un motivo de espera |
| D-S7 | Cliente nuevo nace con `WithRetention = false` y `VatSurplus = false`; facturación a los datos del cliente, nunca a consumidor final | Dos campos más por tienda |
| D-S8 | Orden de búsqueda del comprador: documento, `shopify_customer_id` visto, teléfono, email; completo o incompleto, se usa | Quitar un paso |
| D-S9 | Los webhooks de cumplimiento **sólo auditan**; `shop/redact` borra espejo y entregas, no pedidos ni clientes | Un flujo de anonimización |
| D-S10 | Sin parte de envío cuando la ciudad no resuelve; la dirección va a las notas con aviso | Un motivo de espera (`city_unresolved`) |
| D-S11 | `ListQuotations` **excluye** `Shopify` por defecto; Reporting de cotizaciones lo excluye siempre | Un flag |
| D-S12 | Pedidos `test = true` y cancelados antes de importar → `Ignored`, visibles con filtro | Importarlos |
| D-S13 | Nombre de la conexión por defecto = `shopName`; la configuración vive en `shopify.stores` (D6 de Integrations) | Mover campos |
| D-S14 | Sin permiso nuevo: `integrations.connection.*` para la tienda y `quotations.order.*` para bandeja y sincronización | Un permiso `shopify.store.manage` con sus cuatro piezas |
| D-S15 | `Billing` vacío (factura al cliente); `paymentMethod` = `gateway` | Otra parte de facturación |
| D-S16 | Una instalación canjeada vive **7 días** para dar tiempo a registrarse o a que QCode active el módulo | Una constante |
| D-S17 | La `WebhookCallbackUrl` es configuración global, no se deduce del request | Deducirla |
| D-S18 | Con documento pero sin clasificación en la tienda o sin ciudad, el comprador nace **incompleto** y el documento queda en el aviso; no se inventa clasificación | Clasificación obligatoria al configurar |
| D-S19 | `webhookSubscriptionIds` va como campo interno de la conexión, no en `shopify.stores`: lo escribe y lo repone Integrations (enlace y probador) | Moverlo |

## 16. Verificaciones pendientes contra Shopify (handoff)

| # | Qué | Cómo se cierra | Si falla |
| --- | --- | --- | --- |
| V-1 | Que una app pública **no embebida** siga admitiendo el *authorization code grant* con redirección y token offline en la versión de API elegida, sin obligar a token exchange | Tienda de desarrollo, primer enlace real | Flujo embebido con App Bridge: otro spec |
| V-2 | Que `webhookSubscriptionCreate` con el token offline registre los cinco topics y que las entregas se firmen con el client secret | Primer pedido real | Suscripción a nivel de app en la configuración |
| V-3 | Que sin la aprobación de **datos protegidos de clientes** el payload llegue sin PII y con ella llegue completo | Comparar payloads antes y después | Nada en el código; es un paso del handoff |
| V-4 | La forma exacta de `provinceCode` para Colombia y que `customAttributes` traiga lo que el checkout del tenant captura como documento | Pedido real de la tienda piloto | Ajustar `ColombiaProvinceCodes` o las claves por tienda |
| V-5 | Que `orders/updated` llegue en cada cambio de `financial_status` además de `orders/paid` | Pagar un pedido pendiente en desarrollo | La conciliación lo cubre con retraso |
| V-6 | Códigos de `shipping_lines` para recogida en tienda en el checkout colombiano | Pedido de prueba con recogida | Opción por tienda para marcar el método de recogida |
| V-7 | Que la App URL con `shop`, `hmac` y `timestamp` siga siendo la forma en que Shopify abre una app no embebida al instalarla | Instalar desde la tienda de desarrollo | Sólo el inicio A |

## 17. Entregables

1. Spec de flete en cotizaciones (sesión aparte) y su implementación en `develop`.
2. Backend: Tenancy (clave), Integrations (proveedor, inicios A y B, canje, enlace con registro de webhooks,
   probador), Quotations (origen, servicio externo, `allowIncomplete`, `SyncExternalPaymentStatus`, filtros),
   Customers (`ICustomerProvisioning`, `CreateIncompleteFromExternalSale`, `FindByEmailAsync`), Reporting
   (filtro), módulo Shopify (webhook, workers, espejo, importador, sincronización manual, API), Bootstrapper
   (adaptadores, políticas, mapeo), README.
3. Frontend: tarjeta del módulo en operador, diálogo de dominio, rutas de instalación y enlace,
   configuración de tienda, bandeja en dos vistas, origen en pedidos, filtro en cotizaciones, `returnTo`.
4. Handoff al owner con lo de §10.5 y §16.
5. Plan de implementación (`writing-plans`) tras la lectura de este spec.

## Historial de revisión

- 2026-10-15: decisiones 1–12 tomadas en conversación con el owner; secciones aprobadas una por una; spec
  escrito.
- 2026-10-15 (misma sesión): el owner corrige: el módulo lo activa el operador a mano (no al instalar); dos
  inicios del OAuth (desde QEP y desde Shopify); N tiendas por tenant; QEP registra los webhooks al enlazar;
  sincronización manual además del cron; el comprador sin documento nace **incompleto y el pedido entra
  igual**. Se agregan las decisiones 13–15, la excepción `allowIncomplete`, los avisos del espejo, D-S16 a
  D-S19 y V-7. Pendiente la lectura del owner, el spec de flete y el plan de implementación.

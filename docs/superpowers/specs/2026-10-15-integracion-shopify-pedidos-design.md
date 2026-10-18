# Integración con Shopify: pedidos de la tienda en QEP

**Fecha:** 2026-10-15
**Módulos:** Shopify (nuevo: Domain, Application, Infrastructure, Api), Integrations (proveedor, OAuth,
instalación, enlace y registro de webhooks), Quotations/Orders (origen y referencia externa del pedido,
servicio para pedidos externos, filtro por origen; sobre el modelo de **pedido con datos propios** del spec
previo), Customers (alta de cliente completo o incompleto desde el sistema), Tenancy (clave de módulo
`shopify`, por defecto en el signup), Api/Bootstrapper (webhook, adaptadores, políticas). **Frontend:**
`qep-frontend`, rutas públicas de instalación, `features/integrations`, `features/orders`,
`features/operator`.
**Estado:** alcance, enfoque y las cuatro secciones del diseño aprobados por el owner en conversación el
2026-10-15; revisado el mismo día con sus correcciones (§3, decisiones 2, 8, 11 y 13 a 16). El 2026-10-18 el
owner pidió implementarlo («Al final implementar spec de integrar con Shopify»): recibe el delta del flete y
la reverificación contra el código de ese día (historial). Planes: `docs/superpowers/plans/2026-10-18-shopify-backend.md`
(este repo) y `docs/superpowers/plans/2026-10-18-shopify-frontend.md` (`qep-frontend`).
**Depende de dos specs previos, los dos implementados en `develop` (§11):** (1) **pedidos sin cotización**
(`2026-10-15-pedidos-sin-cotizacion-design.md`): `Order` con cliente, líneas, partes y totales propios,
`IExternalOrderWriter`, `Order.CreateExternal`; (2) **flete** (`2026-10-17-flete-design.md`): transportadoras
con `ExternalNames`, IVA del envío, `ShippingDetails` y contraentrega. Su §10 está aplicado acá (§6.2).
**Ramas:** `feature/shopify` desde `develop` en `qep-backend` (`f4d8849d`) y en `qep-frontend` (`c9405373`).

## 1. Contexto y alcance

Los tenants que venden por Shopify hoy copian a mano cada pedido de la tienda a QEP para que salga en el
Excel de pedidos que importa su ERP. El owner quiere que el pedido de Shopify **entre solo**, se liste junto a
los pedidos que nacen de una cotización, se apruebe, facture o anule desde la misma pantalla y se exporte
con las mismas columnas.

Todo tenant **nuevo** nace con el módulo activo (entra a los módulos por defecto del signup); a los tenants
que ya existen se lo activa el usuario de plataforma desde la consola de operador. Un tenant con el módulo
activo puede conectar **N tiendas**, cada una una sola vez, y la conexión se puede iniciar desde
dos lugares: desde QEP (Ajustes → Integraciones → «Nueva integración de Shopify») o desde Shopify
(instalar la app). En los dos casos corre el OAuth de Shopify y termina en QEP, donde la tienda se vincula a
una cuenta existente o a una cuenta nueva. Al vincular, QEP registra los webhooks de la tienda. Los
pedidos llegan por webhook, por una sincronización manual o por un cron de conciliación; cada pedido crea
el cliente (completo si trae documento, incompleto si no) y el pedido en QEP, que se mezcla con los demás
en la vista de pedidos. Lo que no puede entrar queda en una bandeja con el motivo.

**Dentro:**

- Proveedor `shopify` en Integrations con onboarding por OAuth desde QEP y desde Shopify (§4).
- Enlace tienda ↔ tenant, con registro de tenant nuevo cuando la persona no tiene cuenta (§4.3).
- Módulo `shopify` por tenant: por defecto en los tenants nuevos, activado por el operador en los existentes
  (§4.4).
- Registro de webhooks por tienda al enlazar; webhook firmado, espejo de pedidos, conciliación periódica y
  sincronización manual (§5).
- Traducción a un `Order` **sin cotización**, con cliente completo o incompleto, SKU = código de producto,
  asesor y cuenta de cobro de la tienda, y el envío de la tienda: recogida, flete cobrado o gratis,
  transportadora reconocida por su nombre y contraentrega por la pasarela (§6).
- «Reconocer este nombre»: elegir la transportadora de un pedido que llegó con un método de envío desconocido
  y recordarlo para los próximos (§6.2.5).
- Bandeja de pedidos en espera, con reintento manual y automático (§6.4, §8).
- Origen y referencia externa del pedido en la lista de pedidos, con filtro (§6.5). Las cotizaciones no se
  tocan: un pedido de Shopify no tiene cotización.
- Sincronización de estado de pago y de cancelación desde Shopify (§6.3).
- Webhooks de cumplimiento exigidos a una app pública (§5.5).

**Fuera** (§14): historial anterior al enlace, reembolsos y ediciones de líneas, sincronización hacia
Shopify (fulfillment, inventario), app embebida en el admin de Shopify, WooCommerce u otras plataformas.

## 2. Hechos verificados en el código

Lo que el diseño asume, con el lugar donde se confirmó. Ante discrepancia manda el código. **Reverificado el
2026-10-18 sobre `develop` (`f4d8849d`)**, después de pedidos sin cotización y flete: H1 a H5 se reescribieron
y H20 a H27 son nuevos. Q = `src/Modules/Quotations`, Dom = `Q/Modules.Quotations.Domain`, App =
`Q/Modules.Quotations.Application`, FE = `qep-frontend/src` (`develop`, `c9405373`).

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | Un `Order` tiene datos propios —cliente, asesor, cuenta de cobro, partes, líneas, totales y envío— y tres fábricas: `FromQuotation`, `CreateManual` y `CreateExternal`. `CreateExternal(…, OrderSource source, string externalReference)` rechaza `Quotation`/`Manual` (`order.order.source_invalid`) y una referencia vacía (`order.order.external_reference_required`); `ExternalReference` ≤ 64. **Hoy nace con `ShippingDetails.None`**: el envío del camino externo lo agrega este spec. Nace `Pending`; se anula desde `Pending` o `Approved`, nunca desde `Invoiced` (`order.order.already_invoiced`), y el motivo es obligatorio, ≤ 500. | `Dom/Order.cs:28,250,313,362-398,687-713`; `Dom/OrderSource.cs` |
| H2 | La elegibilidad del cliente del pedido vive en `OrderCustomerEligibility.Ensure(customer, tenantId, clientId, allowIncomplete)` (interna de App): `client_not_found` (también para otro tenant), `client_incomplete`, `client_cuc_missing`, `client_inactive`. `ExternalOrderService` la llama con `allowIncomplete: true`; el pedido manual, con `false`. Las cotizaciones siguen con `QuotationCustomerEligibility`. | `App/OrderCustomerEligibility.cs`; `App/IExternalOrderWriter.cs:64`; `App/QuotationCustomerEligibility.cs` |
| H3 | El precio de línea es **con IVA** (`VatIncludedLine`); los totales salen de `DocumentTotals.Compute` y, con flete, `Total = Subtotal + TaxAmount + FreightSubtotal + FreightTaxAmount`; la retención no incluye el envío. Sólo COP discrimina IVA: fuera de COP la tasa de la línea y la del envío se fuerzan a 0. | `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`; `Dom/DocumentTotals.cs`; `Dom/ShippingDetails.cs:65`; `Dom/QuotationItem.cs` (`ItemizesTaxIn`) |
| H4 | `IExternalOrderWriter` (`CreateAsync`, `SyncPaymentStatusAsync`, `CancelAsync`) es el único camino para crear un pedido sin sesión HTTP; `ExternalOrderService` lo implementa sin `IExecutionContext`, toma la foto del producto del catálogo y **no** re-precia, numera con el consecutivo del tenant, y en una transacción agrega historial (`OrderChangeSummary.CreatedFromExternal`), auditoría `PublishByIntegration` y `quotations.order.created.v1`. `ExternalOrderDraft(TenantId, Source, ExternalReference, ClientId, AdvisorMembershipId, PaymentMethod?, Notes?, Parties?, BillingAccount, Lines, PaymentStatus, OccurredAt)`; `ExternalOrderLine(ProductId, Quantity, UnitPrice, DiscountPercentage, TaxPercentage)`; `ExternalOrderResult(OrderId, OrderNumber, Total, NetTotal, Currency)`. **No hay índice único por referencia externa**: dos llamadas con el mismo pedido crean dos pedidos. | `App/IExternalOrderWriter.cs:8-143`; `Q/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:478-484,589` (`IX_orders_tenant_source`, no único) |
| H5 | `Order.SyncExternalPaymentStatus(status)` exige `Pending` (`order.order.not_pending`) y que no haya comprobantes (`order.order.payment_has_proofs`); con el mismo estado no hace nada. Un pedido con `CashOnDelivery` sólo nace `PaymentPending` y sin comprobantes (`order.order.cash_on_delivery_pending_only`). | `Dom/Order.cs:604-617,814-834,941-947` |
| H6 | La lista de pedidos ya filtra por `Source` y devuelve `source` y `externalReference` en la fila y en el detalle; las cotizaciones no saben de origen. | `App/ListOrders.cs:19,43-44,76`; `App/OrdersDtos.cs:21-23,152-154,220-221` |
| H7 | Un cliente completo exige nombre, tipo y número de documento (`NIT`, `CC`, `CE`, `PASAPORTE`), dirección, país, ciudad DIVIPOLA si es colombiano, y clasificación; el CUC lo arma `CucFormatter` con el número de `ICucGenerator` **después** de resolver clasificación y ciudad. `CreateCustomerHandler` autoriza por sesión. Un cliente `Incomplete` lo crea sólo el sistema (`CreateIncomplete` con BSUID, `CreateIncompleteFromInstagram` con IGSID), sin documento, dirección, clasificación ni CUC. `IdentificationTypeParser.Parse` **lanza** con un valor desconocido. `ICustomersAuditPublisher.Publish` no tiene tipo de actor. | `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:30-160`; `Modules.Customers.Domain/Customer.cs:294-357,412-432`; `IdentificationType.cs:30-80`; `ICustomersAuditPublisher.cs` |
| H8 | Customers busca por documento con `FindExistingIdentificationsAsync(tenantId, [(Type, Number)])`, por teléfono con `ICustomerPhoneDirectory.MatchAsync` y `FindOldestByPhoneE164Async`; no hay búsqueda ni índice por email. La unicidad del documento es `IX_customers_tenant_identification`, traducida por nombre en `CustomersUnitOfWork`. | `Modules.Customers.Application/ICustomerRepository.cs:86-90,161`; `ICustomerPhoneDirectory.cs`; `Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:206-213`; `CustomersUnitOfWork.cs:15` |
| H9 | El Excel de pedidos lee del pedido: `Documento` = CUC o vacío y `Documento de identidad` = número o vacío, sin error. `carrier` = `CarrierName`, «Recoger en tienda», o la constante «Coordinadora» si el pedido no tiene transportadora (flete D-F8). | `App/OrdersExportProcessor.cs` |
| H10 | Geography resuelve ciudad **por nombre dentro de un departamento** (`ICityRepository.FindByNameAsync(departmentId, name)`) y departamento por nombre; `Department` tiene `DivipolaCode` y `Name`, no código ISO 3166-2. | `src/Modules/Geography/Modules.Geography.Application/ICityRepository.cs:32`; `IDepartmentRepository.cs:28`; `Modules.Geography.Domain/Department.cs:23-25` |
| H11 | Catalog busca un producto por código exacto con `IProductRepository.FindByCodeAsync(tenantId, code)`; el producto guarda la **tasa por id** y el porcentaje se lee de `ITaxRateRepository`. Pos ya lo consume así por `PosProductLookup` en Bootstrapper. | `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs:79`; `src/Bootstrapper/PosProductLookup.cs:11-29` |
| H12 | Integrations tiene catálogo en código: `IntegrationCategory { Messaging, Shipping, Ai }`, `ProviderOnboarding { Form, MetaEmbeddedSignup, InstagramLogin }`, `FieldKind { Text, Secret, Phone, Url }`, campos internos (`isInternal`) y secretos, tope `DefaultMaxConnections = 20`, `connection_routes` con `external_id` ≤ 64 y único por proveedor (`IX_connection_routes_provider_external`), y los puertos `IIntegrationConnections` e `IConnectionHealthReporter`. Un proveedor sólo es visible con alguno de sus `ConsumingModules` activo; con onboarding distinto de `Form`, el backend es dueño de todos los campos. | `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs:6-30,200`; `IntegrationProviders.cs:48,155`; `IntegrationConnectionRoute.cs:11`; `Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContext.cs:26`; `Modules.Integrations.Application/IIntegrationConnections.cs`; `IntegrationsSupport.cs:36-70` |
| H13 | El precedente de OAuth por redirección es Instagram Login: el catálogo trae `authorizeUrl`, el SPA abre la ventana, vuelve a `/integrations/instagram/callback`, y `ConnectInstagramHandler` exige `redirectUri` absoluto de un origen permitido (`ConnectInstagramValidator.TryParse`), canjea el `code`, **suscribe el webhook** y crea conexión y ruta en una transacción; la carrera con otro tenant la resuelve `IX_connection_routes_provider_external`. Las opciones globales van por un puerto (`IInstagramAppSettings`) y se validan con `ValidateOnStart`. | `Modules.Integrations.Application/ConnectInstagram.cs`; `InstagramLogin.cs`; `Modules.Integrations.Infrastructure/Meta/MetaInstagramOptions.cs`; FE `routes/_authenticated/integrations/instagram/callback.tsx` |
| H14 | `ConnectionEvents` publica `connection-paused`, `connection-deleted` y `connection-needs-attention`; no hay evento de conexión creada. `ConnectionFailureCodes` tiene `credentials_rejected`, `provider_unreachable`, `field_invalid`, `token_expired`, `number_unregistered`, `account_disabled`. | `Modules.Integrations.Application/ConnectionEvents.cs:10-15`; `Modules.Integrations.Domain/IntegrationsErrorCodes.cs:44-54` |
| H15 | Los webhooks de Meta son rutas anónimas bajo `/api/webhooks/`, con el limitador `webhook` (concurrencia **global** con cola, configurada por `MessagingWebhookOptions`), exentas de CSRF por prefijo, cuerpo acotado, firma verificada y encolado con `ON CONFLICT DO NOTHING`; `WebhookDeliveryWorker` procesa en lotes de 50 con leases crecientes y 8 intentos. | `src/Modules/Messaging/Modules.Messaging.Api/WhatsAppWebhookEndpoints.cs`; `src/Bootstrapper/WebhookPaths.cs`; `src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:50`; `src/Api/Program.cs:82-96,184-186`; `Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryWorker.cs:22-37` |
| H16 | Las claves de módulo son una lista cerrada con `CHECK`; `All` termina en `pipeline` y `DefaultForNewTenants` son seis (sin `pos`, `messaging` ni `pipeline`); se prenden por tenant desde la consola de operador (`POST /operator/tenants/{targetTenantId}/modules/changes`, `ChangeTenantModulesHandler`). Enforcement: `TenantModuleGuard.EnsureEnabledAsync` → 403 `tenancy.module_not_enabled`. **En el frontend `TENANT_MODULE_KEYS` no trae ni `pipeline`**: una clave nueva necesita su entrada y su etiqueta ahí. | `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:36-71`; `Modules.Tenancy.Api/OperatorEndpoints.cs:35`; `Modules.Tenancy.Application/ChangeTenantModules.cs:43`; `TenantModuleGuard.cs:14-16`; FE `features/auth/types/tenant-modules.ts:10-31` |
| H17 | El registro público de tenant es `POST /api/v1/auth/register-tenant` con bearer de Google, detrás de `Registration:PublicTenantSignupEnabled` (false por defecto); aprovisiona el owner y crea el tenant. `/login` y `/register` son rutas públicas del SPA **sin `returnTo`**: el login navega al *landing* (`resolveLanding`). `GET /auth/me` devuelve `activeTenantIds`. | `src/Api/RegistrationEndpoints.cs:20,33,89-118`; FE `routes/login.tsx:24-36`, `routes/register.tsx`, `features/auth/services/auth.api.ts:42` |
| H18 | Pipeline consume `quotations.order.created.v1` (con `source` y `quotationId` nullable) y `order.cancelled.v1`: un pedido sin cotización gana el único negocio abierto del cliente o crea uno ganado, y guarda la foto en `pipeline.deal_orders`. | `Q/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs:39-52`; `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs` |
| H19 | Un módulo consumidor nunca referencia `Modules.Integrations.Application` ni la Application de otro módulo de negocio: declara su puerto y el adaptador vive en Bootstrapper. `AuditActorType.Integration` existe. La auditoría atómica de un módulo proyecta `audit.entries` en su propio `DbContext` (`ExcludeFromMigrations`), como Messaging. | `src/Bootstrapper/*.cs`; `tests/ArchitectureTests/ArchitectureTests/*LayerTests.cs`; `src/Modules/Audit/Modules.Audit.Domain/AuditActorType.cs:9`; `Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs`, `MessagingDbContext.cs:86,101` |
| H20 | `Carrier` (Quotations) tiene `Name` (≤ 60), `ExternalNames` (≤ 20, cada uno ≤ 120), `SupportsCashOnDelivery`, `IsActive`, `Version`; nombre y externos comparten `quotations.carrier_names` con el índice único `IX_carrier_names_tenant_name` sobre el nombre normalizado por `CarrierName.Normalize` (recorta, colapsa espacios, minúsculas, sin tildes), traducido a `quotations.carrier.name_taken` por nombre. No hay una operación para **agregar** un solo nombre externo: `UpdateCarrier` reemplaza la lista con `If-Match` y `tenancy.settings.update`. | `Dom/Carrier.cs`; `Dom/CarrierName.cs:7-25`; `App/UpdateCarrier.cs`; `App/ICarrierRepository.cs`; `Q/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs:51` |
| H21 | `ShippingDetails(CarrierId?, CarrierName?, ChargeMode?, FreightAmount, FreightTaxPercentage, CashOnDelivery)` con `None`; `Validated` exige recogida sin envío (`shipping_not_allowed_on_pickup`), monto ≥ 0 y en pesos enteros en COP, `Charged` > 0, `Free`/`null` en 0. Que la transportadora exista, esté activa y recaude, que el modo esté activo en el tenant y la tasa del envío los resuelve `ShippingResolver` (App), que el camino externo **no** usa (flete D-F18). | `Dom/ShippingDetails.cs`; `App/ShippingResolver.cs` |
| H22 | La tasa del IVA del envío vive en `IQuotationSettingsStore.GetFreightTaxRateIdAsync` y se lee con `IQuotationTaxRateLookup.FindAsync` (adaptador `QuotationTaxRateLookup` en Bootstrapper); sin tasa activa, `ShippingResolver` lanza `…freight_tax_rate_missing`. | `App/IQuotationSettingsStore.cs`; `App/IQuotationTaxRateLookup.cs`; `src/Bootstrapper/QuotationTaxRateLookup.cs` |
| H23 | Un pedido pendiente cambia su envío —transportadora, modo, valor, contraentrega— por `SaveOrderEdits` con el bloque `shipping` (`quotations.order.manage`), y `Order.SetShipping` exige `Pending`. El pedido no edita recoger/enviar ni sus partes. | `App/SaveOrderEdits.cs`; `Dom/Order.cs:582-595` |
| H24 | Las notas del pedido son **un solo campo sin tope** (`NormalizeNotes` sólo recorta); `PaymentMethod` ≤ 50 (`Quotation.PaymentMethodMaxLength`). | `Dom/Order.cs:30,1057,1065-1066` |
| H25 | La parte de envío del pedido es `QuotationPartyRequest(Name, IdentificationNumber, Phone, Email, Address, DepartmentId, CityId)` dentro de `QuotationPartiesRequest(Billing, Shipping, BillingUsesBusinessName, IsStorePickup, …)`; una parte incompleta se rechaza al crear (`order.shipping.party_incomplete`). | `App/QuotationsDtos.cs:191-230`; `Dom/Order.cs:440-460`; `Dom/OrderParty.cs:39` |
| H26 | Existe un módulo `Platform` (log de fallas de requests) que no figura en la lista de CLAUDE.md y no tiene `PlatformLayerTests`. No lo toca este spec; el módulo nuevo `Shopify` sí trae su `ShopifyLayerTests`. | `src/Modules/Platform/`; `tests/ArchitectureTests/ArchitectureTests/` |
| H27 | En el frontend, Integraciones vive en `/integrations` (no en `/settings/integrations`): `routes/_authenticated/integrations/{index,whatsapp/new,instagram/new,instagram/callback}.tsx` y `features/integrations` (`provider-catalog.tsx` despacha por `onboarding.kind`). Pedidos ya tiene `OrderSourceBadge` y el filtro `source` (`ORDER_SOURCE_LABELS` incluye `Shopify`). | FE `routes/_authenticated/integrations/`; `features/integrations/components/provider-catalog.tsx:116-133`; `features/orders/components/order-source-badge.tsx`, `order-filters.tsx:20-215`; `features/quotes/types/order.ts:25-29` |

## 3. Decisiones del owner (2026-10-15)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿Qué hace el tenant con los pedidos de Shopify en QEP? | **Que se listen en Pedidos junto a los de cotización y se exporten** por el mismo Excel. |
| 2 | ¿Cómo se resuelve el cliente, si Shopify casi nunca trae documento? | Primera respuesta: cliente completo o pedido en espera. **Revisado por el owner:** el comprador se crea con lo que traiga Shopify, **completo si trae documento e incompleto si no**, y el pedido **entra igual**. Implica una excepción a la elegibilidad del cliente sólo para origen Shopify (§6.5) y que el Excel salga con `Documento` y `Documento de identidad` vacíos hasta completar la ficha (H9). |
| 3 | ¿Cómo se emparejan los productos? | **SKU de Shopify = `Product.Code` de QEP.** Un SKU desconocido deja el pedido en espera. |
| 4 | ¿De dónde salen el asesor y la cuenta de cobro? | **Configurados por tienda.** Si el asesor queda inactivo, los pedidos nuevos esperan. |
| 5 | ¿Qué pasa con el costo de envío? | **Agregar flete a la cotización**, en un spec aparte que se implementa antes. Mientras no esté, los pedidos con envío esperan (§6.2). *Cumplido:* el spec de flete está en `develop` y su §10 se aplicó en §6.2 (2026-10-18). |
| 6 | ¿Un spec o dos? | **Dos specs.** Éste declara el flete como dependencia. |
| 7 | ¿Cómo llegan los pedidos? | **Webhooks + conciliación periódica**, y además **sincronización manual** por tienda (revisión del owner). |
| 8 | ¿Cómo conecta el tenant su tienda? | **App pública de QCode con OAuth.** Revisión del owner: el flujo se puede iniciar **desde QEP** (menú Integraciones) **o desde Shopify** (instalar la app); en los dos casos termina en QEP vinculando una cuenta existente o creando una nueva. |
| 9 | ¿Qué pedidos entran y qué se sincroniza después? | **Entran al crearse, con su estado de pago**; el pago se actualiza cuando Shopify avisa; la cancelación se refleja. |
| 10 | ¿Dónde se ven los pedidos en espera? | **Bandeja propia en Pedidos, con reintento automático** y botón «Reintentar». Permiso `quotations.order.manage`. |
| 11 | ¿Módulo vendible? | **Módulo `shopify` propio.** Primera revisión del owner: lo activa el usuario de plataforma a mano por tenant; no se activa solo al instalar. **Segunda revisión:** entra a los **módulos por defecto del signup**, así que todo tenant nuevo nace con él activo; a los existentes se lo prende el operador desde la consola. |
| 12 | Enfoque de implementación | **A: módulo nuevo `Shopify`**; Integrations guarda la conexión y hace el OAuth; Quotations gana lo mínimo para aceptar un pedido externo. |
| 13 | ¿Cuántas tiendas por tenant? | **N**, cada tienda una sola vez por tenant (y por plataforma: una tienda no puede estar en dos tenants). |
| 14 | ¿Quién crea los webhooks? | **QEP, al vincular la tienda**, por la Admin API. |
| 15 | ¿Y los campos que Shopify no trae? | **Se dejan en blanco**; el cliente puede quedar incompleto. |
| 16 | ¿El pedido de Shopify nace de una cotización fantasma? | **No.** El owner quiere separar cotizaciones de pedidos: **debe poder existir un pedido sin cotización**, y el pedido lleva **datos propios para todos los casos** (también los que nacen de una cotización). Es un spec previo (§11); este documento asume ese modelo y deja de tocar `Quotation`. |

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
| `Shopify:App:ApiVersion` | ConfigMap, `2026-10` por defecto (D-S29) | Versión de la Admin API; forma `^\d{4}-(01\|04\|07\|10)$` |
| `Shopify:App:Scopes` | ConfigMap, `read_orders,read_customers` | Lo que se pide en la autorización |
| `Shopify:App:AllowedRedirectOrigins:N` | ConfigMap | Orígenes permitidos para el `redirect_uri` del SPA |
| `Shopify:App:WebhookCallbackUrl` | ConfigMap, `https://qep-api.qcode.co/api/webhooks/shopify` | URL pública que QEP registra en cada tienda (§4.3 paso 6) |
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
             ◄── 201 ConnectionResponse  →  /integrations/shopify/{connectionId}
```

- **A1. Desde QEP.** `StartShopifyInstallForTenantHandler`, permiso `integrations.connection.manage`,
  módulo `shopify` activo (si no, 403 `tenancy.module_not_enabled`: la tarjeta ni aparece, por el catálogo
  filtrado). Valida la forma del dominio (`^[a-z0-9][a-z0-9-]*\.myshopify\.com$`, ≤ 64, D-S33), exige `redirectUri` de un
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
  de hoy (H17) y el tenant nuevo ya trae `shopify` (§4.4), así que el enlace sigue de inmediato. Con el
  módulo apagado (sólo pasa en un tenant anterior a este spec, o si el operador lo apagó): «Pide a QCode que
  active la integración con Shopify en *Tenant*; la instalación queda guardada hasta el *fecha*».
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

- `TenantModuleKeys.Shopify = Define("shopify")`, último de `All`, **dentro de `DefaultForNewTenants`**
  (pasan de seis a siete), con dependencia declarada de `orders`. Migración del `CHECK`.
- Un tenant del signup nace con él activo mientras `Entitlements:GrantDefaultModulesOnSignup` esté en `true`
  (el default), por `TenantModuleDefaults` sin código nuevo. Así, quien llega desde la instalación en Shopify y
  registra una cuenta nueva (§4.3 paso 5) enlaza la tienda sin esperar a nadie. La semilla ya concede todos.
- Los tenants **que ya existen** no reciben el módulo por backfill: se lo prende el usuario de plataforma
  desde la consola de operador con el flujo que ya existe (H16, `POST /operator/tenants/{id}/modules/changes`);
  el frontend del operador muestra la tarjeta como las de `pos`, `messaging` y `pipeline`.
- Documentación que queda vieja y se corrige en el mismo cambio: README («nace con los seis sin `pos`») y el
  gotcha de CLAUDE.md («`GrantDefaultModulesOnSignup` concede seis módulos sin `pos`»): pasan a siete, y
  `TenantModuleKeysTests.DefaultForNewTenantsIsEverythingButPosMessagingAndPipeline` se renombra con la lista
  real.
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
| `warnings` (jsonb) | avisos de una importación que sí entró (§6.2.4): `customer_incomplete`, `shipping_address_unresolved`, `total_difference`, `carrier_unrecognized`, `cash_on_delivery_unmatched` |
| `attempts`, `last_attempt_at`, `imported_at`, `last_error` | historia de la importación; `last_error` guarda lo que el worker ignoró (§6.3) |
| `warnings_reviewed_at` | «Marcar como revisado» (D-S31); null = aviso pendiente |
| `qep_customer_id`, `qep_order_id` | referencias blandas |
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
método de su puerto `IShopifyOrderWriter.CreateAsync(ShopifyOrderDraft)`, cuyo adaptador en Bootstrapper
(`ShopifyOrderWriter`) lo traduce a `IExternalOrderWriter.CreateAsync(ExternalOrderDraft)` de Quotations
(H4): `ExternalOrderService` crea el pedido con sus datos propios en **una transacción**, sin cotización
(decisión 16). Ninguna de las dos partes pasa por los handlers HTTP. La auditoría del pedido va con
`AuditActorType.Integration`.

**Lo que el camino externo gana en Quotations** (delta del flete §10 y D-S23):

```text
ExternalOrderDraft(…, OccurredAt,
    ShippingDetails? Shipping = null,     // null = ShippingDetails.None; ya resuelto por el importador
    string? ExternalKey = null,           // "{shopId}:{shopifyOrderId}", ≤ 128; idempotencia (D-S23)
    string? ExternalShippingTitle = null) // el title de Shopify cuando la transportadora no se reconoció (§6.2.5)
ExternalOrderResult(…, Currency, bool AlreadyExisted)
```

- `Order.CreateExternal` recibe el `ShippingDetails` y lo valida con `Validated` como los otros dos caminos
  (H21): la coherencia sí, pero **no** la transportadora activa, el recaudo ni los modos activos del tenant,
  que el importador ya resolvió y que no aplican al camino externo (flete D-F18).
- `ExternalKey` va a `orders.external_key` con el índice único parcial `IX_orders_tenant_external_key`
  (`tenant_id`, `source`, `external_key`) `WHERE external_key IS NOT NULL`. `CreateAsync` busca primero por la
  clave y, si el pedido ya existe, devuelve su resultado con `AlreadyExisted = true` sin escribir nada; la
  carrera la cierra el índice, que `QuotationsUnitOfWork` traduce **por nombre** a
  `order.order.external_key_taken`, y el adaptador vuelve a leer y devuelve el existente. Sin esto, un worker
  que cae entre crear el pedido y marcar la fila del espejo crea un pedido duplicado en el reintento (H4).

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
| `freight_tax_rate_missing` | el pedido cobra envío (> 0) en COP y el tenant no tiene IVA del envío activo (H22) | `{ shippingTitle, amount }` | configurar el IVA del envío en Configuración › Transportadoras |
| `order_rejected` | una regla del pedido tiró un código que esta lista no previó | `{ code }` | corregir lo que el código señala y reintentar |

`freight_unsupported` **se retira** (flete §10): el envío ya tiene dónde ir.

El cliente **nunca** detiene la importación salvo que exista y esté inactivo: sin documento, sin ciudad o
sin clasificación, nace incompleto (decisión 2 revisada). Tampoco la detiene una transportadora desconocida
ni una contraentrega con una transportadora que no recauda: entran con aviso (§6.2.2).

#### 6.2.1 Cliente

1. **Documento.** `customAttributes[document_attribute_key]`; el tipo, de
   `customAttributes[document_type_attribute_key]` comparado sin mayúsculas con `NIT`, `CC`, `CE` y
   `PASAPORTE`, o el `default_identification_type` de la tienda si no viene o no es uno de esos (D-S22:
   `IdentificationTypeParser.Parse` lanza con un valor desconocido, H7, y Shopify no referencia Customers).
   Número recortado, tope 32.
2. **Búsqueda**, por `IShopifyCustomerDirectory`, en este orden y se queda con el primero: por documento
   (`FindExistingIdentificationsAsync`, H8); por `shopify_customer_id` ya visto en el espejo de esta tienda
   (`qep_customer_id` de un pedido anterior del mismo comprador, consulta propia del módulo); por teléfono
   E.164 (`ICustomerPhoneDirectory.MatchAsync`); por email exacto sin mayúsculas (método nuevo
   `FindIdByEmailAsync` en `ICustomerRepository`, con índice nuevo; H8). Un cliente encontrado se usa tal
   cual, completo o incompleto; si está inactivo → `customer_inactive`.
3. **Alta completa** si hay documento **y** la tienda tiene `default_classification_id` **y** la dirección
   de envío se resuelve a ciudad (o el país no es `CO`): `ICustomerProvisioning.CreateCompleteAsync`
   (servicio nuevo de Customers.Application, sin `IExecutionContext`, auditoría con actor `Integration` por un
   método nuevo `ICustomersAuditPublisher.PublishByIntegration`, H7): nombre `firstName lastName` del
   comprador o de la dirección, documento, teléfono, email, dirección (`address1` + `address2`), país
   (`countryCodeV2`), ciudad, clasificación, `WithRetention = false`, `VatSurplus = false`. Emite CUC con el
   mismo orden que `CreateCustomerHandler` (clasificación y ciudad antes del número). Si el documento quedó
   tomado entre la búsqueda y el alta (`IX_customers_tenant_identification`, H8), se busca otra vez y se usa el
   existente.
4. **Alta incompleta** en cualquier otro caso: `Customer.CreateIncompleteFromExternalSale(name, phone, email,
   country)` (factoría nueva en Customers, hermana de las de WhatsApp e Instagram), por
   `ICustomerProvisioning.CreateIncompleteAsync`: nombre (o «Cliente de Shopify» si no viene), teléfono
   normalizado, email, país; sin documento, dirección, clasificación ni CUC. El documento que sí vino pero no
   alcanzó para completar (sin clasificación o sin ciudad) queda en `warnings.customer_incomplete.document`
   del espejo para que quien complete la ficha lo tenga a la mano.
5. **Ciudad.** Sólo si el país es `CO`. Shopify entrega `province` y `provinceCode` (ISO 3166-2 sin `CO-`:
   `ANT`, `DC`, `CUN`…). Geography no tiene código ISO (H10): el módulo trae una tabla **en código**
   `ColombiaProvinceCodes` (33 entradas) que mapea código → nombre de departamento, y luego
   `FindByNameAsync(departmentId, city)` por `IShopifyGeographyLookup`. Sin match por código, se intenta
   `province` por nombre. Sin ciudad, no hay parte de envío (§6.2.3) y el cliente nace incompleto.
6. **El cliente se anota antes que el pedido** (D-S24). En cuanto el cliente existe (encontrado o creado), el
   importador guarda `qep_customer_id` en la fila del espejo en su propia transacción, antes de llamar al
   escritor del pedido: si el pedido falla o el worker cae, el reintento usa ese cliente y no crea otro
   incompleto (un comprador sin teléfono ni email no se volvería a encontrar).

#### 6.2.2 Productos, precios y envío

- Cada línea: `sku` → `IShopifyProductLookup.FindByCodeAsync(tenantId, sku)` (adaptador sobre
  `IProductRepository.FindByCodeAsync` y `ITaxRateRepository`, H11): id, nombre, código, `IsActive` y el
  porcentaje de su tasa. Inactivo o inexistente → `product_unmatched`. Se resuelven **todas** las líneas antes
  de fallar para que `wait_detail.skus` traiga la lista completa.
- Precio unitario con IVA (H3): si `taxes_included`, `originalUnitPrice`; si no, en COP
  `originalUnitPrice × (100 + TaxPercentage) / 100`, y fuera de COP `originalUnitPrice` tal cual, porque ahí
  la tasa se fuerza a 0 (H3, D-S27). La tasa es la del **producto de QEP** (D-S3).
- Descuento: `Σ discountAllocations.amount` de la línea sobre `originalUnitPrice × quantity`, como porcentaje
  con 4 decimales; la línea del pedido lo recibe resuelto.

**Envío** (delta del flete §10). Se miran las líneas de envío **no retiradas** (`shippingLines` con
`isRemoved = false` en GraphQL; `shipping_lines` en el REST del webhook, V-9). El importador arma el
`ShippingDetails` del borrador así:

1. **Recogida.** Alguna línea con `code` que empieza por `pickup` (V-6) → `IsStorePickup = true` en las
   partes y `ShippingDetails.None`: sin transportadora, sin flete y sin contraentrega (el dominio rechaza
   cualquier envío con recogida, H21; D-S28).
2. **Sin líneas de envío** → envío sin registrar (`ShippingDetails.None`) y entrega a la dirección (§6.2.3).
3. **Valor.** `amount = Σ discountedPriceSet.shopMoney.amount` (REST: `discounted_price`). `amount = 0` →
   `Free` (precio 0 o descuento de envío gratis). Si no, `Charged` con `FreightAmount` = `amount` si
   `taxes_included`; si no, en COP `amount × (100 + tasa) / 100` con la tasa del envío del tenant (misma regla
   que los productos, D-S3) y fuera de COP `amount` tal cual (D-S27). En COP se redondea a pesos enteros,
   `AwayFromZero`, porque el dominio los exige (H21). «Al cobro» (`OnDelivery`) **nunca** se asigna desde la
   tienda: Shopify no lo conoce.
4. **Tasa del envío.** Un `Charged` en COP necesita la tasa del tenant (`IShopifyShippingLookup.GetFreightTaxPercentageAsync`,
   adaptador sobre H22): sin tasa activa → `freight_tax_rate_missing`. Va al borrador como
   `FreightTaxPercentage`; el pedido descompone el valor con ella. La diferencia con el IVA que calculó
   Shopify (`taxLines` del envío) no tiene motivo propio: se nota en `total_difference` (D-S6) y no frena.
5. **Transportadora.** El `title` de la primera línea de envío no retirada (D-S30) se busca con
   `IShopifyShippingLookup.MatchCarrierAsync(tenantId, title)`, cuyo adaptador normaliza con
   `CarrierName.Normalize` y compara con el nombre y los `ExternalNames` de las transportadoras **activas**
   (H20). Coincide → `CarrierId` y `CarrierName` (foto del nombre de QEP). No coincide → el pedido entra **sin
   transportadora**, con el aviso `carrier_unrecognized { shippingTitle }` y `ExternalShippingTitle = title`
   en el borrador, para «reconocer este nombre» (§6.2.5). Un `title` vacío no avisa.
6. **Contraentrega.** Si alguna de las pasarelas del pedido (`paymentGatewayNames`; REST
   `payment_gateway_names`), normalizada igual que los nombres de transportadora, está en
   `ShopifyCashOnDeliveryGateways` (constante del módulo; valores iniciales `cash on delivery (cod)`,
   `contra entrega`, `contraentrega`, `pago contra entrega`, a confirmar en V-8) → `CashOnDelivery = true`, con o
   sin transportadora reconocida: es un hecho de la tienda. Si la reconocida no hace recaudo
   (`SupportsCashOnDelivery = false`), entra igual con la marca y el aviso
   `cash_on_delivery_unmatched { carrierName }`.
7. **Modos activos y por defecto del tenant** (flete §4.4 y §4.4.1) **no aplican**: el modo de un pedido de la
   tienda es un hecho de la tienda (D-F18).

#### 6.2.3 El pedido (dentro de `ExternalOrderService`)

Lo que sigue usa el modelo de pedidos sin cotización (H1): un `Order` con cliente, asesor, cuenta de cobro,
partes, líneas, totales y envío propios. El punto de entrada es
`IExternalOrderWriter.CreateAsync(ExternalOrderDraft, CancellationToken)` (implementado por
`ExternalOrderService` en `Modules.Quotations.Application`), que arma el pedido con `Order.CreateExternal`
y devuelve `ExternalOrderResult`. Las líneas llegan como `ExternalOrderLine` ya preciadas y el envío como
`ShippingDetails` ya resuelto (§6.2.2).

1. **Elegibilidad del cliente, sólo para origen externo** (owner, 2026-10-15: «sólo deja pasar el pedido
   incompleto si no viene de cotización»): el pedido de Shopify acepta un cliente `Incomplete` y sin CUC;
   exige que exista en el tenant y esté activo. Ya implementado (H2): `OrderCustomerEligibility.Ensure(…,
   allowIncomplete: true)` sólo desde `ExternalOrderService`; el pedido manual y su `/preview` usan `false`,
   así que ningún handler HTTP puede pedir la excepción.
2. **Cabecera:** consecutivo del tenant (`PED-…`), `Source = Shopify`, `ExternalReference = "#1001"`,
   `ExternalKey = "{shopId}:{shopifyOrderId}"`, cliente, asesor de la tienda, cuenta de cobro de la tienda
   (su moneda es la del pedido: `currency_mismatch` ya lo garantizó), fecha del pedido, `paymentMethod` =
   pasarelas unidas con `, ` y recortadas a 50 (H24), notas = `Shopify #1001` + la nota del comprador, sin
   tope (H24), retención y excedente de IVA del cliente (falsos en uno incompleto). Partes: envío con la
   dirección de Shopify **sólo si queda completa** (nombre, teléfono, dirección, departamento y ciudad, H25);
   si no, sin parte de envío y la dirección cruda va a las notas con el aviso `shipping_address_unresolved`;
   recogida según §6.2.2; facturación a los datos del cliente.
3. **Líneas**, en el orden de Shopify, con producto, cantidad, precio unitario con IVA, descuento y tasa de
   IVA del producto (§6.2.2). **Envío** según §6.2.2. Los totales los calcula el pedido con las mismas reglas
   que la cotización, envío incluido (H3).
4. **Estado:** `Pending`; `PaymentStatus` desde `financial_status` (`paid` → `FullPaymentReceived`;
   `partially_paid` → `PartialPaymentReceived`; el resto → `PaymentPending`); sin comprobantes (un pedido
   externo no los exige aunque esté pagado: el pago vive en Shopify); creado por el asesor de la tienda, con
   auditoría de actor `Integration`. **Con contraentrega** el pedido nace `PaymentPending` aunque Shopify
   diga otra cosa, porque el dominio no admite otro (H5); si Shopify ya lo da por pagado, el importador llama
   a `SyncPaymentStatusAsync` justo después de crearlo (D-S26).
5. Si una regla del pedido rechaza el alta, el motivo se guarda como `order_rejected`.
6. **Pipeline.** Todo pedido publica `quotations.order.created.v1` con `source` y `quotationId` (nulo acá),
   y su `total` incluye el envío cobrado (flete §8). Pipeline lo consume (H18): el pedido de Shopify gana el
   único negocio abierto del cliente o, si no hay ninguno, crea uno ganado, y guarda su foto en
   `pipeline.deal_orders`; con varios abiertos la foto queda suelta. Su anulación (`order.cancelled.v1`)
   reabre ese negocio (D-S21, resuelta). Un `CreateAsync` que devuelve `AlreadyExisted` no publica nada.

#### 6.2.4 Resultado

La fila del espejo pasa a `Imported` con `qep_customer_id`, `qep_order_id`,
`total_difference` = `total_price` de Shopify − `Total` de QEP, y `warnings`: `customer_incomplete` (con el
documento si vino), `shipping_address_unresolved`, `total_difference` si supera el 1 % del total (D-S6),
`carrier_unrecognized` y `cash_on_delivery_unmatched` (§6.2.2). La bandeja muestra los avisos en la vista
«Con avisos»; ninguno frena la importación. «Marcar como revisado» (§7) llena `warnings_reviewed_at` y saca
la fila de esa vista y del contador (D-S31).

#### 6.2.5 Reconocer este nombre

Un pedido que entró con `carrier_unrecognized` guarda el `title` de Shopify en
`orders.external_shipping_title` (null en los demás). El detalle del pedido lo devuelve en
`shipping.externalShippingTitle` —BFF: la pantalla ofrece reconocerlo sin leer el espejo de Shopify— y, con el
pedido `Pending` y sin transportadora, muestra «Elegir transportadora» con la casilla «Reconocer
“*title*” en los próximos pedidos».

`POST /tenants/{tenantId}/orders/{orderId}/carrier-recognition { carrierId, rememberName }` (D-S25)
(`RecognizeOrderCarrierHandler`, Quotations):

- Política `quotations.order.manage`; el handler revalida tenant y permiso, el módulo `orders`, y **si
  `rememberName`** exige además `tenancy.settings.update` (403 antes de tocar nada): recordar un nombre es
  configurar transportadoras (flete §10).
- El pedido debe estar `Pending` (`order.order.not_pending`) y tener `ExternalShippingTitle`
  (`order.order.carrier_recognition_unavailable`, 422). La transportadora debe existir y estar activa
  (`order.order.carrier_not_available`); si el pedido es contraentrega y la elegida no recauda,
  `order.order.cash_on_delivery_not_supported`, como en `ShippingResolver` (H21).
- Fija `CarrierId`/`CarrierName` con `Order.SetShipping` conservando modo, valor, tasa y contraentrega, y
  agrega historial (`quotation.order.shipping_changed`, el mismo de la edición).
- Con `rememberName`, `Carrier.AddExternalName(title)` (método nuevo del dominio: mismas reglas que
  `Update`; si el nombre ya es suyo, no hace nada) y auditoría `quotations.carrier.updated`. El choque con otra
  transportadora lo da `IX_carrier_names_tenant_name` → `quotations.carrier.name_taken` (H20).
- Todo en una transacción de Quotations; responde el detalle del pedido entero (como las demás rutas que
  editan un pedido).
- No toca el espejo: el aviso queda como hecho histórico y se saca de la vista con «Marcar como revisado».

### 6.3 Cambios posteriores

- **Pago.** Un snapshot con otro `financial_status` sobre una fila `Imported` llama a
  `IShopifyOrderWriter.SyncPaymentStatusAsync` → `IExternalOrderWriter.SyncPaymentStatusAsync` →
  `Order.SyncExternalPaymentStatus` (H5): sólo cambia un pedido `Pending` **sin comprobantes** propios.
  `order.order.payment_has_proofs` y `order.order.not_pending` (ya aprobado, facturado o anulado) se auditan
  en el espejo (`last_error`) y se ignoran: después de aprobar, el pago lo lleva QEP. Un aviso con el estado
  que ya tiene no audita ni sube versión. `refunded`, `partially_refunded` y `voided` no cambian nada.
- **Cancelación.** `cancelled_at` no nulo sobre una fila `Imported` →
  `IExternalOrderWriter.CancelAsync(tenantId, orderId, cancelledByMembershipId, "Cancelado en Shopify: {cancel_reason}")`
  a nombre del asesor de la tienda (el motivo cabe en 500, H1). Si responde `order.order.already_invoiced`, la
  fila pasa a `Cancelled` con `wait_detail = { reason: "invoiced_before_cancel" }` y la bandeja la muestra
  para revertir la facturación y anular a mano; `order.order.already_cancelled` → nada. Sobre una fila
  `Waiting` → `Ignored`.
- **Líneas, envío, dirección y cliente** editados en Shopify después de importar: no se sincronizan.

### 6.4 Reintentos

- **Automático:** cada corrida de conciliación (§5.3) reintenta los `Waiting` de la tienda. Completar una
  ficha o crear un producto surte efecto en menos de `IntervalMinutes` sin suscribir el módulo a eventos de
  Customers ni de Catalog (D-S5).
- **Manual:** `POST …/shopify/orders/{id}/retry` (§7) corre la importación en el request y devuelve la fila.
  Sobre una fila que no está `Waiting` → 422 `shopify.order.not_waiting`. «Sincronizar ahora» (§5.3) también
  reintenta los `Waiting` de la tienda.
- `attempts` y `last_attempt_at` se actualizan siempre; sin tope: un pedido en espera no se abandona solo.

### 6.5 Cambios en el módulo de pedidos, Customers y Reporting

- **Ya hecho por los specs previos** (H1-H6, H20-H23): `Order` con datos propios, `Source`
  (`Quotation` | `Manual` | `Shopify`) y `ExternalReference` con su filtro y sus campos en la lista y el
  detalle; `IExternalOrderWriter`/`ExternalOrderService`, `Order.CreateExternal`,
  `Order.SyncExternalPaymentStatus`, `OrderCustomerEligibility`; transportadoras con `ExternalNames`, IVA del
  envío, `ShippingDetails` y contraentrega.
- **Lo que este spec agrega en Quotations:** `ExternalOrderDraft.Shipping`, `ExternalKey` y
  `ExternalShippingTitle`, `ExternalOrderResult.AlreadyExisted`, `Order.CreateExternal` con envío, las
  columnas `orders.external_key` y `orders.external_shipping_title` con el índice
  `IX_orders_tenant_external_key` y su traducción por nombre, `IOrderRepository.FindByExternalKeyAsync`,
  `shipping.externalShippingTitle` en el detalle, `Carrier.AddExternalName` y la ruta de §6.2.5.
- **Las cotizaciones no cambian:** ni `Quotation`, ni su lista, ni `QuotationCustomerEligibility`, ni los
  reportes de cotizaciones. Un pedido de Shopify no aparece en ninguna pantalla de cotizaciones.
- Customers: `ICustomerProvisioning` (altas completa e incompleta desde el sistema),
  `Customer.CreateIncompleteFromExternalSale`, `ICustomerRepository.FindIdByEmailAsync` con índice
  `IX_customers_tenant_email` sobre `(tenant_id, lower(email))`, y `ICustomersAuditPublisher.PublishByIntegration`.
  El `PUT` que completa un incompleto ya existe (spec 2026-10-10 §6.2) y sirve igual.
- Reporting: `OrdersReportSource` ya lee del pedido y suma el envío (flete §7), así que incluye los pedidos de
  Shopify con `source` y sin `quotationId`. Los reportes que agrupan por clasificación o ciudad ya filtran
  `Completeness == Complete` (CLAUDE.md), así que un comprador incompleto no los rompe.
- El Excel de pedidos no cambia de columnas; `order_number` sigue siendo el `PED-…` (D-S2); con cliente
  incompleto, `Documento` y `Documento de identidad` salen vacías (H9), que es lo que el owner aceptó. Un
  pedido de Shopify sin transportadora reconocida exporta «Coordinadora» en `Transportadora` (flete D-F8) y
  sus columnas de flete salen como las de cualquier pedido.

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
| `POST /api/v1/tenants/{tenantId}/shopify/orders/{id}/warnings/review` | `quotations.order.manage` | «Marcar como revisado» (D-S31): llena `warnings_reviewed_at`; devuelve la fila. Sobre una fila sin avisos → 422 `shopify.order.no_warnings` |
| `POST /api/v1/tenants/{tenantId}/orders/{orderId}/carrier-recognition` | `quotations.order.manage` (+ `tenancy.settings.update` con `rememberName`) | §6.2.5. `{ carrierId, rememberName }` → detalle del pedido |
| `POST /api/webhooks/shopify` | anónimo, `webhook` | §5.1 |

Cambios en rutas existentes: el detalle del pedido gana `shipping.externalShippingTitle` (§6.2.5). `GET …/orders`
ya acepta `source` (H6). Las rutas de cotizaciones no cambian. `withWarnings=true` en la lista del espejo
y `withWarnings` en `summary` cuentan sólo las filas con avisos **sin revisar**.

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
| `shopify.order.no_warnings` | 422 | marcar como revisada una fila sin avisos |
| `order.order.carrier_recognition_unavailable` | 422 | el pedido no trae un método de envío sin reconocer |
| `order.order.not_pending`, `order.order.carrier_not_available`, `order.order.cash_on_delivery_not_supported` | 422 | existentes, en «reconocer este nombre» |
| `quotations.carrier.name_taken` | 422 | existente: el nombre ya es de otra transportadora |
| `order.order.external_key_taken` | — | no llega a HTTP: lo traduce `QuotationsUnitOfWork` y lo resuelve el adaptador (§6.2) |
| `validation.failed` | 422 | campos del `PUT` y de los cuerpos, con el mapa `errors` |
| `tenancy.module_not_enabled` | 403 | módulo `shopify` apagado (existente) |

Los motivos de espera y los avisos (§6.2) **no** son errores HTTP.

## 8. Frontend (`qep-frontend`)

| Dónde | Qué |
| --- | --- |
| `features/auth/types/tenant-modules.ts`, `features/operator/` | `shopify` (y `pipeline`, que tampoco está: H16) en `TENANT_MODULE_KEYS` y `MODULE_LABELS` («Shopify»); con eso la consola de operador dibuja su tarjeta como las de `pos` y `messaging`, y `ModuleGate` la acepta. |
| `features/integrations/` | `onboarding.kind === 'ShopifyOAuth'`: botón «Nueva integración de Shopify» abre un diálogo que pide el dominio (`mi-tienda.myshopify.com`, con ayuda) y llama al inicio A con `redirectUri = origin + '/shopify/callback'`; redirige a `authorizeUrl`. Debajo, «También puedes instalar QEP desde el App Store de Shopify» con `VITE_SHOPIFY_APP_URL`. Por conexión: «Configurar tienda», `waitingCount` y «Sincronizar ahora». Hoy `provider-catalog.tsx` despacha `onboarding.kind` entre `Form`, `MetaEmbeddedSignup` e `InstagramLogin` (H27): `ShopifyOAuth` y la categoría `Commerce` («Comercio») entran a sus tipos y etiquetas. |
| `routes/shopify/install.tsx` (pública) | Inicio B: lee `shop`, `hmac`, `timestamp`, `host`; llama al inicio anónimo; redirige. Errores: `hmac_invalid` / `shop_invalid` → «Abre QEP desde tu tienda de Shopify». |
| `routes/shopify/callback.tsx` (pública) | Canje; con `initiatedTenantId` y sesión en ese tenant, enlaza solo y navega a la configuración; con `linkedTenant`, «Esta tienda ya está enlazada a *X*» y botón a ese tenant; si no, `/shopify/link/$installId`, pasando por `/login` o `/register` con `returnTo` cuando no hay sesión. |
| `routes/_authenticated/shopify/link.$installId.tsx` | Tienda, vencimiento y lista de tenants: elegible, «sin permiso para administrar integraciones», o «módulo no activado: pide a QCode que lo active; la instalación queda guardada hasta el *fecha*». Nombre de la conexión editable. Sin tenants: enlace a `/register` con `returnTo`. `install_expired`: «Vuelve a conectar la tienda». |
| `routes/_authenticated/integrations/shopify/$connectionId.tsx` | Formulario de §6.1: asesor (membresías activas), empresa y cuenta de cobro, clasificación por defecto (opcional, con la explicación de qué pasa sin ella), claves de atributos, tipo de documento por defecto. `If-Match`; 412 → «Alguien cambió esta configuración, recarga». Aviso mientras `isConfigured === false`. Botón «Sincronizar ahora» con el resumen que devuelve. |
| `features/orders/` | Columna «Origen» y filtro «Origen» ya existen (`OrderSourceBadge`, H27). Detalle del pedido: con `shipping.externalShippingTitle`, pedido `Pending` y sin transportadora, «Elegir transportadora» con la casilla «Reconocer “*title*” en los próximos pedidos» (deshabilitada sin `tenancy.settings.update`), §6.2.5. Pestaña «Desde Shopify» con dos vistas: **En espera** (`Waiting` + `Cancelled` tras facturar, motivo en texto desde `describe-wait-reason.ts`, acción contextual: `product_unmatched` → SKUs y enlace a Catálogo; `store_not_configured` → «Configurar tienda»; `customer_inactive` → «Ver cliente»; «Reintentar» con `quotations.order.manage`) y **Con avisos** (`Imported` con `warnings`: «Cliente incompleto» con «Completar ficha» prellenando el documento si vino, «Dirección sin ciudad», «Total distinto al de Shopify», «Transportadora sin reconocer: *title*» con «Ver pedido», «Contraentrega con una transportadora que no recauda»; «Marcar como revisado» por fila). Insignias con `summary` en la pestaña. Los motivos y avisos nuevos (`freight_tax_rate_missing` → «Configura el IVA del envío» con enlace a `/settings/carriers`) van al diccionario. |
| `features/customers/` | Un cliente incompleto que viene de Shopify se completa con el `PUT` de hoy, desde «Completar ficha» de la bandeja. La insignia «Creado desde Shopify» en la ficha queda fuera de esta entrega (D-S32). |
| `features/auth/` | `/login` y `/register` aceptan `returnTo` interno (ruta relativa que empieza con `/` y no con `//`) y vuelven ahí en lugar del *landing*; hoy no existe (H17). |

Textos en español con tuteo; sin exponer el token ni el `state` en logs del navegador.

## 9. Base de datos

Esquema nuevo `shopify` (`ShopifyDbContext`, migraciones en
`Modules.Shopify.Infrastructure/Persistence/Migrations`):

- `shopify.stores` (§6.1). PK `connection_id`; índice `(tenant_id)`.
- `shopify.orders` (§5.4). Único `(connection_id, shopify_order_id)`; índices `(tenant_id, import_state,
  shopify_created_at DESC)`, `(tenant_id, qep_order_id)`, `(connection_id, shopify_customer_id)`.
- `shopify.webhook_deliveries`: `id`, `webhook_id` (único), `topic`, `shop_domain`, `api_version`,
  `payload` jsonb, `received_at`, `attempts`, `lease_until`, `processed_at`, `ignored_reason`, `last_error`.
- Auditoría: `ShopifyDbContext` proyecta `audit.entries` (`ExcludeFromMigrations`, H19) para
  `IShopifyAuditRecorder`, como Messaging e Integrations; no hay tabla `shopify.audit_entries`.

Integrations:

- `integrations.shopify_installs`: `id`, `shop_domain`, `state` (único), `redirect_uri`,
  `initiated_tenant_id` (null), `initiated_by` (null), `shop_id`, `shop_name`, `scopes`,
  `access_token_protected` (bytea, null hasta el canje), `key_id`, `created_at`, `expires_at`. Un
  `IHostedService` barre las vencidas cada hora. El worker de re-cifrado de llaves
  (`ConnectionSecretRekeyWorker`) **sí** la incluye: una fila puede vivir 7 días.
- Campo interno `webhookSubscriptionIds` en la conexión (`fields`), separado por coma.
- Migración del `CHECK` de `provider_key` con `shopify`.

Tenancy: migración del `CHECK` de `tenant_modules.module_key` con `shopify`. **Sin backfill** a los tenants
existentes (D-S20).

Pedidos (`quotations.orders`): `source`, `external_reference` e `IX_orders_tenant_source` **ya existen**
(`AddOrderOwnData`, H1, H4). Este spec agrega `external_key` (`varchar(128)`, null) con el índice único
parcial `IX_orders_tenant_external_key` (`tenant_id`, `source`, `external_key`) `WHERE external_key IS NOT NULL`,
y `external_shipping_title` (`varchar(120)`, null), en una migración `AddOrderExternalKey` del factory de
diseño. Los `FromSql` que leen `quotations.orders`, si los hay, se revisan para que listen las columnas nuevas
(CLAUDE.md). Nada en `quotations.quotations`.

Customers: sin columnas nuevas; índice `IX_customers_tenant_email` sobre `(tenant_id, lower(email))` para
`FindIdByEmailAsync`.

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

## 11. Dependencias: dos specs previos

Los dos están escritos, aprobados e implementados en `develop` (reverificado el 2026-10-18, §2).

### 11.1 Pedidos sin cotización (decisión 16)

`2026-10-15-pedidos-sin-cotizacion-design.md`: `Order` con datos propios dentro de `Modules.Quotations`, tres
fábricas (`FromQuotation`, `CreateManual`, `CreateExternal`), copia independiente al convertir, pedido
manual, `Source`/`ExternalReference`, eventos `quotations.order.created.v1` y `order.cancelled.v1` con
`source`, foto `pipeline.deal_orders`, y migración de los pedidos existentes (`AddOrderOwnData`). Con eso,
D-S21 queda resuelta: un pedido de Shopify **sí** toca el embudo, por `order.created` sin cotización. Su
delta ya está aplicado (§6.2.3, §6.5, §9; historial del 2026-10-15).

### 11.2 Flete

`2026-10-17-flete-design.md`: transportadoras con `ExternalNames` e índice único de nombres, IVA del envío
del tenant, `ShippingDetails` en la cotización y el pedido, `Charged`/`OnDelivery`/`Free`, contraentrega,
totales con envío, Excel y reportes. Su §10 (delta a este spec) está aplicado: `freight_unsupported` se
retira y entra `freight_tax_rate_missing` (§6.2), el envío de Shopify se traduce en §6.2.2, la transportadora
se reconoce por nombre con el aviso `carrier_unrecognized` y «reconocer este nombre» (§6.2.5), la
contraentrega sale de la pasarela con el aviso `cash_on_delivery_unmatched`, `ExternalOrderDraft` gana
`Shipping` (§6.2) y V-8 entra a §16. Los modos activos y por defecto de «¿Quién paga el envío?» (flete §4.4 y
§4.4.1) **no aplican** al camino externo (D-F18).

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
`ShopifyOrderSnapshot` (fixtures armados con la forma documentada por Shopify, que V-9 reemplaza por
reales de una tienda de desarrollo: `taxes_included` en los dos modos, descuentos, recogida, envío cobrado,
envío gratis por descuento, línea de envío retirada, contraentrega, pedido de prueba, cancelado, sin
documento, sin ciudad); verificación HMAC con
vectores conocidos; `ColombiaProvinceCodes` completa contra Geography; evaluador de precondiciones (orden,
`wait_detail`); decisión completo/incompleto del cliente (documento × clasificación × ciudad); avisos;
mapeo de `financial_status`; máquina de estados del espejo; precio con IVA y descuento en COP y en USD;
envío de §6.2.2 caso por caso (recogida, sin líneas, gratis, cobrado con y sin `taxes_included`, redondeo a
pesos, sin tasa → `freight_tax_rate_missing`, transportadora reconocida por nombre y por nombre externo, no
reconocida → aviso, contraentrega con y sin recaudo); `ShopifyCashOnDeliveryGateways` normalizado.
**Quotations:** `Order.CreateExternal` con envío (coherencia de `Validated`, contraentrega pendiente),
`Carrier.AddExternalName`, `RecognizeOrderCarrierHandler` (permisos, `rememberName` sin
`tenancy.settings.update` → 403, no pendiente, sin título, contraentrega sin recaudo).

**Integración (`Modules.Shopify.IntegrationTests`, Testcontainers, `ShopifyApiHarness` que prende el módulo
y crea tienda configurada, asesor, empresa con cuenta, clasificación y productos):** webhook firmado → 200 y
fila; sin firma → 401; duplicado; tienda desconocida; importación con documento → cliente completo con
CUC; sin documento → cliente incompleto y pedido `Pending` igual, con `warnings`; segundo pedido del mismo
comprador reutiliza el cliente (por `shopify_customer_id`, teléfono y email); cliente inactivo → espera;
cada motivo de espera y su reintento manual y automático; cancelación sobre `Pending`, `Approved`,
`Invoiced` y `Waiting`; pago con y sin comprobantes; conciliación con `HttpMessageHandler` falso
(paginación, `THROTTLED`, 401); «Sincronizar ahora» y su 409; conexión pausada y módulo apagado; tenant
ajeno 403; el pedido **no tiene cotización** y no aparece en `GET …/quotations`; Pipeline recibe
`order.created` con `source = Shopify` (D-S21); Excel de pedidos con cliente incompleto sale con las dos
columnas vacías; envío cobrado que suma al total y entra al Excel; reimportar el mismo pedido no lo duplica
(`ExternalKey`, también tras borrar la fila del espejo); «Marcar como revisado»; fuga de token con sentinela.
**Quotations (integración):** `ExternalOrderServiceTests` con envío e idempotencia; `carrier-recognition`
por HTTP (detalle entero, historial, `name_taken` por nombre de índice, 403 de otro tenant).

**Integración en Integrations:** inicio A (permiso, módulo apagado 403, dominio inválido), inicio B (HMAC
bueno y malo, origen no permitido), canje (`state` desconocido, vencido, reutilizado; gateway falso; scopes
faltantes), `installs/{id}` (404 ajeno, `canLink` y `moduleEnabled`), enlace (crea conexión, ruta y
**webhooks** con gateway falso; registro rechazado → `NeedsAttention`; reenlace del mismo tenant; otro
tenant → `shop_already_linked`; `install_tenant_mismatch`; tope 20), probador que repone webhooks.

**Tenancy:** `TenantModuleKeysTests` y `TenantModuleDefaultsTests` con los siete por defecto;
`TenantRegistrationServiceTests` verifica que el tenant nuevo recibe `shopify`; `ChangeTenantModulesHandlerTests`
sigue cubriendo el prendido manual.

**Pedidos y Customers:** los campos nuevos del borrador externo son opcionales y nada se vuelve obligatorio,
así que no hay barrido de cuerpos crudos; sí se actualizan los `new ExternalOrderDraft(...)` de las pruebas
si el record cambia de forma. Reporting de pedidos incluye los de Shopify; `ICustomerProvisioning`,
`CreateIncompleteFromExternalSale` y `FindIdByEmailAsync`.

**Arquitectura:** `ShopifyLayerTests` (capas; sólo Tenancy y Audit entre los módulos; prohibido referenciar
Integrations, Quotations, Customers, Catalog y Geography); `IntegrationsLayerTests` sigue prohibiendo que
un módulo de negocio referencie `Modules.Integrations.Application`; `ConfigurationExampleTests`.

**Frontend (Vitest + Testing Library):** las rutas nuevas (carga, error, éxito, `returnTo`, enlace
automático con `initiatedTenantId`, módulo apagado), diálogo de dominio, formulario de tienda (validación,
412), bandeja en sus dos vistas con cada motivo y aviso, «Marcar como revisado», «Sincronizar ahora»,
«reconocer este nombre» en el detalle del pedido, tarjeta del módulo en operador.

**Suite por módulo tocado**, no la completa (memoria del repo).

## 14. Fuera de alcance

- Historial de pedidos anterior al enlace (D-S4).
- Reembolsos, devoluciones, ediciones de líneas o de dirección después de importar.
- Sincronización hacia Shopify: fulfillment, tracking, inventario, facturas.
- App embebida (App Bridge, token exchange) y la ficha en el App Store.
- Otras plataformas (WooCommerce, VTEX, Mercado Libre).
- Comprobantes de pago para pedidos de la tienda: el pago lo registra Shopify.
- Clientes de Shopify sin pedido (sincronizar la libreta).
- Activación automática del módulo **al enlazar** una tienda a un tenant existente que no lo tiene: el owner
  la descartó; el operador lo prende. Si se quiere después, es un servicio de Tenancy que reutilice
  `ChangeTenantModulesHandler` y una llamada en el enlace.
- Backfill del módulo a los tenants existentes.

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
| D-S11 | **Retirada** (decisión 16): ya no hay cotización de origen Shopify que ocultar | — |
| D-S12 | Pedidos `test = true` y cancelados antes de importar → `Ignored`, visibles con filtro | Importarlos |
| D-S13 | Nombre de la conexión por defecto = `shopName`; la configuración vive en `shopify.stores` (D6 de Integrations) | Mover campos |
| D-S14 | Sin permiso nuevo: `integrations.connection.*` para la tienda y `quotations.order.*` para bandeja y sincronización | Un permiso `shopify.store.manage` con sus cuatro piezas |
| D-S15 | `Billing` vacío (factura al cliente); `paymentMethod` = las pasarelas del pedido unidas con `, ` y recortadas a 50 | Otra parte de facturación |
| D-S16 | Una instalación canjeada vive **7 días** para dar tiempo a registrarse o a que QCode active el módulo | Una constante |
| D-S17 | La `WebhookCallbackUrl` es configuración global, no se deduce del request | Deducirla |
| D-S18 | Con documento pero sin clasificación en la tienda o sin ciudad, el comprador nace **incompleto** y el documento queda en el aviso; no se inventa clasificación | Clasificación obligatoria al configurar |
| D-S19 | `webhookSubscriptionIds` va como campo interno de la conexión, no en `shopify.stores`: lo escribe y lo repone Integrations (enlace y probador) | Moverlo |
| D-S20 | `shopify` entra a `DefaultForNewTenants` **sin backfill** a los tenants existentes: el operador decide tenant por tenant, como se hizo con `pos`, `messaging` y `pipeline` | Una migración de datos que inserte la fila en cada tenant activo |
| D-S21 | **Resuelta** (spec de pedidos sin cotización §7): `order.created` sin cotización gana o crea el negocio, y `order.cancelled` lo reabre | — |
| D-S22 | El tipo de documento del atributo se compara sin mayúsculas con `NIT`, `CC`, `CE`, `PASAPORTE` en el módulo Shopify; uno desconocido cae al tipo por defecto de la tienda, no a espera (2026-10-18) | Un motivo de espera `document_type_unknown` o una tabla de sinónimos por tienda |
| D-S23 | **Idempotencia por `ExternalKey`** = `"{shopId}:{shopifyOrderId}"` con índice único parcial en `quotations.orders`; un pedido repetido devuelve el existente. Con el `shopId` y no el `connectionId`, para que borrar y volver a enlazar la tienda no duplique pedidos ya importados (2026-10-18) | Una columna y un índice; sin ella, duplicados tras una caída del worker |
| D-S24 | El cliente creado se anota en el espejo **antes** de crear el pedido, en una transacción propia del módulo, para no crear dos incompletos en un reintento (2026-10-18) | Un incompleto huérfano si el pedido nunca entra; se ve en Clientes |
| D-S25 | El `title` no reconocido se guarda en el pedido (`external_shipping_title`) para que el detalle ofrezca «reconocer este nombre» sin leer el espejo (BFF); la ruta exige `quotations.order.manage` y, para recordar, también `tenancy.settings.update` (2026-10-18) | Mover la acción a la bandeja de Shopify y quitar la columna |
| D-S26 | Un pedido contraentrega nace `PaymentPending` aunque Shopify lo dé por pagado (el dominio no admite otro, H5) y el pago se sincroniza justo después (2026-10-18) | Relajar `cash_on_delivery_pending_only` para el camino externo |
| D-S27 | Fuera de COP el precio y el envío **no** se inflan con la tasa (QEP no discrimina IVA ahí, H3); en COP el envío se redondea a pesos `AwayFromZero` (2026-10-18) | Una regla por moneda en el importador |
| D-S28 | Con recogida no hay contraentrega: el cliente paga en la tienda y el dominio rechaza envío con recogida (H21); la pasarela queda en `paymentMethod` (2026-10-18) | Un aviso `cash_on_delivery_on_pickup` |
| D-S29 | `Shopify:App:ApiVersion` por defecto `2026-10`, la estable del trimestre en que se implementa (2026-10-18) | Una clave de configuración |
| D-S30 | Con varias líneas de envío no retiradas, se suman los valores y la transportadora sale del `title` de la **primera** (2026-10-18) | Avisar `multiple_shipping_lines` |
| D-S31 | Los avisos de una fila importada se sacan de «Con avisos» con «Marcar como revisado» (`warnings_reviewed_at`); no se limpian solos al resolver la causa (2026-10-18) | Que cada corrección (ficha, transportadora) limpie su aviso por evento |
| D-S32 | La insignia «Creado desde Shopify» en la ficha del cliente queda fuera: exigiría que Customers lea el espejo o guarde el origen del cliente (2026-10-18) | Un campo `origin` en el cliente |
| D-S33 | El dominio de la tienda se acepta hasta 64 caracteres, el tope de `connection_routes.external_id` (H12); uno más largo es `shop_invalid` (2026-10-18) | Ampliar la columna de rutas |

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
| V-8 | El `gateway` (`payment_gateway_names`) y el `financial_status` que trae un pedido **contraentrega** de una tienda colombiana (flete §10) | Pedido de prueba pagado contraentrega | Ajustar `ShopifyCashOnDeliveryGateways` |
| V-9 | La forma real de `shipping_lines` en el REST del webhook (`discounted_price`, `tax_lines`, `code`, `title`, si trae líneas retiradas) frente a `shippingLines` en GraphQL (`isRemoved`, `discountedPriceSet`), y fixtures reales para reemplazar los armados de las pruebas | Pedidos de prueba con envío cobrado, gratis por descuento y recogida | Ajustar los dos parsers |
| V-10 | Que el `title` del método de envío llegue con el nombre que el tenant ve en su checkout, para emparejarlo con `ExternalNames` | Pedido de prueba con cada transportadora configurada | Nada en el código: el tenant agrega el nombre con «reconocer este nombre» |

## 17. Entregables

1. ~~Spec de **pedidos sin cotización**~~ y ~~spec de **flete**~~: escritos e implementados en `develop`.
2. ~~Delta a este spec~~: aplicado (2026-10-15 y 2026-10-18).
3. Backend: Tenancy (clave y módulo por defecto; README y CLAUDE.md actualizados), Integrations (proveedor,
   inicios A y B, canje, enlace con registro de webhooks, probador), pedidos (envío, `ExternalKey` y
   `ExternalShippingTitle` del borrador externo, «reconocer este nombre»), Customers (`ICustomerProvisioning`,
   `CreateIncompleteFromExternalSale`, `FindIdByEmailAsync`), módulo Shopify (webhook, workers, espejo,
   importador, sincronización manual, API), Bootstrapper (adaptadores, mapeo), README.
4. Frontend: claves de módulo, diálogo de dominio, rutas de instalación y enlace, configuración de tienda,
   bandeja en dos vistas, «reconocer este nombre», `returnTo`.
5. Handoff al owner con lo de §10.5 y §16.
6. Planes: `docs/superpowers/plans/2026-10-18-shopify-backend.md` y, en `qep-frontend`,
   `docs/superpowers/plans/2026-10-18-shopify-frontend.md`.

## Historial de revisión

- 2026-10-15: decisiones 1–12 tomadas en conversación con el owner; secciones aprobadas una por una; spec
  escrito.
- 2026-10-15 (misma sesión): el owner corrige: el módulo lo activa el operador a mano (no al instalar); dos
  inicios del OAuth (desde QEP y desde Shopify); N tiendas por tenant; QEP registra los webhooks al enlazar;
  sincronización manual además del cron; el comprador sin documento nace **incompleto y el pedido entra
  igual**. Se agregan las decisiones 13–15, la excepción `allowIncomplete`, los avisos del espejo, D-S16 a
  D-S19 y V-7.
- 2026-10-15 (misma sesión): el owner precisa que la excepción del cliente incompleto vale sólo si el pedido
  no viene de una cotización manual (§6.2.3), y que el módulo `shopify` **entra a los módulos por defecto
  del signup** (decisión 11, segunda revisión; §4.4, D-S20): los tenants nuevos nacen con él, los existentes
  los prende el operador.
- 2026-10-15 (misma sesión): el owner pide **separar cotizaciones de pedidos**: debe poder existir un pedido
  sin cotización y el pedido lleva datos propios para todos (decisión 16). Se retira la cotización fantasma
  de origen Shopify, `Quotation.Origin`, el filtro de la lista de cotizaciones, la exclusión en Reporting y la
  excepción `allowIncomplete` sobre `QuotationCustomerEligibility`; §6.2.3 pasa a describir el pedido con
  datos propios; §11 declara los dos specs previos (pedidos sin cotización y flete); D-S11 retirada, D-S21
  agregada. Pendiente la lectura del owner, los dos specs previos y el plan de implementación.
- 2026-10-15: delta tras implementar el spec de pedidos sin cotización: §6.2.3, §6.3 y §6.5 citan los nombres
  reales (`IExternalOrderWriter`, `ExternalOrderDraft`, `ExternalOrderLine`, `ExternalOrderResult`,
  `Order.CreateExternal`, `OrderCustomerEligibility`), un pedido externo pagado no exige comprobantes, y
  D-S21 queda resuelta.
- 2026-10-18: el owner pide implementarlo. Delta del flete §10 aplicado (§6.2, §6.2.2, §6.2.5, §6.3, §7, §8,
  §9, §13, §16): `freight_unsupported` se retira y entra `freight_tax_rate_missing`; el envío de Shopify se
  traduce a `ShippingDetails` (recogida, gratis, cobrado con la tasa del tenant); la transportadora se
  reconoce por `ExternalNames` con el aviso `carrier_unrecognized` y la ruta «reconocer este nombre»; la
  contraentrega sale de la pasarela con el aviso `cash_on_delivery_unmatched`; `ExternalOrderDraft` gana
  `Shipping`; V-8 a V-10. Reverificación de §2 contra `develop` (`f4d8849d`): H1-H6 reescritos (pedido con
  datos propios, `IExternalOrderWriter`, `SyncExternalPaymentStatus` sólo en pendiente, filtro por origen ya
  hecho), H20-H27 nuevos (transportadoras, `ShippingDetails`, tasa del envío, edición del envío, notas sin
  tope, partes, módulo `Platform`, rutas del frontend); se corrigen la ruta de configuración de la tienda
  (`/integrations/shopify/$connectionId`, no `/settings/…`), las claves de módulo del frontend (no viene del
  backend: falta incluso `pipeline`), la auditoría (proyección de `audit.entries`, no tabla propia), las notas
  (sin tope), el host del webhook (`qep-api.qcode.co`, `k8s/prod-ingress.yaml:24`) y la cancelación de un facturado (la detecta el código `already_invoiced`). Decisiones sin el
  owner D-S22 a D-S33, entre ellas la idempotencia por `ExternalKey` (D-S23), que el código de hoy no tiene.

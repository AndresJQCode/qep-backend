# Perfiles de facturación del cliente — instrucciones para `qep-backend`

Fecha: 2026-11-24. Para: quien implemente en `qep-backend` (otra persona o sesión). Origen: el frontend
(`qep-frontend/docs/superpowers/specs/2026-11-24-perfiles-de-facturacion-design.md` y su plan). Este documento se copia
a `qep-backend/docs/superpowers/specs/` como spec del backend; ahí se escribe su plan. **Se despliega primero el
backend.** Rutas y líneas citadas: `develop` al 2026-11-24; confírmalas antes de editar. Incluye las decisiones del
owner del 2026-11-24 (§9).

## 1. Qué se pide

Un cliente factura con distintos datos (otra razón social, otro documento, otro correo de facturación, otras
retenciones). Se agrega a Customers una **libreta de perfiles de facturación**, gemela de la libreta de direcciones
(CLI-DIR-01): sub-recurso del cliente, entidad hija del agregado `Customer`, con una principal y un tope de 50. La
cotización y el pedido la leen en el bloque `client` y **copian** el perfil elegido en su facturación de siempre
(`QuotationPartyRequest` + `BillingWithholdingTaxRateIds` + `BillingVatSurplus`); ningún documento guarda el id del
perfil. La ficha del cliente los administra (alta, edición, baja, principal).

No es `CustomerBillingProfile` de ServiceContracts (cobros recurrentes): otro concepto, no se reutiliza ni se renombra.

## 2. Convenciones que se imitan (con su fuente)

| Convención | Dónde está hoy |
| --- | --- |
| Sub-recurso con 4 operaciones, todas responden `CustomerResponse` entero | `Modules.Customers.Api/CustomerEndpoints.cs:101-133`; `Modules.Customers.Application/ManageCustomerAddresses.cs:8-17` |
| Permiso `customers.customer.manage` en la política **y** revalidado en el handler antes de validar | `CustomerEndpoints.cs:104`; `ManageCustomerAddresses.cs:164-168`, `:316-317`; `CustomersPermissions.cs:13` |
| Sin `If-Match` en la libreta | `CustomerEndpoints.cs:101-102` |
| La operación de libreta sube la versión del cliente (`Touch`) | `Customer.cs:940-946` |
| Sin auditoría en las operaciones de libreta | `ManageCustomerAddresses.cs:148-301` (ningún handler publica auditoría) |
| Entidad hija sin repositorio; la principal la decide el agregado | `CustomerAddress.cs:3-7`, `:114-125`; `Customer.ApplyPrincipal`, `Customer.cs:853-865` |
| La primera es principal; marcar otra desmarca; la principal no se borra | `Customer.cs:746`, `:813-816`, `:836-851` |
| Cliente incompleto, fusionado o inactivo no se edita | `Customer.cs:733-742` (`customers.customer.incomplete`), `EnsureNotMerged`, `EnsureActive` (`Customer.cs:1292-1298`, `CustomersErrorCodes.Inactive`) |
| Validador de FluentValidation para el mapa `errors` además del código de dominio | `ManageCustomerAddresses.cs:53-118`; `CLAUDE.md` «Un caso de uso con texto libre lleva validador» |
| Dos carriles de ciudad + región sólo fuera de CO, con CHECK en base | `CustomerAddress.cs:211-256`; `CustomersDbContext.cs:357-360`; `ManageCustomerAddresses.cs:77-109` |
| Región resuelta contra Geography en el handler; ciudad comprobada antes del agregado | `ManageCustomerAddresses.cs:169-170`, `:323-330` |
| Comparación «misma fila» normalizada | `Customer.EnsureAddress`, `Customer.cs:760-792` |
| Tipos de documento por país, una sola tabla | `BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs`; `.claude/rules/customers.md` |
| Jurídica en Colombia se identifica con NIT | `Quotations.Domain/BillingPartyFiscalRules.cs:18-35`; `CustomersErrorCodes.PersonTypeRequiresNit` (`CustomersErrorCodes.cs:26`) |
| Reglas de retenciones del cliente (existe, activa, retiene; nunca municipal; sin solape país+tipo+alcance) | `CustomerWithholdingRules.ValidateAsync` / `Validate`, `CustomerWriteRules.cs:205-261`; almacenamiento `Customer.SetWithholdingTaxRates`, `Customer.cs:871-897` (`CustomerTaxRate`) |
| Topes de la parte de facturación (nombre 255, teléfono 32, email 254, dirección 255, documento 32, ciudad 120) | `Quotations.Domain/QuotationPartyDetails.cs:53-68` |
| Bloque `client` del documento armado en Bootstrapper desde el agregado | `Bootstrapper/QuotationCustomerLookup.cs:156-232`; `IQuotationCustomerLookup.cs:89-146`; `QuotationResponseComposer.cs:258-296`; `QuotationsDtos.cs:695-738` |
| Tasas del cliente pintadas sin otra llamada (BFF) | `CustomerWithholdingTaxRateDto`, `CustomersDtos.cs:147-162` |
| La fusión mueve lo propio del fusionado | `CustomerMerge.cs:115-123`; `CustomerMergeStore.cs:113-185` |

## 3. Contrato HTTP

Base: `/api/v1/tenants/{tenantId:guid}/customers/{customerId:guid}`. Todas con
`RequireAuthorization(CustomersPermissions.CustomerManage)`, **sin `If-Match`**, y responden **200 con
`CustomerResponse` completo**.

| Método | Ruta | Qué hace |
| --- | --- | --- |
| `POST` | `/billing-profiles` | Agrega un perfil. Idempotente por contenido (§3.2). |
| `PUT` | `/billing-profiles/{profileId:guid}` | Reemplaza el perfil entero. `isPrincipal: true` lo hace principal; `false` no desmarca. |
| `DELETE` | `/billing-profiles/{profileId:guid}` | Lo quita. La principal no se quita (422). |
| `POST` | `/billing-profiles/{profileId:guid}/principal` | Lo marca principal. |

### 3.1 Cuerpo (`CustomerBillingProfileRequest`, mismo para `POST` y `PUT`)

```json
{
  "name": "Comercial Andina SAS",
  "personType": "Juridica",
  "identificationType": "NIT",
  "identificationNumber": "900123456",
  "phone": "3001234567",
  "email": "facturas@andina.co",
  "address": "Cra 43A # 1-50",
  "country": "CO",
  "cityId": "6f1c…",
  "cityName": null,
  "regionId": null,
  "withholdingTaxRateIds": ["a1b2…"],
  "vatSurplus": true,
  "isPrincipal": false
}
```

| Campo | Regla | Código (422) |
| --- | --- | --- |
| `name` | requerido, ≤ 255 recortado. Es a quién se factura; no hay alias. | `customers.billing_profile.name_required` / `name_too_long` |
| `identificationNumber` | requerido, ≤ 32 recortado | `customers.billing_profile.identification_required` / `identification_too_long` |
| `identificationType` | requerido; uno de `DocumentTypesByCountry.All`; permitido para `country` | `customers.billing_profile.identification_type_invalid` / `document_type_not_allowed` |
| `personType` | opcional; `Natural` \| `Juridica`. En CO, `Juridica` exige `NIT` | `customers.billing_profile.person_type_invalid` / `person_type_requires_nit` |
| `phone` | opcional, ≤ 32 | `customers.billing_profile.phone_too_long` |
| `email` | opcional, ≤ 254, forma de email | `customers.billing_profile.email_invalid` |
| `address` | requerida, ≤ 255 | `customers.billing_profile.address_required` / `address_too_long` |
| `country` | opcional; vacío = `CO`; ISO alfa-2, a mayúsculas | `customers.billing_profile.country_invalid` |
| `cityId` / `cityName` / `regionId` | carriles idénticos a la dirección (`CustomerAddress.cs:211-256`) | `customers.billing_profile.city_required` / `city_lane_invalid` / `region_lane_invalid`; ciudad inexistente `customers.customer.city_not_found`; región inválida con una constante propia en `CustomerRegionRules` |
| `withholdingTaxRateIds` | lista (ausente o `null` = vacía). **Las mismas reglas y códigos que las tasas del cliente**: `CustomerWithholdingRules.ValidateAsync` (`CustomerWriteRules.cs:205-261`) — existe en el tenant, activa, retiene; ninguna municipal; sin solape país+tipo+alcance. Se deduplica. | `customers.customer.withholding_tax_rate_invalid` / `withholding_tax_rate_municipal` / `withholding_type_repeated` |
| `vatSurplus` | bool, requerido en el cuerpo (ausente = `false`) | — |
| `isPrincipal` | bool | — |

Por qué estos campos: son los de la facturación con datos propios del documento — la parte
(`QuotationPartyRequest`, `QuotationsDtos.cs:367-387`), sus retenciones (`BillingWithholdingTaxRateIds`,
`QuotationsDtos.cs:419-423`) y su excedente de IVA (`BillingVatSurplus`, `:416-418`) — menos `BillingUsesBusinessName`
(sólo aplica con los datos del cliente). En el documento el excedente puede ser `null` («sin contestar»); un perfil ya
contestó, por eso es `bool`. Las retenciones municipales (ReteICA) no se guardan: son de la empresa facturadora y del
municipio de la operación (A15 del spec ReteICA por empresa), y la regla del cliente ya las rechaza.

El documento valida las tasas contra el país de la empresa facturadora; el perfil no conoce esa empresa, así que acepta
tasas de cualquier país (el frontend precarga las del país de la empresa elegida y descarta el resto). Un perfil cuya
tasa se desactiva después se sigue leyendo (con `isActive: false`), pero un `PUT` igual la rechaza: es la misma
conducta que la ficha del cliente (`CustomerWriteRules.cs:234-239`).

El validador de FluentValidation da el mapa `errors` (`Name`, `IdentificationNumber`, `IdentificationType`,
`PersonType`, `Phone`, `Email`, `Address`, `CityId`, `CityName`, `RegionId`, `WithholdingTaxRateIds`); el dominio da el
código. «Autorizar antes de validar» como `ManageCustomerAddresses.cs:164-168`.

**Tipo de persona exigido por el tenant:** el perfil no lo exige. Si el tenant lo exige, lo pide el documento al usarlo;
así un cambio de política no deja perfiles imposibles de guardar.

### 3.2 Idempotencia del `POST` (deduplicación)

Antes de agregar, el agregado busca un perfil igual; si lo encuentra lo devuelve sin tocarlo y sin subir versión
(200 igual). «Igual» = mismo `country`, mismo `identificationType`, mismo número normalizado (sin no-alfanuméricos y en
mayúsculas, la expresión de `CustomerIdentificationSql.NormalizedNumber`, ver `.claude/rules/customers.md`), mismo
`name` y misma `address` con `SameText` (`Customer.cs:788-792`), y misma ciudad (id en CO, texto normalizado afuera).

Teléfono, email, región, tipo de persona, **retenciones y excedente de IVA no cuentan**: un duplicado con otros
impuestos devuelve el existente sin cambiarlo (para cambiarlos está el `PUT`). Motivo: el frontend guarda el perfil
después de grabar el documento, y un reintento o dos documentos seguidos con el mismo tercero no deben duplicarlo.
El duplicado tampoco cuenta contra el tope.

### 3.3 Principal

- El primer perfil de un cliente queda principal aunque llegue `isPrincipal: false` (como `Customer.cs:746`).
- Marcar uno desmarca el resto. El `PUT` con `false` no desmarca.
- La principal no se quita: 422 `customers.billing_profile.principal_not_removable`.

### 3.4 Tope

50 perfiles por cliente (constante del dominio, p. ej. `CustomerBillingProfile.MaxPerCustomer = 50`). Un `POST` que no
es duplicado con 50 ya guardados responde 422 `customers.billing_profile.limit_reached`. El `PUT` no cuenta.
`CustomerResponse` y `QuotationClientResponse` mandan `billingProfileLimit: 50` para que la pantalla no ofrezca guardar
sin escribir el número en el frontend (BFF; anótalo en el DTO).

### 3.5 Errores

| Status | Cuándo |
| --- | --- |
| 403 | Sin `customers.customer.manage`, u otro tenant (nunca 404 por otro tenant) |
| 404 `customers.customer.not_found` | Cliente inexistente en el tenant (`CustomerNotFound.cs:11`) |
| 422 `validation.failed` + `errors` | Validador |
| 422 `customers.billing_profile.*` | §3.1–3.4; perfil inexistente ⇒ `customers.billing_profile.not_found` (como `customers.address.not_found`, `Customer.cs:948-951`) |
| 422 `customers.customer.withholding_*` | Retenciones (§3.1) |
| 422 `customers.customer.incomplete` / `inactive` / `merged` | Cliente no editable |

### 3.6 Lectura

- `CustomerDto`/`CustomerResponse` agregan `BillingProfiles` (**siempre presente**, vacía si no hay; principal primero
  y luego por `Name`) y `BillingProfileLimit`:

```json
{ "id": "…", "name": "…", "personType": "Juridica" | null, "identificationType": "NIT", "identificationNumber": "…",
  "phone": "…" | null, "email": "…" | null, "address": "…", "country": "CO",
  "cityId": "…" | null, "cityName": "Medellín", "departmentId": "…" | null, "departmentName": "Antioquia" | null,
  "region": { "id": "…", "name": "Madrid" } | null,
  "withholdingTaxRates": [ { "id": "…", "name": "…", "type": "Retefuente", "country": "CO", "percentage": 2.5,
                             "isActive": true, "appliesTo": "All" } ],
  "vatSurplus": true, "isPrincipal": true }
```

  `withholdingTaxRates` usa `CustomerWithholdingTaxRateDto` (`CustomersDtos.cs:155-162`), resuelto con el mismo
  `ICustomerTaxRateLookup` que las tasas del cliente. `cityName`, `departmentId` y `departmentName` viajan resueltos
  también en CO, como `QuotationClientAddressResponse` (`QuotationCustomerLookup.cs:206-232`).
- `QuotationCustomerRef` gana `BillingProfiles` y `BillingProfileLimit`; `QuotationClientResponse` los expone con la
  misma forma, llenados en `ToRef` (`QuotationCustomerLookup.cs:156-204`) y `QuotationResponseComposer.ToClientResponse`
  (`QuotationResponseComposer.cs:258-296`). Lo leen el editor de la cotización y el del pedido (`OrderResponseComposer`
  reusa `ToClientResponse`). Las ciudades, regiones y tasas de los perfiles entran a los mismos batch que las de las
  direcciones y del cliente (`CustomerCityIds.OfAddresses`, `CustomerMapping.cs:218-227`; agrega `OfBillingProfiles`).
- El listado de clientes y el Excel no los traen.

## 4. Dominio y persistencia

- `CustomerBillingProfile` (entidad hija, `Modules.Customers.Domain`), `CustomerBillingProfileId`,
  `CustomerBillingProfileDetails` (value object con `Normalized()`), calcados de `CustomerAddress.cs`.
- En `Customer`: `_billingProfiles`, `BillingProfiles`, `EnsureBillingProfile` (el `POST`, con tope),
  `UpdateBillingProfile`, `RemoveBillingProfile`, `MakeBillingProfilePrincipal`; cada uno con `EnsureNotMerged`,
  `EnsureActive`, la regla de incompleto y `Touch`.
- Tabla `customers.customer_billing_profiles`: `id`, `customer_id` (FK CASCADE), `name` (255), `person_type` (null),
  `identification_type`, `identification_number` (32), `phone` (32, null), `email` (254, null), `address` (255),
  `country` (2, default `CO`), `city_id` (null, FK manual a `geography.cities` ON DELETE RESTRICT como
  `CustomersDbContext.cs:378-385`), `city_name` (120, null), `region_id` (null), `vat_surplus` (bool, default false),
  `is_principal`, `created_at`, `updated_at`. CHECK `CK_customer_billing_profiles_city_lane` con el texto de
  `CK_customer_addresses_city_lane` (`CustomersDbContext.cs:357-360`). Índices `IX_customer_billing_profiles_customer` y
  `(customer_id, is_principal)` **no único** (`CustomersDbContext.cs:402-407`).
- Tabla `customers.customer_billing_profile_tax_rates` (`billing_profile_id` FK CASCADE, `tenant_id`, `tax_rate_id`;
  PK compuesta), calcada de la de tasas del cliente (`CustomerTaxRate`, migración `AddCustomerTaxRates`).
- Repositorio: `CustomerRepository.FindAsync` incluye `BillingProfiles` (y sus tasas) donde hoy incluye `Addresses`
  (`CustomerRepository.cs:73`, `:355`, `:609`).
- Migración `AddCustomerBillingProfiles`: sólo crea (expand), sin backfill. Lee `.claude/rules/build-y-despliegue.md`
  antes de generarla. Si alguna consulta de otro módulo lee estas tablas por SQL crudo, regístralo en
  `.claude/rules/sql-entre-modulos.md` (hoy no: Quotations lee por el adaptador del agregado).
- **Borrar una tasa del catálogo:** si Catalog tiene un candado o conteo de uso de tasas por clientes, súmale los
  perfiles (a confirmar leyendo cómo se protege hoy `customer_tax_rates`).
- **Fusión:** el que queda recibe los perfiles del fusionado (dedupe §3.2: si colisiona gana el del que queda; el tope
  no bloquea la fusión) y `CustomerMergeMoveKind` suma `BillingProfiles` (`CustomerMerge.cs:15`, `:115-123`;
  `CustomerMergeStore.cs:113-185`). Ninguno pasa a principal si el que queda ya tiene uno.
- **Borrado de clientes:** cae por CASCADE.

## 5. Auditoría y eventos

**Ninguno** (decisión del owner: paridad con direcciones, que no auditan). Sin eventos de integración: ningún módulo
reacciona a un perfil, el documento copia.

## 6. Qué NO cambia en Quotations

`QuotationPartyRequest`, `QuotationPartiesRequest`, sus validadores (`QuotationEdits.cs:113-165`,
`OrderEdits.cs:479-489`) y el dominio de la parte no cambian: el documento sigue recibiendo la copia. No se agrega
`billingProfileId` a ningún request ni respuesta de documento. Sólo cambia el bloque `client` de la respuesta (§3.6).

## 7. Pruebas esperadas

Unitarias (`tests/Modules/Customers/Modules.Customers.UnitTests`), como `CustomerAddressTests.cs` y
`CustomerEnsureAddressTests.cs`:

- Carriles: CO sin `cityId` ⇒ `city_required`; CO con `cityName` ⇒ `city_lane_invalid`; ES con `cityId` ⇒
  `city_lane_invalid`; CO con `regionId` ⇒ `region_lane_invalid`.
- Documento: tipo fuera de la lista; `NIF` con `CO` ⇒ `document_type_not_allowed`; `Juridica` + `CC` en CO ⇒
  `person_type_requires_nit`; `Juridica` + `NIF` en ES permitido.
- Principal: el primero queda principal con `false`; marcar otro desmarca; la principal no se borra.
- Ensure: duplicado con número escrito distinto (puntos, guiones, minúsculas) devuelve el existente sin `Touch`;
  teléfono, retenciones o excedente distintos siguen siendo duplicado; dirección distinta agrega.
- Tope: el 51.º distinto ⇒ `limit_reached`; un duplicado con 50 guardados devuelve 200.
- Incompleto, inactivo y fusionado rechazan.

Integración (`Modules.Customers.IntegrationTests`, nuevo `CustomerBillingProfilesApiTests.cs` calcado de
`CustomerAddressesApiTests.cs`; filtra por nombre mientras iteras):

- `POST` devuelve `CustomerResponse` con `billingProfiles` (CO con ciudad y departamento resueltos; ES con `region`;
  tasas con nombre, tipo y porcentaje) y `billingProfileLimit: 50`.
- Repetir el `POST` no crea fila ni sube la versión.
- Retenciones: municipal ⇒ `withholding_tax_rate_municipal`; inactiva ⇒ `withholding_tax_rate_invalid`; dos
  Retefuente del mismo alcance ⇒ `withholding_type_repeated`; con el mapa `errors` donde aplique.
- `PUT` reemplaza (omitir `regionId` la borra; lista vacía de tasas las borra); `DELETE` de la principal ⇒ 422;
  `POST …/principal` cambia la principal.
- 403 sin permiso y con otro tenant; 404 cliente inexistente.
- `GET /customers/{id}` trae `billingProfiles: []` en un cliente sin perfiles (nunca ausente).
- Fusión: los perfiles del fusionado pasan al que queda; la vista previa cuenta `BillingProfiles`.

Quotations / Bootstrapper (donde se prueba hoy el bloque `client`): el detalle de cotización y el de pedido traen
`client.billingProfiles` y `client.billingProfileLimit` con el mismo orden y forma.

Arquitectura: sin cambios de capas.

## 8. Orden de despliegue

1. Backend: migración (expand), lectura en cliente y en `client` del documento, y endpoints.
2. Frontend (`feature/billing-profiles`). Con backend viejo el frontend no ofrece nada nuevo.

Merges de agente terminan en `develop`; `docs/estado.md` se actualiza en `develop` al mergear.

## 9. Decisiones del owner (2026-11-24)

- Tarjeta de administración en la ficha del cliente: sí (este contrato ya la cubre con las cuatro operaciones).
- Retenciones (sin municipales) y excedente de IVA se guardan en el perfil; el documento los precarga y siguen
  editables por documento.
- Sin alias: `name` es a quién se factura.
- Tope de 50 perfiles por cliente, 422 con `code`.
- Sin auditoría (paridad con direcciones).
- Teléfono y email opcionales.
- Clave de dedupe de §3.2 (impuestos fuera de la clave).

- En «Editar pedido» la entrega (recoger/enviar y dirección de envío) se edita mientras el pedido no esté facturado ni
  preparado, validado en el backend (§10).
- La entrega se edita en todo origen, también `Shopify` y `Wholesale` (§11), y en Shopify también fuera de Colombia.
- Sin aviso al distribuidor en esta rama (S-2).
- S-3 y S-4 resueltos con specs aparte (§11.5).

Decisiones tomadas en este documento al incorporar lo anterior: las retenciones reusan las reglas y los códigos de las
del cliente en vez de códigos propios; el tope viaja en la respuesta (`billingProfileLimit`); un duplicado no cuenta
contra el tope ni actualiza impuestos; el perfil acepta tasas de cualquier país.

## 10. Entrega editable al editar un pedido

Por orden del owner (2026-11-24) se revierte, para la entrega, la regla «el pedido no edita sus partes» (H5):
`SaveOrderEdits.cs:290-292` («el pedido no edita sus partes (H5)») y el comentario de `SaveOrderEditsRequest.Shipping`
(`OrdersDtos.cs:297-299`: «No cambia recoger o enviar, que el pedido no edita»). Es otra pieza de la misma rama del
frontend: el «Editar pedido» gana el selector de dirección y el switch «Guardar esta dirección en el cliente» que ya
tiene «Nuevo pedido».

### 10.1 Condición (la misma en el spec y el plan del frontend)

La entrega se edita sólo si el pedido **no está facturado y no está preparado** (y está abierto). El backend ya expresa
los dos estados: `Order.IsInvoiced => InvoicedAt is not null` y `Order.IsPrepared => FulfillmentStatus ==
OrderFulfillmentStatus.Prepared` (`Order.cs:364-366`); la guarda general es `Order.IsEditable` (`Order.cs:368-375`:
abierto, sin preparar, sin facturar, mayorista sin aprobar) con `EnsureEditable` → `order.order.not_editable`
(`Order.cs:2413-2419`, `ExternalOrderErrors.cs:14`). Para que el cambio de entrega diga el motivo exacto, el nuevo
`Order.SetDelivery` llama primero a los guardas existentes `EnsureNotInvoiced` → 422 `order.order.already_invoiced` y
`EnsureNotPrepared` → 422 `order.order.already_prepared` (`Order.cs:2429-2443`, `OrderStateErrors.cs:16,24`), y después
a `EnsureEditable`. El handler aplica la entrega **antes** que el resto de la edición, para que esos códigos salgan aunque
otra parte del cuerpo también fallara por `not_editable`.

### 10.2 Contrato

`SaveOrderEditsRequest` (`OrdersDtos.cs:287-330`) y, por herencia, `PreviewOrderEditsRequest` (`OrdersDtos.cs:336`,
`POST /orders/{id}/preview`) ganan:

```csharp
/// <summary>Recoger o enviar y a dónde (owner 2026-11-24, revierte H5 para la entrega). <b>Ausente conserva</b> la
/// entrega guardada: un frontend viejo no la toca. Sólo con el pedido sin facturar y sin preparar.</summary>
OrderDeliveryEditRequest? Delivery = null

public sealed record OrderDeliveryEditRequest(
    bool IsStorePickup,
    /// <summary>La parte de envío con datos propios; null = la dirección del cliente. Con recogida debe venir null.</summary>
    QuotationPartyRequest? Shipping);
```

Mismo significado que en `QuotationPartiesRequest` (`QuotationsDtos.cs:389-404`): `IsStorePickup` gana; `Shipping`
null y enviar = los datos del cliente; la parte de envío no lleva documento (el validador de partes la ignora,
`QuotationEdits.cs:107-108`).

Reglas:

| Caso | Resultado |
| --- | --- |
| `Delivery` ausente | Nada cambia (frontend viejo). |
| Pedido facturado / preparado con `Delivery` presente | 422 `order.order.already_invoiced` / `order.order.already_prepared` (§10.1), también en el preview. |
| `IsStorePickup: true` y `Shipping` no null | 422 `order.delivery.shipping_party_on_pickup` (en la cotización el agregado la descarta; aquí se rechaza para que el frontend no crea que guardó una dirección). |
| Enviar → recoger | Se quita la fila de envío, `IsStorePickup = true`, y el envío pasa a `ShippingDetails.None` (transportadora, quién paga, valor y contraentrega limpios). Un `Shipping` (`ShippingRequest`) con transportadora o valor en el mismo cuerpo sigue siendo `order.order.shipping_not_allowed_on_pickup` (`ShippingDetails.cs:31-36`). |
| Recoger → enviar, `Shipping` null | Usa la dirección del cliente; si el cliente no tiene domicilio (`QuotationCustomerRef.Address` vacío), 422 `order.delivery.address_required`. |
| `Shipping` con datos propios | Las reglas de la parte de envío de crear pedido: `QuotationPartyDetails` (carriles y topes, `QuotationPartyDetails.cs:53-151`) y `PartyRegionRules.EnsureAsync` con prefijo `Parties.Shipping` (`PartyRegionRules.cs:14-30`), como hace hoy la facturación en `SaveOrderEdits.cs:209-211`. El validador de crear pedido para la parte (`CreateOrder.cs:104-107`) se reutiliza con la clave `Delivery.Shipping.*` en el mapa `errors`. |
| Contraentrega | Pasar a recoger la apaga. Prenderla con envío sigue `Order.SetShipping` (`Order.cs:1334-1343`, R16, `order.order.cash_on_delivery_pending_only`). |
| Mismo estado que el guardado | No cambia nada: ni versión, ni historial, ni auditoría (el corto de `SaveOrderEdits.cs:255-260` lo incluye). |

El envío (`ShippingRequest`) se resuelve **después** de la entrega, con el `IsStorePickup` ya actualizado: hoy se
resuelve con el del pedido (`SaveOrderEdits.cs:230-234`, `order.IsStorePickup`), que sería el viejo. Igual el
recálculo del estado de pago va después, como ahora.

Dominio: `Order.SetDelivery(bool isStorePickup, QuotationPartyDetails? shipping, MemberId updatedBy, DateTimeOffset
occurredAt)` devuelve si cambió; modifica la fila existente en vez de reemplazarla (como la facturación,
`SaveOrderEdits.cs:201-203`, índice `IX_order_parties_order_role`) y la borra con recogida o con «datos del cliente».

### 10.3 Capacidad

El detalle del pedido no tiene hoy una bandera para esto (`OrderDetailResponse`, `OrdersDtos.cs:536-558`; el
frontend infiere la facturación editable de la sesión). Se agrega a `OrderResponse` un
`bool DeliveryEditable` = el backend soporta §10 **y** `IsEditable` (abierto, sin preparar, sin facturar). **No depende
del origen** (owner 2026-11-24: también `Shopify` y `Wholesale`, `OrderSource.cs:5-14`); lo que cada origen necesita está
en §11. Ausente en un backend viejo: el frontend deja la entrega de sólo lectura.

### 10.4 Historial y auditoría

- Una fila `OrderHistoryEventType.PartiesChanged` (`OrderHistoryEventType.cs:7`) con un texto nuevo
  `OrderChangeSummary.DeliveryChanged(before, after)` — p. ej. «Entrega: Enviar a Cra 7 # 12-30, Medellín → Recoger en
  tienda» —, en el mismo bloque y con la misma forma que la del envío (`SaveOrderEdits.cs:288-302`).
- Auditoría `quotation.order.delivery_changed` por el `auditPublisher` del módulo, igual que
  `quotation.order.shipping_changed` (`SaveOrderEdits.cs:299-301`). (La regla «sin auditoría» del §5 es de los perfiles en
  Customers; aquí se sigue la convención de los cambios del pedido.)
- Si en el mismo guardado cambian la entrega y el envío, salen las dos filas.
- La cotización de origen no se toca.

### 10.5 Pruebas esperadas (Quotations)

Unitarias del agregado:

- `SetDelivery` en un pedido facturado ⇒ `order.order.already_invoiced`; preparado ⇒ `order.order.already_prepared`
  (los dos casos, aunque el resto del pedido sea editable).
- Enviar → recoger deja `ShippingDetails.None`, quita la fila de envío y apaga la contraentrega.
- Recoger → enviar con parte propia crea la fila; con `null` no crea fila.
- Mismo estado ⇒ `false` sin `Touch`.

Integración (`PUT /orders/{id}` y `POST /orders/{id}/preview`):

- Facturado ⇒ 422 `order.order.already_invoiced`; preparado ⇒ 422 `order.order.already_prepared`, en el `PUT` y en
  el preview.
- `Delivery` ausente conserva la entrega (cuerpo de un frontend viejo).
- Enviar → recoger: el detalle sale sin parte de envío, `isStorePickup: true`, envío en cero; el total y el estado de
  pago se recalculan; fila `PartiesChanged` y auditoría `quotation.order.delivery_changed` en la misma transacción.
- Recoger → enviar con parte de España con región: guarda `regionId`; con región de otro país ⇒
  `quotations.party.region_invalid` con la clave `Delivery.Shipping…` en `errors`.
- Recoger → enviar sin parte y cliente sin domicilio ⇒ `order.delivery.address_required`.
- Recoger con `Shipping` ⇒ `order.delivery.shipping_party_on_pickup`.
- Contraentrega: con comprobantes, pasar a enviar y prenderla ⇒ `order.order.cash_on_delivery_pending_only`.
- `deliveryEditable` en el detalle: `true` en un pedido abierto sin preparar ni facturar de cualquier origen (`Manual`,
  `Shopify`, `Wholesale` por aprobar); `false` facturado, preparado o mayorista aprobado.

### 10.6 Despliegue

Igual que el resto: backend primero. El frontend sólo manda `delivery` con `deliveryEditable === true`.

## 11. Correcciones por origen del pedido (entrega editable en todo origen)

Decisión del owner (2026-11-24): la entrega se edita también en pedidos de `Shopify` y `Wholesale`, mientras no estén
facturados ni preparados (§10.1). Lo que hace falta para que sea seguro:

### 11.1 Shopify — empujar la dirección cambiada

Hoy:

- La corrección del panel (`PUT …/shopify/orders/{id}/corrections`) escribe el pedido y encola un envío
  `ShopifyPushGroup.ShippingAddress` (`ShopifyOrderCorrections.cs:355-408`; grupos en `ShopifyOrderPush.cs:4-9`), por
  `ShopifyOrderCorrectionWriter` (`Bootstrapper/ShopifyOrderCorrectionWriter.cs:27`). Es sólo Colombia
  (`ShopifyOrderCorrections.cs:42-43`, plan D21).
- El worker arma el `orderUpdate` leyendo el pedido **al mandar** (`ShopifyOrderPush.cs:19-27`: el envío guarda qué
  grupos, no los valores) y exige una ciudad DIVIPOLA (`ShopifyOrderPushProcessor.cs:101-125`; con otra lanza en
  `:106-109`).
- Un cambio de la parte de envío por `PUT /orders/{id}` **no** encola nada: queda divergente con Shopify. No se pisa:
  el planificador lo ve como `qep_changed` y gana QEP (`ShopifyEditPlanner.cs:39-48`).

Corrección:

1. `SaveOrderEdits`, cuando aplica `Delivery` (§10) a un pedido `Source == Shopify` y la parte de envío propia cambió,
   publica al outbox de Quotations, en la misma transacción, un evento nuevo
   `quotations.order.shipping-party-changed.v1` `{ tenantId, orderId, source, occurredAt }`. Shopify lo consume por su
   inbox y llama `ShopifyOrderPushes.EnqueueAsync(row, [ShopifyPushGroup.ShippingAddress], …)` como hace la corrección
   de ciudad (`ShopifyCityCorrections.cs:296`). Idempotente: un envío abierto se fusiona (`ShopifyOrderPushes.cs:19-31`,
   índice `IX_order_pushes_mirror_pending`). Con la conexión inactiva queda `Held`, como hoy.
   *Por qué un evento y no un puerto síncrono:* Quotations no puede ver Shopify (`ShopifyLayerTests` del lado de
   Shopify, regla general de puertos en `IQuotationCustomerLookup.cs:3-11`), y el outbox garantiza que el pedido y la
   petición de envío quedan juntos. Esto **no** escribe el pedido desde Shopify, así que no choca con la regla «una
   corrección de un pedido de Shopify que encola su envío pasa por `ShopifyOrderCorrectionWriter`»; súmalo a
   `.claude/rules/shopify.md` como el tercer origen de envíos (corrección, sinónimos de ciudad, edición del pedido).
2. **No** mover `applied_projection` (misma regla que la corrección del asesor, `.claude/rules/shopify.md`): una edición
   posterior de Shopify sobre la dirección sigue siendo conflicto.
3. **El worker arma direcciones de cualquier país.** Hoy `BuildAsync` sólo sabe Colombia: lee la ciudad DIVIPOLA y el código
   de departamento (`ShopifyOrderPushProcessor.cs:101-125`) y lanza si no hay ciudad de Geography (`:106-109`). La
   mutación ya admite otro país: `MailingAddressInput` con `address1`, `address2`, `city`, `countryCode`, `provinceCode`
   (opcional), `phone`, `firstName`, `lastName`, `company` (`ShopifyOrderUpdateClient.cs:123-149`). Cambios:
   - `ShopifyOrderState.ShippingParty` (el estado del pedido que el adaptador de Bootstrapper le da a Shopify) suma
     `Country`, `CityName` y `RegionId`; hoy `ShopifyPartyDraft` es sólo Colombia (`ShopifyPorts.cs:430-431`).
   - Parte de `CO`: lo de hoy.
   - Otro país: `countryCode` = `Country`; `city` = `CityName`; `provinceCode` = la región traducida a ISO 3166-2 sin
     prefijo (el formato del snapshot, `ShopifyOrderSnapshot.cs:95-96`, V-4) con una tabla por país. Geography guarda el
     código **INE** (`Region.cs:28-29`), así que hace falta `SpainProvinceCodes` (INE → ISO, «28» → «M»), estática en
     Shopify.Application como `ColombiaProvinceCodes`, con prueba de que cubre todas las regiones de `ES` sembradas por `RegionSeeder` (parity test en
     Bootstrapper.UnitTests, que ve Geography). *A confirmar contra la documentación de Shopify el código de cada provincia.*
   - `zip`: lo define el spec de código postal (`2026-11-24-codigo-postal-envio-backend-handoff.md` §5), que va antes que
     esta rama: `PostalCode` de la parte si no está vacío; si no, el `Zip` del último snapshot sólo si país y provincia no
     cambiaron; si no, ninguno. Vale también para Colombia (deja de borrar el código postal que tuviera).
4. **Validación en el guardado (y en el preview), con 422 y `code`, para que el worker nunca falle por un dato que falta.**
   Quotations no ve las tablas de Shopify: declara un puerto `IExternalOrderDeliveryCheck` (adaptador en Bootstrapper,
   que llama a una función pura de Shopify.Application con las mismas reglas que usa `BuildAsync`). Sólo corre si el pedido
   es `Source == Shopify` y la parte de envío propia cambió:
   - `order.delivery.shopify_party_required` — enviar con «la dirección del cliente» (`Delivery.Shipping` null): el
     worker no tendría parte que mandar (`order.ShippingParty` null, `ShopifyOrderPushProcessor.cs:106`).
   - `order.delivery.shopify_region_required` — país con catálogo de regiones (hoy `ES`) sin `regionId`.
   - `order.delivery.shopify_region_unmapped` — la región no está en la tabla del país, o el país tiene catálogo y no tiene
     tabla.
   - `order.delivery.shopify_postal_code_unknown` — país de la lista de los que exigen código postal (propuesta: `ES`; la
     lista vive junto a la tabla), la parte no trae `PostalCode` y no hay `Zip` conservable del snapshot. Con el spec de código
     postal el usuario puede escribirlo.
   - País sin catálogo de regiones: se acepta y se manda sin `provinceCode`; Shopify decide. Si responde un `userError`,
     el envío queda `Failed` como hoy (sólo se loguean los campos, `ThePushNeverLogsTheTokenOrTheUserErrorMessage`).
   Se elimina el `order.delivery.shopify_address_outside_colombia` de la versión anterior de este documento: la limitación
   era del worker, no del negocio.
5. **Recoger ↔ enviar** no se empuja: `orderUpdate` no cambia el método de entrega y no hay grupo para eso. Pasar a
   recoger no encola nada (no hay dirección que mandar). Queda sólo en QEP; el grupo `Shipping` de una edición posterior de
   Shopify será conflicto y gana QEP.

### 11.2 Mayorista — nada que corregir

Verificado: el pedido mayorista nace con las partes del cliente —«el distribuidor no manda direcciones propias en esta
fase» (`IWholesaleOrderGateway.cs:188-189`)—, `WholesaleOrderDraft` no trae entrega (`IWholesaleOrderGateway.cs:13-23`) y
`quotations.order.wholesale-approved.v1` sólo lleva líneas (`QuotationLifecycleEventPublisher.cs:109-115`). La entrega no
se espeja en la orden de compra ni hay evento que la propague. Aprobado no es editable (`Order.IsEditable`,
`Order.cs:368-375`). Avisar al distribuidor queda fuera de esta rama (owner, S-2: no).

### 11.3 Otras partes revisadas

- `RecognizeOrderCarrierRequest` (`OrdersDtos.cs:268-270`): con recogida no hay transportadora; el título externo queda en
  el espejo y la pantalla no ofrece reconocerlo.
- PDF del pedido, envío por WhatsApp, Excel (`IQuotationCustomerLookup.FindManyAsync`, `:49-57`), reportes y Siigo leen
  lo guardado; la entrega sólo cambia antes de facturar.
- `Store`: la entrega del checkout no se espeja; sin corrección.

### 11.4 Pruebas adicionales

- Shopify: cambiar la dirección de un pedido de Shopify por `PUT /orders/{id}` deja en el outbox
  `quotations.order.shipping-party-changed.v1`; el consumidor encola un `ShippingAddress` (o lo fusiona con uno abierto);
  la proyección no se mueve (`AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict` como modelo).
- Shopify: con la conexión inactiva el envío queda `Held`.
- Shopify, España: cambiar a una dirección de Madrid con región guardada encola el envío y el worker manda
  `countryCode: "ES"`, `city` = `cityName`, `provinceCode: "M"` y el `zip` del snapshot (misma provincia); cambiar de
  provincia manda sin `zip` sólo si el país no exige código postal, y si lo exige el `PUT` respondió antes
  `order.delivery.shopify_postal_code_unknown`.
- Shopify: España sin región ⇒ `shopify_region_required`; región fuera de la tabla ⇒ `shopify_region_unmapped`; enviar con
  datos del cliente ⇒ `shopify_party_required`; pasar a recoger no encola nada.
- Shopify, Colombia: el `zip` del snapshot se conserva con el mismo departamento (antes se borraba).
- `SpainProvinceCodes` cubre todas las regiones de `ES` sembradas en Geography (parity test).
- Shopify: el preview aplica las mismas validaciones y no publica nada.
- Mayorista: un pedido «Por aprobar» cambia la entrega; uno aprobado responde `order.order.not_editable`; la aprobación
  posterior publica las líneas sin cambios de contrato.

### 11.5 Resueltos por el owner (2026-11-24)

- **S-3:** `postalCode` opcional en la parte de envío — `2026-11-24-codigo-postal-envio-backend-handoff.md`. Va antes que
  esta rama.
- **S-4:** el importador guarda la parte de envío de otros países —
  `2026-11-24-shopify-parte-de-envio-otros-paises-backend-handoff.md`. Reusa `SpainProvinceCodes` (§11.1) en sentido
  inverso; si se implementa antes que esta rama, crea él la tabla y esta la reutiliza.

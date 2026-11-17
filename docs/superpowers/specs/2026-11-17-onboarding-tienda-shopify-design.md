# Onboarding de la tienda de Shopify (backend)

Estado: borrador para aprobación del owner (2026-11-17). Trabajo 3 de 3 (1 reglas de empresa → 2 productos desde
Shopify → 3 onboarding). Base: §9 del spec `2026-10-21-onboarding-design.md` (flujo, pasos y §9.6), más las decisiones
del owner del 2026-11-16/17. Lo marcado **(criterio)** lo decidió quien escribe con el owner fuera de línea. Mockup:
https://claude.ai/artifact/5XMmGydnUSrL9HGZoMxZZH. El frontend lleva su propio spec en `qep-frontend`.

## 1. Por qué y qué

Un comercio que instala la app desde Shopify, tenga o no cuenta, tiene que dejar su tienda funcionando sin ayuda. QEP
detecta lo que falta y guía; cada paso se marca solo por el estado real, no porque el usuario lo tache.

Decisiones del owner que este spec implementa:
- Sin sesión: «Ya tengo cuenta» o «Crear cuenta». Con sesión: confirmar «¿Conectas {tienda} a esta cuenta?», con
  «Cambiar de cuenta» y el selector de organización (la pantalla `/shopify/link/{installId}` de hoy).
- La empresa que factura se crea **con los datos de la tienda** y el usuario sólo completa el NIT.
- La cuenta de cobro es opcional y el IVA es por empresa (trabajo 1). La pregunta «¿Cobras impuestos?» viene sugerida
  desde Shopify.

**No objetivos:** la página general «Primeros pasos» (`/setup`, módulo `Onboarding`, §1-8 del spec viejo), la app
embebida (S-8), la migración de Lulo v1 (§9.10-9.11), el material de revisión de Shopify (§9.7).

## 2. Los datos de la tienda

- `ShopifyAdminGateway.ShopQuery` pasa de `shop { id name myshopifyDomain }` a traer también `email`, `contactEmail`,
  `currencyCode`, `ianaTimezone`, `taxesIncluded` y `billingAddress { company address1 address2 city province
  provinceCode zip countryCodeV2 phone }`.
- Se guarda como **perfil de la tienda** en la conexión (campos de sólo lectura, como `shopName`) y se refresca con el
  probador de la conexión. Lo leen la sugerencia de empresa (§4.1) y el BFF.
- Nada de esto es secreto, pero el correo y el teléfono son datos personales del comerciante: no van a logs.

## 3. Elegir la organización (pantalla de enlace)

`GET /api/v1/integrations/shopify/installs/{installId}` ya trae cada tenant con `canLink` y `moduleEnabled`
(`GetShopifyInstall.cs`). Se agrega por tenant `connectedShops: [{ shopName, shopDomain }]`, las tiendas Shopify ya
conectadas a ese tenant, para que la pantalla diga «ya tiene 1 tienda conectada» y nadie cruce tiendas por error
(owner). La sesión y su correo los pinta el frontend con `/auth/me`. `shop_already_linked` sigue igual.

Registro desde Shopify: el frontend lleva `returnTo=/shopify/link/{installId}` y el nombre sugerido. El nombre sale de
la respuesta de `install/complete` (anónima), que gana `shopName` **(criterio:** es el dato que el comercio acaba de
autorizar; el perfil completo sólo después de iniciar sesión).

## 4. El asistente

### 4.1 `GET …/shopify/stores/{connectionId}/setup` (`integrations.connection.manage`)

Una llamada con todo lo que la pantalla dibuja (BFF). Cada paso trae `status` (`Done` | `Pending` | `Blocked`),
`blockedBy` y `missing` (lista de códigos de lo que falta, para que la pantalla explique sin deducir).

| Paso | `Done` cuando | `missing` |
|---|---|---|
| `company` | la tienda tiene empresa activa con NIT (`TaxId`) | `company_missing`, `tax_id_missing` |
| `advisor` | la tienda tiene asesor y su membresía está activa | `advisor_missing`, `advisor_inactive` |
| `buyers` | la tienda tiene clasificación por defecto y fuente del documento | `classification_missing`, `document_source_missing` |
| `products` | importación `Done`, o la cobertura aceptada, o el catálogo de la tienda 100 % vinculado | `import_not_done`, `import_failed`, `scope_missing` |
| `history` | se corrió una sincronización desde el asistente | `history_not_run` |

- `history` está `Blocked` con `blockedBy: ["products"]` mientras haya una importación viva.
  Además, mientras la tienda no esté configurada, está `Blocked` por `company` y `advisor`, los que falten (revisión
  2026-11-17): sin empresa ni asesor la sincronización no arma pedidos.
- El paso abierto por defecto es el primero que no está `Done` **(criterio:** retomar donde quedó, también días después).

Secciones del cuerpo:
- `store`: nombre, dominio, moneda, `version` de la tienda.
- `company`: la empresa elegida (id, nombre, NIT, `chargesVat`, si tiene cuenta) o `null`; `suggested` con razón social
  (`billingAddress.company` o el nombre de la tienda), dirección, ciudad resuelta en Geography (`cityId` o `null` con el
  texto), teléfono, correo, país; y `suggestedChargesVat` (§4.3). `companies`: las empresas activas del tenant para
  elegir una existente.
- `advisor`: opciones (membresías activas con `isMe`) y el elegido; por defecto quien configura.
- `buyers`: fuentes del documento (las del endpoint de opciones de hoy), tipo por defecto, clasificaciones y las
  elegidas.
- `products`: lo que compone el trabajo 2 (`scopeGranted`, `reauthorizeUrl`, la última importación, la cobertura si ya se
  pidió).
- `history`: la última corrida desde el asistente (`days`, `fetched`, `imported`, `waiting: [{ reason, count }]`).
- `waiting`: los pedidos en espera de la tienda agrupados por motivo, con muestra (hasta 5 SKU para `product_unmatched`).
  Es lo que la cabecera «Conectada y funcionando» muestra como «Lo que te falta».

### 4.2 Guardar cada paso
- **Empresa:** `POST …/setup/company` con `{ companyId? , tax_id, documentType, name, address, cityId, phone, email,
  chargesVat }`. Sin `companyId` crea la empresa (por el mismo caso de uso de Companies, trabajo 1: siembra de tasas
  incluida) y la asigna a la tienda; con `companyId` sólo la asigna. Una transacción por módulo: si la empresa se crea y
  la asignación falla por versión, la respuesta trae la empresa creada para que el reintento la use **(criterio:** no se
  puede deshacer una creación en otro módulo; el reintento idempotente es por `companyId`).
- **Asesor y compradores:** el `PUT …/shopify/stores/{id}` de hoy (`If-Match`), sin cambios.
- **Productos:** los endpoints del trabajo 2, más `POST …/products/coverage/accept` (204), que guarda
  `products_coverage_accepted_at` en la tienda: «Seguir así».
- **Historial:** el `POST …/sync { historyDays }` de hoy, con `source: "setup"` para que la tienda guarde
  `setup_history_ran_at` y el resumen de esa corrida.

### 4.3 Sugerir «¿Cobras impuestos?»
`suggestedChargesVat`: con `read_products`, `true` si alguna variante de la tienda es `taxable`
(`productVariants(first: 1, query: "taxable:true")`), `false` si ninguna; sin el scope o sin productos, `null` y la
pantalla no preselecciona **(criterio:** no adivinar sin datos).

## 5. Estado y errores
- Columnas nuevas en `shopify.stores`: `products_coverage_accepted_at`, `setup_history_ran_at`,
  `setup_history_summary` (jsonb). Aditivas.
- Errores nuevos: ninguno fuera de los del trabajo 2; `POST …/setup/company` usa los de Companies.

## 6. Verificación
- Unitarias: cálculo de cada paso y de `missing` en todas sus combinaciones, el primero abierto, `Blocked`.
- Integración: `GET …/setup` de una tienda recién enlazada (todo `Pending`, sugerencia de empresa con ciudad resuelta),
  `POST …/setup/company` crea y asigna, `connectedShops` en el install, `coverage/accept`, historial desde el asistente.
- Manual contra una tienda de desarrollo (§9.9 del spec viejo), con el frontend.
- Las pruebas se escriben; se corren todas al final.

## 7. Ajustes por el spec del frontend (criterio)

El spec del frontend (`qep-frontend`, `2026-11-17-onboarding-tienda-shopify-frontend-design.md` §12) encontró huecos del
contrato. Se resuelven así:
- **H3:** `POST …/setup/company` acepta `bankAccount?: { bankName, accountNumber, currency }`, opcional, que se guarda en
  la empresa y en la tienda.
- **H5:** `advisor` y `buyers` quedan `Blocked` con `blockedBy: ["company"]` mientras la tienda no tenga empresa (el `PUT`
  de la tienda la exige).
- **H7:** la cobertura se guarda en la tienda (`products_coverage` jsonb con su hora) cada vez que se pide
  `GET …/products/coverage`; el BFF devuelve la última, y la pide sola la primera vez que se abre el paso con el scope
  concedido y sin importación.
- **Cuerpo de la tienda:** `store` trae la configuración completa que el `PUT` necesita, con su `version`.
- **Estadísticas de la cabecera:** `stats: { importedLast7Days, productsSynced, lastSyncAt }`.
- **Sincronización:** `POST …/sync` conserva su forma de hoy (días de historial) y gana `source=setup` como parámetro de
  query; el permiso es el de hoy.
- **Empresa creada y asignación fallida:** `201 { companyId, storeVersion }`; si la asignación da 412, la respuesta de
  error trae `companyId` en `details` y el reintento lo manda.
- **Volver de Shopify tras conceder `read_products`:** pasa por la pantalla de enlace; como la tienda ya está en ese tenant
  (`connectedShops`), la pantalla ofrece «Ir a la configuración». El enlace de hoy reutiliza la conexión del mismo tenant.

## 8. Lo que queda a quien construye
- Dónde vive el ensamblado del BFF (Shopify.Application con puertos a Integrations, Companies y Geography, adaptadores en
  Bootstrapper, como el resto del módulo).
- La forma exacta de guardar el perfil de la tienda (campos de la conexión o columna jsonb).

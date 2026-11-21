# Integración con Coordinadora — diseño

Fecha: 2026-11-21. Repo: `qep-backend`. El frontend lleva su propio spec (pendiente, ver §10).

## 1. Intención

Hoy la guía de Coordinadora de un pedido se genera **por fuera de QEP**: el Excel de pedidos lleva la columna
`coordinadora_city` («ABEJORRAL (ANT)») justo para que el ERP del tenant la importe y genere la guía allá (README § Nombres
de ciudad de Coordinadora). El flete se escribe a mano en la cotización (spec 2026-10-17, decisión 8), y el spec del flete dejó
fuera, para un spec propio, «cotizar el envío en vivo con la transportadora (Coordinadora primero), guías, seguimiento y
conciliación del recaudo» (§12).

El objetivo es que un tenant **cotice, genere, imprima, siga y anule la guía de Coordinadora desde el pedido**, con la
configuración hecha una vez en QEP, sin Excel ni ERP intermedio. Es el mismo movimiento que hizo Siigo con la factura
(spec 2026-11-18): un módulo propio, la conexión y el secreto en Integrations, Quotations expone un puerto de lectura.

Este spec se escribió **sin una sesión con el owner** (pedido: «implementar nueva integración con Coordinadora»). Por eso
todas las decisiones van marcadas **[decisión del diseño]** y las dudas que cambian el alcance quedan en §9 como pendientes,
no asumidas. Lo que el código hace hoy es la autoridad; este documento se corrige si difieren.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**

1. Conectar la cuenta de Coordinadora del tenant en Integrations (categoría `Shipping`, ya existía vacía).
2. Configurar por conexión el **remitente** (origen del despacho), la cuenta y el producto de Coordinadora, y las
   medidas por defecto del paquete (el catálogo no tiene peso ni medidas: §3).
3. Desde un pedido: **cotizar** el flete, **generar** la guía, **imprimir** el rótulo, **consultar** el estado y
   **anular** la guía.
4. Decir antes de enviar todo lo que falta (destinatario sin teléfono, ciudad que Coordinadora no atiende, remitente
   incompleto) en una sola respuesta.

**No objetivos (esta entrega)**

- Peso y medidas por producto en Catalog: la guía sale con las **medidas por defecto de la conexión** (§5.2) y la
  pantalla puede corregirlas por envío (§5.4). Cuando Catalog las tenga, el armador las toma de ahí.
- Recogidas (`Recogidas_programar`), despachos agrupados (`Guias_generarDespacho`) y conciliación del recaudo
  (`Recaudos_consultar`).
- Escribir el flete cotizado en la cotización o el pedido: la cotización se **muestra**; quien edita el pedido decide si
  la copia al flete (§9, P-3).
- Seguimiento automático con worker o webhooks: el estado se consulta a pedido de la pantalla (§5.6).
- Guardar la guía en `quotations.orders` (una columna `tracking_number`) o sacarla en el Excel de pedidos (§9, P-2).
- Reemplazar `coordinadora_city` del Excel: sigue igual mientras el tenant migre.

**Anti-objetivos (fallaría aunque funcione)**

- **Dos guías vivas para el mismo pedido.** Una guía es un servicio que Coordinadora cobra. Ningún doble clic ni
  reintento puede generar dos; si no se sabe si se generó, lo resuelve una persona (§5.5).
- Una guía con destinatario incompleto o con una ciudad que Coordinadora no reconoce: se frena antes de llamar.
- Una credencial de Coordinadora (apikey, claves) en logs, auditoría, `request_json` o respuestas.

## 3. Restricciones (verificadas)

**API de Coordinadora.** El portal `developers.coordinadora.com` (REST, OAuth2 client credentials, `POST /cotizador/nacional`,
`GET /digitales/{codigo_remision}`) **no se pudo leer** desde esta sesión (no resuelve) y sus esquemas no están publicados en
ningún sitio alcanzable. Lo que sí está verificado, y es el contrato que este diseño usa:

- **El repo ya habla con el AGS 1.5 de Coordinadora por JSON-RPC 2.0** (README § Nombres de ciudad de Coordinadora):
  `POST https://ws.coordinadora.com/ags/1.5/server.php` con `{"jsonrpc":"2.0","method":"Cotizador.ciudades","params":{},"id":1}`
  devuelve `result[]` con `codigo` (8 dígitos: DIVIPOLA + `000` para el municipio), `nombre` y `estado`. Es el camino que
  regenera `coordinadora-cities.json`.
- **El contrato de las operaciones** (wrapper público `saulmoralespa/coordinadora-webservice-php`, leído el 2026-11-21, y el
  conector de Odoo `carrier_co_servientrega_coordinadora`, que llama `Guias_generarGuia`, guarda el número de guía y adjunta
  el PDF que vuelve). Dos servidores, dos credenciales:
  - **AGS** (`ws.coordinadora.com/ags/1.5/server.php`; sandbox `sandbox.coordinadora.com/ags/1.5/server.php`): `Cotizador_cotizar`,
    `Cotizador_ciudades`, `Cotizador_departamentos`, `Recaudos_consultar`, `Recogidas_*`. Autenticación **dentro de `p`**:
    `apikey`, `clave` y, en cotizar, `nit`. `Cotizador_cotizar` recibe `div` (opcional, acuerdo mercantil), `cuenta`,
    `producto`, `origen` y `destino` (códigos de 8 dígitos), `valoracion`, `nivel_servicio` (lista) y `detalle.item[]` con
    `ubl` (0 automática, 1 mercancía), `alto`, `ancho`, `largo` (cm), `peso` (kg) y `unidades`.
  - **Guías** (`guias.coordinadora.com/ws/guias/1.6/server.php`; sandbox `sandbox.coordinadora.com/agw/ws/guias/1.6/server.php`),
    SOAP 1.2 con WSDL: `Guias_generarGuia`, `Guias_editarGuia`, `Guias_anularGuia`, `Guias_rastreoSimple`,
    `Guias_imprimirRotulos`, `Guias_generarDespacho`. Autenticación **dentro de `p`**: `id_cliente`, `usuario` y `clave` =
    **SHA-256 en hexadecimal de la contraseña**. `Guias_generarGuia` recibe: `codigo_remision` (vacío al crear), `fecha`,
    `id_remitente`, `nit_remitente`, `nombre_remitente`, `direccion_remitente`, `telefono_remitente`, `ciudad_remitente`;
    `nit_destinatario`, `div_destinatario`, `nombre_destinatario`, `direccion_destinatario`, `ciudad_destinatario`,
    `telefono_destinatario`; `valor_declarado`, `codigo_cuenta`, `codigo_producto`, `nivel_servicio`, `linea`, `contenido`,
    `referencia`, `observaciones`, `estado` (`IMPRESO`), `detalle[]` (los ítems de arriba más `referencia` y
    `nombre_empaque`), `cuenta_contable`, `centro_costos`, `recaudos`, `margen_izquierdo`, `margen_superior`, `id_rotulo`,
    `usuario_vmi`, `formato_impresion`, `atributo1_nombre/valor`, `notificaciones`, `atributos_retorno`, `nro_doc_radicados`,
    `nro_sobre`. Devuelve `codigo_remision` (la guía, 11 dígitos) y, según el conector de Odoo, el PDF de la guía.
- **Lo que no se sabe** y queda en §9 como verificación con la primera llamada real: el espacio de nombres SOAP del servidor
  de guías (el WSDL no se pudo descargar), la forma exacta de la respuesta de `Guias_generarGuia` (nombre del campo del PDF),
  de `Guias_imprimirRotulos` y de `Guias_rastreoSimple`, el tipo de `recaudos`, y si el AGS acepta `Cotizador.cotizar` por
  JSON-RPC con `params: {"p": {...}}` como acepta `Cotizador.ciudades`. El cliente HTTP se diseñó para que **cada una de esas
  dudas sea una constante o una opción** (`CoordinadoraOptions`), no una reescritura.
- **No hay cuenta de pruebas en QEP**: el sandbox existe en Coordinadora pero sus credenciales las da Coordinadora al
  cliente. Las pruebas automáticas usan un doble (§11); lo que no se pueda saber de antemano se descubre con las primeras
  guías reales.

**QEP:**

- `Order` lleva `IsStorePickup`, `CarrierId`/`CarrierName`, `CashOnDelivery`, `FreightAmount`, `NetTotal`, `Total`,
  `RequiresShipping` (alguna línea `Physical`), la parte de envío (`Order.Shipping`: nombre, teléfono, dirección, `CityId`,
  `Country`, `CityName`) y, cuando no hay parte de envío, la ficha del cliente (`IQuotationCustomerLookup`), con el mismo
  criterio de `ExternalOrderInvoicingService`. **No tiene** peso, medidas ni número de guía.
- `City` lleva `DivipolaCode` (5 dígitos) y `CoordinadoraName` (null si Coordinadora no lo lista o lo lista inactivo). El
  código de Coordinadora de un municipio es `DivipolaCode + "000"` (README § Nombres de ciudad de Coordinadora).
- `IntegrationCategory.Shipping` existe desde el spec 2026-10-08 sin proveedor. Un proveedor existe sólo con módulo
  consumidor (decisión 4 de ese spec).
- Los módulos no se ven entre sí: la integración entra por puertos y adaptadores en Bootstrapper, como Siigo
  (`SiigoLayerTests`).

## 4. Enfoques

| | Enfoque | A favor | En contra |
|---|---|---|---|
| **A** | **Módulo `Coordinadora` propio** (esquema `coordinadora`); la conexión y los secretos en Integrations; Quotations expone un puerto de lectura | El patrón de Siigo, probado dos veces (puertos, `*LayerTests`, adaptadores) | Un módulo más con su migración |
| B | Dentro de Quotations (la guía es «parte del envío») | Menos piezas; la guía al lado del flete | Mete un cliente SOAP y credenciales en el módulo más grande del repo; Quotations no debe conocer transportadoras concretas |
| C | Módulo genérico `Shipping` con Coordinadora de primer proveedor | Listo para Servientrega o Interrapidísimo | Abstracción con un solo caso real; el contrato de cada transportadora es distinto |

**Elegido: A [decisión del diseño]**, por paridad con Siigo. Si llega una segunda transportadora, se extrae lo común entonces.

## 5. Diseño

### 5.1 Piezas

- **Tenancy:** clave de módulo `coordinadora`, depende de `orders`, **fuera** de `DefaultForNewTenants` (se prende por
  tenant, como `siigo`) **[decisión del diseño]**. Migración que ensancha el `CHECK` de `tenant_modules`.
- **Integrations:** proveedor `coordinadora`, categoría `Shipping`, consumidor `TenantModuleKeys.Coordinadora`,
  `ProviderOnboarding.Form`, **tope 1 por tenant** **[decisión del diseño]**: una cuenta de Coordinadora por tenant, así el
  módulo toma «la conexión del tenant» sin que el pedido tenga que elegir cuál (Siigo elige por la empresa que factura; una guía
  no tiene esa ancla). Campos: `nit` (texto, requerido), `apiKey` (secreto, requerido:
  apikey del AGS), `apiPassword` (secreto, requerido: `clave` del AGS), `clientId` (texto, requerido: `id_cliente` de guías),
  `guidesUser` (texto, requerido: `usuario` de guías), `guidesPassword` (secreto, requerido: la contraseña de guías; el
  cliente manda su SHA-256). Migración que ensancha el `CHECK` de `connections.provider_key`.
  **Probador** (`CoordinadoraConnectionTester`): valida la forma de los campos y comprueba que el AGS responde
  (`Cotizador.departamentos`, que no lleva credenciales) **[decisión del diseño]**: ninguna operación de lectura barata
  verifica las dos credenciales sin cotizar o generar algo con una cuenta y un producto que viven en la configuración del
  módulo, no en la conexión. El resultado se documenta en la pantalla: «Probar» verifica alcance; las credenciales se
  confirman con la primera cotización (apikey/clave) y la primera guía (usuario/clave de guías).
- **Módulo `Coordinadora`** (`src/Modules/Coordinadora/`, cuatro capas, `CoordinadoraLayerTests`): configuración por conexión,
  guías emitidas, cliente del AGS y de guías, endpoints. Application ve sólo Tenancy y Audit; Integrations, Quotations,
  Customers y Geography entran por puertos (`CoordinadoraPorts.cs`) con adaptadores en `src/Bootstrapper/Coordinadora*.cs`.
- **Quotations:** un puerto de **lectura** nuevo, `IExternalOrderShipping` (§5.7). Sin columnas nuevas ni migración.

### 5.2 Configuración por conexión (esquema `coordinadora`)

`coordinadora.settings` — PK `connection_id`; `tenant_id`; `version` (concurrencia optimista, `If-Match`). Nace vacía al
primer acceso (`INSERT … ON CONFLICT DO NOTHING`, como Siigo).

| Columna | Qué es (campo de Coordinadora) | Requerido para generar |
|---|---|---|
| `sender_name` | `nombre_remitente` | Sí |
| `sender_address` | `direccion_remitente` | Sí |
| `sender_phone` | `telefono_remitente` | Sí |
| `sender_city_id` | Ciudad de origen (Geography); viaja como `ciudad_remitente` = DIVIPOLA + `000` | Sí, y con nombre de Coordinadora |
| `account_code` | `codigo_cuenta` / `cuenta` | Sí |
| `product_code` | `codigo_producto` / `producto` | Sí |
| `service_level` | `nivel_servicio` (entero) | Sí |
| `div` | Acuerdo mercantil; vacío si no hay | No |
| `default_height_cm`, `default_width_cm`, `default_length_cm` | Medidas del paquete por defecto (`alto`, `ancho`, `largo`), enteros en cm | Sí (hasta que Catalog tenga medidas) |
| `default_weight_kg` | `peso` por defecto, decimal con 2 decimales | Sí |
| `packaging_type` | `ubl`: `0` automática, `1` mercancía. Default `0` | — |
| `declared_value_minimum` | Piso del `valor_declarado` en pesos. Default `0` (el valor declarado es el total con IVA de las líneas del pedido, y nunca menos que este piso) | — |
| `collect_on_delivery` | Pedir **recaudo** a Coordinadora cuando el pedido es contraentrega: `recaudos` = `NetTotal`. **Default `false`** [decisión del diseño]: un recaudo es una obligación financiera y no hay cuenta de pruebas; lo prende quien configura | — |
| `label_format` | `formato_impresion`, opcional, texto | No |

El remitente va en la configuración y no en la empresa de QEP **[decisión del diseño]**: la empresa que factura y la bodega
que despacha no son necesariamente la misma, y la cuenta de Coordinadora es una por tenant.

`GET …/coordinadora/connections/{id}/settings` devuelve la configuración entera más `readiness` (`complete` | códigos que
faltan), para que la pantalla marque el paso. `PUT …/settings` la reemplaza entera, con `If-Match` (428 sin él, 412 vieja).

### 5.3 Qué se manda por pedido (el armador)

`CoordinadoraGuideBuilder.Build(input, settings, overrides)` junta **todos** los faltantes antes de armar:

- Pedido: no anulado (`coordinadora.shipment.order_cancelled`, 422), no recogida en tienda (`store_pickup`), con algo que
  enviar (`nothing_to_ship`: ninguna línea física), transportadora del pedido vacía o Coordinadora
  (`carrier_mismatch`: el nombre o un nombre externo de la transportadora contiene «coordinadora», normalizado) **[decisión
  del diseño]**.
- Destinatario: la parte de envío del pedido y, si no hay, la ficha del cliente (mismo criterio que la factura). Faltantes
  `recipient_name`, `recipient_phone`, `recipient_address`, `recipient_city` (sin ciudad, o fuera de Colombia: Coordinadora
  nacional) y `recipient_city_not_served` (ciudad sin `CoordinadoraName`).
- Remitente y cuenta: `sender_incomplete` (algún campo de §5.2 requerido vacío), `sender_city_not_served`,
  `package_defaults_missing`.
- Conexión: `connection_missing` (el tenant no tiene conexión de Coordinadora) o no operativa (409
  `coordinadora.connection_unavailable`, con el motivo).

Campos de la guía: `fecha` = hoy en la zona del tenant; `id_remitente` = `0`, `nit_remitente` = el `nit` de la conexión;
`nit_destinatario` = la identificación de la parte de facturación o del cliente si la hay, `0` si no; `div_destinatario` =
`0`; `valor_declarado` = `max(ProductsTotal, declared_value_minimum)` redondeado a pesos; `codigo_cuenta`, `codigo_producto`,
`nivel_servicio`, `linea` = vacío; `contenido` = las líneas como «3 x Aceite esencial, 1 x Vela» recortado a 200
caracteres; `referencia` = `OrderNumber`; `observaciones` = `Order.Notes` recortado a 500; `estado` = `IMPRESO`; `detalle`
= **un ítem** con las medidas por defecto (o las de `overrides`), `unidades` = 1, `referencia` = `OrderNumber`,
`nombre_empaque` = `Paquete`; `recaudos` = `NetTotal` sólo con `CashOnDelivery` y `collect_on_delivery`; el resto vacío.
La cotización (`Cotizador.cotizar`) usa los mismos datos: `origen`, `destino`, `valoracion` = `valor_declarado`,
`detalle.item` = el mismo paquete.

`overrides` (cuerpo opcional del `POST` y del `quote`): `package: { heightCm, widthCm, lengthCm, weightKg, units }` y
`declaredValue`, para que la persona corrija el paquete de este envío sin tocar la configuración. Se validan con
FluentValidation (enteros positivos, peso > 0, hasta 2 decimales).

### 5.4 Generar la guía

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/coordinadora-shipment`. Permiso `coordinadora.shipment.manage` más la
revalidación en el handler (doble capa). Responde la guía (§5.5).

1. **Candado por pedido**: `pg_advisory_xact_lock(20261121, hashtext(orderId))`. Con la fila vigente del pedido:
   - `Created` → se devuelve tal cual, sin llamar.
   - `Pending` (otra petición la está mandando: reclamo de `SendLease` = 2 min) → se devuelve tal cual.
   - `Unknown` → se devuelve tal cual: la resuelve una persona (§5.5). **No se reenvía**: Coordinadora no tiene clave de
     idempotencia y reenviar puede generar dos guías **[decisión del diseño]**.
   - `Rejected`, `Failed`, `Cancelled` o ninguna → se arma una fila nueva.
2. **Validación completa** (§5.3): `422 coordinadora.shipment.not_ready` con `details.missing`. Nada sale a Coordinadora.
3. **Armar y guardar** la fila `Pending` con `request_json` (la petición entera: lleva datos del destinatario; nunca sale en
   un `GET`) y la versión del pedido. **Commit.**
4. **Llamar** `Guias_generarGuia`, fuera de la transacción y sin reintentos.
5. **Resultado:** 2xx con `codigo_remision` → `Created` con `guide_number` y, si la respuesta trae el PDF, `label_pdf`.
   Error de validación de Coordinadora → `Rejected` con `error_code` y `error_detail` (recortado; el mensaje es de
   Coordinadora y puede repetir datos del destinatario, así que la fila guarda los **200 primeros caracteres** y la pantalla
   lo muestra sólo a quien tiene el permiso). Timeout, 5xx o red → **`Unknown`**: no se sabe si se generó.

### 5.5 Estado y pantalla

`coordinadora.shipments`: `id`, `tenant_id`, `connection_id`, `order_id`, `status` (`Pending` | `Created` | `Rejected` |
`Unknown` | `Failed` | `Cancelled`), `request_json`, `guide_number`, `declared_value`, `label_pdf` (bytea, nullable),
`tracking_status`, `tracking_detail`, `tracked_at`, `error_code`, `error_detail`, `attempts`, `order_version`,
`sending_since`, `created_by`, `created_at`, `updated_at`, `cancelled_at`, `cancelled_by`. Índice único parcial
`(tenant_id, order_id) WHERE status IN ('Pending','Created','Unknown')`, traducido por nombre a `409
coordinadora.shipment.already_exists`: el candado da la respuesta amable, el índice cierra la carrera.

- `GET …/coordinadora-shipment` → la fila vigente del pedido (o la última si todas están cerradas), o 404
  `coordinadora.shipment.not_found`. Sin `request_json` ni `label_pdf`. De paso aplica la transición perezosa: una `Pending`
  cuyo reclamo de 2 min venció pasa a `Unknown` (la petición murió sin respuesta) y se guarda.
- `GET …/coordinadora-shipment/preview` → la petición armada (sin credenciales), los faltantes y el paquete que se usaría.
- `POST …/coordinadora-shipment/quote` → `Cotizador.cotizar` con los mismos datos; responde los valores que Coordinadora
  devuelve (`flete_fijo`, `flete_variable`, `flete_total`, `otros`, `total` cuando vienen) **y el resultado crudo**, porque la
  forma exacta no está documentada (§3). No escribe nada.
- `GET …/coordinadora-shipment/label` → el PDF del rótulo: el guardado al crear si lo hay; si no, `Guias_imprimirRotulos`
  en vivo (nunca se cachea). Sólo `Created`.
- `POST …/coordinadora-shipment/track` → `Guias_rastreoSimple`; guarda `tracking_status`, `tracking_detail` y `tracked_at`
  y devuelve la fila. Sólo `Created`.
- `POST …/coordinadora-shipment/cancel` → `Guias_anularGuia`; la fila pasa a `Cancelled` y el pedido queda libre para otra
  guía. Sólo `Created`. Un rechazo de Coordinadora al anular es 422 `coordinadora.shipment.cancel_rejected` con el detalle.
- `POST …/coordinadora-shipment/resolve` con `{ "outcome": "exists", "guideNumber": "…" }` o `{ "outcome": "missing" }`, sólo
  desde `Unknown`: la persona miró en Coordinadora. `exists` pasa a `Created` con ese número; `missing` pasa a `Failed` y
  libera el pedido.

### 5.6 Permisos y auditoría

- `coordinadora.settings.manage` (configurar la conexión) y `coordinadora.shipment.manage` (cotizar, generar, imprimir,
  seguir, anular, resolver), con sus `PermissionDefinition` (`RequiredModules: [coordinadora]`) y sus políticas en
  `AddAuthorization`. De fábrica: `admin` los dos y `advisor` el de guías **[decisión del diseño]**, como
  `quotations.order.manage`, que es quien prepara el pedido.
- Auditoría atómica con puerto propio (`ICoordinadoraAuditRecorder`): `coordinadora.settings.updated`,
  `coordinadora.shipment.created`, `coordinadora.shipment.rejected`, `coordinadora.shipment.cancelled`,
  `coordinadora.shipment.resolved`. Nunca llevan la petición, el destinatario ni las claves.

### 5.7 Cambios en Quotations

Puerto `IExternalOrderShipping` (Quotations.Application), implementado por `ExternalOrderShippingService` y expuesto a
Coordinadora por el adaptador `CoordinadoraOrderSource` (Bootstrapper):

- `GetForShippingAsync(tenantId, orderId)` → `ExternalShippingOrder`: id, número, estado (`Cancelled`), `IsStorePickup`,
  `RequiresShipping`, `CarrierName`, `CashOnDelivery`, `ProductsTotal`, `NetTotal`, `Notes`, `Version`, las líneas físicas
  (nombre, código, cantidad) y el **destinatario** resuelto (parte de envío o ficha del cliente: nombre, teléfono, dirección,
  `CityId`, `Country`, `CityName`) más la identificación de la parte de facturación o del cliente.
- `GetVersionAsync(tenantId, orderId)`.

Sin escrituras: el pedido no cambia por tener guía (§2). Es sólo lectura, así que no toca `Order` ni necesita migración.

## 6. Seguridad

- `apiKey`, `apiPassword` y `guidesPassword` nunca salen a logs, auditoría, `request_json` ni respuestas. `request_json`
  guarda la petición **sin** el bloque de credenciales (se agregan al mandar). Cliente HTTP nombrado `coordinadora` con
  `RemoveAllLoggers()` y sin redirecciones; el del probador, `integrations.coordinadora`, igual. `CompositionRootTests` lo amarra.
- `request_json`, `error_detail` y `label_pdf` llevan datos del destinatario: sólo los leen los handlers del módulo; el
  `GET` de la guía no los devuelve y el rótulo sale sólo con `coordinadora.shipment.manage`.
- Las credenciales van en una clase con `ToString` que no las escribe (`CoordinadoraCredentials`), como `SiigoCredentials`.

## 7. Datos y migraciones

- `coordinadora.settings` y `coordinadora.shipments`: migración `InitialCoordinadora`, aditiva (esquema nuevo).
- Tenancy `AddCoordinadoraModuleKey` e Integrations `AddCoordinadoraProvider`: sólo ensanchan un `CHECK`; su `Down` borra las
  filas del módulo y del proveedor, como las de Siigo.
- `coordinadora.shipments` no se purga: es el registro de un servicio que Coordinadora facturó.

## 8. Configuración

Sección `Coordinadora` (`CoordinadoraOptions`, validada al arrancar): `AgsUrl` (default
`https://ws.coordinadora.com/ags/1.5/server.php`), `GuidesUrl` (default `https://guias.coordinadora.com/ws/guias/1.6/server.php`),
`GuidesNamespace` (el `xmlns` del cuerpo SOAP; vacío = `GuidesUrl`, que es lo que hace el cliente PHP sin WSDL). Las tres son
absolutas y HTTPS. El sandbox se usa cambiando las URL en el ambiente, no por conexión. Ninguna es secreta: van en
`appsettings.example.json` y en el ConfigMap.

## 9. Pendientes y verificaciones

**Decisiones pendientes del owner** (asumidas como dice este spec hasta que decida):

- **P-1** ¿`coordinadora` fuera de `DefaultForNewTenants`? (asumido: sí, se prende por tenant).
- **P-2** ¿La guía se escribe en el pedido (`tracking_number`) y sale en el Excel de pedidos? (asumido: no en esta entrega).
- **P-3** ¿El flete cotizado reemplaza el flete manual del pedido? (asumido: sólo se muestra).
- **P-4** ¿`advisor` genera guías de fábrica? (asumido: sí, como prepara pedidos).
- **P-5** ¿El recaudo contraentrega nace apagado? (asumido: sí).

**Verificaciones con la primera llamada real** (el código tiene cada una en un solo lugar):

- **V-1** El `xmlns` del cuerpo SOAP de guías (`Coordinadora:GuidesNamespace`) y que el servidor acepte SOAP 1.2
  (`application/soap+xml`). Dónde: `CoordinadoraSoap`.
- **V-2** El nombre del campo del PDF en la respuesta de `Guias_generarGuia` y de `Guias_imprimirRotulos` (el cliente busca
  cualquier elemento cuyo texto sea base64 de un PDF: `%PDF-`). Dónde: `CoordinadoraGuidesResponse`.
- **V-3** El tipo de `recaudos` (se manda el valor como texto). Dónde: `CoordinadoraGuideBuilder`.
- **V-4** Que el AGS acepte `Cotizador.cotizar` por JSON-RPC con `params: {"p": {...}}` y la forma de su `result`.
  Dónde: `CoordinadoraApiClient.QuoteAsync` y `CoordinadoraQuoteDto`.
- **V-5** Los campos de `Guias_rastreoSimple` (se guardan `estado`/`descripcion` si vienen, y el texto del resultado si no).

## 10. Frontend

Spec propio en `qep-frontend`: pantalla de configuración de la conexión (remitente, cuenta, paquete por defecto), tarjeta
«Envío con Coordinadora» en el detalle del pedido (cotizar, generar, rótulo, seguimiento, anular, resolver), y la tarjeta
del proveedor en Integraciones.

## 11. Pruebas

- **Unitarias** (`Modules.Coordinadora.UnitTests`): agregados (`CoordinadoraSettings`, `CoordinadoraShipment`), el armador
  (faltantes, valor declarado, recaudo, recorte de textos), el cliente (JSON-RPC y SOAP contra un handler falso: credenciales
  en `p`, SHA-256 de la clave, clasificación de respuestas, el PDF en base64), el mapeo de EF contra el snapshot, las
  opciones.
- **Arquitectura**: `CoordinadoraLayerTests`, el conteo de permisos, los dos clientes HTTP sin loggers, el proveedor en el
  catálogo y la clave de módulo en Tenancy.
- **Integración**: **no se escribieron en esta sesión.** El contenedor no tiene Docker, así que Testcontainers no arranca y
  una prueba que no se puede correr no deja evidencia. Quedan como pendiente: `Modules.Coordinadora.IntegrationTests` con un
  `FakeCoordinadoraApi` como handler primario del cliente `coordinadora` (configuración, generación con candado, 403 por tenant,
  índice único de la guía viva), más las de Tenancy e Integrations que enumeran módulos y proveedores (ya actualizadas, sin correr).

**Lo que no corrió en esta sesión** (2026-11-21): ninguna llamada a Coordinadora (ni sandbox), las migraciones contra una base,
el arranque del host (`ValidateOnStart`, `MigrateAsync`) y toda prueba de integración. Corrieron el build del host, las unitarias
de Coordinadora, Integrations, Tenancy, Quotations y Bootstrapper, y `ArchitectureTests`.

# Integración con Siigo — diseño

Fecha: 2026-11-18. Repo: `qep-backend`. El frontend lleva su propio spec (pendiente, ver §10).

## 1. Intención

Hoy QEP no factura: un pedido se marca «facturado» a mano (`InvoiceOrderHandler`, sólo una marca) y la factura real se
hace por fuera. En Ritual el puente es el Excel de pedidos (`OrdersExportProcessor`, hoja `MIGRACION 1`), que importa
`ritual-botanico-backend` y factura en Siigo emparejando la forma de pago por texto literal de banco y cuenta. Ese acople
no se ve desde QEP y se rompe en silencio.

El objetivo es que un tenant facture en Siigo **desde el pedido**, sin Excel ni repo intermedio, con la configuración
hecha una vez en QEP.

Decisiones del owner (conversación del 2026-11-18):

- Un tenant puede conectar **una o más cuentas de Siigo**; cada una se vincula a **una empresa** de QEP.
- Desde un pedido se genera la factura en Siigo. Si la genera el sistema, **la marca de facturado es automática**.
- Hay un **asistente de configuración** por conexión: vincular productos de Siigo con productos de QEP y configurar el
  documento. Cada conexión tiene su propia configuración; el asistente se muestra al crearla y la configuración queda
  editable en cualquier momento.
- La factura se emite electrónica a la DIAN. Aun así, cada conexión tiene el interruptor **«Enviar a la DIAN al
  generar»**, que **nace apagado** (owner, 2026-11-18, porque no hay cuenta de pruebas: §9).
- El flete se factura **junto con el pedido o en una factura aparte, según la configuración de cada conexión** (owner,
  2026-11-18).
- Enfoque elegido: **A** (§4), un módulo `Siigo` propio, como `Shopify`.

Las decisiones que tomé yo van marcadas **[decisión del diseño]**.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**

1. Conectar N cuentas de Siigo por tenant, cada una atada a una empresa de QEP distinta.
2. Configurar por conexión: documento, vendedor, centro de costo, impuestos, retenciones, formas de pago, producto del
   flete y vínculo de variantes; editable siempre.
3. Generar desde un pedido una factura de venta en Siigo, sin duplicados, y marcar el pedido facturado con el número
   de Siigo.
4. Decir antes de enviar todo lo que falta (vínculos, configuración, datos del cliente) en una sola respuesta.

**No objetivos (esta entrega)**

- Notas crédito, anulación o edición de una factura ya creada en Siigo.
- **Recibos de caja (fase 2, owner 2026-11-18).** Un comprobante que llega después de facturar (saldo a crédito o
  contraentrega) no se envía a Siigo en esta entrega: la cuenta por cobrar de esa factura queda abierta allá hasta que
  alguien registre el pago en Siigo. La fase 2 lo manda como recibo de caja contra la factura (`POST /v1/vouchers`, con
  `Idempotency-Key` y la fecha `PaidOn` del comprobante). Los comprobantes nunca van como documento soporte, que en
  Siigo es para compras.
- Facturar en otra moneda que no sea COP.
- Facturar ventas del POS o cotizaciones: sólo pedidos.
- Facturación masiva (varios pedidos en una llamada) y facturación automática al pagar.
- Sincronizar catálogo, clientes o inventario entre Siigo y QEP más allá del vínculo de variantes.
- Reemplazar el Excel de pedidos: sigue igual y Ritual puede seguir usándolo mientras migra.

**Anti-objetivos (fallaría aunque funcione)**

- **Una factura duplicada.** Es un documento legal ante la DIAN y no se borra. Ningún reintento, doble clic ni carrera
  puede crear dos facturas para el mismo pedido.
- **Una factura con valores distintos al pedido.** El total, el IVA y las retenciones de Siigo tienen que ser los del
  pedido; si no cuadran, se rechaza antes de enviar (§6.4), no se «ajusta».
- Un pedido marcado facturado sin factura en Siigo, o una factura en Siigo sin pedido marcado, sin que la pantalla lo
  diga.
- Una credencial de Siigo en logs, auditoría o respuestas.

## 3. Restricciones (verificadas)

**API de Siigo** (developers.siigo.com, consultada el 2026-11-18):

- `POST /v1/invoices` exige `document.id` (tipo de comprobante, de `/document-types`), `date`, `customer.identification`,
  `seller` (de `/users`), `items[].code` (producto existente y activo), `items[].quantity` (2 decimales),
  `items[].price` (6 decimales; o `taxed_price` con IVA incluido), y `payments[]` con `id` (de `/payment-types`),
  `value` y `due_date` si la forma de pago es a crédito. Opcionales: `cost_center`, `items[].discount`,
  `items[].taxes[].id` (uno por tipo), `retentions[].id` (ReteICA, ReteIVA, autorretención, de `/taxes`),
  `observations` (4.000), `stamp.send`
  (DIAN, por defecto `false`), `mail.send`.
- El cliente tiene que existir y estar activo en Siigo, o se crea en la misma petición mandando el objeto `customer`
  completo (tipo de persona, tipo de documento, dirección con códigos de país, departamento y ciudad).
- **Idempotencia:** header `Idempotency-Key`, alfanumérico, sin espacios ni caracteres especiales, **máximo 30
  caracteres**. Repetir la clave devuelve la factura ya creada. La documentación no dice cuánto tiempo la recuerda.
- `POST /v1/invoices/{id}/stamp` envía a la DIAN una factura ya creada; `GET /v1/invoices/{id}/stamp/errors` da el
  rechazo.
- **Retenciones** (documentación `1-create-invoice` + contrato que ya usa el repo `ritual-botanico-backend`, sólo como
  referencia del contrato; de ahí no se trae código). Siigo **no recibe porcentaje ni valor**, sólo el id de un impuesto
  de `GET /v1/taxes` (`{ id, name, type, percentage, active }`), y calcula el monto él: **ReteICA** va a nivel de factura,
  `retentions: [{ "id": n }]`; **ReteFuente** va **dentro de los `taxes` de cada ítem**, después del IVA, `{ "id": n }`.
  `type` llega como `"Retefuente"` y `"ReteICA"`; el porcentaje de ReteICA viene por mil. Puede haber varias entradas
  activas con el mismo tipo y porcentaje en una cuenta, así que el vínculo con QEP lo elige una persona, no se adivina.
  Cómo calcula Siigo (observado, no documentado): la base es el subtotal **sin IVA** de los ítems (cada uno redondeado a
  2 decimales), `ReteFuente = round(subtotal × % / 100, 2)`, `ReteICA = round(subtotal × tasa / 1000, 2)`, y los pagos
  tienen que sumar `total con IVA − ReteFuente − ReteICA`.
- **Siigo sí rechaza pagos que no suman su total** (contrato observado): responde 4xx con
  `{ "Errors": [ { "Code": "invalid_total_payments", "Message": "…<total esperado>" } ] }`, y el mensaje termina con el
  total que esperaba. Es lo que permite que el total que no cuadra lo rechace Siigo (§6.4).
- Toda llamada lleva `Authorization` (token) y `Partner-Id`: 3 a 100 alfanuméricos, sin espacios ni especiales, el mismo
  valor para todas las empresas de una integración. Un usuario se bloquea si en 7 días más del 80 % de sus peticiones
  fallan, hasta que se corrijan los errores (`bloqueo-de-usuarios`): otra razón para validar todo antes de enviar.
  **Autenticación (contrato observado):** `POST https://api.siigo.com/auth` con `{ "username", "access_key" }` →
  `{ "access_token", "expires_in", "token_type", "scope" }`, y de ahí `Authorization: Bearer <token>`. `expires_in` dice la
  vida del token. **Sigue sin saberse:** el límite de peticiones y cuánto recuerda Siigo una `Idempotency-Key` (la
  referencia oficial, <https://developers.siigo.com/docs/siigoapi/>, no lo documenta).
- **No hay sandbox ni empresa de prueba** (owner, 2026-11-18): se va directo a producción, con envíos normales. Nada de
  este diseño supone una cuenta de pruebas. Las pruebas automáticas usan un fake (§11); lo que no se pueda saber de antemano
  se descubre con las primeras facturas reales, que salen con el interruptor «Enviar a la DIAN» apagado (§9).

**QEP:**

- La empresa que factura ya está en el pedido: `Order.BillingAccount.CompanyId`, con banco y cuenta en la foto
  `QuotationBillingAccount`. De ahí sale la conexión. Dos conexiones para la misma empresa no caben: el índice único
  `(tenant_id, company_id)` lo impide.
- La línea del pedido lleva `VariantId`, `ProductCode`, `Quantity`, `UnitPrice`, `DiscountPercentage`, `Subtotal`,
  `TaxPercentage` (0 cuando la empresa no cobra IVA), `TaxAmount`. Los precios de QEP son **con IVA incluido** y
  `Subtotal` es sin IVA (memoria `precios-con-iva-incluido`). El pedido lleva flete (`FreightSubtotal`,
  `FreightTaxPercentage`), retenciones congeladas (`AppliedWithholdings`, cada una con su `TaxRateId`), `Total`,
  `RetentionAmount`, `NetTotal` y `PaymentMethod` (texto libre).
- `Order.Invoice()` exige no anulado, no facturado y, si el tenant lo pide, pagado (`RequireFullPaymentToInvoice`).
  `InvoiceOrderHandler` además corre `BilledPersonTypeRequired` y `BilledCustomerFieldsRequired`.
- Los módulos no se ven entre sí: Siigo entra por puertos y adaptadores en Bootstrapper, como Shopify
  (`ShopifyLayerTests`).
- Un proveedor de Integrations existe sólo con módulo consumidor (`IntegrationProviders`, decisión 4 del spec
  2026-10-08).

## 4. Enfoques

| | Enfoque | A favor | En contra |
|---|---|---|---|
| **A** | **Módulo `Siigo` propio** (esquema `siigo`); la conexión y el secreto en Integrations; Quotations expone un puerto para marcar facturado | Patrón probado en Shopify (puertos, `*LayerTests`, adaptadores en Bootstrapper); Quotations no conoce Siigo | Un módulo más con su migración y pruebas de arquitectura |
| B | Todo dentro de Integrations | Menos piezas | Mete facturación, mapeos y llamadas de negocio en un módulo que sólo guarda conexiones; rompe la regla de que el consumidor declara su puerto |
| C | Módulo genérico `Invoicing` con Siigo de primer proveedor | Listo para Alegra u otro | Abstracción con un solo caso real, probablemente equivocada |

**Elegido: A** (owner). Si llega un segundo proveedor, se extrae lo común entonces.

## 5. Diseño

### 5.1 Piezas

- **Tenancy:** clave de módulo nueva `siigo` (`TenantModuleKey`), **fuera** de `DefaultForNewTenants`: la prende el
  operador por tenant **[decisión del diseño]**, como `pos`. Sin el módulo, todo `/siigo/*` da 403 y el proveedor no sale
  en el catálogo de Integraciones.
- **Integrations:** proveedor `siigo` en `IntegrationProviders`, categoría nueva `Accounting`, consumidor
  `TenantModuleKeys.Siigo`, `ProviderOnboarding.Form`, tope 20 por tenant (`DefaultMaxConnections`). Campos:
  `username` (texto, requerido; el usuario API de Siigo), `accessKey` (secreto, requerido), `companyName` (sólo
  lectura, lo escribe el probador con el nombre de la cuenta si la API lo da). El probador autentica y llama a un
  endpoint de lectura barato (`/v1/document-types?type=FV`).
- **Módulo `Siigo`** (`src/Modules/Siigo/`, cuatro capas, `SiigoLayerTests`): configuración por conexión, vínculos,
  facturas emitidas, cliente HTTP de Siigo, endpoints. Application ve sólo Tenancy y Audit; Quotations, Catalog,
  Companies, Customers y Geography entran por puertos (`SiigoPorts.cs`) con adaptadores en `src/Bootstrapper/Siigo*.cs`.
- **Quotations:** campos nuevos en el pedido y un puerto para que Siigo lo marque (§5.5).

### 5.2 Configuración por conexión (esquema `siigo`)

Una fila de configuración por conexión, creada vacía al crear la conexión (o al primer acceso, como `PipelineProvisioner`,
**[decisión del diseño]**: al primer acceso, para no acoplar el alta de Integrations a Siigo).

`siigo.settings` — PK `connection_id`; `tenant_id`; `version` (concurrencia optimista, `If-Match`):

| Columna | Qué es | Requerido para facturar |
|---|---|---|
| `company_id` | Empresa de QEP que factura con esta cuenta. Índice único `(tenant_id, company_id)` | Sí |
| `document_type_id` | Id de Siigo del comprobante de venta (FV) | Sí |
| `seller_id` | Id de usuario de Siigo que va como vendedor | Sí |
| `cost_center_id` | Centro de costo de Siigo | No |
| `send_to_dian` | «Enviar a la DIAN al generar», `stamp.send`. **Default `false`** (owner, 2026-11-18): sin cuenta de pruebas, una factura emitida no se borra (§9). Es un `false` explícito en el modelo y en la columna (`HasDefaultValue(false)`); la pantalla avisa mientras esté apagado | — |
| `freight_separate_invoice` | El flete sale en una factura aparte (un ítem, cantidad 1) y no como un ítem más de la factura del pedido. Default `false` **[decisión del diseño]**: una sola factura por pedido, que es lo simple | — |
| `send_mail` | `mail.send`: Siigo le manda la factura al cliente. Default `false` **[decisión del diseño]** | — |
| `freight_product_code` | Código del producto o servicio de Siigo que factura el flete | Sólo si el pedido cobra flete |
| `warehouse_id` | Bodega de Siigo para los ítems (`items[].warehouse`); el contrato observado la manda en cada ítem | Sólo si los productos llevan control de inventario (V-8) |
| `credit_payment_type_id` | Forma de pago de Siigo a crédito, para lo no pagado | Sólo si el pedido tiene saldo |
| `credit_days` | Días para el `due_date` a crédito | Con el anterior |

Tablas hijas, todas con `connection_id` y `tenant_id`, borradas en cascada con la configuración:

- `siigo.variant_links` — `(connection_id, variant_id)` único; `siigo_product_code`, `siigo_product_id`,
  `siigo_product_name` (foto para la pantalla), `linked_by` (membresía), `linked_at`, `origin` (`AutoCode` | `Manual`).
- `siigo.tax_links` — IVA de QEP por porcentaje entero (`0`, `5`, `19`…) → `siigo_tax_id`. Único
  `(connection_id, tax_percentage)`. El 0 puede quedar sin vínculo: una línea al 0 % va sin `taxes`.
- `siigo.withholding_links` — `TaxRateId` de QEP (la tasa de retención del catálogo de impuestos) → `siigo_tax_id`.
  Único `(connection_id, tax_rate_id)`.
- `siigo.payment_links` — la cuenta bancaria de la empresa (la que el pedido congela en `BillingAccount`) →
  `siigo_payment_type_id`. `QuotationBillingAccount` no trae id de cuenta (verificado: `CompanyId`, `BankName`,
  `AccountNumber`, `Currency`, `Country`, `CityId`, `ChargesVat`), así que la clave es **banco + número normalizados**
  (mayúsculas, sin espacios, guiones ni puntos), única por conexión **[decisión del diseño]**. Si la cuenta se renombra en
  la empresa después de congelar el pedido, ese pedido sale con el faltante `siigo.invoice.payment_unlinked` y la
  pantalla deja vincular el texto viejo: es el mismo acople de Ritual, pero visible. Un pedido sin banco en la foto usa
  `default_payment_type_id` de la configuración (columna nueva en `siigo.settings`, requerida sólo en ese caso).

Las listas de Siigo (comprobantes, usuarios, centros de costo, impuestos, formas de pago, productos) **no se copian**:
se leen en vivo cuando la pantalla las pide, con una caché corta por conexión (5 min) **[decisión del diseño]**. Al
vincular se guarda el id y el nombre como foto.

### 5.3 Asistente y pantalla de configuración

Son los mismos pasos: el asistente los recorre en orden al crear la conexión y la pantalla de configuración los muestra
todos, editables. Cada paso es un endpoint propio, para guardar de a pedazos.

1. **Credenciales y prueba** — es la conexión de Integrations (formulario y probador existentes).
2. **Empresa** — `PUT …/siigo/connections/{id}/company`. `409 siigo.settings.company_taken` si otra conexión ya la
   tiene (por nombre de índice, en `SiigoUnitOfWork`).
3. **Documento** — comprobante, vendedor, centro de costo, interruptores DIAN y correo, días de crédito.
4. **Impuestos y retenciones** — lista las tasas de IVA en uso en el catálogo del tenant y las retenciones de la empresa,
   cada una con un selector de impuestos de Siigo.
5. **Formas de pago** — las cuentas de cobro de la empresa, cada una con un selector; más la forma de pago a crédito.
6. **Productos** — tabla de variantes de QEP (paginada, con búsqueda) y su producto de Siigo vinculado; acción
   «Vincular por código» que empareja todas las variantes sin vínculo cuyo código sea igual, sin distinguir mayúsculas y
   sin espacios, al `code` de un producto activo de Siigo (`origin = AutoCode`); vínculo y desvínculo manual por fila.
   Muestra la **cobertura**: variantes vinculadas sobre variantes activas.

`GET …/siigo/connections/{id}/settings` devuelve la configuración entera más un `readiness` por paso (`complete` |
`incomplete` con los códigos que faltan), para que la pantalla marque los pasos y el asistente sepa dónde seguir. El
vínculo es **por variante** y no por producto: en QEP el código vive en `catalog.product_variants` y cada variante es un
producto distinto en Siigo.

### 5.4 Generar la factura

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice` (el endpoint vive en el módulo Siigo). Sin cuerpo.
Permiso `siigo.invoice.issue` más la revalidación en el handler (doble capa). Responde el estado de la factura (§5.6).

Orden:

1. **Candado por pedido**: `pg_advisory_xact_lock(<namespace siigo>, hashtext(orderId))`. Con la fila vigente del
   pedido **[decisión del diseño]**:
   - `Pending` → se retoma: se reenvían los bytes guardados con la misma clave, **sin** volver a armar ni validar (rearmar
     rompería la idempotencia). Antes se pasa a `Unknown` si tiene más de 24 h (transición perezosa al leer o reintentar,
     sin worker). La fila guarda la `version` del pedido al armar; si al confirmar el pedido ya no está en esa versión, la
     fila queda con `order_changed = true` y la pantalla lo muestra.
   - `Created`, `Stamped`, `StampRejected`, `Unknown` → se devuelve tal cual, sin llamar a Siigo.
   - `Rejected`, `Failed` o ninguna → se arma una fila nueva (pasos 2 y 3).
   - Conexión no operativa en Integrations al retomar → `409 siigo.connection_unavailable`, la fila no cambia.
2. **Validación completa** (§6), que junta **todos** los faltantes y responde
   `422 siigo.invoice.not_ready` con `details.missing` (lista de `{ code, … }`: variantes sin vínculo con su id y nombre,
   tasa sin vínculo, cuenta sin forma de pago, cliente sin dato). Nada sale a Siigo.
3. **Armar la petición** (§6.3) y guardar la fila `siigo.invoices` en `Pending` con la petición entera (`request_json`)
   y su `idempotency_key`. **Commit.** De acá en adelante, un reintento manda los mismos bytes con la misma clave.
4. **Crear en Siigo**: `POST /v1/invoices` con `stamp.send` = `send_to_dian` de la conexión (owner, 2026-11-18: el envío a
   la DIAN lo manda la configuración del tenant), fuera de la transacción y con `Idempotency-Key`. Sin reintentos en el
   cliente HTTP (como `MaxRetries = 0` de los agentes): reintenta quien llama, con la misma fila.
5. **Resultado:**
   - 2xx → fila `Created` (o `Stamped` si Siigo devuelve el CUFE) con `siigo_invoice_id`, `number` (`name`, p. ej.
     `FV-1-123`) y `total` devuelto, y **marca el pedido** (§5.5).
   - 4xx de validación de Siigo → `Rejected` con el error de Siigo (códigos y campos, sin datos del cliente), pedido sin
     marcar. Como Siigo no la creó, una nueva llamada crea otra fila. **Aquí cae un total que no cuadra** (§6.4): los
     pagos los arma QEP y Siigo rechaza la factura si no suman lo que él calculó.
   - Timeout, 5xx, 429 o red → la fila queda `Pending`. La siguiente llamada al endpoint (o el botón «Reintentar»)
     reenvía **la misma petición con la misma clave**; Siigo devuelve la ya creada si existía. Una `Pending` de más de
     24 h pasa a `Unknown` **[decisión del diseño]**: la documentación no dice cuánto recuerda la clave, y pasado ese
     tiempo reenviar puede duplicar. `Unknown` no se reenvía sola: la resuelve una persona (§5.6).
6. Con `send_to_dian` apagado la fila termina en `Created` y la emite una persona en Siigo. Una vez emitida, la factura
   no se corrige con QEP (§2).

`idempotency_key`: los 30 primeros caracteres de la codificación base32 en minúsculas, sin relleno, del id de la fila
(GUID v7 → 26 caracteres) **[decisión del diseño]**. Es única por intento, no por pedido: el candado y la regla del
paso 1 dan la unicidad por pedido; la clave da la seguridad del reintento.

DIAN rechaza: si Siigo crea la factura pero la DIAN la rechaza, la fila queda `StampRejected` con los errores de
`GET …/stamp/errors`. El pedido **se marca** (la factura existe en Siigo y tiene número) y la pantalla muestra el
rechazo. `POST …/siigo-invoice/stamp` reintenta el envío (`POST /v1/invoices/{id}/stamp`) después de corregir en Siigo.

### 5.5 Cambios en Quotations

- Columnas nuevas en `quotations.orders` (aditivas, nullable): `invoice_provider` (`siigo` o `NULL` = marca manual),
  `invoice_number`, `invoice_external_id`. Viajan en el `OrderDto` para que la pantalla muestre «Facturado en Siigo
  FV-1-123».
- Puerto `IExternalOrderInvoicing` (Quotations.Application), adaptador en Bootstrapper:
  - `GetForInvoicingAsync(tenantId, orderId)` → la foto del pedido que Siigo necesita (líneas, flete, retenciones,
    cuenta de cobro, cliente, totales, estado, versión).
  - `MarkInvoicedAsync(tenantId, orderId, provider, number, externalId, invoicedBy)` → llama a un método nuevo
    `Order.InvoiceExternally(...)` que **no** vuelve a exigir pagado: el pago se valida una vez, al armar (§6.1), y la
    factura ya existe en Siigo, así que marcar tiene que salir aunque un comprobante haya cambiado entre medio
    **[decisión del diseño]**. Si el pedido ya está facturado con el mismo proveedor y número, no hace nada. Escribe historial
    `Invoiced` y la auditoría `quotation.order.invoiced` con `provider` en `changedFields`. Reintenta una vez ante
    `RequestConcurrencyException` (como `BuyerOrderGateway.LinkAsync`). Idempotente.
- `RevertOrderInvoicing` responde `422 order.order.invoiced_externally` si `invoice_provider` no es nulo. Revertir una
  factura de Siigo es una nota crédito y queda fuera (§2).
- `InvoiceOrderHandler` (la marca manual) responde `409 order.order.external_invoice_pending` si hay una factura de Siigo
  `Pending` o `Unknown` para el pedido. Lo pregunta por un puerto de Quotations con adaptador en Bootstrapper que lee
  `siigo.invoices` **[decisión del diseño]**: así nadie marca a mano mientras la factura real puede estar saliendo.

### 5.6 Estado y pantalla

`siigo.invoices`: `id`, `tenant_id`, `connection_id`, `order_id`, `kind` (`Order` | `Freight`: una fila por factura, y con
`freight_separate_invoice` un pedido puede tener las dos), `status` (`Pending` | `Created` | `Stamped` |
`StampRejected` | `Rejected` | `Unknown` | `Failed`), `idempotency_key`, `request_json`, `siigo_invoice_id`,
`number`, `cufe`, `siigo_total`, `error_code`, `error_detail` (de Siigo, recortado; sin datos del cliente), `attempts`,
`created_by`, `created_at`, `updated_at`. Índice único parcial `(tenant_id, order_id, kind) WHERE status NOT IN
('Rejected', 'Failed')`, traducido por nombre a `409 siigo.invoice.already_exists`: el candado da la respuesta amable, el
índice cierra la carrera (precedente `IX_orders_tenant_external_key`).

- `GET …/orders/{orderId}/siigo-invoice` → las filas vigentes del pedido, una por `kind` (la del pedido y, si hay, la de
  flete), o 404. `resolve` y el reintento reciben el `kind` (`Order` por defecto).
- `POST …/orders/{orderId}/siigo-invoice/resolve` con `{ "outcome": "exists", "number": "FV-1-123" }` o
  `{ "outcome": "missing" }`, sólo desde `Unknown`: la persona miró en Siigo. `exists` marca el pedido con ese número;
  `missing` pasa la fila a `Failed` y libera el pedido para facturar de nuevo. Auditoría con quién lo resolvió.

### 5.7 Permisos y auditoría

- `siigo.settings.manage` (configurar conexiones) y `siigo.invoice.issue` (generar y reintentar), con sus políticas en
  `AddAuthorization`. De fábrica: `admin` los dos y `billing` el de facturar **[decisión del diseño]**, como
  `quotations.order.invoice`.
- Auditoría atómica con puerto propio (`ISiigoAuditRecorder`, como Integrations): `siigo.settings.updated` (pasos
  cambiados, sin valores de Siigo), `siigo.invoice.created`, `siigo.invoice.rejected`, `siigo.invoice.resolved`.

## 6. Validación y armado de la factura

### 6.1 Precondiciones del pedido

Las mismas de `InvoiceOrderHandler`, en el mismo orden: no anulado, **no facturado** —un pedido marcado a mano no se
factura en Siigo, porque su factura pudo hacerse por fuera y sería un duplicado; para facturarlo en Siigo primero se
revierte la marca **[decisión del diseño]**—, pagado si el tenant lo exige, `BilledPersonTypeRequired`, `BilledCustomerFieldsRequired`. Además:
moneda COP (`siigo.invoice.currency_unsupported`), cuenta de cobro con empresa (`siigo.invoice.no_company`) y empresa
con conexión configurada (`siigo.invoice.no_connection`), conexión operativa en Integrations.

### 6.2 Cliente

El cliente se resuelve **antes** de la factura y en una llamada aparte (contrato observado en `ritual-botanico-backend`;
cambia lo que decía este diseño de crearlo dentro de la factura): `GET /v1/customers?identification=` devuelve
`{ pagination, results[] }`. Si hay un cliente activo, la factura va sólo con `customer: { identification, branch_office: 0 }`
y los datos de Siigo mandan. Si no, `POST /v1/customers` con `type: "Customer"`, `person_type` (`Person` | `Company`),
`id_type`, `identification`, `name` (lista: `[nombres, apellidos]` para persona, `[razón social]` para empresa),
`active: true`, `address { address, city { country_code, state_code, city_code } }`, `phones [{ number }]` y
`contacts [{ first_name, last_name, email }]`; con la identificación y el cliente guardados, se factura. No manda
`fiscal_responsibilities` ni `vat_responsible` y Siigo lo acepta (**falta confirmar** que una factura electrónica no los
exija, V-6).

- Tipo de documento de QEP → `id_type` de Siigo, tabla fija en `SiigoIdentificationTypes`, con prueba: CC `13`, NIT `31`,
  CE `22`, TI `12`, PP/PA `41`, PEP `47`, RC `11`. Un tipo de QEP sin código es faltante, no se deduce.
- **NIT sin dígito de verificación y sólo con números**; Siigo acepta crear sin él. Cómo se limpia el documento cambió: mira
  la adición del 2026-11-25 en
  [`2026-11-25-siigo-telefono-indicativo-design.md`](2026-11-25-siigo-telefono-indicativo-design.md).
- Ciudad: código DANE de cinco dígitos de Geography (`city_code`) y departamento de dos (`state_code`; Bogotá `11001`/`11`).
  Un cliente sin ciudad con código DANE es `siigo.invoice.customer_city_missing`.
- Los datos salen de la parte facturada del pedido (`order_parties`) y, lo que falte, del cliente en Customers por puerto
  **[decisión del diseño]**. Crear el cliente es una llamada que no se deshace: se hace **después** de la validación
  completa y de armar la petición, no antes, para no dejar clientes sueltos en Siigo si el pedido no estaba listo.
- El consumidor final (`BillsToFinalConsumer`) va con el documento 222222222222 que define la DIAN **[decisión del diseño;
  confirmar con la primera factura real, V-3]**.

### 6.3 Ítems, impuestos, retenciones, pagos

- Una línea del pedido → un ítem: `code` del vínculo de la variante, `description` = nombre de la línea, `quantity`,
  `price` = precio unitario **sin IVA** derivado de `Subtotal` (6 decimales) y `discount` = `DiscountPercentage`; `taxes`
  = el vínculo de `TaxPercentage` (sin `taxes` si es 0 y no hay vínculo del 0). **El builder decide** entre `price` y
  `taxed_price` con la prueba de cuadre (§6.4) como juez.
- Flete cobrado (`FreightSubtotal > 0`) → un ítem con `freight_product_code`, cantidad 1, su IVA vinculado. Con
  `freight_separate_invoice` apagado va como un ítem más de la factura del pedido. Con la opción prendida va en **su propia
  factura** (`kind = Freight`), con un solo ítem, la observación «Flete del pedido N» y su propia `Idempotency-Key`, y las
  dos facturas se arman así **[decisión del diseño]**:
  - La del pedido (`kind = Order`) lleva las líneas, las retenciones y el total `ProductsTotal` menos retenciones; la de
    flete lleva sólo el flete con su IVA (`FreightSubtotal` + `FreightTaxAmount`) y **ninguna retención**, porque la base
    de retenciones de QEP sale de las líneas y no incluye el flete (`WithholdingBases.From(items)`).
  - Cada factura valida su propio total (§6.4) y cada una tiene sus `payments` que suman **su** neto. La propuesta reparte
    los comprobantes primero a la del pedido y lo que sobre a la de flete; el saldo de cada una va a contraentrega o a
    crédito, y quien factura puede cambiar el reparto antes de enviar (el `POST` acepta `payments` por factura).
  - Se crea primero la del pedido. **El pedido se marca facturado cuando la del pedido sale** (§5.5); la de flete sigue
    después en la misma petición y, si Siigo la rechaza, queda `Rejected` con el pedido ya marcado y la pantalla ofrece
    «Facturar flete» para reintentar sólo esa. Una sin la otra es un estado válido, no un error del pedido.
  - `send_to_dian` aplica a las dos por igual.
- `AppliedWithholdings` → por vínculo (`withholding_links`). **Sólo se manda el id**: el monto lo calcula Siigo, así que
  el de QEP (porcentaje y base mínima por UVT) puede no coincidir, y ahí actúa el rechazo `invalid_total_payments`
  (§6.4). **Dónde va depende del tipo**: ReteICA en `retentions` de la factura; ReteFuente en los `taxes` de **cada
  ítem**, después del IVA. Un tipo de QEP que no sea ni ReteICA ni ReteFuente (ReteIVA, autorretención) no se manda y
  queda como faltante hasta que se decida (V-7). Una retención de QEP que no aplicó (`Applied = false`) no se manda; una
  aplicada sin vínculo es faltante.
- Pagos (owner, 2026-11-18): **cada comprobante de pago del pedido (`PaymentProofs`) es un pago de la factura**, con su
  monto. Su forma de pago de Siigo se propone con el vínculo de la cuenta de cobro del pedido (`payment_links`) y quien
  factura la puede cambiar comprobante por comprobante: el `POST` acepta `payments: [{ proofId, siigoPaymentTypeId }]`
  opcional; sin él, se usa la propuesta. Un comprobante sin forma de pago (ni propuesta ni elegida) es faltante. El saldo
  (`NetTotal` − Σ comprobantes), si queda, va con la forma de contraentrega (`CashOnDelivery`) o la de crédito con
  `due_date` = hoy + `credit_days` **[decisión del diseño]**. Comprobantes que suman más que el total:
  `422 siigo.invoice.payments_exceed_total`. La suma tiene que dar el total que Siigo espera (con o sin retenciones,
  V-2). `GET …/orders/{id}/siigo-invoice/preview` devuelve la factura armada, los faltantes y la propuesta de pagos.
- `date` = hoy en la zona del tenant. `observations` = número del pedido de QEP y `BuyerNotes` si hay, recortado a 4.000.
  Sin `additional_fields.purchase_order` (2026-11-20): Siigo lo rechaza con `document_settings` si el comprobante no tiene la orden de compra
  configurada; el número del pedido ya va en `observations`.

### 6.4 Cuadre de totales

Decisión del owner (2026-11-18): **un pedido cuyos totales no cuadran con los que calcula Siigo no se factura**, y lo
rechaza Siigo mismo: QEP arma los `payments` con sus valores (comprobantes y saldo) y, si no suman lo que Siigo calculó
(ítems, IVA y retenciones), Siigo no crea la factura. Por eso `stamp.send` va con la configuración del tenant y no hay
verificación posterior.

1. **Antes de crear** (barata, evita errores y peticiones fallidas, que cuentan para el bloqueo del 80 %): el armador
   calcula el subtotal, el IVA y el total de los ítems como Siigo los va a redondear, sin retenciones. Si ya ahí difieren
   del pedido en más de 1 peso (tolerancia **[decisión del diseño]**), `422 siigo.invoice.totals_mismatch` con los dos
   valores y nada sale a Siigo.
2. **En Siigo**: el rechazo de Siigo por pagos que no suman se traduce al mismo `siigo.invoice.totals_mismatch` (con el
   código y el texto de Siigo en `details`), la fila queda `Rejected` y el pedido sin marcar. La causa típica es una
   retención: Siigo y QEP no aplican la misma base mínima o el mismo redondeo; se corrige la configuración del impuesto en
   Siigo o la tasa en QEP.

**Verificado en el contrato** (repo `ritual-botanico-backend`, sólo como referencia): Siigo responde
`invalid_total_payments` con el total esperado cuando los pagos no suman, así que el rechazo existe y no hace falta
verificar después de crear. Dos consecuencias: (1) el total que Siigo espera es el neto, **con las retenciones ya
descontadas** (§3), así que los `payments` que arma QEP suman `NetTotal`, no `Total`; (2) el mensaje de rechazo trae el
total esperado, y la pantalla lo muestra junto al de QEP para que se vea de cuánto es la diferencia. **No se reintenta
solo con el total que Siigo propone**: ajustar los pagos a lo que Siigo dice tapa justo el error que esta regla quiere
mostrar. La verificación V-7(a) queda para confirmar el código con el primer rechazo real, no para decidir el flujo.

## 7. Seguridad

- `accessKey` y el token de Siigo nunca salen a logs, auditoría, `request_json` ni respuestas. Cliente HTTP nombrado
  `siigo` con `RemoveAllLoggers()` y prueba `SiigoNoLeakTests` con el host en `Trace` (precedente `AnthropicNoLeakTests`).
- `request_json` lleva datos del cliente (documento, dirección): sólo lo leen los handlers del módulo, nunca sale en
  un `GET`.
- El token se cachea por conexión en memoria hasta su vencimiento menos un margen; con varias réplicas cada pod tiene el
  suyo **[decisión del diseño]**. El cliente de token se clasifica en `MultiReplicaInventory`.
- Tenant doble capa en toda ruta; 403, nunca 404, para la conexión de otro tenant.

## 8. Migraciones y despliegue

- `InitialSiigo` (esquema nuevo) y `AddOrderInvoiceProvider` (Quotations, tres columnas nullable): las dos aditivas;
  despliegue sin corte.
- `CHECK` de proveedores de Integrations ampliado con `siigo` (migración de Integrations). Su `Down` borra las conexiones
  `siigo` con sus secretos (`CASCADE`), igual que `AddOpenAiProvider`: no se corre en producción.
- `siigo.invoices` es registro de un documento legal: no tiene purga y su `Down` no se corre.
- Un `Partner-Id` de QEP como configuración global (`Siigo:PartnerId`), en el ConfigMap; requerido en `Production` con
  la regla de `#{…}#` como ausente.

## 9. Verificaciones abiertas y puesta en marcha

**No hay empresa de prueba: todo corre contra la cuenta real del tenant** (owner, 2026-11-18). Por eso las verificaciones
de abajo ya no son un paso previo a escribir el cliente: se resuelven **con las primeras facturas reales**, y el diseño se
protege para que ninguna de ellas pueda salir mal sin que se vea:

- **Las primeras facturas de cada conexión salen con «Enviar a la DIAN» apagado.** Es el interruptor que ya existe
  (§5.2), no un modo de pruebas: la factura queda creada en Siigo, sin emitir, y una persona la revisa allá antes de
  prender el interruptor. Una factura emitida a la DIAN no se borra, y sin cuenta de pruebas es la única red que hay.
  **Decisión del owner (2026-11-18):** la conexión nace con el interruptor **apagado**, y la pantalla de configuración
  avisa que mientras esté apagado nada llega a la DIAN. Prenderlo es una decisión de quien configura.
- Una factura creada y sin emitir se borra a mano en Siigo; QEP no la borra, y `resolve` con `missing` libera el pedido.
- Toda respuesta o error inesperado de Siigo se guarda recortado en la fila (sin datos del cliente) para poder ajustar el
  armado de la petición sin volver a facturar.

- **V-1** Límite de peticiones. (La autenticación y `expires_in` ya salen del contrato observado, §3; la referencia oficial,
  <https://developers.siigo.com/docs/siigoapi/>, no lo documenta.)
- **V-2** Redondeo de Siigo por ítem con descuento y cuándo conviene `price` y cuándo `taxed_price`. (Que los pagos suman
  con las retenciones descontadas ya salió del contrato observado, §3.)
- **V-3** Documento del consumidor final. (Los códigos de tipo de documento ya salen del contrato observado, §6.2.)
- **V-4** (resuelta: `QuotationBillingAccount` no trae id de cuenta; la clave es banco + número, §5.2).
- **V-5** Cuánto tiempo recuerda Siigo un `Idempotency-Key` (decide si 24 h para `Unknown` es corto o largo).
- **V-6** Si una factura electrónica exige `fiscal_responsibilities` y `vat_responsible` en el cliente (el contrato
  observado los omite), y si `person_type` va como `Person`/`Company` (lo que se usa hoy) o en minúscula (lo que dice la
  documentación).
- **V-7** Con la primera factura real sin emitir (`stamp.send = false`): **(a)** confirmar el código y el texto de
  `invalid_total_payments` con y sin retenciones; **(b)** si Siigo aplica la base mínima (UVT) configurada en el impuesto
  de retención, cómo redondea cada retención y si se pueden mandar varias del mismo tipo; **(c)** ReteIVA y
  autorretención: dónde van y si QEP las necesita; **(d)** si una factura sin emitir se puede borrar por API
  (`DELETE /v1/invoices/{id}`), para no depender de borrar a mano.
- **V-8** Cuándo exige Siigo la bodega en un ítem (productos con control de inventario) y si un servicio la rechaza.
- **P-1 (resuelta, owner 2026-11-18)** El contrato observado factura el flete en una factura aparte; este diseño lo deja
  como opción por conexión (`freight_separate_invoice`, §5.2 y §6.3).

Conexión y empresa **[decisión del diseño]**: cambiar la empresa de una conexión se permite; las facturas ya emitidas
conservan su `connection_id` y los pedidos nuevos se enrutan por la empresa vigente. Borrar la conexión en Integrations
borra la configuración y los vínculos, pero no `siigo.invoices` (sin FK a la conexión: el número y el estado siguen
visibles en el pedido).

## 10. Frontend (spec aparte en `qep-frontend`)

Tarjeta Siigo en Integraciones; asistente de seis pasos y pantalla de configuración con el `readiness`; botón «Facturar
en Siigo» en el detalle del pedido con la lista de faltantes del 422 enlazando a cada paso; estado de la factura
(número, DIAN, rechazo, reintentar, resolver `Unknown`); «Facturado en Siigo FV-…» y el bloqueo de revertir. Se despliega
después del backend; el backend no rompe nada sin él porque el módulo `siigo` nace apagado.

## 11. Pruebas mínimas

- Dominio: armado de la petición (ítems, IVA, flete, retenciones, pagos y crédito) y cuadre, con casos de pedidos reales.
- Un pedido, dos `POST` simultáneos → una factura (candado + índice).
- Timeout tras crear en Siigo → reintento con la misma clave → una factura, pedido marcado.
- 4xx de Siigo → `Rejected`, pedido sin marcar, nueva factura posible.
- Marca manual con factura `Pending` → 409; revertir facturado por Siigo → 422.
- Faltantes juntos en un solo 422.
- Sin módulo → 403; conexión de otro tenant → 403; empresa repetida → 409.
- `SiigoLayerTests`, `SiigoNoLeakTests`, `MultiReplicaInventory`.
- Fake de Siigo en las pruebas de integración (sin red). Ninguna prueba automática llama a Siigo: lo que se descubra con
  las primeras facturas reales (§9) se copia a los fixtures del fake, junto con los del contrato observado (§3).
- `stamp.send` sale con `send_to_dian` de la conexión (prendido y apagado); un rechazo de Siigo por pagos que no suman
  → `Rejected` con `siigo.invoice.totals_mismatch`, pedido sin marcar y nuevo intento posible; una factura creada pero
  rechazada por la DIAN → `StampRejected` con el pedido marcado.

## 12. Lo que queda para el builder

Nombres de clases y rutas exactas, forma de los DTO (con el motivo BFF anotado), paginación de la tabla de variantes,
duración de la caché de listas, códigos de error finales dentro del prefijo `siigo.*`, y la división en tareas.

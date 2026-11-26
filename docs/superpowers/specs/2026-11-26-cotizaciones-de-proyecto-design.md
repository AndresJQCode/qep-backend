# Cotizaciones de proyecto — diseño (backend)

Fecha: 2026-11-26. Repo: `qep-backend`. Spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-26-cotizaciones-de-proyecto-design.md`.
El contrato (§6) es idéntico en los dos archivos, salvo lo que lista §12 (cambios que el owner tiene que llevar al
frontend). Este spec es autónomo: no depende del interruptor «Cotizar servicios», de `/settings/quotations` ni de nada
del spec `2026-11-26-cotizar-servicios-design.md`.

Revisado contra el código de `develop` en `4dc94b00c` (2026-11-26). Plan: `docs/superpowers/plans/2026-11-26-cotizaciones-de-proyecto.md`.

## 1. Intención

Un tenant que vende proyectos (por ejemplo, desarrollo de software) cotiza un alcance —ítems de texto, agrupados o no
en fases— con un precio global o por fase, y quiere que esa cotización se envíe, se convierta en pedido, se cobre y
se facture como cualquier otra. Más adelante, una gestión de proyectos importará ese alcance.

### Decisiones del owner (no se reabren)

1. Un módulo de tenant nuevo, «Gestión de proyectos». Las cotizaciones de proyecto vienen con él; un tenant sin el
   módulo no ve nada de proyectos. No lo gobierna el interruptor «Cotizar servicios».
2. Pantallas separadas: `/quotes` sigue para productos y servicios fijos; las de proyecto viven en `/projects/quotes`.
   `/projects` (ejecución) es otra entrega con su propio diseño.
3. Enfoque: dos pantallas, un motor. Una cotización de proyecto es una cotización de tipo `Project` que reusa
   numeración, impuestos, retenciones, PDF, envío y conversión a pedido.
4. El alcance son ítems de texto libre editables, opcionalmente agrupados en fases. Cada ítem y cada fase tiene un
   identificador estable. Los ítems no tienen precio propio.
5. El modo de precio se elige en cada cotización: por fase o global. Sin fases sólo tiene sentido el global.
6. P1 a P9 de §10, aprobados como se propusieron (owner, 2026-11-26).

### Supuestos que el owner vio y no corrigió

- El módulo depende de Cotizaciones y Pedidos (cotización → pedido → proyecto; la cotización no tiene estado
  «aprobada»: convertir es aprobar).
- Cada cotización de proyecto elige **un** servicio del catálogo, que aporta IVA, retenciones y el producto de Siigo.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**: módulo `projects`; cotización `Project` con alcance estructurado y modo de precio; envío, PDF y
conversión con el motor de hoy; pedido `Project` que se cobra y factura como cualquier pedido.

**No objetivos**: hitos de pago, estimador por horas, ejecución del proyecto (`/projects`), ítems opcionales que elige
el cliente, pedido de proyecto creado a mano, duplicar cotizaciones (hoy no existe para ninguna: no hay ruta ni
pantalla), borradores del agente de IA (`IExternalQuotationWriter` sigue creando `Standard`).

**Anti-objetivos**

- Que un tenant sin el módulo vea un menú, un filtro, una columna o un documento de proyecto.
- Que una lista, un reporte o un export de hoy cambie al desplegar (no hay documentos `Project` hasta que alguien
  prenda el módulo).
- Que se relaje la regla de una línea por variante.
- Que el navegador sume precios de fase.

## 3. Restricciones

- **BFF**: `total`, `kind` y la línea los arma el backend.
- **Módulo nuevo** (`src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`): clave en `TenantModuleKeys`, al
  final de `All` (después de `Wms`, que hoy es la última), dependencias en `Dependencies`, fuera de
  `DefaultForNewTenants`; migración de Tenancy que reescribe `CK_tenant_modules_module_key` (precedente
  `20261126021100_AddWmsModuleKey.cs`, con su `Down` que borra las filas de la clave) y el mismo `CHECK` en
  `TenancyDbContext.ConfigureTenantModule`.
- **Código**: todo vive en Quotations (dos pantallas, un motor). No hay módulo de código nuevo ni referencia nueva
  entre módulos, así que no hay `<Modulo>LayerTests.cs` nuevo. `Modules.Quotations.Application` ya usa
  `TenantModuleGuard` (`GetQuotationSettings`, `GetOrderSettings`).
- **Expand/contract**: columnas aditivas con default `Standard`/`null`. Nadie prende el módulo hasta que no quede un
  pod viejo: un pod viejo no conoce `kind` y listaría o editaría un `Project` como `Standard`, y
  `TenantModuleKey.Parse` lanza con una fila `projects` que no conoce.
- **Contrato**: este spec autoriza los cambios marcados **(nuevo)** y los de §12 una vez aprobados (regla de cambio de
  arquitectura del `CLAUDE.md`).

## 4. Enfoques considerados

| Enfoque | Resultado | Motivo |
| --- | --- | --- |
| **Dos pantallas, un motor: cotización `kind = Project`** | **Elegido** (owner) | Reusa numeración, impuestos, retenciones, PDF, envío, conversión, Siigo. |
| Documento «Propuesta» propio en un módulo de proyectos | Descartado (owner) | Duplica todo lo anterior. |
| Casilla «historias de usuario» en servicios del catálogo | Descartado (owner) | Reemplazado por este diseño. |
| Alcance en líneas, una línea por fase con la misma variante | Descartado (decisión del diseño, revisable) | Obliga a relajar `*.item.duplicate_product` y a cambiar la identidad de línea en diffs y borradores. |
| **Una línea del servicio con precio = total; alcance y precios de fase colgando del documento** | **Elegido** (decisión del diseño, revisable) | La regla de una variante por documento queda intacta y todo lo que lee líneas sigue funcionando (§8). |

## 5. Modelo de datos

- `TenantModuleKeys.Projects = Define("projects")` **(nuevo)**; `Dependencies[Projects] = [Quotations, Orders]`.
- `quotations.quotations.kind` y `quotations.orders.kind`: `text not null default 'Standard'`, CHECK
  `IN ('Standard','Project')` **(nuevo)**; enum `QuotationKind { Standard, Project }` en `Quotations.Domain`. El default
  de la base también cubre los `INSERT` crudos que nombran columnas (`ExportLoadSeeder`, pruebas de migración).
- `quotations.quotations.project_scope` y `quotations.orders.project_scope`: `jsonb null` **(nuevo)**. Value object
  `ProjectScope(Guid? ServiceVariantId, ProjectPricingMode PricingMode, decimal? GlobalPrice,
  IReadOnlyList<ProjectPhase> Phases, IReadOnlyList<ProjectItem> Items)`; `ProjectPhase(Guid Id, string Name,
  decimal? Price, IReadOnlyList<ProjectItem> Items)`; `ProjectItem(Guid Id, string Text)`. `jsonb` (decisión del
  diseño, revisable): es foto que viaja entera, no se consulta por campo y la gestión de proyectos la copiará. Se
  mapea con convertidor y comparador JSON como `ShopifyDbContext` (`JsonConverterFor<T>`/`JsonComparerFor<T>`): son
  records con listas adentro. La moneda y el total no se guardan: son la del documento y un derivado.
- `quotations.quotations.project_pricing_mode text null` **(nuevo)**, CHECK `IN ('Global','PerPhase')`, espejo de
  `ProjectScope.PricingMode` que escribe el agregado: la fila de la lista lo lee sin abrir el `jsonb` (decisión del
  diseño, revisable por el owner).
- Sin índice nuevo por `kind`: dos valores, y las listas ya filtran por tenant y fecha (decisión del diseño).

### Reglas del dominio

- `Quotation.Create(…, QuotationKind kind = Standard)`: `kind` fijado al nacer; ningún método lo cambia.
- `Quotation.ReplaceProjectScope(scope, service, updatedBy, occurredAt)`: redondea cada precio con la regla del
  documento (`PriceRounding`; P8) y deja **una** línea: si cambió el servicio, quita la vieja y agrega la nueva
  (`AddItemCore` con cantidad 1, precio `total`, descuento 0, tasa del producto); si no, cambia el precio de la línea
  (`QuotationItem.ChangeUnitPrice`, nuevo, gemelo de `OrderItem.ChangeUnitPrice`). Sin servicio, sin líneas; sin total,
  precio 0. La aplicación baja antes la regla vigente del tenant (`Quotation.UsePriceRounding`), así los precios de
  fase y la línea se redondean con la misma.
- En `Project` no corren las escalas, el piso, el grupo ni la compra mínima: `QuotationPricingRecalculation` sale
  después de `UsePriceRounding` y `OrderPricingRecalculation` sale antes de todo (P4: el pedido no cambia de precio).
- `AddItem`, `UpdateItemQuantity`, `RemoveItem` públicos rechazan en `Project` con `quotation.project.items_not_allowed`.
- Cambio de moneda: `QuotationEditsApplication.ApplyAsync` (líneas 80-91) pide al catálogo los precios de todas las
  líneas **antes** de `UpdateDetails`, y `Quotation.ApplyBillingAccount` exige ese repricing
  (`quotation.quotation.repricing_incomplete`). En `Project` no se pide nada al catálogo: el agregado deja la línea en 0
  y los precios del alcance en `null` al cambiar de moneda. El bloque `project` se aplica **después** de
  `UpdateDetails`, cuando la moneda resultante ya se conoce, con la regla de `pricesCurrency` (§6.5 y §12-A). La moneda
  resultante es la que ya decide el código: la de la cuenta si trae banco y número; sin cuenta o con una empresa sin
  cuenta, la del documento al editar (`Quotation.CurrencyFor`) y la por defecto del tenant al crear
  (`QuotationDraftBuilder`, `ITenantDefaultCurrency`; `QuotationBillingAccountResolver` lo documenta).
- Servicio: se consulta el catálogo (`IQuotationProductPricingLookup.FindVariantAsync`) sólo si `serviceVariantId`
  cambió (o al crear). Hoy ninguna ruta revalida la variante de una línea que no cambia: `QuotationItemEdits` sólo
  resuelve altas y cambios de cantidad, y enviar y convertir (`SendQuotation`, `MarkQuotationSent`,
  `ConvertQuotationToOrder`) no llaman `QuotationProductPricingResolver`; la foto del nombre
  (`QuotationItemProductLabel.ResolveMissingAsync`) tolera una variante ausente. Un servicio que se desactivó después no
  bloquea nada (decisión del diseño, revisable por el owner).
- Borrar el producto del servicio (`DeleteProduct` → `QuotationVariantDeletion`) no le quita la línea a un borrador
  `Project` ni lo reprecia: se congela su foto como la de una cotización enviada (decisión del diseño, revisable por el
  owner). Hoy le quitaría la única línea y correría `QuotationPricingRecalculation`.
- `RequiresShipping` es `false` en todo documento `Project` (hoy sería `true` en un borrador sin servicio, porque
  «sin líneas» cuenta como físico); la regla SQL de las listas (`FindIdsRequiringShippingAsync` de los dos
  repositorios) dice lo mismo.
- `Order.FromQuotation` copia `Kind` y `ProjectScope`; su bucle de líneas ya copia `UnitPrice` y descuento tal cual.
- `Order` `Project`: `AddItem`, `AddGift`, `UpdateItemQuantity`, `ChangeItemUnitPrice` y `RemoveItem` rechazan con
  `order.project.items_locked`. La edición (`SaveOrderEdits`, `PreviewOrderEdits`) ignora `isRetail`,
  `globalScaleFloor` y `shipping` del cuerpo (§12-C).
- Enviar y convertir: además de las reglas de hoy, `Project` exige servicio y total (`quotation.project.incomplete`).
  En `EnsureConvertibleToOrder` va antes de `quotation.quotation.items_required`, o un proyecto sin servicio diría
  «sin productos». `CanBeConvertedToOrder` lo incluye.

## 6. Contrato HTTP (idéntico en los dos specs)

Prefijo: `/api/v1/tenants/{tenantId}`. «Ausente» = la propiedad no está en el JSON. Todo lo marcado **(nuevo)** no
existe hoy. Los permisos son los de hoy (`quotations.quotation.read|manage`, `quotations.order.read|manage`); lo
nuevo es el chequeo de módulo `projects` (P1, aprobada: sin permisos propios).

### 6.1 Módulo

- Clave `projects` **(nuevo)**, nombre visible «Gestión de proyectos». Depende de `quotations` y `orders` (supuesto
  del owner). Fuera de los módulos por defecto de un tenant nuevo. Aparece en `GET …/modules` y en la consola de
  operador como los demás.
- Con `projects` apagado (`TenantModuleGuard.EnsureEnabledAsync`, `403 tenancy.module_not_enabled`) se bloquea
  (P9, aprobada):
  - toda operación sobre una cotización `Project`: leer, listar con `kind=Project`, guardar, preview, PDF, enviar,
    marcar enviada, anular y convertir;
  - crear una cotización `Project`.
- Con `projects` apagado **no** se bloquean los pedidos `Project` que ya existen: se ven, se listan, se cobran, se
  facturan, van a Siigo, se envían, se descarga su PDF y se editan con los mismos candados de §6.6.

### 6.2 Tipo de documento

`kind`: `"Standard" | "Project"` **(nuevo)** en la cotización y en el pedido. Se fija al crear la cotización y no
cambia; el pedido lo copia al convertir. Un backend viejo no lo manda: la pantalla lee `Standard`.

### 6.3 Alcance y precio del proyecto (`project`)

Objeto **(nuevo)**, presente sólo en documentos `Project` (`null` en `Standard`):

```json
"project": {
  "serviceVariantId": "uuid | null",
  "pricingMode": "Global | PerPhase",
  "globalPrice": 12000000,
  "phases": [
    { "id": "uuid", "name": "Descubrimiento", "price": null,
      "items": [ { "id": "uuid", "text": "Talleres con el equipo de ventas" } ] }
  ],
  "items": [ { "id": "uuid", "text": "Soporte durante el lanzamiento" } ],
  "pricesCurrency": "COP",
  "total": 12000000
}
```

- `pricesCurrency` **(nuevo)** (decisión del diseño, revisable por el owner): código de la moneda en que la pantalla
  escribió `globalPrice` y `phases[].price`. Obligatorio en el cuerpo si algún precio trae valor (si no,
  `422 validation.failed`, `errors.Project.PricesCurrency`). En la respuesta es la moneda del documento.

- `serviceVariantId`: el servicio del catálogo que aporta IVA, retenciones y el producto de Siigo (supuesto del
  owner). **Se valida sólo cuando cambia** (o al crear): variante existente, activa, de un producto `Service`; si no:
  `422 quotation.project.service_invalid` **(nuevo)**. Un documento cuyo servicio guardado quedó inactivo después se
  sigue editando, enviando y convirtiendo con su línea congelada, igual que hoy una línea cuya variante se desactivó
  (decisión del diseño, revisable por el owner). **No** exige precio de catálogo en la moneda ni escalas: el precio lo
  escribe el asesor (P2, aprobada). `null` sólo en borrador.
- `items` sueltos y `phases[].items`: texto libre, sin precio propio. `id` de ítem y de fase: uuid que genera la
  pantalla, único entre todos los ids del documento; el backend nunca lo regenera.
- `pricingMode`:
  - `Global`: precio en `globalPrice` (o `null` en borrador); `phases[].price` debe ser `null`. Admite fases e ítems
    sueltos a la vez.
  - `PerPhase`: al menos una fase, cada `phases[].price` con valor o `null` en borrador, `globalPrice` `null` y sin
    ítems sueltos.
  - Sin fases sólo vale `Global`.
- Precios con IVA incluido, `>= 0`, redondeados al guardar con la regla de redondeo del documento (cada precio de
  fase por separado, para que la suma cierre).
- `total` lo calcula el backend (`globalPrice` o la suma de los precios de fase); nunca se suma en el navegador.
  `null` mientras falte algún precio.
- Validación: `422 validation.failed` con `errors` en `Project.ServiceVariantId`, `Project.PricingMode`,
  `Project.GlobalPrice`, `Project.Phases[i].Name`, `Project.Phases[i].Price`, `Project.Phases[i].Items[j].Text`,
  `Project.Items[j].Text`, `…Id`. Nombre de fase 1 a 200 caracteres; texto de ítem 1 a 2000; hasta 30 fases y 300
  ítems por documento (P6, aprobados).

### 6.4 Línea de facturación (decisión del diseño, revisable por el owner)

Un documento `Project` tiene **una sola línea**, la del servicio elegido: cantidad 1, precio unitario = `total`,
descuento 0, `kind`/`salesUnit` congelados como hoy. La arma el backend: la pantalla no la manda ni la edita. Sin
servicio elegido o con `total` `null`, la línea no existe o vale 0 y el documento no se puede enviar ni convertir.
La regla de «una línea por variante» (`*.item.duplicate_product`) no cambia. Las líneas se responden igual que hoy
(`items` de la cotización y del pedido). `requiresShipping` es `false` en todo documento `Project`: cotización, pedido
y filas de las dos listas.

### 6.5 Cotización

- `POST /quotations` y `POST /quotations/preview`: `kind?` (ausente = `Standard`) y `project?`. Con `Project`,
  `items` ausente o vacío; con líneas: `422 quotation.project.items_not_allowed` **(nuevo)**.
- `PUT /quotations/{quotationId}` y `POST /quotations/{quotationId}/preview`: `project?` reemplaza el bloque entero;
  ausente conserva. `items` igual que arriba. `project` en una cotización `Standard`: `422 validation.failed`.
- En `Project` se ignoran y se guardan neutros: `globalScaleFloor` (`null`), `isRetail` (`false`), `shipping`
  (sin envío). No aplican escalas, piso global, compra mínima, regalos, flete ni entrega.
- **Moneda de los precios** (decisión del diseño, revisable por el owner). El backend calcula la moneda resultante del
  cuerpo (la de su cuenta de cobro, o la por defecto del tenant sin cuenta) y la compara con `pricesCurrency`:
  - **iguales**: aplica los precios, aunque el mismo cuerpo cambie la cuenta y la moneda (cambio en un solo paso);
  - **distintas**: descarta los precios del cuerpo; si además la moneda del documento cambió, los guardados quedan en
    `null` (no hay forma honesta de convertirlos). La respuesta trae `pricesCurrency` y `total: null`, y la pantalla
    pide escribirlos de nuevo.
  - Vale igual en `POST /quotations`, `POST /quotations/preview`, `PUT /quotations/{quotationId}` y
    `POST /quotations/{quotationId}/preview`; en un documento nuevo no hace falta moneda guardada.
  - **Pestaña vieja**: el `PUT` lleva `If-Match` (`SaveQuotation`, `ExpectedVersion`): si otra pestaña cambió la
    cuenta, el guardado responde 412 antes de mirar precios. La vista previa no tiene versión: calcula con lo que
    manda la pestaña y aplica la misma regla.
- `POST /quotations/{quotationId}/items`, `…/items/batch` y `PUT …/items/{itemId}` sobre una `Project`:
  `422 quotation.project.items_not_allowed`.
- Enviar (WhatsApp, correo, marcar enviada) y convertir exigen servicio y `total`: `422 quotation.project.incomplete`
  **(nuevo)**.
- Detalle (`GET /quotations/{quotationId}`) y vista previa responden `kind` y `project`. Listado: cada fila trae
  `kind` y `pricingMode` (`"Global" | "PerPhase"`, `null` en `Standard`) **(nuevos)** (decisión del diseño, revisable
  por el owner: la columna «Modo» de la lista de proyectos).
- `GET /quotations?kind=` **(nuevo)**: `Standard` (por defecto, así nada que hoy lista cotizaciones cambia),
  `Project` (exige el módulo) o `All` (con el módulo apagado devuelve sólo `Standard`). Otro valor:
  `422 validation.failed`, `errors.kind`. El export de cotizaciones usa el mismo filtro. El historial de cotizaciones
  de la ficha del cliente pide `All` (decisión del diseño, revisable por el owner).
- PDF de la cotización **y del pedido** (decisión del diseño, revisable por el owner): el mismo `quotation.typ`; bajo
  la línea, una sección «Alcance» con fases (y su precio en `PerPhase`) e ítems, en texto plano. Ids sin imprimir.

### 6.6 Conversión y pedido

- `POST /quotations/{quotationId}/order` no cambia de cuerpo. El pedido copia `kind`, `project` (mismos ids) y la
  línea tal cual, sin releer el catálogo.
- Detalle y listado de pedidos traen `kind` (y el detalle `project`). `GET /orders?kind=` **(nuevo)**: `All` por
  defecto (los pedidos de proyecto se cobran y facturan en la misma bandeja; P3, aprobada), `Standard` o `Project`.
  Los pedidos `Project` se listan y se abren con el módulo encendido o apagado (P9).
- `PUT /orders/{orderId}` y su preview sobre un pedido `Project`: `items` ausente o igual a la línea actual
  (variante, cantidad y precio); otra cosa: `422 order.project.items_locked` **(nuevo)**. `gifts` con valor: el mismo
  código. `POST /orders/{orderId}/items`: el mismo código. Lo demás (facturación, retenciones, comprobantes, notas,
  fecha) se edita como hoy. El alcance del pedido no se edita (P4, aprobada).
- No hay pedido de proyecto manual: `POST /orders` sigue creando `Standard`.

### 6.7 Códigos nuevos

| Código | Status | Cuándo |
| --- | --- | --- |
| `quotation.project.service_invalid` | 422 | Servicio inexistente, inactivo o que no es `Service`. |
| `quotation.project.items_not_allowed` | 422 | Líneas mandadas a mano en una cotización `Project`. |
| `quotation.project.incomplete` | 422 | Enviar o convertir sin servicio o sin precio. |
| `order.project.items_locked` | 422 | Cambiar líneas o regalos de un pedido `Project`. |

Existentes que también aplican: `tenancy.module_not_enabled` (403), `validation.failed` (422).

## 7. Permisos y módulo

- Permisos de hoy, sin nuevos (P1, aprobada): `quotations.quotation.read|manage` para la cotización y
  `quotations.order.read|manage` para el pedido.
- Guardas del módulo `projects` (`TenantModuleGuard.EnsureEnabledAsync`, P9 aprobada), después del permiso y de cargar
  el documento (un 404 sigue siendo 404), sólo si el documento es `Project`:
  - `GetQuotationHandler`, `ListQuotationHistoryHandler`, `SaveQuotationHandler`, `PreviewQuotationHandler`,
    `ChangeQuotationClientHandler` (`PUT …/client`), `AddQuotationItemHandler`, `BatchUpdateQuotationItemsHandler`,
    `UpdateQuotationItemHandler`, `RemoveQuotationItemHandler` (`DELETE …/items/{itemId}`), `SendQuotationHandler`,
    `MarkQuotationSentHandler`, `ExportQuotationPdfHandler`, `VoidQuotationHandler`,
    `ConvertQuotationToOrderHandler` y `PreviewQuotationConversionHandler` (`GET …/order/preview`);
  - `CreateQuotationHandler` y `PreviewNewQuotationHandler` con `kind=Project`;
  - `ListQuotationsHandler` y `ExportQuotationsHandler` con `kind=Project` (con `All` y el módulo apagado filtran
    `Standard`).
- **Ningún handler de pedido** exige el módulo: un pedido `Project` existente se opera siempre. `ListOrders` no filtra
  por módulo, y `GET /quotations/{quotationId}/order` (`GetOrderAsync`, que responde el detalle del pedido) tampoco.
- Consola de operador: lista `GET …/modules` (sale de `TenantModuleKeys.All`); nada que construir más allá de la
  clave. Prenderlo exige `quotations` y `orders` por `Dependencies`.

## 8. Comprobación del enfoque de una línea

| Lector | ¿Funciona? | Nota |
| --- | --- | --- |
| Cálculo (`RecalculateTotals`, `DocumentTotals`, retenciones por alcance) | Sí | Una línea `Service` con su tasa; la retención de servicios aplica sobre su subtotal. |
| Conversión (`Order.FromQuotation`) | Sí | Copia precio y descuento; hay que saltar el recálculo del pedido (§5). |
| Pantallas de pedido | Sí, con candado | Una línea de solo lectura; sin agregar productos ni regalos. |
| PDF (`quotation.typ`, `QuotationPdfDocumentMapper.From` y `.FromOrder`) | Sí | La línea sale como hoy; se agrega la sección «Alcance» en los dos (`QuotationPdfDocument` la lleva). El PDF guardado se regenera por `Version`, que el alcance sube. |
| Lista de cotizaciones (`IQuotationRepository.SearchAsync`: `ListQuotations` y copiloto; `ListForExportAsync`: `QuotationsExportProcessor`) | Sí | Con `kind` por defecto `Standard`, `/quotes` y su export no cambian; el historial del cliente pide `All`. El copiloto, P7. |
| Siigo (`SiigoOrderSource` → `ExternalOrderInvoicingService.GetForInvoicingAsync`, tandas) | Sí | Una línea con `VariantId`, nombre, cantidad 1 y subtotal; sin flete. La variante del servicio necesita su vínculo con Siigo (`SiigoMissing.VariantUnlinked`). El alcance no viaja. |
| Excel de pedidos, reporte de pedidos | Sí | Una línea más. |
| Reporte de márgenes (`MarginReportSource`) | Sí, con salvedad | El costo es el del servicio en el catálogo congelado al pagar (`CatalogVariantCostLookup`); sin costo cargado, la línea cuenta «sin costo». |
| Reporte de cotizaciones (`QuotationsReportSource`, SQL crudo) | Sí, mezcla | Cuenta las dos clases; P5. |
| Copiloto (`CopilotDocumentTools` → `ListQuotationsQuery`, `GetQuotationQuery`) | Sí | Con `kind` por defecto `Standard` no lista proyectos; el detalle sí (con módulo). P7. La respuesta sigue trayendo `minimumPurchase` y pisos, que no aplican a un proyecto. |
| Pipeline (`QuotationEvents`, `pipeline.deal_quotations`) | Sí | El negocio enlaza por id; la pantalla redirige a `/projects/quotes/…`. Los payloads no cambian. |
| Borrado de producto (`QuotationVariantDeletion`, `DeletedVariantLineStore`) | Con cambio | Hoy le quita la línea a un borrador y lo reprecia; en `Project`, congela la foto (§5). |
| Shopify, tienda, comprador, mayorista (`IExternalOrderWriter`, `IStoreOrderGateway`, `IBuyerOrderGateway`, `IWholesaleOrderGateway`) | No aplica | Crean o tocan pedidos de su propio `Source`; un pedido `Project` siempre es `Source = Quotation`. |
| WMS, POS | No aplica | No leen pedidos. |
| `ExportLoadSeeder` y pruebas con `INSERT` crudo | Sí | Nombran columnas; el default de la base llena `kind`. |
| API externa (`/api/v1/external`) | No aplica | Hoy sólo expone WhatsApp. |
| Borrador del agente de IA | No aplica | Crea `Standard`. |
| Referencias de cliente (borrado, fusión) | Sí | Cuentan cotizaciones y pedidos de toda clase. |

## 9. Pruebas

TDD con RED y GREEN literales (RED por compilación con la salida del build); integración filtrada por nombre
mientras se itera; para mergear, `.\scripts\test-feature.ps1` en verde leyendo sus avisos; nunca dos corridas a la vez.

- Unitarias: `ProjectScope` (modos, ids únicos, límites, redondeo por fase, total); la línea única (cambio de
  servicio, precio, sin servicio); recálculos saltados; cambio de moneda; candados de líneas en cotización y pedido;
  `FromQuotation` copia kind y alcance con los mismos ids; `RequiresShipping` en `Project`.
- Integración: módulo apagado → 403 en cotizaciones `Project`, y los pedidos `Project` siguen operando (detalle,
  pago, facturación, PDF, lista); listas con `kind` por defecto sin cambios y `pricingMode` en la fila; crear,
  guardar y preview con cambio de cuenta y `pricesCurrency` igual (aplica) o distinto (descarta); 412 de pestaña
  vieja; servicio desactivado después no bloquea guardar, enviar ni convertir; enviar incompleto → 422; convertir;
  editar el pedido (candado); PDF de cotización y de pedido con alcance; migración del `CHECK` y de las columnas.
- `TenantModuleKeys` (orden topológico, dependencias) en `Modules.Tenancy.UnitTests`.

## 10. Puntos abiertos — aprobados por el owner (2026-11-26)

Los nueve se aprobaron como se propusieron; no se reabren.

- **P1. Permisos propios** (`projects.quotation.read|manage`). **Aprobado: no por ahora**; el módulo ya oculta todo y
  el asesor que cotiza proyectos es el mismo que cotiza. Si después se quiere separar, se agregan con
  `RequiredModules: [Projects]`.
- **P2. Servicio sin precio de catálogo.** **Aprobado: se permite** (el precio lo escribe el asesor). Exige servicio
  activo y `Service`.
- **P3. Pedidos de proyecto en `/orders`.** **Aprobado: sí**, con insignia y filtro «Tipo»: allí se cobran, facturan y
  van a Siigo. Hasta que exista `/projects`.
- **P4. Editar precio o alcance de un pedido de proyecto.** **Aprobado: no**; para cambiar el alcance vendido se anula
  y se cotiza de nuevo.
- **P5. Reportes.** **Aprobado**: el de cotizaciones y los de pedidos siguen contando todo (son ventas); un filtro por
  tipo, en otra entrega.
- **P6. Límites.** **Aprobado**: 30 fases, 300 ítems, 200 caracteres el nombre de fase, 2000 el texto del ítem.
- **P7. Copiloto.** **Aprobado: sin cambios** en esta entrega (no lista proyectos).
- **P8. Precio por fase con redondeo.** **Aprobado**: cada precio de fase se redondea con la regla del documento, para
  que la suma que se cobra coincida con la que se lee en el PDF.
- **P9. Apagar `projects` con documentos existentes.** **Aprobado**: los pedidos `Project` siguen operando (ver, cobrar,
  facturar, Siigo, enviar, PDF, editar con candados) y siguen en `/orders`; las cotizaciones `Project` y crear o
  convertir documentos nuevos quedan bloqueados (403 `tenancy.module_not_enabled`).

## 11. Lo que queda al builder

- Nombres de migraciones y la prueba de mapeo del `jsonb`.
- El texto del historial («Alcance actualizado», «Precio del proyecto actualizado»).
- Actualizar `docs/estado.md` en `develop` al mergear (la rama de feature no lo toca).

## 12. Cambios de contrato que el código obliga (pendientes de llevar a los dos specs)

§6 quedó como en el frontend salvo los estados de P1–P9 y la frase de `requiresShipping` de §6.4 (punto 5 del owner).
Lo que sigue cambia o precisa §6 y necesita el visto bueno del owner antes de copiarlo a los dos specs. El plan
implementa la versión «después».

- **A. Moneda resultante (§6.5).** Antes: «la de su cuenta de cobro, o la por defecto del tenant sin cuenta». Después:
  «la de su cuenta de cobro si trae banco y número; sin cuenta, o con una empresa sin cuenta, la moneda actual del
  documento (en `POST /quotations` y `POST /quotations/preview`, la por defecto del tenant)». Motivo:
  `Quotation.CurrencyFor` y `QuotationBillingAccountResolver`; quitar la cuenta nunca devolvió la cotización a la
  moneda del tenant. **El frontend tiene que corregirse** (`0e61d4a9`, «reprice against the tenant default when the
  account goes away»): al editar, sin cuenta, la moneda es la del documento.
- **B. Dónde viajan `kind` y `project` del pedido (§6.6).** Antes: «Detalle y listado de pedidos traen `kind` (y el
  detalle `project`)». Después: «… traen `kind`; en el detalle, `kind` y `project` van dentro de `order`
  (`OrderResponse`, como `requiresShipping`), así que también los traen `/approve`, `/cancel`… y la vista previa de la
  conversión». Ya es lo que lee el frontend.
- **C. Edición del pedido `Project` (§6.6).** Antes: «`items` ausente o igual a la línea actual (variante, cantidad y
  precio) … `gifts` con valor: el mismo código». Después: «`items` ausente, vacío o con exactamente la línea actual
  (misma variante y cantidad; `unitPrice` ausente o igual al guardado) … `gifts` con algún elemento: el mismo código;
  ausente o `[]` no cambian nada. Se ignoran `isRetail`, `globalScaleFloor` y `shipping` del cuerpo». Motivo: el
  endpoint ya convierte `items` ausente en lista vacía (`OrderEndpoints.ToEdits`), y la pantalla manda `items` sin
  `unitPrice` y el envío guardado. Compatible con el frontend.
- **D. Export de pedidos (§6.6).** Agregar: «`POST /orders/export` acepta el mismo `kind`. Otro valor, en las dos
  rutas: `422 validation.failed`, `errors.kind`». El frontend ya lo manda (`orderFilterParams`).
- **E. Claves de los topes (§6.3).** Agregar: «El tope de 300 ítems (los de las fases más los sueltos) sale en
  `Project.Items`, también en `PerPhase`; el de 30 fases, en `Project.Phases`. Los ids, en `Project.Phases[i].Id`,
  `Project.Phases[i].Items[j].Id` y `Project.Items[j].Id`». Es lo que pinta el frontend.
- **F. Vista previa con textos vacíos (§6.3).** Agregar: «La vista previa valida igual que el guardado; la pantalla
  manda `-` en un nombre o texto todavía vacío y el backend lo trata como cualquier texto». Compatible.
- **G. `kind` y `project` en el alta (§6.5).** Agregar: «`kind` con otro valor: `422 validation.failed`,
  `errors.Kind`. `project` sin `kind: "Project"`: `422 validation.failed`, `errors.Project`. `kind: "Project"` sin
  `project` nace con un alcance vacío (`Global`, sin servicio)».
- **H. Precios descartados sin cambio de moneda (§6.5).** Precisar: «distintas y la moneda del documento no cambió: se
  conservan los precios guardados (`globalPrice` y el de cada fase por su `id`; una fase nueva queda en `null`), y
  `total` es `null` sólo si falta alguno». Hoy el texto dice a la vez «se conservan» y «la respuesta trae
  `total: null`».

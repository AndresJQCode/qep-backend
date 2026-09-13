# Exportación asíncrona de cotizaciones y ventas por correo

**Fecha:** 2026-09-12
**Módulos:** Quotations (cotizaciones y ventas), Notifications (backend) — listas de cotizaciones
y ventas (frontend)
**Estado:** aprobado, con plan de implementación (revisado el 2026-09-12: cuatro intentos, keyset,
lifecycle de R2 existente)

## Problema

Las listas de cotizaciones y de ventas no tienen cómo exportarse. El único export que había, el
de reportes, lo quitó el frontend en `99771b9`. Se quiere un botón como el de clientes: se pide
el export y el archivo llega por correo.

El export de clientes **no es asíncrono de verdad**. `ExportCustomersHandler` lee todas las
filas, arma el Excel en memoria y lo sube a Storage **dentro del request**
(`ExportCustomers.cs:76-83`); lo único que corre en segundo plano es el correo
(`CustomerExportDeliveryWorker`). Catalog hace lo mismo (`ExportProducts.cs:63-106`). Por eso los
dos llevan tope de filas (50.000 y 20.000): el propio código lo dice, *"sin limite un tenant grande
tumba el proceso"*.

El trabajo pesado en el request es más caro de lo que parece: producción corre **una réplica con
límite de 1Gi** (`k8s/prod-deployment.yaml:11,69-75`), así que un export que se come la memoria
no falla sólo él: tumba la API para todos.

`572200c` implementó un `GET /quotations/export` que devolvía el archivo en la respuesta. Este
diseño lo reemplaza (ver [Qué pasa con 572200c](#qué-pasa-con-572200c)).

## Decisiones

Todas tomadas con el developer durante el brainstorming del 2026-09-12.

### D1 — Asíncrono de verdad: el request encola, un worker procesa

El request valida y guarda una solicitud; responde `202` en milisegundos. Un worker lee, arma el
Excel, lo sube y dispara el correo. Nada pesado queda atado a un request HTTP.

### D2 — La cola es una tabla en Postgres, no un broker

`quotations.export_jobs`, detrás del puerto `IExportJobQueue` (Application) con adaptador en
Infrastructure. Motivos para no usar Google Pub/Sub ni Azure Service Bus:

- **Doble escritura.** Guardar la solicitud y publicar al broker son dos sistemas sin
  transacción común; la solución estándar es un outbox en Postgres que después publica. El broker
  se suma a la tabla, no la reemplaza.
- **Estado.** El broker entrega mensajes; no dice que un export quedó `Failed` al cuarto intento.
  Soporte y reintentos necesitan la tabla igual.
- **Infraestructura y volumen.** Monolito modular, una réplica, Postgres ya presente, pocas
  exportaciones por día. Otra nube suma credenciales, red, costo y otro secreto que cuidar.
- **Pruebas.** Testcontainers ya levanta Postgres; un broker exige emulador o credenciales en CI.

El puerto deja la puerta abierta: cambiar a un broker es cambiar el adaptador, no el caso de uso.

Tampoco se usa el outbox existente como cola: guarda **hechos** para otros módulos y no tiene
estado, reintentos ni toma exclusiva — el inbox marca procesado aunque falle.

Descartado también Hangfire: resuelve lo mismo con dashboard, pero este repo hace su propia
infraestructura (CQ, outbox, inbox) y sumar su esquema y su serialización por dos exports no se
paga. Revisarlo si aparecen muchos procesos en segundo plano.

### D3 — Rango de fechas obligatorio de hasta un año, sin tope de filas

El volumen lo acota el rango, no un tope: un tope castiga a quien más vende y no dice qué filtro
tocar. Regla: ambas fechas obligatorias, `from <= to` y `to <= from.AddYears(1)` (un año exacto
vale; `AddYears` resuelve los bisiestos: 2024-02-29 + 1 año = 2025-02-28).

| Export     | Campos del rango                  |
| ---------- | --------------------------------- |
| Cotizaciones | `createdFrom`, `createdTo`      |
| Ventas     | `convertedFrom`, `convertedTo`    |

El frontend replica la regla para deshabilitar el botón; la autoridad es el validador del backend.

### D4 — Lo que se valida en el request

En este orden, y todo antes de encolar:

1. Tenant y permiso → `403` (nunca `404`, regla de la casa).
2. Filtros y rango → `422 validation.failed` con el mapa `errors` (validador de FluentValidation).
3. Que exista al menos una fila (`EXISTS`, barato) → `422 quotation.export.empty` /
   `sale.export.empty`. Enterarse de que no había nada después de esperar un correo es peor.
4. Límite de **3 exportaciones pendientes** (`Pending` + `Processing`) por usuario, contando los
   dos tipos → `422 quotation.export.pending_limit` / `sale.export.pending_limit`. Frena el doble
   clic y el abuso.

Los prefijos de los códigos siguen la convención del módulo; el plan los confirma contra los
códigos existentes de ventas.

### D5 — Contrato

Mismo patrón que `customers/export`: `POST` porque tiene efecto, filtros por query string igual
que el listado.

| Endpoint | Filtros (los del listado, sin paginación) | Permiso |
| --- | --- | --- |
| `POST /api/v1/tenants/{tenantId}/quotations/export` | `clientId, advisorId, status, createdFrom, createdTo, clientNit, quotationNumber` | `QuotationRead` |
| `POST /api/v1/tenants/{tenantId}/sales/export` | `clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo, clientCuc, saleNumber` | `SaleRead` |

Respuesta `202 { jobId, requestedAt }`. No lleva nombre de archivo ni cantidad de filas: todavía
no existen. Tampoco el enlace: el canal de entrega es el correo, igual que en clientes.

### D6 — Toma exclusiva con `SKIP LOCKED` y lease

Un solo `UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING *` toma un
`Pending` con `next_attempt_at` ya cumplido **o** un `Processing` con `locked_until` vencido
(worker muerto), suma un intento y fija un lease de 10 minutos. Las dos comparaciones van contra el
reloj de la aplicación (`IClock`), un solo reloj en el proceso, que el adaptador recibe como
parámetro. SQL crudo en el adaptador.

Hoy hay una réplica; `SKIP LOCKED` hace que escalar no genere el mismo export dos veces. Esa
garantía vale para los jobs que terminan dentro del lease de 10 minutos: no hay heartbeat, así que
uno que tarda más puede ser retomado por otro worker. Antes de pasar a más de una réplica hace falta
un heartbeat del lease.

### D7 — Un worker, un job a la vez

`ExportJobWorker : BackgroundService`, mismo esqueleto que los workers de Notifications
(`PeriodicTimer` de 5 s, scope por tick, una falla se loguea y no mata el loop). **Concurrencia
1 por proceso, a propósito:** protege el 1Gi que comparte con la API. Despacha por `kind` a un
procesador por tipo (`QuotationsExportProcessor`, `SalesExportProcessor`).

### D8 — El Excel se escribe en streaming

Con `OpenXmlWriter` sobre un zip propio en modo `Create` (`System.IO.Compression.ZipArchive`) que
escribe a un archivo temporal, leyendo **por lotes de 1.000 con keyset** y los mismos filtros del
listado (`FilteredQuery` en cotizaciones, el filtro de `SaleRepository` en ventas). No con
ClosedXML: ClosedXML mantiene cada celda como objeto en memoria, y con un año de un tenant grande
eso son cientos de MB dentro del pod de 1Gi (orden de magnitud, no medido). Tampoco con
`SpreadsheetDocument.Create`: abre el zip en modo update, que guarda cada parte sin comprimir en
memoria hasta el `Dispose` (se midió cerca de 1 KB por fila). En modo `Create` cada fila sale
comprimida al temporal apenas se escribe, así que la memoria queda acotada al lote. Las partes
chicas (`[Content_Types].xml`, relaciones, `workbook.xml`, `styles.xml`) se escriben completas
primero y la hoja va última, abierta hasta el final.

**Keyset y no offset.** Cada lote pide lo que viene después de la última fila leída, en el orden
del listado y con el número como desempate —único por tenant, así que la clave nunca empata—:

| Export | Orden (los dos descendentes) | Lote siguiente | Índice nuevo |
| --- | --- | --- | --- |
| Cotizaciones | `created_at`, `quotation_number` | `(created_at, quotation_number) < (@fecha, @numero)` | `(tenant_id, created_at, quotation_number)` |
| Ventas | `converted_at`, `sale_number` —el mismo del listado— | `(converted_at, sale_number) < (@fecha, @numero)` | `(tenant_id, converted_at, sale_number)` |

El corte es una comparación de filas de Postgres, que Npgsql traduce desde
`EF.Functions.LessThan(ValueTuple.Create(…), ValueTuple.Create(…))`. No la forma OR
(`created_at < @fecha OR (created_at = @fecha AND …)`): con ella el índice no tiene un límite de
rango y cada lote vuelve a recorrer desde el tope del período. La comparación de filas es un rango
que el btree resuelve, y el número se compara con la collation de la columna, la misma del ORDER BY.

Offset se descartó por exactitud, no sólo por rendimiento. El rango por defecto es el mes en curso
con hoy adentro, así que el export corre mientras la gente sigue trabajando: una fila creada
durante el export corre las páginas y repite la del borde, y una que sale del filtro hace
desaparecer otra. Quien suma la columna Total recibe un número equivocado sin ninguna señal. Y
offset además lee y descarta todas las filas que salta, así que cada lote cuesta más que el
anterior. Con keyset, las filas que existían al empezar salen una vez cada una. El desempate es el
número y no el id porque `QuotationId` y `SaleId` son value objects sin comparación.

`DocumentFormat.OpenXml` 3.1.1 ya se resuelve hoy como dependencia de ClosedXML; pasa a
referencia directa de `Modules.Quotations.Infrastructure` con la misma versión.

Misma forma de hoja que Reporting: cabecera en negrita y congelada, fechas como texto ISO-8601,
totales numéricos, encabezados sin tildes. **Anchos fijos por columna** (medir el contenido
obligaría a recorrerlo dos veces).

- **Cotizaciones:** `Numero, Fecha, Cliente, Asesor, Estado, Moneda, Total` — las de la tabla
  del listado, en su orden.
- **Ventas:** las de la tabla del listado de ventas, en su orden. El plan las fija leyendo el DTO
  del listado (`SalesPageResponse`); no se inventan acá.

Nombres: `cotizaciones-yyyy-MM-dd-HHmm.xlsx` y `ventas-yyyy-MM-dd-HHmm.xlsx`, con la hora en que
se generó.

### D9 — Subida a Storage con clave estable

Puerto `IExportFileStorage` en Application, adaptador calcado de `ICustomerExportStorage`. La
clave del objeto lleva el `jobId`: un reintento pisa el mismo objeto y no deja basura. Va **bajo
`exports/`** —`exports/tenants/{tenantId}/jobs/{jobId}.xlsx`— para que la limpie la regla de
lifecycle del bucket (D13). La URL prefirmada vence a las `Storage:ExportUrlHours` horas
(`StorageOptions.cs:15`, 24 por defecto en `appsettings.json:27`): la misma opción que ya lee
`CustomerExportStorage.cs:49`, no una nueva.

### D10 — Terminar es una sola transacción

`Completed` + evento `quotations.export-ready.v1` en el outbox + auditoría, en la misma
transacción. Nunca sale un correo de un job que no terminó, ni queda un job terminado sin correo.

Payload: `tenantId, subjectId, kind, downloadUrl, fileName, rowCount, expiresAt`.

### D11 — Reintentos y fallos

- **Transitorio** (R2, base, timeout): vuelve a `Pending` con backoff. **Cuatro intentos:** después
  del 1.º fallido espera 1 minuto, después del 2.º 5 y después del 3.º 15; el 4.º fallido →
  `Failed` + `quotations.export-failed.v1`. Así se usan las tres esperas. El archivo llega por
  correo y nadie está mirando la pantalla: una ventana de ~21 minutos que se recupera de una caída
  corta de R2 o de la base cuesta menos que un correo de fallo.
- **Definitivo** (filtros ilegibles, cero filas al procesar): `Failed` directo, sin reintentar.
- **Worker muerto a mitad:** el lease vence y el siguiente tick lo retoma (D6).

`last_error` guarda el motivo para soporte. Nunca un secreto: sólo tipo y mensaje.

### D12 — Correos por Notifications

Dos workers y dos plantillas calcados de `CustomerExportDeliveryWorker`: export listo (enlace,
nombre del archivo, cantidad de filas, vigencia) y export fallido ("No pudimos generar tu
exportación de cotizaciones. Intenta de nuevo."). El texto nombra el tipo según `kind`. Español
colombiano, tuteando (`CLAUDE.md` § Cómo se escriben los mensajes).

### D13 — Retención

El worker borra una vez al día los jobs `Completed` y `Failed` de más de 30 días. El enlace muere
con la vigencia de la URL prefirmada.

Los objetos de R2 los borra una **regla de lifecycle que ya existe** sobre el prefijo `exports/`
del bucket privado (`expire-exports`, `--expire-days 2`), configurada a mano en Cloudflare y
documentada en el README (§ Reportes exportados, `README.md:846-860`). No está pendiente: sólo hay
que verificar que sigue en el bucket (`npx wrangler r2 bucket lifecycle list <bucket-privado>`). Lo
que este diseño tiene que cumplir es subir los archivos **bajo `exports/`** (D9); fuera de ese
prefijo la regla no los ve. El README ya advierte que `--expire-days` tiene que cubrir
`ExportUrlHours` con margen: 2 días contra 24 h.

### D14 — Frontend

- Botón sólo con el ícono `Download`, igual al de clientes, en la barra del título de las dos
  listas, `aria-label` "Exportar cotizaciones" / "Exportar ventas".
- Rango inválido: `aria-disabled` con texto de ayuda **visible** ("Elige un rango de fechas de
  hasta un año."), no `disabled` nativo con `title` — el navegador no muestra ese tooltip sobre
  un botón deshabilitado y lo saca del orden de tabulación (hallazgo de la revisión).
- Al solicitar: toast "Tu exportación se enviará por correo electrónico a: {email}", mismo
  criterio que `useExportCustomers`.
- Errores `422`/`403` en toast, con el texto por código.

### D15 — Fuera de alcance

- Endpoint de estado del job y pantalla "mis exportaciones": la tabla lo permite, nadie lo pidió.
- Pasar clientes y Catalog a este modelo. Recomendado como trabajo aparte: hoy siguen armando el
  Excel dentro del request.

## Modelo de datos

`quotations.export_jobs`:

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid | PK |
| `tenant_id` | uuid | todo acceso filtra por tenant |
| `requested_by` | uuid | sujeto que pidió; a quien va el correo |
| `kind` | text | `Quotations` \| `Sales` |
| `filters` | jsonb | los filtros ya validados |
| `status` | text | `Pending` \| `Processing` \| `Completed` \| `Failed` |
| `attempts` | int | intentos consumidos |
| `next_attempt_at` | timestamptz | cuándo puede tomarse |
| `locked_until` | timestamptz null | lease |
| `last_error` | text null | motivo del último fallo |
| `file_name` | text null | al completar |
| `row_count` | int null | al completar |
| `requested_at` | timestamptz | |
| `completed_at` | timestamptz null | `Completed` o `Failed` |

Índices: parcial para tomar trabajo (`status`, `next_attempt_at`) y para el límite de pendientes
(`tenant_id`, `requested_by`, `status`). En la misma migración, los dos del keyset de D8 sobre
tablas que ya existen: `quotations (tenant_id, created_at, quotation_number)` y
`sales (tenant_id, converted_at, sale_number)`. Migración con el factory de diseño
(`CLAUDE.md` § Gotchas).

## Flujo

1. `POST …/export` → autoriza, valida, `EXISTS`, límite de pendientes → inserta `Pending` → `202`.
2. `ExportJobWorker` toma el job (D6).
3. El procesador del `kind` lee por lotes con keyset y escribe el `.xlsx` en streaming (D8).
4. Sube el archivo (D9).
5. Una transacción: `Completed` + `quotations.export-ready.v1` + auditoría (D10).
6. Notifications manda el correo con el enlace (D12).
7. Si algo falla: reintento o `Failed` + `quotations.export-failed.v1` → correo de fallo (D11).

## Qué pasa con 572200c

El export síncrono ya está publicado: `572200c` (backend) y `098dbce` (frontend) entraron a
`develop` y a `main` y están en el remoto. Mientras este diseño no se implemente, lo que se
despliegue desde `main` arma el Excel dentro del request —el riesgo de memoria de
[Problema](#problema)— y el botón del frontend usa `disabled` + `title` (ver D14).

El trabajo nuevo sale de `develop`, en `feature/export-asincrono` de cada repo, y construye
encima:

- **Se reutiliza:** `ExportQuotationsValidator` (el año), `QuotationListing`, `FilteredQuery`,
  `ListForExportAsync` (que pasa de devolver todo a devolver un lote por keyset).
- **Se va:** el `GET` que devuelve el archivo, `ClosedXmlQuotationExportBuilder` y la referencia
  a ClosedXML de Quotations (lock files regenerados en ese commit). Con el builder se va también
  el aviso de `AdjustToContents` de la revisión.

En el frontend se conservan `quoteFilterParams`, `isExportableQuoteRange` (se generaliza para
ventas) y sus pruebas; cambian `useExportQuotes` (de descarga a `POST` + toast) y el botón (D14).

## Entrega

Backend, rama `feature/export-asincrono` desde `develop`; cada commit compila y deja las pruebas
en verde:

1. `feat(quotations): cola de exportaciones con worker y reintentos` — tabla, migración (con los
   índices del keyset de D8), `IExportJobQueue`, toma exclusiva, lease, backoff, `ExportJobWorker`.
2. `feat(notifications): correos de exportación lista y fallida`.
3. `feat(quotations): exportar cotizaciones por correo` — `POST` + `202`, procesador, writer en
   streaming, `IExportFileStorage`; sale el `GET`.
4. `feat(sales): exportar ventas por correo`.

Frontend, rama `feature/export-asincrono` desde `develop`: `feat(quotes): exportar cotizaciones
por correo` y
`feat(sales): exportar ventas por correo`.

**Despliegue: backend primero** (migración y endpoints), después frontend. Al revés, el botón le
pega a un endpoint que no existe.

## Pruebas

TDD, RED antes que GREEN con evidencia literal.

- **Unitarias:** validadores (rango, bisiestos, un año exacto); handlers de solicitud (orden de
  validaciones de D4, no encola si algo falla); procesadores (mapeo de fallos a transitorio o
  definitivo); writer (cabecera, tipos de celda, orden de columnas).
- **Integración (Testcontainers):** `POST` → `202` → el worker procesa → `Completed` + evento en
  el outbox + workbook con las filas correctas; `403`, `422` de rango, vacío y límite; dos tomas
  concurrentes no se llevan el mismo job; un lease vencido se retoma; los reintentos terminan en
  `Failed` al cuarto intento con su evento; una fila creada o que sale del filtro entre dos lotes
  no se repite ni hace saltar otra (keyset); Notifications entrega los dos correos.
- **Frontend:** helper del rango, hooks (`POST` con los filtros sin paginación, toasts por
  código), botones (`aria-disabled` + texto visible).

## Riesgos y pendientes

- **Memoria medida en local, falta un export real.** La cifra de ClosedXML es un orden de
  magnitud. Con el zip en streaming (D8), una prueba local de 50.000 filas de 8 columnas dejó el
  heap en ~1 MB, contra ~48 MB con `SpreadsheetDocument.Create`. Conviene medir un export de un año
  real en staging antes de dar el tema por cerrado.
- **Sin snapshot transaccional.** El keyset (D8) evita filas repetidas o saltadas, pero cada lote
  lee el estado de ese momento: una fila que cambia antes de que le toque sale con el valor nuevo,
  o no sale si dejó el filtro. Leer todo en una transacción `REPEATABLE READ` lo cerraría a costa
  de sostenerla minutos; por ahora no se paga.
- **Se corre aunque quien lo pidió ya no tenga acceso.** El permiso y la membresía se revisan al
  pedir, no al generar: si quien pidió el export los pierde antes de que el worker lo tome, el
  export corre igual. La ventana es a lo sumo un ciclo de procesamiento más el lease, el archivo va
  al correo de quien lo pidió y lo pidió cuando tenía acceso. Se acepta.
- **Clientes y Catalog** siguen sincrónicos (D15).

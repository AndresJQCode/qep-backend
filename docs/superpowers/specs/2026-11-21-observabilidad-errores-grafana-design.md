# Errores y APIs externas en Grafana — diseño

Fecha: 2026-11-21 · Repo: `qep-backend` · Estado: **diseño aprobado por el owner (2026-11-21)**

## Intención

QCode necesita **ver en Grafana los errores de QEP**: cualquier excepción no controlada, venga de un request o de un worker, y las
fallas de las llamadas a APIs externas, empezando por Siigo. También quiere saber, antes de desplegar, si hay trabajo encolado que el
despliegue podría demorar. Hoy el tenant ya ve sus fallas de Siigo en QEP (`siigo.call_failures`) y el operador ve las escrituras que
fallan (`platform.request_failures`), pero nada de eso llega a Grafana. Una excepción de un worker no queda en ninguna tabla.

El owner decidió (2026-11-21): **Grafana, no una consola propia dentro de QEP.** La vista por tenant sigue en el producto.

## Metas

- Contar en Prometheus **todo log `Error` o `Critical`** de QEP, por categoría y tipo de excepción. Eso cubre los requests, los 55
  `BackgroundService` y cualquier otro camino, sin tocar cada worker.
- Contar en Prometheus **cada falla de una llamada a Siigo**, por operación y motivo.
- Dejar listas las métricas para el dashboard y las alertas, que viven en `migracion-k8s`.

## No metas y antimetas

- **El tenant no es etiqueta de ninguna métrica.** Infla la cardinalidad. El tenant se busca en Loki (los logs llevan `TraceId`) o en
  `siigo.call_failures` / `platform.request_failures`.
- **No se cuentan llamadas exitosas a Siigo.** El volumen y la latencia ya salen de `http.client.request.duration` con
  `server.address` (instrumentación de `HttpClient` en `QepObservability`). Hay que confirmarlo en Prometheus (ver Pendientes).
- **No cambia `siigo.call_failures` ni `platform.request_failures`.** Son el diagnóstico con detalle; esto es el conteo.
- **No hay pantalla nueva en QEP.** El dashboard y las alertas van en `migracion-k8s`, con su propio documento.
- Antimeta: que una etiqueta lleve un dato del cliente, un id de tenant, una ruta con su query o el mensaje de una excepción.
  Los mensajes de Siigo repiten documento, dirección y nombre del cliente.

## Restricciones

- Todo meter propio lleva el prefijo `Qep.`: `QepObservability.MeterWildcard` (`Qep.*`) lo exporta por OTLP sin tocar la lista.
- `Modules.Siigo.Application` no referencia EF ni la infraestructura de observabilidad. Las métricas del módulo viven en Infrastructure,
  como `ShopifyMetrics` (`IMeterFactory`, meter `Qep.Shopify`).
- El cliente HTTP `siigo` no tiene loggers (`RemoveAllLoggers`) y `SiigoNoLeakTests` exige cero líneas en su categoría. Este trabajo no
  agrega logs: agrega un contador.
- `AddQepLogging` limpia los proveedores y deja solo la consola JSON. Un proveedor nuevo no puede cambiar lo que se escribe a stdout.
- TDD obligatorio, RED antes que GREEN. Sin migraciones.

## Enfoques

1. **Un contador por worker** (en el `catch` de cada `ExecuteAsync`). Exacto, pero son 55 archivos, sin base común, y el próximo worker
   lo olvida. Descartado.
2. **Contar los logs `Error`/`Critical` con un `ILoggerProvider` (elegido).** Todo error ya se loguea: `ApiExceptionHandler`
   (`EventId 5000`), el host cuando un `BackgroundService` se cae, y cada worker en su `catch`. Un proveedor que solo cuenta los ve
   todos, sin tocar a nadie. El costo: también cuenta errores que el código maneja y loguea en `Error`. Eso es deseable para vigilar, y
   la etiqueta `exception_type` separa los que traen excepción.
3. **Contar en Loki** (reglas de grabación sobre los logs JSON). Sin código en QEP, pero depende de los nombres de campo de Alloy y de
   que el log siga en JSON. Queda como respaldo para buscar el detalle, no como fuente de la métrica.

Para Siigo, el contador va en `SiigoApiClient.RecordAsync`: es el único punto por el que pasa toda falla, y ya conoce la operación
y el motivo.

## Diseño

### 1. Contador de errores logueados

- Proveedor `ErrorCountingLoggerProvider` en `BuildingBlocks.Observability`, registrado en `AddQepLogging` junto a la consola JSON.
- Sus loggers solo actúan con `LogLevel.Error` y `LogLevel.Critical`. `IsEnabled` devuelve `false` para el resto, y `Log` no formatea
  el mensaje ni lee el estado.
- Instrumento: `Counter<long>` `qep.log.errors` sobre `QepObservability.Meter` (`Qep.Platform`), unidad `{entry}`. En Prometheus:
  `qep_log_errors_total`.
- Etiquetas:
  - `level`: `Error` o `Critical`.
  - `category`: la categoría del logger (el nombre completo del tipo). Su número está acotado por los tipos del código.
  - `exception_type`: `exception.GetType().FullName`, o `none` si el log no trae excepción. Nunca el mensaje.
- **No implementa `ISupportExternalScope`** (decisión del builder): no lee scopes, y así `AddSensitivePathRedaction`, que reenvuelve a
  los proveedores que lo implementan, lo deja tal cual. Una prueba fija que el orden de registro frente a la redacción no cambia el conteo.
- Las fallas de Siigo y Meta que `ApiExceptionHandler` mapea a 502/503 se loguean en `Warning`: no suman aquí, y no disparan la alerta
  de excepciones. Las vigila `qep_siigo_call_failures_total` y `http_client_*`.
- **Las categorías `Microsoft.EntityFrameworkCore.*` no cuentan** (revisión final, 2026-11-21): EF registra en `Error`, con su excepción, los choques de
  índice único que el `UnitOfWork` ya traduce a un 409 de dominio (`catalog.product.code_taken`). Contarlos dispararía la alerta de excepciones con
  un camino normal, y la serie de la alerta no tiene `category` para filtrarlos en Grafana. Un error de base que de verdad se escapa sale por
  `ApiExceptionHandler` o por el worker que lo atrapa, y esos sí cuentan.
- Un `TaskCanceledException`/`OperationCanceledException` logueado en `Error` cuenta igual: si alguien lo loguea así, es ruido que
  conviene ver.

- **Serie de alerta sembrada** (ajuste del builder, 2026-11-21): `increase()` toma la primera muestra de una serie como base, así que
  la primera excepción de un `category`/`exception_type` nuevo no dispararía una alerta sobre `qep.log.errors`. Para la alerta hay un
  segundo instrumento, `ObservableCounter<long>` `qep.log.error_entries` (`qep_log_error_entries_total`), con solo `level`
  (`Error`, `Critical`) y `has_exception` (`true`, `false`): cuatro series que nacen en cero y se reportan siempre. Un
  `ObservableCounter` no depende de que el exportador ya esté escuchando cuando se siembra (precedente: `MessagingMetrics`).
  El proveedor recibe el `Meter` por constructor (`QepObservability.Meter` en producción, uno propio en cada prueba).

### 2. Contador de fallas de Siigo

- Clase `SiigoMetrics` en `Modules.Siigo.Infrastructure`, meter `Qep.Siigo`, registrada como singleton e inyectada en `SiigoApiClient`.
- Instrumento: `ObservableCounter<long>` `qep.siigo.call_failures`, unidad `{call}`. En Prometheus: `qep_siigo_call_failures_total`.
  Nace sembrado en cero para cada operación × los motivos fijos (`timeout`, `network`, `credentials_rejected`, `invalid_response`,
  `partner_id_missing`, `auth_without_token`), por la razón de `increase()` de la sección 1. Los `http_<status>` nacen al ocurrir.
  Un motivo vacío (`http_` sin status) se cuenta como `unknown`.
- Se suma en `SiigoApiClient.RecordAsync` **antes** de la guarda `failures is null || capture is null`. Así cuentan también las fallas sin
  captura (`partner_id_missing`) y los hosts sin `ISiigoCallFailureLog`.
- Etiquetas:
  - `operation`: el nombre de `SiigoCallOperation` (`Auth`, `ListRead`, `ProductSearch`, `CustomerFind`, `CustomerCreate`,
    `InvoiceCreate`, `InvoiceStamp`, `StampErrors`, `InvoicePdf`), con la misma regla que la fila: si la ruta es `auth`, `Auth`.
  - `reason`: el motivo que ya se guarda (`timeout`, `network`, `http_<status>`, `credentials_rejected`, `invalid_response`,
    `partner_id_missing`, `auth_without_token`).
- Sin tenant, conexión, factura ni ruta.

### 3. Lo que consume Grafana (contrato con `migracion-k8s`)

El documento de `migracion-k8s` define paneles y reglas. Este repo garantiza los nombres:

| Serie | Etiquetas | Uso propuesto |
|---|---|---|
| `qep_log_errors_total` | `level`, `category`, `exception_type` | Errores por categoría y tipo (paneles, no alertas) |
| `qep_log_error_entries_total` | `level`, `has_exception` | Alerta de excepciones no controladas |
| `qep_siigo_call_failures_total` | `operation`, `reason` | Fila Siigo; alertas de credenciales y transitorios |
| `http_client_request_duration_seconds_*` | `server_address`, `http_response_status_code`, `error_type` | Errores por host externo (ya existe) |
| `qep_queue_*` | `queue` | Fila «¿Seguro desplegar?» (ya existe) |

Alertas aprobadas por el owner (umbrales iniciales):

| Alerta | Expresión (orientativa) | Severidad |
|---|---|---|
| Siigo rechaza las credenciales | `increase(qep_siigo_call_failures_total{reason="credentials_rejected"}[5m]) > 0` | crítica |
| Siigo con fallas transitorias | `sum(increase(qep_siigo_call_failures_total{reason=~"timeout\|network\|http_(408\|429\|5..)"}[10m])) > 3` | aviso |
| Excepciones no controladas | `sum(increase(qep_log_error_entries_total{has_exception="true"}[5m])) > 0` | aviso |

## Pendientes y decisiones del builder

- **Confirmar en Prometheus** que `http_client_request_duration_seconds_count{server_address="api.siigo.com"}` existe. `RemoveAllLoggers`
  quita los logs del cliente, no la instrumentación, pero no se verificó en producción.
- **Confirmar en Loki** los nombres de campo del JSON (`LogLevel`, `Category`, `TraceId`) para el enlace del panel de errores a sus logs.
- `qep.siigo.invoices{status,error_code}`, que estaba en el diseño hablado, **se cae**: una factura rechazada ya cuenta como
  `operation="InvoiceCreate"` con `reason="http_400"`, y el código de negocio vive en `siigo.call_failures`. Decisión del builder,
  corregible.
- Si `qep_log_errors_total` resulta ruidoso por errores manejados, la alerta se ajusta en `migracion-k8s` (filtrar `category` o subir el
  umbral). El contador no cambia.

## Pruebas

- **Unitarias (`BuildingBlocks` / `Bootstrapper.UnitTests`)**, con `MeterListener`:
  - Un `LogError` con excepción suma 1 con `level`, `category` y `exception_type` correctos.
  - Un `LogWarning` y un `LogInformation` no suman nada.
  - Un `LogCritical` sin excepción suma con `exception_type="none"`.
  - Ninguna etiqueta lleva el mensaje del log.
  - Registrar el proveedor no cambia lo que escribe la consola JSON.
- **Unitarias (`Modules.Siigo.UnitTests`, `SiigoApiClientTests`)**:
  - Un 401 doble en una llamada normal suma la operación de quien llamó (por ejemplo `InvoiceCreate`/`credentials_rejected`): la
    captura trae la ruta real, no `auth`. Un `/auth` rechazado suma `Auth`/`credentials_rejected`. Es la misma regla que la fila
    (decisión del builder: no se fuerza `Auth` para todo `credentials_rejected`; la alerta filtra por `reason` y funciona igual).
  - Un timeout suma `timeout`.
  - Un 400 de creación de factura suma `InvoiceCreate`/`http_400`.
  - `partner_id_missing` suma sin captura.
  - Una llamada exitosa no suma.
  - `SiigoNoLeakTests` sigue en verde: cero líneas en la categoría del cliente.

## Otros repos

- **`migracion-k8s`:** dashboard (fila «Errores» con enlace a Loki, errores por host externo, fila Siigo, fila «¿Seguro desplegar?» con
  semáforo por cola) y las tres reglas. Va en su propio documento y se aplica después de que el backend esté desplegado.
- **`qep-frontend`:** el fallo de Siigo traducido en el pedido. Antes de su spec se revisa si el borrador de `call-failures` ya lo cubre.

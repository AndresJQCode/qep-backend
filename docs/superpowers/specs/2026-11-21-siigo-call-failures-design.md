# Fallas de las llamadas a Siigo — tabla propia

Fecha: 2026-11-21 · Repo: `qep-backend` · Estado: **diseño aprobado por el owner (2026-11-21)**

## Intención

Quien configura Siigo necesita ver **qué petición se mandó a Siigo y qué contestó cuando algo falla**, para corregir datos (un
cliente, una dirección, un total) sin pedirle un log al developer. Hoy el rechazo de una factura deja en `siigo.invoices.error_detail`
solo los códigos de Siigo, y los mensajes se descartan a propósito (repiten datos del cliente). El curl y la respuesta existen en
`platform.request_failures` desde el 2026-11-21 (`8a8a502c7`), pero ese log es de requests de QEP que se caen, solo cubre escrituras y
lo lee un operador de plataforma (`RequestLogRead`).

El owner decidió (2026-11-21): **tabla propia de Siigo, y se guarda solo lo que falla; lo que responde bien no se guarda.**

## Metas

- Una fila por cada llamada a Siigo que falla: la petición como `curl` reproducible, la respuesta de Siigo, el motivo, el tenant y la conexión.
- Atar la fila a la factura (`siigo.invoices`) sin que el cliente de Siigo conozca pedidos.
- Lectura del tenant con permiso propio; expiración automática, sin que nadie tenga que acordarse de borrar.

## No metas y antimetas

- **No se guardan llamadas exitosas**, ni su cuerpo. Una respuesta buena de `/auth` trae el token; guardarla sería una fuga.
- **No es un log de consumo** (conteos, latencias, cuotas). Eso, si hace falta, es otro trabajo.
- **No cambia `siigo.invoices`**: `error_detail` sigue guardando solo códigos. La tabla nueva es diagnóstico, no estado.
- Antimeta: que el token de Siigo o la `access_key` queden legibles en una fila. Un curl con token real sería una credencial en una tabla.
- La pantalla de `qep-frontend` va en **su propio spec** (regla del workspace: un spec por repo). Este cubre el backend.

## Restricciones

- Capas por módulo; `Modules.Siigo.Application` no referencia EF ni Platform (verifica `SiigoLayerTests`). El módulo no tiene hosted services.
- Un permiso nuevo necesita sus dos mitades: la constante y su política en `QepServiceCollectionExtensions` (sin la política es 500, no 403),
  más su `PermissionDefinition` con `RequiredModules: [siigo]`.
- Aislamiento de tenant en doble capa: ruta con `{tenantId}`, política y revalidación en el handler; **403, nunca 404**.
- La fila lleva datos del cliente (cuerpo de factura y respuesta de Siigo): la lista no los devuelve.
- Migración **expand-only** (tabla nueva): segura con `RollingUpdate`. TDD obligatorio, RED antes que GREEN.

## Lo que hacen los grandes (investigado 2026-11-21)

Stripe guarda el cuerpo del request y de la respuesta, conserva 31 días los GET y 15 meses las escrituras, y filtra por endpoint, código
de respuesta y tiempo. GitHub muestra cabeceras, cuerpo y respuesta de cada entrega de webhook durante 3 días y permite reenviar. Shopify
conserva 7 días de entregas, filtrables por estado. Twilio adjunta la petición HTTP y la respuesta a cada error. Vapi y Chargebee excluyen
las cabeceras de autenticación antes de guardar. De ahí salen dos ajustes: expiración automática y un id de correlación (la
`Idempotency-Key`, como el id de request de Stripe). Stripe separa la retención de escrituras y lecturas; el owner prefirió 7 días para todo.

## Enfoques

1. **Dejarlo en `platform/request-log`** (lo que hay hoy). Cero trabajo, pero es de operador de plataforma, solo ve escrituras y mezcla
   un hecho saliente con requests que entran. Descartado por el owner.
2. **Tabla propia `siigo.call_failures` escrita por el cliente de Siigo (elegido).** El cliente ya ve la petición y la respuesta; escribe la
   fila con un `DbContext` propio, fuera de la transacción del request (como `RequestFailureLog`). Cubre lecturas y escrituras.
3. **Tabla propia escrita por un `DelegatingHandler`.** Ve todo, pero no sabe de la operación y reconstruirla desde la URL es frágil. Descartado.

El enfoque 2 **reemplaza** el camino del request-log: se retiran `SiigoCallFailureLog` (Bootstrapper) y el `Exception.Data["request.diagnostic"]`
(Siigo y `RequestFailureCapture`), para no guardar el mismo cuerpo con datos personales en dos tablas. El request-log vuelve a su fila
normal (la excepción), ligada por `TraceId`.

## Diseño

### Tabla `siigo.call_failures` (append-only)

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid | v7 |
| `tenant_id` | uuid | de `IExecutionContext`; obligatorio |
| `connection_id` | uuid | `SiigoCredentials.ConnectionId` |
| `operation` | text(40) | por nombre: `Auth`, `ListRead`, `ProductSearch`, `CustomerFind`, `CustomerCreate`, `InvoiceCreate`, `InvoiceStamp`, `StampErrors`; lo fija cada método del cliente |
| `method` | text(10) | |
| `path` | text(500) | ruta de Siigo con su query (la identificación de un cliente va en ella) |
| `status` | int null | null si no hubo respuesta (timeout, red) |
| `reason` | text(60) | `http_400`, `timeout`, `network`, `credentials_rejected`, `partner_id_missing`… |
| `idempotency_key` | text(30) null | la de la factura creada: une la fila con `siigo.invoices` (que la guarda única) y con su pedido |
| `siigo_invoice_id` | text(64) null | en el sellado y sus errores |
| `request_curl` | text | tope 30.000 caracteres |
| `response_body` | text null | tope 8.000 caracteres |
| `subject_id` | uuid null | quien disparó la acción |
| `trace_id` | text(120) null | el mismo de `request_failures` y del log de la aplicación |
| `occurred_at` | timestamptz | |

Índices `(tenant_id, occurred_at desc)` y `(occurred_at)` para la expiración (que borra de todos los tenants lo vencido: el log es operativo).

### Escritura

- `ISiigoCallFailureLog` (puerto existente) cambia su implementación: pasa a `Modules.Siigo.Infrastructure`, con un `SiigoDbContext` de vida
  corta y su propia transacción. **No propaga sus errores** (quien llama está por devolver el resultado real).
- `SiigoApiClient` registra **todo** fallo (`Reply` no exitoso o excepción de red), con o sin lanzar después: las lecturas ya no dependen
  de `Exception.Data`. Un 401 que se renueva y sale bien no deja nada; el segundo 401 sí (`credentials_rejected`).
- `operation`, `idempotency_key` y `siigo_invoice_id` los conoce el propio cliente: no hace falta pasar contexto de pedido.
- Token y `access_key` enmascarados (`SiigoCurl`, ya hecho). El `Partner-Id` viaja: es de QEP, no secreto.

### Expiración automática

Tras insertar, la misma escritura borra un lote acotado (hasta 200 filas) de lo anterior a **7 días**, para lecturas y escrituras por igual
(decisión del owner, 2026-11-21). Sin worker ni endpoint de borrado. Si el borrado falla, la fila nueva ya quedó y el fallo solo se anota en
el log de la aplicación.

### Lectura

- `GET /api/v1/tenants/{tenantId}/siigo/call-failures?page&pageSize&operation&status&from&to`: resumen por fila (`id`, `occurredAt`,
  `operation`, `method`, `path`, `status`, `reason`, `idempotencyKey`, `siigoInvoiceId`, `traceId`), orden descendente, `MaxPageSize` 100.
  **Sin curl ni cuerpo.**
- `GET …/call-failures/{id}`: detalle con `requestCurl` y `responseBody`. Motivo BFF escrito en el DTO: la lista no puede pesar 25 × 38 KB.
- Permiso nuevo `siigo.calls.read` (con `RequiredModules: [siigo]`; de fábrica `admin` y `billing`) para los dos `GET`.

### Pruebas

- Unitarias del cliente: cada tipo de fallo deja una fila (curl enmascarado, sin token ni `access_key`); un éxito y un 401 renovado, ninguna;
  un fallo del log no cambia el resultado; la factura lleva su `idempotency_key`.
- Repositorio: la expiración borra solo lo anterior a 7 días, en lote acotado de 200.
- Integración: 403 sin permiso y de otro tenant (nunca 404); lista sin cuerpos; detalle con curl; filtros por operación y status;
  `SiigoNoLeakTests` suma la fila a lo que revisa (sentinelas de `access_key` y token ausentes).
- Arquitectura: `SiigoLayerTests` y la política del permiso nuevo registrada.

## Decisiones mías (a revisar)

- Expiración en la misma escritura, lote de 200 y sin endpoint de borrado (la ventana de 7 días es del owner).
- Un solo permiso nuevo, `siigo.calls.read`.
- Topes de 30.000 caracteres para el curl y 8.000 para la respuesta.
- `idempotency_key` y `siigo_invoice_id` en vez de `order_id` y de un contexto scoped: el cliente no necesita saber de pedidos.
- Se retira lo escrito el 2026-11-21 en `platform/request-log` para Siigo.

## Decidido por el owner

- `siigo.calls.read` solo para `admin` de fábrica. Quien deba ver la pantalla (soporte, por ejemplo) recibe un rol personalizado con ese permiso,
  que el owner asigna después (2026-11-21).
- `billing` también lo lleva de fábrica (2026-11-25); reemplaza el «solo `admin`» de la viñeta anterior.

## Queda al implementador

Nombres de clases y archivos, el orden de las migraciones, y el texto exacto de los códigos `reason` que no se listan arriba.

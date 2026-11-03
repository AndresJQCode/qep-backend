# Bloque HTTP del chatbot: un reintento ante fallas de red, y el detalle de la falla

**Fecha:** 2026-11-03
**Módulos:** Automations (`HttpBlockExecutor`). **Bootstrapper y frontend:** sin cambios.
**Estado:** aprobado por el owner el 2026-11-03 con **dos recortes** (decisiones del owner, 2026-11-03):
1. **El timeout no se reintenta, ni en `GET`.** Sigue como hoy: una fila, `outcome = timeout`. Las fallas de producción fueron de 145 y 175 ms,
   no timeouts, y reintentar uno duplica la espera del cliente (~21 s) frente a una API lenta que probablemente vuelva a vencer. Resuelve OD-4.
2. **Sin ajuste del pool (D-8) en este hotfix.** Se difiere hasta que el `error_code` detallado (D-4) diga la causa real. Sin cambio en
   Bootstrapper ni en `CompositionRootTests`. Deja OD-1 sin objeto por ahora.

Además, OD-2 (5xx) y OD-3 (columna `attempt`) se resuelven como recomendaba el spec: no. Sale como **hotfix** (§7): cambio mínimo, sin
migración, sin frontend, sin Bootstrapper.
**Acota** P2-R37 y D-H6 del spec `2026-10-21-constructor-de-chatbots-design.md`: el bloque puede mandar la misma llamada dos veces, sólo en
los casos de D-2, y cada intento deja su fila.

## 1. Contexto

**Lo que pasó (producción, verificado).** Tenant `01a18ce5-20b8-7b71-be13-d04f25a714a7`, bloque HTTP `buscar-conductor`,
`GET /Drivers/external`. El 2026-11-03, dos llamadas (11 h y 15 h) terminaron `outcome = network`, `error_code = network`, sin status, en 145 ms
y 175 ms. En 3 días hubo unas 85 llamadas y sólo esas dos fallaron, con llamadas `ok` en las mismas horas: fallas aisladas, no una caída.

**Consecuencia.** El bot traspasó la conversación a la bandeja sin asignar: «El asistente dejó la conversación en la bandeja, sin asignar,
porque «Buscar conductor por cédula» no pudo conectarse con la API». Una consulta de sólo lectura que un segundo después habría salido bien le
costó al cliente la atención automática.

**Por qué no sabemos la causa.** El log del pod sólo dice `Chatbot HTTP block buscar-conductor ... failed: network (status ) in 145 ms`.
`HttpBlockExecutor.SendAsync` (`HttpBlockExecutor.cs:200-213`) atrapa `HttpRequestException` e `IOException` y los dos salen como
`Failure(HttpCallOutcomes.Network, answered, HttpCallOutcomes.Network)`, sin mirar la excepción; y el cliente nombrado
`automations.external-api` no tiene loggers (`RemoveAllLoggers()`, `QepServiceCollectionExtensions.cs:1229-1231`). Conexión rechazada,
reset, TLS, DNS o respuesta cortada se ven idénticos.

**Hipótesis (sin verificar).** Una falla rápida y aislada encaja con una conexión keep-alive del pool que el servidor ya cerró y que
`SocketsHttpHandler` reutilizó. Matiz: `SocketsHttpHandler` ya reintenta por su cuenta un request que falla en una conexión reutilizada antes
de recibir el primer byte de respuesta, así que la hipótesis puede no ser la causa entera. Por eso D-4 (el detalle) va junto con D-1 (el
reintento): la próxima falla dirá qué fue, y con eso se decide si hace falta tocar el pool (D-8, diferido).

**Lo que dice el código hoy, y que este spec usa:**

- El bloque sólo acepta `GET`, `POST`, `PUT` y `PATCH` (`FlowDefinition.cs:241`). No hay `HEAD` ni `DELETE`.
- La espera del adjunto ya usa la función inyectable `delay` del constructor (`HttpBlockExecutor.cs:32`, `:55`), que las pruebas capturan en
  `Bed.Delays`.
- El rechazo anti-SSRF de `ConnectCallback` también llega como `HttpRequestException`; se reconoce antes con `urlPolicy.RejectionIn`
  (`HttpBlockExecutor.cs:204`).
- `http_calls.error_code` es `varchar(64)` (`AutomationsDbContext.cs:257`) y `HttpCallLog` recorta a 64 (`HttpCallLog.cs:18`, `:36`).
  `http_calls.outcome` es `varchar(24)`.
- Precedente en el repo: `WhatsAppCloudClient.SendAsync` (`WhatsAppCloudClient.cs:639-642`) ya distingue «nunca salió» con
  `HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError`.

## 2. Objetivos y no objetivos

**Objetivos**

- Que una falla de red aislada en una consulta no traspase la conversación: un reintento, sólo cuando repetir no puede duplicar un efecto.
- Que la próxima falla diga qué fue, sin escribir en ningún lado la URL, los headers, la clave ni el mensaje de la excepción.

**No objetivos**

- Reintentar un timeout del bloque, con cualquier método (owner, 2026-11-03).
- Ajustar el pool del cliente nombrado (D-8, diferido por el owner el 2026-11-03).
- Reintentar respuestas con status (5xx incluido): D-3.
- Columna de número de intento en `http_calls`, o cualquier migración: D-5.
- Cambiar el texto del traspaso, el frontend o el simulador (el simulador hereda el reintento sin cambiar: D-7).
- Reintentar un `POST`, `PUT` o `PATCH` cuya petición pudo haber llegado.

## 3. Decisiones

**D-1. Un solo reintento, tras 1 s, sólo en los casos seguros de D-2.** Constante `HttpBlockExecutor.NetworkRetryDelay = 1 s`, esperada con
la función `delay` que ya existe (`wait`). Un segundo alcanza para que el pool descarte la conexión muerta y abra otra; más largo sólo demora
al cliente. Presupuesto, sin reintento de timeout (owner, 2026-11-03):

- **Caso que motiva el hotfix** (falla rápida, 145-175 ms): ~0,2 + 1 + la duración del segundo intento. Si el segundo sale bien, el cliente
  espera poco más de un segundo extra.
- **API lenta que vence:** `timeoutSeconds` y nada más, como hoy. El timeout no se reintenta.
- **Cota teórica:** una falla de red que llega justo antes de vencer (p. ej. `ResponseEnded` en un `GET` a los 9,9 s) y un segundo intento que
  vence: con `timeoutSeconds` al máximo (10, `MaxTimeoutSeconds`) son 10 + 1 + 10 = 21 s, dentro del lease de 30 s. Es la misma cota de
  antes pero ya no la alcanza el caso común (la API lenta), sólo una falla de red tardía, que es rara.

Con la espera del adjunto (hasta 20 s) el peor caso ya pasaba el lease antes de este cambio; lo cubre el candado
`pg_try_advisory_xact_lock` de la conversación (gotcha «Esperas con tope» del `CLAUDE.md`), igual que hoy.

**D-2. Cuándo se reintenta.** La clasificación sale sólo de `HttpRequestException.HttpRequestError` (y de `HttpIOException.HttpRequestError`,
que hereda de `IOException`), nunca del mensaje:

| Falla | Qué significa | `GET` | `POST` / `PUT` / `PATCH` |
|---|---|---|---|
| `NameResolutionError`, `SecureConnectionError` | La petición no salió: no resolvió el host o el TLS no cerró | Reintenta | Reintenta |
| `ConnectionError` con un `SocketException` y **ninguna** `IOException` en la cadena de `InnerException` | Falló el connect: no salió | Reintenta | Reintenta |
| `ConnectionError` con una `IOException` en la cadena, o sin causa | Pudo fallar al escribir el request o al leer los headers | Reintenta | **No** (revisión del hotfix) |
| `ResponseEnded`, `HttpIOException` a mitad del cuerpo, `IOException` sin `HttpRequestError`, `Unknown` | Pudo llegar al servidor | Reintenta | **No** (P2-R37) |
| Timeout del bloque (`OperationCanceledException` sin cancelación de quien llama) | Pudo llegar, y la API está lenta | **No** (owner, 2026-11-03) | **No** |
| `HttpProtocolError`, `ConfigurationLimitExceeded`, `VersionNegotiationError`, `UserAuthenticationError`, `ProxyTunnelError`, `InvalidResponse`, `ExtendedConnectNotSupported` | Determinista: repetir da lo mismo | No | No |
| `url_rejected` (D-H2, `RejectionIn`) | Es la URL, no la red | No | No |
| Cancelación de quien llama | Apagado o lease | No: se propaga, como hoy | No |

- **`Unknown` se trata como «pudo llegar»**, la dirección segura: sólo `GET`.
- **`ConnectionError` se mira por dentro** (revisión independiente del hotfix, 2026-11-03). La duda era si `SocketsHttpHandler` envuelve como
  `ConnectionError` una `IOException` al escribir el request o al leer los headers; si fuera así, un `POST` que llegó se repetiría. Regla
  conservadora: para un método con efecto, `ConnectionError` es «no salió» sólo con un `SocketException` y sin `IOException` en la cadena; si
  no, sólo `GET`. **Resultado empírico** (.NET 10, Windows, sockets reales contra `127.0.0.1`, `WhatDotNetReportsForAResetAfterThePost`): un
  servidor que lee el `POST` entero y resetea sin responder produce
  `HttpRequestException(Unknown) -> IOException -> SocketException(ConnectionReset)`, no `ConnectionError`. Ese caso ya caía en «pudo llegar»
  por `Unknown`, así que la sospecha no se reprodujo ahí; la regla se deja igual porque es gratis y cubre otros caminos (otro SO, otra versión,
  el reset mientras se escribe). Un puerto donde nadie escucha sí da `ConnectionError` con el `SocketException` adentro, y se repite.
- **El timeout no se reintenta** (decisión del owner, 2026-11-03; resuelve OD-4): las fallas de producción fueron rápidas, y repetir frente a
  una API lenta duplica la espera del cliente (~21 s) para volver a vencer. Una fila, `outcome = timeout`, como hoy.
- **P2-R37 se mantiene para los métodos con efecto.** Clasic no tiene llave de idempotencia: un `POST /service-orders` repetido crearía dos
  órdenes.
- **El cuerpo tiene que poder rearmarse.** Un `HttpRequestMessage` no se manda dos veces, así que el segundo intento arma mensaje y contenido
  nuevos. Se reintenta sin cuerpo o con cuerpo JSON (el texto ya está interpolado). **Un multipart no se reintenta**: el archivo es un stream
  de R2 que no se puede rebobinar, y reabrirlo es otra lectura y otra espera. En la práctica el multipart va por `POST`, que en los casos que
  pudieron llegar ya no reintenta.
- **Sólo una vez.** Si el segundo intento falla, el bloque sale por `error` con ese resultado, sea cual sea.

**D-3. Las respuestas con status no se reintentan, 5xx incluido.** Si hubo status, el servidor procesó algo: un 500 de Clasic puede ser semántico
(el `invoiceItems[0].id` como string del gotcha de Lista dinámica en `CLAUDE.md`), un 502/503 de un proxy no dice si el backend alcanzó a ejecutar, y para `GET` un 5xx rara vez es la
conexión vieja que motiva este hotfix. OD-2 resuelta por el owner el 2026-11-03: no.

**D-4. `error_code` detallado; `outcome` sigue en `network`.** Formato `network:<falla>[:<socket>]`, armado sólo con nombres de enums en
`snake_case`:

- `<falla>`: el `HttpRequestError` (`connection_error`, `name_resolution_error`, `secure_connection_error`, `response_ended`, `unknown`, …).
  Una `IOException` que no es `HttpIOException` da `io`.
- `<socket>`: el `SocketError` de la primera `SocketException` en la cadena de `InnerException`, si la hay (`connection_refused`,
  `connection_reset`, `host_not_found`, `timed_out`, …).
- Ejemplos: `network:connection_error:connection_refused`, `network:response_ended`, `network:io`.
- Un valor de enum que .NET agregue en el futuro sale con su nombre, o con su número si no tiene: nunca texto libre.
- **Largo:** el peor caso realista es `network:secure_connection_error:protocol_family_not_supported` (60), menor que 64; de todos modos
  `HttpCallLog` recorta.
- **Nunca** `exception.Message` ni `ToString()`: pueden traer el host, la URL o la query (D-H5).
- **El timeout no cambia:** sigue `outcome = timeout`, `error_code = timeout`.
- **`LogFailed`** gana `{ErrorCode}` y `{Attempt}`: `Chatbot HTTP block {NodeId} of tenant {TenantId} failed: {Outcome} ({ErrorCode}),
  attempt {Attempt}, (status {Status}) in {DurationMs} ms`. Sin URL ni clave, como hoy. `LogCalled` también lleva `{Attempt}`, para que un
  `ok` en el intento 2 se vea en el log.

**Quién lee `error_code` hoy (verificado), y por qué nada se rompe:**

- `HttpCallOutcomes.Network` sólo se usa en `HttpBlockExecutor.cs:206` y `:212`. Ningún código del backend compara `ErrorCode == "network"`.
- El traspaso: `RunEffects.cs:238` copia `ErrorCode` a `HandoffHttpFailure` (`HandoffCause.cs:31`) tal cual, y viaja en `details.handoff` del
  evento. El frontend (`qep-frontend/src/features/messaging/utils/handoff-cause.ts:92`) elige el texto por `outcome` (`network: … no pudo
  conectarse con la API.`) y sólo lee `errorCode` para `http_NNN` (`:49`) y para `connection_unavailable` (`:71-78`). **El frontend no
  cambia.**
- El simulador muestra `La llamada a la API falló ({ErrorCode ?? Outcome})` (`Simulations.cs:159`): verá el código detallado, que es justo
  lo útil ahí.
- `run_units` no guarda el `ErrorCode` del bloque HTTP.

**D-5. Cada intento deja su fila en `http_calls`, sin columna nueva.** D-H6 sigue: una llamada que salió es una fila. El primer intento se
registra con `FinishAsync` (con `CancellationToken.None`, P2-R33a) **antes** de esperar el reintento, así que un apagado durante la espera no
pierde la fila. Sin migración: dos filas con el mismo `(run_id, node_id, step_number)` son un reintento, ordenadas por `called_at`; el
`error_code` de la primera dice por qué se repitió. En el simulador `step_number` es `null` y se distinguen sólo por `called_at`; se acepta. El
número de intento va al log (D-4), no a la base. OD-3 resuelta por el owner el 2026-11-03: sin columna `attempt` ni migración.

**D-6. El traspaso refleja el último intento.** `ExecuteAsync` devuelve el resultado del segundo intento; `RunEffects` y `HandoffCause` no
cambian. Si el primero falla y el segundo sale `ok`, no hay traspaso.

**D-7. El simulador hereda el reintento, sin tocarlo.** `Simulations.CallHttpAsync` usa el mismo `IHttpBlockExecutor` (`Simulations.cs:156`):
con «llamar a la API de verdad» se comporta igual que en producción, que es lo que el simulador promete. Costo: el peor caso de un bucle HTTP
en el simulador sigue en `25 × timeoutSeconds` con timeouts (no se reintentan); sólo una falla de red tardía en cada vuelta lo llevaría a
`25 × (2 × timeoutSeconds + 1)`. El tope de 25 reentradas (`EngineReentries.Max`) no cambia. Se acepta.

**D-8. DIFERIDA por el owner el 2026-11-03: no entra en este hotfix.** Se retoma cuando el `error_code` detallado (D-4) diga si las fallas son
de conexión reutilizada (`connection_reset`, `response_ended` en el primer intento con `ok` en el segundo) u otra cosa. Lo que sigue queda
como la propuesta para ese cambio, sin aplicar.

*Propuesta (sin aplicar):* pool del cliente del bloque con `PooledConnectionIdleTimeout = 4 s`, `PooledConnectionLifetime = 5 min`. Hoy el handler usa los
defaults de .NET (inactividad de 1 min, vida sin límite), más largos que el keep-alive de muchos servidores (5 s en Node.js por defecto,
60-75 s en balanceadores y nginx). Con 4 s el cliente suelta la conexión antes que un servidor de 5 s. El costo es un handshake TLS más por
llamada, que con ~28 llamadas al día por tenant ya se paga casi siempre. La vida de 5 min hace que un cambio de DNS del proveedor se vea sin
esperar a que el pool se vacíe. Las dos van como constantes en `HttpBlockExecutor` (`PooledConnectionIdleTimeout`, `PooledConnectionLifetime`).

**Dónde:** en el registro de Bootstrapper, no en `ExternalApiUrlPolicy.CreateHandler`:

```csharp
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = ExternalApiUrlPolicy.CreateHandler();
    handler.PooledConnectionIdleTimeout = HttpBlockExecutor.PooledConnectionIdleTimeout;
    handler.PooledConnectionLifetime = HttpBlockExecutor.PooledConnectionLifetime;
    return handler;
})
```

`CreateHandler` devuelve `SocketsHttpHandler` (`ExternalApiUrlPolicy.cs:89`) y también lo usa `ExternalApiConnectionTester`
(`IntegrationsInfrastructureExtensions.cs:88-89`), que hace una llamada suelta y no gana nada. Dejarlo en el registro mantiene el hotfix dentro
de Automations y Bootstrapper. El handler seguiría siendo el de la política: `CompositionRootTests.TheChatbotHttpClientIsBoundToThePolicyHandler`
no cambiaría de sentido y se extendería con las dos propiedades.

**D-9. Sin cambios de contrato, de esquema ni de configuración.** No hay clave nueva en `appsettings` ni en `Backend-prod`: la espera del
reintento es una constante. Si hace falta moverla, es otro cambio.

## 4. Qué cambia en el código

Rutas relativas a `qep-backend/src/`.

- `Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs`:
  - `ExecuteAsync`: el envío pasa a un bucle de a lo sumo dos intentos. Cada intento arma su `HttpRequestMessage` y su contenido (sin cuerpo o
    JSON; multipart sólo en el primero), registra su fila con `FinishAsync` y, si D-2 lo permite y quien llama no canceló, espera
    `NetworkRetryDelay` con `wait` y repite.
  - `SendAsync`: devuelve, además del resultado, si la falla es reintentable para el método (D-2). La clasificación y el código de D-4 en un
    método estático puro (p. ej. `NetworkFailure.Classify(Exception, string method)`), probado aparte.
  - `LogFailed` y `LogCalled` con `{ErrorCode}` y `{Attempt}` (D-4).
  - Constante `NetworkRetryDelay`. El `<summary>` de la clase dice que hay un reintento y cuándo.

- `Http/NetworkFailure.cs` (nuevo): el clasificador `NetworkFailure.Classify(Exception, string method, bool responded)` →
  `(ErrorCode, Retryable)`. `responded` = ya llegó el status: entonces un `ConnectionError` no cuenta como «no salió» y sólo `GET` se repite.
  Un `ConnectionError` sin status cuenta como «no salió» sólo con un `SocketException` y ninguna `IOException` en la cadena (D-2).
  Si el código con el socket pasara los 64 de la columna, va sin el socket (nunca se parte).
- `Http/HttpCallLog.cs`: `ErrorCodeMaxLength` pasa de `private` a `internal` para que el clasificador lo use; el recorte no cambia.

No cambian: `HttpCallOutcomes`, el recorte de `HttpCallLog`, `http_calls`, `RunEffects`, `HandoffCause`, `Simulations`, Bootstrapper (D-8 diferida), el
frontend.

## 5. Pruebas (TDD estricto: RED antes que GREEN, con evidencia literal de los dos)

**Unitarias nuevas, `tests/Modules/Automations/Modules.Automations.UnitTests/HttpBlockExecutorTests.cs`** (el `Bed` ya captura `Delays` y
`Handler.Requests`):

1. `AGetThatCouldNotConnectIsRetriedOnceAndSucceeds`: el primer envío lanza `HttpRequestException` con `HttpRequestError.ConnectionError`, el
   segundo responde 200 → `Ok`, dos requests, dos filas (`network` con `network:connection_error` y `ok`), `Delays == [1 s]`, mismo
   `StepNumber` en las dos.
2. `APostThatCouldNotConnectIsRetried`: igual con `POST` y cuerpo JSON (`ConnectionError` con `SocketException(ConnectionRefused)` adentro)
   → dos requests con el mismo cuerpo.
3. `APostWhoseResponseEndedIsNotRetried`: `POST`, `HttpRequestError.ResponseEnded` → un request, una fila, `Delays` vacío.
4. `AGetWhoseResponseEndedIsRetried`: `GET` con `ResponseEnded` → dos requests.
5. `AGetCutWhileReadingTheBodyIsRetried` y `APutCutWhileReadingTheBodyIsNotRetried`: el `DroppingStream` existente (`HttpIOException`
   `ResponseEnded`), con `GET` y con `PUT`.
6. `ATimeoutIsNotRetriedEvenForGet` (owner, 2026-11-03): `GET` que vence → un request, una fila `timeout`, `Delays` vacío.
7. `AnUnknownNetworkErrorIsRetriedOnlyForGet`: `HttpRequestException` sin `HttpRequestError` (es `Unknown`).
8. `TheSecondFailureIsTheResult`: los dos intentos fallan → sale el segundo resultado, dos filas, ni un tercer request.
9. `AnErrorStatusIsNotRetried`: 503 con `GET` → un request (D-3).
10. `AConnectRejectionIsNotRetried`: el caso de `AConnectRejectionIsUrlRejected`, un solo request.
11. `AMultipartBodyIsNotRetried`: multipart con archivo y `ConnectionError` → un request.
12. `TheCallerCancellationDuringTheRetryWaitPropagates`: quien llama cancela durante la espera; el `delay` del `Bed` respeta el token que
    recibe y se afirma que es el de quien llama → la excepción se propaga, sin segundo intento, y la fila del primero ya está escrita.
    `ACallerCancelledDuringTheFirstAttemptIsNeitherWaitedNorRetried`: si quien llama cancela durante el primer intento, `HttpClient` convierte
    la falla en `OperationCanceledException` → se propaga sin fila, sin espera y sin segundo intento (como antes del reintento).
13. `TheNetworkErrorCodeComesFromEnumsOnly`: `HttpRequestException(ConnectionError)` con mensaje `"https://secreto.test/?k=CLAVE"` e
    `InnerException = new SocketException((int)SocketError.ConnectionRefused)` → `error_code == "network:connection_error:connection_refused"`,
    y ni el código ni ninguna línea de log contienen `secreto`, `CLAVE` ni la URL base del `Bed`.
14. `AnIOExceptionWithoutHttpErrorIsNetworkIo`: `IOException` simple → `network:io`.
15. `TheLongestNetworkErrorCodeFitsTheColumn`: recorre todos los valores de `HttpRequestError` × `SocketError` con el clasificador → largo ≤ 64.
16. Clasificador: `AFailureAfterTheStatusIsNeverPreSend`, `ADeterministicFailureIsNeverRetried` y
    `AConnectionErrorWithoutAConnectFailureIsOnlyRetriedForGet` (`ConnectionError` con `IOException` adentro, o sin causa → sólo `GET`).
17. **Sockets reales** (`SocketsHttpHandler` de verdad contra `127.0.0.1`, con el doble de la política anti-SSRF):
    `ARealRefusedConnectionRetriesAPost` (puerto sin nadie escuchando → dos filas `network:connection_error…`),
    `ARealResetAfterThePostArrivedIsNotRetried` (un `TcpListener` lee el request entero y resetea con `LingerState(true, 0)` → una fila),
    `ARealResetAfterTheGetArrivedIsRetried` (lo mismo con `GET` → dos filas) y `WhatDotNetReportsForAResetAfterThePost`, que escribe la
    cadena de excepciones real en la salida de la prueba y afirma que el clasificador no la repite para `POST`.

**Unitarias existentes que cambian a propósito** (hoy afirman una sola fila con una falla de red en `GET`, y con D-2 pasan a dos):
`ANetworkFailureIsNetwork` (además, `error_code` pasa a `network:unknown`), `AConnectionDropWhileReadingTheBodyIsNetwork` y el caso `network`
de `NoBytesWithoutABodyRead`. Se ajustan en el mismo commit que el RED, diciendo por qué: o pasan a `POST` para conservar lo que probaban
(una fila, `ResponseBytes` nulo), o afirman las dos filas. `ATimeoutIsTimeout` y el caso `timeout` de `NoBytesWithoutABodyRead` **no**
cambian: el timeout no se reintenta.

**Composición:** sin cambios (D-8 diferida).

**Integración:** `tests/Modules/Automations/Modules.Automations.IntegrationTests/HttpBlockCallLogTests.cs`,
`TheColumnTakesTheLongestNetworkCodeAndTwoRowsPerStep`. **No ejercita el ejecutor**: escribe dos filas con `IHttpCallLog` y prueba sólo el lado
de la base, que la columna guarda tal cual el código detallado más largo realista (61 caracteres, con los dos puntos) y que dos filas con el
mismo `(run_id, node_id, step_number)` conviven sin columna nueva. Pasa sin cambio de producción (no tiene RED). Se corre una vez, al final, con
`$env:Seed__Enabled = "false"`.

**Sin prueba automática:** que la conexión vieja del pool sea la causa. Se lee en producción (§6) y decide si se retoma D-8.

## 6. Despliegue, verificación y rollback

- **Sin migración, sin configuración nueva, sin frontend, sin Bootstrapper.** Se despliega sólo el backend.
- **Verificación tras desplegar:** en `automations.http_calls`, filas `network` con `error_code` detallado; pares con el mismo
  `(run_id, node_id, step_number)` donde la segunda es `ok` son traspasos evitados. En el log, `attempt 2`. Si los `network:*` siguen y son
  `connection_error`/`connection_reset`/`response_ended` en el primer intento con `ok` en el segundo, la hipótesis del pool gana fuerza y se
  retoma D-8; si aparece otra cosa (TLS, DNS), se abre otro análisis. Las filas `timeout` siguen siendo una sola por visita.
- **Rollback:** revertir el commit. Las filas con `network:*` quedan como texto; nada las lee por valor.

## 7. Entrega como hotfix

- **Rama:** `hotfix/bloque-http-reintento-red`, desde `main`.
- **`main` tiene el código en la misma forma que `develop` (verificado el 2026-11-03 con `git diff main develop`):**
  - `src/Modules/Automations/Modules.Automations.Infrastructure/Http/`, `src/Modules/Integrations/Modules.Integrations.Infrastructure/ExternalApi/`
    y `HttpBlockExecutorTests.cs`: **sin diferencias**.
  - `src/Bootstrapper` no se toca en este hotfix (D-8 diferida), así que su diferencia entre ramas no importa para el merge.
- **Merges:** el owner lleva la rama a `main` (un agente nunca mergea a `main`). Después, la rama se mergea a `develop`, y en ese merge se
  actualiza `docs/estado.md` (la rama de hotfix no lo toca).
- Commits en conventional commits, sin atribución de IA (p. ej. `fix(automations): retry the HTTP block once on safe network failures`).

## 8. Propuesta de gotcha para `CLAUDE.md`

> **El bloque HTTP reintenta una vez, y sólo cuando repetir no duplica un efecto** (spec 2026-11-03). Tras 1 s: cualquier método si la
> petición no salió (`NameResolutionError`, `SecureConnectionError`, o `ConnectionError` con un `SocketException` y sin `IOException` en la
> cadena), y sólo `GET` si pudo llegar (`ResponseEnded`, corte
> del cuerpo, `Unknown`). Un timeout no se reintenta con ningún método (owner, 2026-11-03), ni un status, 5xx incluido, ni un multipart. Cada intento es una fila de `http_calls`: dos
> filas con el mismo `(run_id, node_id, step_number)` son un reintento, no dos visitas. El `error_code` de una falla de red
> (`network:connection_error:connection_refused`) sale sólo de enums (`HttpRequestError`, `SocketError`); nunca del mensaje de la excepción,
> que puede traer la URL. Un método nuevo en `FlowDefinition` (`DELETE`) tiene que decidir su fila en la tabla de D-2 antes de habilitarse.

## 9. Decisiones del owner (resueltas el 2026-11-03)

1. **OD-1: keep-alive real de Clasic.** Sin objeto en este hotfix: D-8 se difirió. Se retoma con D-8 si el detalle de D-4 apunta al pool.
2. **OD-2: 5xx en `GET` (D-3).** No se reintentan. Si el detalle de D-4 muestra 502/503 aislados de un proxy, se evalúa aparte.
3. **OD-3: columna `attempt` en `http_calls` (D-5).** No: sin migración; el par `(run_id, node_id, step_number)` ya lo distingue en corridas.
4. **OD-4: timeout en `GET` (D-2).** No se reintenta, con ningún método: las fallas de producción fueron de 145/175 ms y repetir frente a una
   API lenta duplica la espera del cliente (~21 s).

Para el builder: el nombre exacto del clasificador y la forma del resultado de `SendAsync`.

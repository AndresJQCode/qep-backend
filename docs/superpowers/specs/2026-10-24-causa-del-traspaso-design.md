# Automatizaciones: la causa del traspaso a un asesor

**Fecha:** 2026-10-24
**Módulos:** Integrations (resultado de `ResolveAsync`), Automations (motor, efectos, bloque HTTP, puertos), Messaging (evento
`AutomationPaused`, `BotOutbound`, directorio de conexiones, «Datos capturados»), Bootstrapper (adaptadores). **Frontend:**
`qep-frontend`, `features/messaging/`.
**Estado:** borrador para el owner. Las decisiones marcadas «del owner» vienen del pedido; las «del redactor» las tomé yo y se
pueden discutir; las DECISIÓN-PENDIENTE (§9) las decide el owner. **No se escribe código hasta que el developer lo diga.**
**Es un delta sobre** `2026-10-21-constructor-de-chatbots-design.md` (D-M5, D-M14, D-M15, D-H4..D-H8, P2-R21) y
`2026-10-21-mensajeria-asistente-y-horarios-design.md` (D-A9, evento `AutomationPaused`). Lo que este documento no toca sigue
como está ahí.
**Plan:** `docs/superpowers/plans/2026-10-24-causa-del-traspaso.md`.
**Ramas:** `feature/causa-del-traspaso` en los dos repos (worktrees ya creados).

## 1. Contexto: el caso verificado

El owner probó el chatbot de Clasic por WhatsApp y **cada mensaje terminó con el bot pasando la conversación a los asesores**,
sin que nadie pudiera saber por qué. Las causas reales se encontraron a mano, en este orden:

1. **La clave de la conexión «API externa» no se podía descifrar** (la base se restauró desde otra máquina, con otra `k1`).
   `IntegrationConnections.ResolveAsync` devolvió `null` (`IIntegrationConnections.cs:65-69`), `AutomationExternalApis.ResolveAsync`
   también (`AutomationExternalApis.cs:24-29`), y `HttpBlockExecutor.ExecuteAsync` respondió `connection_unavailable` **sin llamar y
   sin fila en `http_calls`** (`HttpBlockExecutor.cs:85-93`). El bloque «Buscar cliente por teléfono» (`policy-lookup`) salió por
   `error` hacia el bloque «Pasar a un asesor». El texto de ese bloque tampoco salió: la conexión de WhatsApp tenía el mismo
   problema y `BotOutbound` devolvió `ConnectionUnavailable` (`BotOutbound.cs:109-114`) → `messaging.connection_unavailable`
   (`AutomationMessenger.cs:55`) → `RunEffects.StopAsync` → `FailAsync` → `HandoffAsync` (`RunEffects.cs:190-192`, `146-156`).
2. **Con la clave arreglada, el bloque recibió `404`** (`http_calls.outcome = error_status`, `error_code = http_404`,
   `HttpBlockExecutor.cs:169-172`) porque el túnel apuntaba a otra API → salida `error` → traspaso.

En los dos casos el hilo sólo mostró `AutomationPaused` con `PauseReason = Handoff` (`AutomationConversations.cs:78-97`,
`Conversation.PauseAutomation`, `Conversation.cs:369-383`), que el frontend pinta como «El asistente pasó la conversación a un
asesor.» (`conversation-event.ts:53-57`), y el cliente recibió «Te paso con un asesor.».

**Pedido del owner:** «la idea es que el tenant pueda saber por qué el chatbot lo transfirió a un agente, el contacto/cliente no
reciba el mensaje de que se le pasó a un agente, la idea es intentar una conversación fluida natural».

**Dentro:**

- La causa del traspaso viaja en el evento `AutomationPaused` del hilo, como códigos más la foto de los títulos.
- `IIntegrationConnections.ResolveAsync` deja de colapsar las causas en `null`; el bloque HTTP y el envío del bot propagan un
  motivo de conexión.
- «Datos capturados» muestra el `failureCode` de la corrida.
- Frontend: el diccionario código → texto y su línea en el hilo.

**Fuera** (§10): cuándo traspasa el bot, reintentos, avisos por otros canales, marcar conexiones `NeedsAttention` solas.

## 2. Decisiones

| ID | Decisión | Motivo | Quién |
| --- | --- | --- | --- |
| D-T1 | **La causa la ve el tenant y nunca el cliente.** Es parte del evento `AutomationPaused` del hilo (dirección `System`, `kind = Event`), que no sale a Meta. | Lo pidió el owner. Los eventos del hilo ya son internos por construcción (`ConversationEvents.cs:9-21`: `Direction = System`, `Status = Delivered`, sin envío). | owner |
| D-T2 | **Un traspaso por falla no le escribe al cliente, y eso ya es así; el motor no cambia.** `FailAsync` sólo pausa, cierra la corrida y audita (`RunEffects.cs:146-156`): no envía texto. Lo que el cliente recibe es el `text` del bloque «Pasar a un asesor» cuando el flujo **llega** a ese bloque, y ese texto ya es opcional (`FlowJson.cs:346`, `OptionalString(element, "text", …)`; el motor no manda nada si es `null` o queda en blanco, `FlowEngine.cs:322-325`, `399-412`). El editor ya crea el bloque con `text: null` (`qep-frontend` `features/automations/types/flow.ts:429`). Para un traspaso sin aviso, quien arma el flujo deja el texto vacío. | Verificado en el código: no hay qué arreglar en el motor para la falla. Que el bloque calle solo cuando llegó por un `error` es otra regla (DECISIÓN-PENDIENTE P-1). | redactor (se le muestra al owner) |
| D-T3 | **La causa viaja como códigos más la foto de los títulos**, nunca como texto libre de una API externa: ni cuerpo, ni headers, ni la clave (D-H5, D-H6). Los únicos textos son títulos de bloques y nombres de conexiones, que escribió el tenant. El frontend es dueño del diccionario código → texto. | BFF con enums por nombre (CLAUDE.md, «Los enums viajan con su nombre»). Un código desconocido cae en un texto genérico; un texto libre no se puede traducir ni filtrar. | owner |
| D-T4 | **`IIntegrationConnections.ResolveAsync` devuelve `ConnectionResolution`** (§3.1), que distingue no existe / pausada / necesita atención / proveedor oculto / proveedor deshabilitado / secreto ilegible. Se cambia la firma del método, no se agrega otro: los cuatro adaptadores que la usan pasan a leer `.Connection` y conservan su semántica de hoy. | Dos métodos dejarían a un consumidor nuevo seguir perdiendo la causa sin darse cuenta; el tipo obliga a cada uno a decidir. El cambio en los otros consumidores es mecánico (§3.1, tabla de llamadores). | owner (forma: redactor) |
| D-T5 | **El bloque HTTP sin conexión usable sigue con `outcome = connection_unavailable` y el motivo va en `HttpCallResult.ErrorCode`** (hoy repite `connection_unavailable`, `HttpBlockExecutor.cs:92`). **Sin migración:** ese resultado nunca escribe fila en `http_calls` (`IHttpBlockExecutor.cs:40-41`, `HttpBlockExecutor.cs:85-86`), así que ni `outcome varchar(24)` ni `error_code varchar(64)` cambian. | El motivo ya tiene un lugar con el significado correcto («`ErrorCode` lo precisa», `IHttpBlockExecutor.cs:10-15`). | redactor |
| D-T6 | **Todo camino a `HandoffAsync` arma su causa** (§4, tabla). Un traspaso que viene de un HTTP fallido **en el mismo paso del motor** lleva además el bloque HTTP, su `outcome`, su `errorCode`, su `status` y el nombre de su conexión. Se detecta por la entrada que arrancó el paso (`EngineInput.HttpResult` con `Ok = false`), no adivinando por el trace. | Un recorrido del motor sólo se corta al esperar o al pedir un efecto externo (`FlowEngine.cs:276-280`): todo lo que pasa entre el resultado del HTTP y el traspaso es consecuencia de ese resultado. El trace guarda `nodeId/kind/outcome` pero no el código (`RunState.cs`, `TraceEntry`). | owner (forma: redactor) |
| D-T7 | **Se extiende `AutomationPaused` con un objeto opcional `handoff`**; no hay tipo de evento nuevo. Los eventos guardados sin `handoff` se leen como hoy y se pintan con el texto de hoy. | Es el mismo hecho (el asistente se pausó por traspaso), un solo evento por cambio, así que la regla de los `occurred_at` distintos (CLAUDE.md, «Dos eventos del mismo cambio…») no aplica; un tipo nuevo obligaría a escribir dos eventos con `now + 1 µs` y a que cada pantalla vieja pinte una línea genérica de más. | redactor |
| D-T8 | **`ConversationAutomationDetailsDto` gana `failureCode`** (el de `conversation_runs.failure_code`, `ConversationRun.cs:53`, columna de 64 `AutomationsDbContext.cs:199`). La causa completa **no** se repite ahí: vive en el evento. | El `failureCode` sale gratis del mismo `FindLatestAsync`; la causa completa obligaría a leer los eventos de Mensajería desde otra consulta. | redactor |
| D-T9 | **`Effect.Handoff` lleva el motivo explícito** (`HandoffReasons.Block` / `HandoffReasons.InvalidReply`), en vez de que `RunEffects` lo deduzca por el tipo de bloque. | Hoy se emite en dos lugares (`FlowEngine.cs:206` y `324`); un tercero no debe quedar mal clasificado en silencio. El motor sigue puro. | redactor |
| D-T10 | **Los motivos de conexión son códigos `snake_case` que arma Bootstrapper** (`ConnectionReasonCodes`, §3.2) desde el enum de Integrations; los módulos sólo emiten los suyos (`not_chosen`, `auth_header_reserved`, `credentials_rejected`). | Automations y Messaging no referencian Integrations (`AutomationsLayerTests`, `IntegrationsLayerTests`); un solo traductor evita dos copias de la misma tabla. | redactor |
| D-T11 | **Messaging agrega `IMessagingConnectionDirectory.ResolveSenderWithReasonAsync`** y deja `ResolveSenderAsync` como está; sólo `BotOutbound` usa el nuevo. | `ResolveSenderAsync` tiene 14 llamadores (envíos de asesor, plantillas, campañas, recibos de lectura, medios) que no necesitan la causa; cambiar su firma sería mover medio módulo por un caso. | redactor |
| D-T12 | **El dominio de Mensajería limpia la causa antes de guardarla** (`AutomationHandoff.Create`, §5.1): títulos y nombres con `SnapshotText.Fit(…, 120)`; códigos que no cumplen `^[a-z0-9_.]{1,64}$` y ids de más de 64 se guardan `null`; un `status` fuera de 100..599 se guarda `null`. | D-T3 hecho regla y no costumbre: aunque un adaptador futuro pasara un mensaje de error, no llega al hilo. 120 es el largo del paso del asistente (`Conversation.cs:348-352`). Un código recortado sería otro código: se descarta. | redactor |

## 3. Integrations y los adaptadores de conexión

### 3.1 `ConnectionResolution` (D-T4)

En `Modules.Integrations.Application/IIntegrationConnections.cs` (nuevo, junto a `ResolvedConnection`):

```csharp
/// <summary>Por qué una conexión no se puede usar; el primero que aplica, en este orden.</summary>
public enum ConnectionUnavailableReason
{
    NotFound,          // no existe o es de otro tenant (el repositorio filtra por tenant)
    Paused,            // Status == Paused
    NeedsAttention,    // Status == NeedsAttention
    ProviderHidden,    // el catálogo no conoce el proveedor, o no es visible con los módulos del tenant
    ProviderDisabled,  // visible pero no habilitado para el tenant (ProviderVisibility.IsEnabledAsync)
    SecretUnreadable,  // algún secreto no descifra (ConnectionSecrets.Read → Unreadable)
}

/// <param name="Name">El nombre de la conexión cuando existe, también si no se puede usar; null en NotFound.</param>
public sealed record ConnectionResolution(ResolvedConnection? Connection, ConnectionUnavailableReason? Unavailable, string? Name)
{
    public static ConnectionResolution Resolved(ResolvedConnection connection);
    public static ConnectionResolution Unusable(ConnectionUnavailableReason reason, string? name);
}

Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
```

`IntegrationConnections.ResolveAsync` (`IIntegrationConnections.cs:54-77`) hace las mismas comprobaciones de hoy, en el mismo
orden, y devuelve el motivo de la primera que falla. `Connection` no es `null` **sólo** si `Unavailable` es `null`. El
`ToString` de `ResolvedConnection` sigue sin imprimir secretos (`IIntegrationConnections.cs:17-18`).

**Llamadores** (todos en Bootstrapper; el comportamiento no cambia salvo donde se dice):

| Llamador | Hoy | Después |
| --- | --- | --- |
| `AutomationExternalApis.ResolveAsync` (`AutomationExternalApis.cs:24`) | `null` → sin credenciales | Devuelve `ExternalApiResolution` con el motivo (§3.2). **Cambia.** |
| `MessagingConnectionDirectory.ResolveSenderAsync` (`MessagingConnectionDirectory.cs:30`) | `null` → sin remitente | Pasa a ser `(await ResolveSenderWithReasonAsync(…)).Sender` (§3.3). Igual. |
| `MessagingConnectionDirectory.ResolveInstagramSenderAsync` (`:44`), `ResolveTemplateSourceAsync` (`:70`), `ListActiveAsync` (`:90`, `:99`) | `is { } resolved` | `.Connection is { } resolved`. Igual. |
| `QuotationsWhatsAppChannelDirectory.ListAvailableAsync` (`QuotationsWhatsAppChannelDirectory.cs:20`) | `resolved is null` | `.Connection`. Igual. |
| `ShopifyConnectionDirectory.OperationalTokenAsync` (`ShopifyConnectionDirectory.cs:107`) | `resolved is not null` | `.Connection`. Igual. |

Dobles de prueba que implementan el puerto y se ajustan: `StubIntegrationConnections` (`AutomationAdaptersTests.cs:415`),
`StubConnections` (`QuotationsWhatsAppChannelDirectoryTests.cs:102`), `FakeIntegrationConnections`
(`ShopifyAdaptersTestDoubles.cs:340`). Las pruebas de integración que llaman `ResolveAsync` directo (`ConsumerPortsApiTests`,
`InstagramConnectApiTests`, `ProviderEnablementApiTests`) leen `.Connection`.

### 3.2 Bloque HTTP: `ExternalApiResolution` y el motivo en `ErrorCode` (D-T5, D-T10)

Puerto de Automations (`AutomationsPorts.cs:122-129`), cambia `ResolveAsync`:

```csharp
/// <param name="UnavailableReason">null si Credentials no es null; si no, uno de §3.4.</param>
/// <param name="ConnectionName">El nombre de la conexión cuando existe (para el hilo); null si no existe.</param>
public sealed record ExternalApiResolution(ExternalApiCredentials? Credentials, string? UnavailableReason, string? ConnectionName);

Task<ExternalApiResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
```

`AutomationExternalApis` (Bootstrapper): con `Unavailable` → `ConnectionReasonCodes.Of(reason)`; otro proveedor →
`wrong_provider`; URL base que no es absoluta o clave vacía → `incomplete`. Las credenciales nunca viajan con un motivo.

`HttpBlockExecutor.ExecuteAsync` (`HttpBlockExecutor.cs:85-93`):

- `node.ConnectionId` es `null` → `ErrorCode = not_chosen`.
- `Credentials` es `null` → `ErrorCode = UnavailableReason`.
- `urlPolicy.IsReservedHeader(AuthHeader)` → `ErrorCode = auth_header_reserved`.
- En los tres: `Outcome = connection_unavailable`, `Ok = false`, sin fila y con el mismo `LogUnavailable` (el log lleva el
  `ErrorCode` además del outcome; no lleva URL ni clave).

`HttpCallResult` (`IHttpBlockExecutor.cs:15`) gana `string? ConnectionName = null`, que el ejecutor llena siempre que la
resolución lo trajo (con o sin éxito). No va a `http_calls`.

### 3.3 Envío del bot: el motivo de la conexión de WhatsApp (D-T11)

- `IMessagingConnectionDirectory` (`IMessagingConnectionDirectory.cs:44-45`) gana
  `Task<MessagingSenderResolution> ResolveSenderWithReasonAsync(Guid tenantId, Guid connectionId, CancellationToken)` con
  `public sealed record MessagingSenderResolution(MessagingSender? Sender, string? UnavailableReason, string? ConnectionName)`.
  El adaptador (`MessagingConnectionDirectory.cs`) usa `ConnectionReasonCodes`; otro proveedor → `wrong_provider`; sin
  `PhoneNumberId` o sin token → `incomplete`.
- `BotSendResult.ConnectionUnavailable` (`BotOutbound.cs:19`) pasa a `ConnectionUnavailable(string? Reason = null, string?
  ConnectionName = null)`. `BotOutbound.SendAsync` usa el método nuevo (`BotOutbound.cs:109`) y, cuando la liquidación tira
  `messaging.connection_unavailable` porque Meta rechazó la credencial (`OutboundSettlement.cs:47-56`, `126-127`; capturado en
  `BotOutbound.cs:127-130`), devuelve `ConnectionUnavailable("credentials_rejected", <nombre de la resolución>)`.
- `BotSendOutcome.Rejected` (`AutomationsPorts.cs:254`) pasa a `Rejected(string Code, string? ConnectionReason = null, string?
  ConnectionName = null)`; `AutomationMessenger.Map` (`AutomationMessenger.cs:55`) copia los dos campos.

Dobles que implementan `IMessagingConnectionDirectory` y se ajustan: `FakeConnectionDirectory` (`MessagingTestBed.cs:343`),
`FakeDirectory` (`WebhookRoutingTests.cs:43`), `StubDirectory` (`AutomationAdaptersTests.cs:390`).

### 3.4 Los motivos de conexión

| Código | Lo emite | Significa |
| --- | --- | --- |
| `not_found` | `ConnectionReasonCodes` | La conexión no existe (o es de otro tenant). |
| `paused` | `ConnectionReasonCodes` | Está `Paused`. |
| `needs_attention` | `ConnectionReasonCodes` | Está `NeedsAttention`. |
| `provider_hidden` | `ConnectionReasonCodes` | El proveedor no es visible para los módulos del tenant. |
| `provider_disabled` | `ConnectionReasonCodes` | El proveedor no está habilitado para el tenant. |
| `secret_unreadable` | `ConnectionReasonCodes` | Un secreto no descifra (el caso 1 de §1). |
| `wrong_provider` | `AutomationExternalApis`, `MessagingConnectionDirectory` | La conexión es de otro proveedor. |
| `incomplete` | `AutomationExternalApis`, `MessagingConnectionDirectory` | Le falta un campo o secreto requerido (URL base, clave, número, token). |
| `not_chosen` | `HttpBlockExecutor` | El bloque HTTP no tiene conexión elegida. |
| `auth_header_reserved` | `HttpBlockExecutor` | El header de la clave es uno reservado (P2-R30). |
| `credentials_rejected` | `BotOutbound` | Meta rechazó el token al enviar (la conexión ya quedó `NeedsAttention` por `ReportRejectedAsync`). |

## 4. Automations: de dónde sale la causa (D-T6, D-T9)

### 4.1 Todos los caminos a `HandoffAsync`

`conversations.HandoffAsync` sólo se llama desde `RunEffects` (`RunEffects.cs:92` y `:149`). Lo demás que termina una corrida
**no** traspasa: `StopAsync` con `NotAutomated` cierra `Interrupted` y con `WindowClosed` llama `EndAsync`
(`RunEffects.cs:181-189`); `StandDownAsync` y `AbandonAsync` llaman `EndAsync` (`RunProcessor.cs:150-166`, `188-230`);
`HandsOverAsync` atrapa el `DomainException` y cierra `Interrupted` (`RunEffects.cs:204-217`).

| # | Camino | Dónde | `kind` | `code` | `node` | `http` |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | El flujo **llega** a un bloque «Pasar a un asesor» | `FlowEngine.cs:322-325` → `RunEffects.cs:91-98` | `Block` | `null` | el bloque de traspaso | si el paso empezó con un HTTP fallido |
| 2 | Botones o Lista con `fallback = handoff` y una respuesta inválida | `FlowEngine.cs:204-209` → `RunEffects.cs:91-98` | `InvalidReply` | `null` | el bloque que esperaba | nunca (el paso empezó con un mensaje) |
| 3 | Un envío del bot que Mensajería rechaza | `RunEffects.cs:62-66` → `StopAsync` `:190-192` → `FailAsync` | `SendFailed` | el de Mensajería (`messaging.connection_unavailable`, `messaging.message.rejected`, `messaging.interactive.invalid`, …) | el bloque cuyo mensaje no salió (`Effect.NodeId`) | si el paso empezó con un HTTP fallido |
| 4 | Tope de reentradas (P2-R40) | `RunEffects.cs:75-79` | `FlowFailed` | `automations.run.loop_detected` | el bloque que pidió la reentrada 26 | si el paso empezó con un HTTP fallido |
| 5 | `EndRun` por ciclo o bloque inexistente (P2-R13, R14, R17) | `FlowEngine.cs:56`, `271-273`, `283-285` → `RunEffects.cs:112-115` | `FlowFailed` | `automations.run.loop_detected` / `automations.run.target_missing` | `end.NodeId` | si el paso empezó con un HTTP fallido |
| 6 | La versión de la corrida ya no existe (P2-R6) | `RunProcessor.cs:349-353` | `FlowFailed` | `automations.run.version_missing` | `null` (no hay flujo) | `null` |
| 7 | Corrida `Running` cuyo bloque no pide efecto | `RunProcessor.cs:379-383` | `FlowFailed` | `automations.run.target_missing` | `run.CurrentNodeId` | `null` |

Caso 1 de §1 = fila 3 con `node = handoff`, `code = messaging.connection_unavailable`, `connectionReason = secret_unreadable` y
`http = { policy-lookup, connection_unavailable, secret_unreadable }`. Caso 2 = fila 1 con
`http = { policy-lookup, error_status, http_404, 404 }`.

El título de un bloque se busca en `unit.Flow.Nodes` con el primero que tenga ese id, como el motor (`FlowEngine.cs:150-153`);
si no está (el Inicio, una versión corrupta), `nodeTitle = null`.

### 4.2 Tipos y cambios del motor

**Dominio del motor** (`Modules.Automations.Domain/Engine`), puro como hoy:

- `Effect.Handoff(string NodeId, string Reason = HandoffReasons.Block)` (`Effect.cs:22`), con
  `public static class HandoffReasons { Block = "block"; InvalidReply = "invalid_reply"; }`. `Walk.Reject` emite
  `InvalidReply` (`FlowEngine.cs:206`); `Enter` de `HandoffNode` emite `Block` (`FlowEngine.cs:324`). D-T9.
- `EngineInput.HttpResult` (`EngineInput.cs:21`) gana `string? Outcome = null, string? ConnectionName = null` al final. **El
  motor no los lee**, igual que hoy no lee `Status` ni `ErrorCode` (`FlowEngine.cs:100-114` sólo mira `Ok` y `Mapped`): viajan
  para que el bucle de efectos explique un traspaso, también en el camino de `PendingInputAsync` (`RunEffects.cs:129-143`), que
  arma la entrada por el mismo `CallHttpAsync`.

**Aplicación** (`Modules.Automations.Application/Engine/HandoffCause.cs`, nuevo):

```csharp
public enum HandoffCauseKind { Block, InvalidReply, SendFailed, FlowFailed }

public sealed record HandoffHttpFailure(string NodeId, string? NodeTitle, string Outcome, string? ErrorCode, int? Status, string? ConnectionName);

/// <param name="Code">El failure_code de la corrida en SendFailed y FlowFailed; null en Block e InvalidReply.</param>
public sealed record HandoffCause(
    HandoffCauseKind Kind, string? Code, string? NodeId, string? NodeTitle,
    string? ConnectionName, string? ConnectionReason, HandoffHttpFailure? Http);
```

- `IAutomationConversations.HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, CancellationToken)`
  (`AutomationsPorts.cs:234`).
- `RunEffects.FailAsync(ConversationRun run, RunState state, HandoffCause cause, DateTimeOffset now, CancellationToken)`
  (`RunEffects.cs:146`): cierra con `cause.Code` como `failure_code` (lo que hoy recibe como `failureCode`), así la causa y
  el código guardado no pueden divergir. `Code` nunca es `null` en `SendFailed` ni `FlowFailed`.
- `RunEffects.RunAsync` (`RunEffects.cs:35-123`) guarda, antes de cada `FlowEngine.Step`, la entrada con que arranca el paso;
  si es `EngineInput.HttpResult { Ok: false }`, de ella sale `HandoffHttpFailure` (`Outcome ?? "error"`, `ErrorCode`, `Status`,
  `ConnectionName`, título por `NodeId`). `CallHttpAsync` (`RunEffects.cs:233-237`) pasa `result.Outcome` y
  `result.ConnectionName` a la entrada.
- `RunProcessor` (`RunProcessor.cs:351`, `381`) arma las causas de las filas 6 y 7.

La auditoría no cambia (`automations.run.handed_off` / `automations.run.failed`, `RunEffects.cs:97`, `155`). El simulador
(`Simulations.cs:76-78`) no cambia: su `HttpResult` usa los parámetros por defecto; su línea «La llamada a la API falló (…)»
(`Simulations.cs:124`) pasa a mostrar el motivo (`secret_unreadable`) en vez de `connection_unavailable`, que es mejor y no se
prueba hoy.

## 5. Messaging: el evento (D-T7, D-T12)

### 5.1 Dominio

En `Modules.Messaging.Domain/ConversationAutomation.cs` (junto a `AutomationPauseReason`, `ConversationAutomation.cs:13-20`):

```csharp
/// <summary>Spec 2026-10-24: de qué camino salió un traspaso del asistente. Se guarda por nombre.</summary>
public enum AutomationHandoffKind { Block, InvalidReply, SendFailed, FlowFailed }

public sealed record AutomationHandoffHttp(string? NodeId, string? NodeTitle, string? Outcome, string? ErrorCode, int? Status, string? ConnectionName);

public sealed record AutomationHandoff(
    AutomationHandoffKind Kind, string? Code, string? NodeId, string? NodeTitle,
    string? ConnectionName, string? ConnectionReason, AutomationHandoffHttp? Http)
{
    public const int TextMaxLength = 120;   // títulos y nombres: SnapshotText.Fit
    public const int CodeMaxLength = 64;    // códigos e ids: se descartan si no caben o no son ^[a-z0-9_.]+$
    public static AutomationHandoff Create(...);   // aplica D-T12; un http sin nodeId ni outcome queda null
}
```

- `ConversationEvent` (`ConversationEvent.cs:28-36`) gana `AutomationHandoff? Handoff = null` al final. Sólo lo usa
  `AutomationPaused` con `PauseReason = Handoff`.
- `Conversation.PauseAutomation(Guid? actorMemberId, AutomationPauseReason reason, DateTimeOffset now, AutomationHandoff?
  handoff = null)` (`Conversation.cs:369-383`): un `handoff` con un motivo que no es `Handoff` es `ArgumentException` (error de
  programación, no regla de negocio). Los llamadores de hoy (`ConversationAutomation.cs` de Application, `WithAutomationPause`,
  `Conversation.cs:416-426`) no cambian.

### 5.2 JSON del evento y DTO

`ConversationEventJson.Serialize` (`ConversationEventJson.cs:11-34`) escribe, si hay `Handoff`:

```json
{ "type": "AutomationPaused", "pauseReason": "Handoff",
  "handoff": { "kind": "SendFailed", "code": "messaging.connection_unavailable", "nodeId": "handoff",
               "nodeTitle": "Pasar a un asesor", "connectionName": "Servicio Clasic", "connectionReason": "secret_unreadable",
               "http": { "nodeId": "policy-lookup", "nodeTitle": "Buscar cliente por teléfono", "outcome": "connection_unavailable",
                         "errorCode": "secret_unreadable", "status": null, "connectionName": "API de Clasic" } } }
```

Las claves `null` se omiten al escribir (como los ids, `ConversationEventJson.cs:71-77`). `Parse` (`:37-69`) es tolerante como
con `pauseReason` (`:56-59`): un `kind` desconocido o un `handoff` que no es objeto dejan `Handoff = null`; un campo con otro
tipo se lee `null`; nunca tumba el hilo. `details` es `jsonb` (`MessagingDbContext.cs:291`): sin migración.

`MessageEventDto` (`MessagingDtos.cs:97`) gana `AutomationHandoffDto? Handoff = null`:

```csharp
/// <summary>Spec 2026-10-24 (BFF): la causa del traspaso, en códigos; el texto lo arma el frontend. Kind por nombre.</summary>
public sealed record AutomationHandoffDto(
    string Kind, string? Code, string? NodeId, string? NodeTitle, string? ConnectionName, string? ConnectionReason,
    AutomationHandoffHttpDto? Http);

public sealed record AutomationHandoffHttpDto(string? NodeId, string? NodeTitle, string? Outcome, string? ErrorCode, int? Status, string? ConnectionName);
```

`MessageMapping.ToDto` (`MessagingSupport.cs:190-200`) lo copia del evento.

### 5.3 El adaptador

`AutomationConversations.HandoffAsync` (`AutomationConversations.cs:78-97`) recibe la `HandoffCause`, la convierte a
`AutomationHandoff.Create(...)` y la pasa a `PauseAutomation`. El `kind` se traduce por nombre
(`Enum.Parse<AutomationHandoffKind>(cause.Kind.ToString())`), amarrado por una prueba de paridad en `Bootstrapper.UnitTests`
(como `ShopifyWebhookTopicsParityTests`). Un solo evento por cambio, con el mismo `now` de hoy: el orden con el texto que el
bot mandó antes no cambia.

### 5.4 «Datos capturados» (D-T8)

`ConversationAutomationDetails` (`IConversationAutomationDetails.cs:9-18`) y `ConversationAutomationDetailsDto`
(`GetConversationAutomation.cs:10-19`) ganan `string? FailureCode` al final; `AutomationConversationDetails`
(`AutomationConversationDetails.cs:38-47`) pasa `run.FailureCode`.

## 6. Contrato para el frontend

**El hilo.** Cada `MessageDto` de `kind = Event` trae `event` (`MessagingDtos.cs:97`). Desde este cambio, un
`event.type = "AutomationPaused"` con `event.pauseReason = "Handoff"` **puede** traer `event.handoff`:

| Campo | Tipo | Valores |
| --- | --- | --- |
| `handoff` | objeto o ausente/`null` | Ausente en eventos viejos y en pausas que no son traspaso. |
| `handoff.kind` | string | `Block`, `InvalidReply`, `SendFailed`, `FlowFailed`. Uno desconocido: texto genérico. |
| `handoff.code` | string o `null` | `SendFailed`: `messaging.connection_unavailable`, `messaging.message.rejected`, `messaging.interactive.invalid` (otro código de Mensajería es posible). `FlowFailed`: `automations.run.loop_detected`, `automations.run.target_missing`, `automations.run.version_missing`. `null` en `Block` e `InvalidReply`. |
| `handoff.nodeId` / `nodeTitle` | string o `null` | El bloque donde pasó (§4.1). `nodeTitle` mide ≤ 120 y puede terminar en «…». |
| `handoff.connectionName` / `connectionReason` | string o `null` | Sólo `SendFailed` con `messaging.connection_unavailable`: la conexión de WhatsApp y su motivo (§3.4). |
| `handoff.http` | objeto o `null` | El HTTP fallido que arrancó el paso (D-T6). |
| `http.nodeId` / `nodeTitle` | string o `null` | El bloque HTTP. |
| `http.outcome` | string o `null` | `error_status`, `timeout`, `network`, `mapping_failed`, `url_rejected`, `connection_unavailable`, `media_not_ready`, `response_too_large`, `body_not_json` (`IHttpBlockExecutor.cs:42-54`). |
| `http.errorCode` | string o `null` | Con `connection_unavailable`, un motivo de §3.4; con `error_status`, `http_<status>`; con `url_rejected`, el motivo técnico de la política (`scheme_not_https`, `path_dot_segment`, …); si no, repite el outcome. |
| `http.status` | número o `null` | El status HTTP, sólo si la API respondió. |
| `http.connectionName` | string o `null` | La conexión «API externa» del bloque, si existe. |

Ejemplos: caso 2 de §1 → `{ "kind": "Block", "nodeId": "handoff", "nodeTitle": "Pasar a un asesor", "http": { "nodeId":
"policy-lookup", "nodeTitle": "Buscar cliente por teléfono", "outcome": "error_status", "errorCode": "http_404", "status": 404,
"connectionName": "API de Clasic" } }`. Caso 1: el JSON de §5.2.

**«Datos capturados».** `GET …/messaging/conversations/{id}/automation` trae además `failureCode` (string o `null`; ausente
en un backend viejo): uno de los `code` de arriba cuando `status = Failed`.

**Compatibilidad.** El frontend de hoy ignora `handoff` y `failureCode` (`parseEvent` y `fetchConversationAutomationDetails`
leen claves sueltas, `messaging.api.ts:1233-1254`, `183-212`). El frontend nuevo con un backend viejo ve `handoff` ausente y
pinta el texto de hoy. Se pueden desplegar en cualquier orden.

## 7. Frontend (`qep-frontend`, `features/messaging/`)

- **Tipos** (`types/messaging.ts`): `AutomationHandoff`, `AutomationHandoffHttp` y `handoff: AutomationHandoff | null` en
  `ConversationEvent` (`types/messaging.ts:410-421`); `failureCode: string | null` en `ConversationAutomationDetails`
  (`:201-215`). `kind` se guarda como texto (un valor desconocido es contenido, como `status` de los detalles,
  `types/messaging.ts:205-209`).
- **Parser** (`services/messaging.api.ts`): `parseEvent` (`:1233-1254`) lee `handoff` con un lector propio que nunca lanza (un
  evento nunca puede dejar el hilo en blanco, `:1232`); `fetchConversationAutomationDetails` lee `failureCode` (`:203-212`).
- **Diccionario** (`utils/handoff-cause.ts`, nuevo): `describeHandoff(handoff)` y `describeRunFailure(code)`.
  `describeConversationEvent` (`utils/conversation-event.ts:53-60`) usa `describeHandoff` cuando `pauseReason = 'Handoff'` y hay
  `handoff`; si no, el texto de hoy.
- **Línea del hilo** (`components/conversation-event-line.tsx:22`): la píldora pasa de `rounded-full` a `rounded-2xl` para
  que dos líneas de texto no se vean deformes.
- **«Datos capturados»** (`components/captured-data-panel.tsx:57-64`): con `failureCode`, una línea «Motivo: …» bajo el
  estado.

### 7.1 Textos (tuteo, registro profesional)

Prefijo de toda causa: **«El asistente pasó la conversación a un asesor porque »**. `«{x}»` es el título o el nombre tal como
llega. Sin causa (`handoff` `null`) o con un `kind` desconocido: **«El asistente pasó la conversación a un asesor.»** (el de
hoy).

**Qué frase manda.** `Block` con `http` → la frase del HTTP. `Block` sin `http` → la del bloque. `InvalidReply` → la suya.
`SendFailed` con `http` → la del HTTP + « Además, » + la del envío. `SendFailed` sin `http` → la del envío. `FlowFailed` → la
suya (ignora `http`).

| Caso | Texto después del prefijo |
| --- | --- |
| `Block` | `el flujo llegó a «{nodeTitle}».` · sin título: `el flujo llegó a un bloque «Pasar a un asesor».` |
| `InvalidReply` | `el cliente no eligió una opción válida en «{nodeTitle}».` · sin título: `…en un menú.` |
| HTTP `error_status` | `«{http.nodeTitle}» respondió {status}.` (status, o el número de `http_NNN`) · sin ninguno: `…respondió con un error.` |
| HTTP `timeout` | `«{t}» no respondió a tiempo.` |
| HTTP `network` | `«{t}» no pudo conectarse con la API.` |
| HTTP `mapping_failed` | `«{t}» recibió una respuesta que no se pudo leer.` |
| HTTP `response_too_large` | `«{t}» recibió una respuesta de más de 1 MB.` |
| HTTP `url_rejected` | `«{t}» usa una dirección que QEP no tiene permitido llamar.` |
| HTTP `body_not_json` | `el cuerpo de «{t}» no es un JSON válido.` |
| HTTP `media_not_ready` | `«{t}» no recibió a tiempo el archivo que envió el cliente.` |
| HTTP `connection_unavailable` + `not_chosen` | `«{t}» no tiene una conexión elegida. Elígela en el editor del chatbot.` |
| … + `not_found` | `«{t}» usa una conexión que ya no existe. Elige otra en el editor del chatbot.` |
| … + `secret_unreadable` | `«{t}» no pudo usar {C}: su clave ya no se puede leer. Vuelve a pegarla en Integraciones.` |
| … + `paused` | `«{t}» no pudo usar {C}: está pausada. Reactívala en Integraciones.` |
| … + `needs_attention` | `«{t}» no pudo usar {C}: necesita atención. Revísala en Integraciones.` |
| … + `provider_hidden` / `provider_disabled` | `«{t}» no pudo usar {C}: su proveedor no está habilitado para tu empresa.` |
| … + `wrong_provider` | `«{t}» no pudo usar {C}: no es una conexión de tipo «API externa». Elige otra en el editor del chatbot.` |
| … + `incomplete` | `«{t}» no pudo usar {C}: le falta la URL base o la clave. Complétala en Integraciones.` |
| … + `auth_header_reserved` | `«{t}» no pudo usar {C}: el header de la clave no está permitido. Cámbialo en Integraciones.` |
| … + otro | `«{t}» no pudo usar {C}. Revísala en Integraciones.` |
| HTTP, otro `outcome` | `«{t}» falló.` |
| Envío `messaging.connection_unavailable` | `no pudo enviar su mensaje: {W} {motivo}.` con motivo `paused` «está pausada», `needs_attention` «necesita atención», `credentials_rejected` «fue rechazada por Meta», `secret_unreadable` «tiene una clave que ya no se puede leer», `not_found` «ya no existe», otro o `null` «no está disponible»; y « Revísala en Integraciones.» salvo con «no está disponible» sin motivo. |
| Envío `messaging.message.rejected` | `WhatsApp no aceptó el mensaje de «{nodeTitle}».` |
| Envío `messaging.interactive.invalid` | `el mensaje con botones o lista de «{nodeTitle}» no cumple las reglas de WhatsApp.` |
| Envío, otro código | `no pudo enviar el mensaje de «{nodeTitle}».` |
| `FlowFailed` `loop_detected` | `el flujo daba vueltas sin esperar al cliente (en «{nodeTitle}»).` · sin título, sin el paréntesis |
| `FlowFailed` `target_missing` | `una salida de «{nodeTitle}» apunta a un bloque que no existe.` · sin título: `una salida apunta…` |
| `FlowFailed` `version_missing` | `la versión del chatbot con que empezó ya no existe.` |
| `FlowFailed`, otro | `el flujo falló.` |

`{t}` = `http.nodeTitle`; sin título, el sujeto es «una llamada a la API» sin comillas. `{C}` = `la conexión «{connectionName}»`
o `su conexión`. `{W}` = `la conexión de WhatsApp «{connectionName}»` o `la conexión de WhatsApp`.

Resultados esperados de los casos de §1:

- Caso 2: «El asistente pasó la conversación a un asesor porque «Buscar cliente por teléfono» respondió 404.»
- Caso 1: «El asistente pasó la conversación a un asesor porque «Buscar cliente por teléfono» no pudo usar la conexión «API de
  Clasic»: su clave ya no se puede leer. Vuelve a pegarla en Integraciones. Además, no pudo enviar su mensaje: la conexión de
  WhatsApp «Servicio Clasic» tiene una clave que ya no se puede leer. Revísala en Integraciones.»

**«Datos capturados»** (`describeRunFailure`): `messaging.connection_unavailable` → «Motivo: no pudo enviar un mensaje porque la
conexión de WhatsApp no está disponible.»; `messaging.message.rejected` → «Motivo: WhatsApp no aceptó un mensaje.»;
`messaging.interactive.invalid` → «Motivo: un mensaje con botones o lista no cumple las reglas de WhatsApp.»;
`automations.run.loop_detected` → «Motivo: el flujo daba vueltas sin esperar al cliente.»; `automations.run.target_missing` →
«Motivo: una salida apunta a un bloque que no existe.»; `automations.run.version_missing` → «Motivo: la versión del chatbot con
que empezó ya no existe.»; otro → «Motivo: ocurrió un error en el flujo.»; `null` → sin línea.

## 8. Pruebas (TDD, RED antes que GREEN)

- **Unitarias Integrations:** `ConsumerPortsTests` — cada motivo de `ConnectionResolution` con el nombre (`NotFound` sin nombre);
  la de hoy (`ResolveIsNullFor…`, `ConsumerPortsTests.cs:33-54`) pasa a afirmar `Connection = null` y el motivo.
- **Unitarias Automations:** `FlowEngineWaitingTests` (el `Handoff` de `fallback = handoff` lleva `InvalidReply`;
  `FlowEngineWaitingTests.cs:434`), `FlowEngineTests` (el bloque lleva `Block`), `HttpBlockExecutorTests` (motivo en
  `ErrorCode` y `ConnectionName`; `:211-217`, `:345-354`), `RunProcessorTests` (una causa por fila de §4.1, con el doble
  `Conversations` de `RunProcessorTests.cs:606` guardando la causa).
- **Unitarias Messaging:** `ConversationAutomationTests` (el evento lleva la causa; `handoff` con otro motivo lanza),
  `AutomationHandoffTests` nuevo (D-T12), `ConversationEventJsonTests` (ida y vuelta, evento viejo sin `handoff`, `kind`
  desconocido), `ConversationAutomationMappingTests` (el DTO).
- **Unitarias Bootstrapper:** `AutomationAdaptersTests` (`AutomationExternalApis` y `AutomationMessenger` con motivo),
  paridad de `HandoffCauseKind` ↔ `AutomationHandoffKind`, `ConnectionReasonCodes`.
- **Integración:** `ConsumerPortsApiTests` (secreto ilegible con el byte invertido, como `ConnectionSecretRekeyWorkerTests.cs:75`),
  `BotOutboundTests` (Messaging, `ConnectionUnavailable` con motivo), `AutomationConversationsAdapterTests` (el `details` del
  evento lleva `handoff`; `AutomationConversationsAdapterTests.cs:100-122`), `ClasicFlowEndToEndTests` (los dos casos de §1 de
  punta a punta, y `AnApiErrorHandsOff`, `:364-387`, afirma la causa), `RunsPersistenceTests` (doble `FailingEndConversations`,
  `:332`).
- **ArchitectureTests:** no cambian y siguen verdes: los tipos nuevos de Automations viven en su Application, los de Messaging en
  su Domain/Application, y la traducción en Bootstrapper.
- **Vitest:** `messaging.api.test.ts` (parser tolerante), `handoff-cause.test.ts` nuevo (tabla de §7.1 entera más los dos casos de
  §1), `conversation-event.test.ts`, `$conversationId.test.tsx` (la línea del hilo), `contact-sheet.test.tsx` («Motivo: …»).

## 9. DECISIÓN-PENDIENTE (del owner)

- **P-1. ¿El bloque «Pasar a un asesor» calla cuando el flujo llegó a él por un `error`?** Hoy, si el bloque tiene texto, lo
  manda siempre (`FlowEngine.cs:322-325`); en el caso 2 de §1 el cliente recibió «Te paso con un asesor.» aunque el traspaso
  vino de un 404. Opciones: (a) dejarlo como está y que quien arma el flujo vacíe el texto (D-T2; es lo que este spec supone);
  (b) que el motor no mande el texto cuando el paso empezó con un HTTP fallido (cambia el comportamiento de flujos ya
  publicados; el fixture de Clasic, `tests/…/Fixtures/clasic.json`, tiene once salidas `error` hacia `handoff`). Recomiendo (a): el autor puede poner un bloque «Mensaje» propio en
  la salida `error` si quiere avisar, y (b) es invisible en el editor.
- **P-2. ¿Se vacía el texto del traspaso en la plantilla del sistema «Fuera de horario»?** `outside-hours.json:18` trae
  `"text": "Te paso con un asesor."`. Afecta sólo a instalaciones nuevas de la plantilla.

**Decidido por el owner (2026-10-24):** P-1 → (a), el motor no cambia y el texto del bloque sigue en manos de quien arma el
flujo. P-2 → sí, la plantilla «Fuera de horario» deja el bloque de traspaso sin texto.

## 10. Fuera de alcance y seguimientos

- Cambiar **cuándo** traspasa el bot, reintentar un HTTP o un envío, avisar por otro canal (correo, notificación).
- **F-1.** Marcar la conexión `NeedsAttention` sola cuando un secreto no descifra (`secret_unreadable`). Hoy nadie la marca y el
  síntoma se repite en cada mensaje; con este cambio por lo menos se ve.
- **F-2.** La palabra de reinicio con puntuación: `TextMatch.Normalize` conserva `*` y `.`, así que «*reiniciar*» o «reiniciar.»
  no reinician.
- La causa en la fila de la bandeja o en el chip del encabezado: el hilo alcanza para el pedido del owner.

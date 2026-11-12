# Agentes IA con Claude — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un tenant cree **agentes IA** que atienden las conversaciones de Mensajería (WhatsApp e Instagram) con
**Claude** a través de la API de Anthropic, con herramientas propias de QEP (catálogo, estado de pedidos, borrador de
cotización, traspaso a humano), sin canvas y sin bloquear el motor de chatbots.

**Architecture:** Es el spec [`2026-10-29-agentes-ia-design.md`](../specs/2026-10-29-agentes-ia-design.md) con el proveedor
cambiado: donde el spec dice OpenAI, este plan pone Anthropic. Todo lo demás del spec se conserva tal cual: módulo
nuevo `Ai` (D-A1), ocupación neutral de la conexión en Mensajería (§4.3), ejecución en dos etapas con cola de trabajos
de modelo fuera del lock y de la transacción (D-A13), herramientas con principal de servicio (§8), playground (§7) y
registro de consumo (D-A27). El adaptador `ClaudeModelClient : IAiModelClient` usa el **SDK oficial de C#**
(`Anthropic`, NuGet) sobre la **Messages API**, con un bucle de herramientas manual (no `BetaToolRunner`), porque el
worker tiene que contar llamadas, registrar consumo por llamada, aplicar los topes de D-A20 y escribir `ai.tool_calls`
entre vuelta y vuelta.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3,
Testcontainers.PostgreSql, `WebApplicationFactory<Program>`, paquete NuGet `Anthropic` (13.x).

**Spec:** [`docs/superpowers/specs/2026-10-29-agentes-ia-design.md`](../specs/2026-10-29-agentes-ia-design.md). Este plan
**reemplaza** §4.5, §9, D-A22, D-A24, D-A25 (sólo el valor del proveedor), D-A26 y la tabla de errores de §6.7, y
**resuelve** P-1, P-6 y parte de P-2. Spec de producto (frontend, sólo contexto):
`qep-frontend/docs/superpowers/specs/2026-10-28-agentes-ia-design.md`.

---

## Antes de empezar

- Rama propia desde `develop` (`feat/agentes-ia-<slice>`), una por slice. Antes de **cada** commit,
  `git branch --show-current`; si dice `main`, frena y pregunta.
- Todo comando para el developer va en PowerShell, uno por línea, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. No lo detengas:
  avísale al developer.
- Pruebas de integración: Docker arriba y `$env:Seed__Enabled = "false"` en la misma sesión.
- **Nunca** se imprime una clave de Anthropic (`sk-ant-…`): ni en logs, ni en pruebas, ni al verificar que una conexión
  existe. Las pruebas usan claves falsas (`sk-ant-test-0000`) contra un servidor HTTP falso.
- `docs/estado.md` **no** se toca en las ramas de feature: se actualiza en `develop` al mergear cada slice.

## Global Constraints

- **Proveedor único `anthropic`.** El modelo de datos sigue siendo neutral (`provider varchar(24)`, D-A25), pero el
  único valor es `anthropic`. Nada de OpenAI llega al código.
- **El SDK no sale de `Modules.Ai.Infrastructure`.** Application ve sólo `IAiModelClient` con tipos propios;
  `AiLayerTests` prohíbe `Anthropic*` en Domain y Application, igual que prohíbe EF Core.
- **Agregar el paquete toca los lock files.** `Anthropic` entra en `Directory.Packages.props` y los `packages.lock.json`
  se regeneran con `dotnet restore --force-evaluate` y se commitean **junto** con el props; sin eso el `docker build`
  (`--locked-mode`) muere con `NU1004`. La versión se fija exacta, la última estable con al menos dos semanas de
  publicada al empezar AI-B5.
- **Contenido fuera de logs y de trazas (D-A29).** El SDK emite spans de OpenTelemetry (`com.anthropic.sdk.csharp*`) en
  modo `metadata_only` por defecto; el cliente se construye con ese modo explícito y una prueba lo amarra. Nunca se usa
  `OpenTelemetryContentMode.Content`.
- **Comentarios en español, tuteando**; códigos de error, rutas, permisos y enums quedan como contrato.
- **TDD:** RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- **Commits:** conventional commits, sin atribución de IA, `git commit -m "…" -- <rutas>`.

## Decisiones de este plan (lo que cambia del spec)

Cada fila reemplaza la decisión del spec que nombra. Donde no se nombra una decisión, rige el spec.

### Proveedor, clave y modelos

| # | Decisión | Por qué |
| --- | --- | --- |
| C-1 (reemplaza D-A22 y §4.5) | La clave es una conexión de Integrations del proveedor nuevo **`anthropic`** («Anthropic (Claude)»), categoría `IntegrationCategory.Ai` (ya existe y nadie la usa), consumidor `[TenantModuleKeys.Ai]`, onboarding `Form`, `maxConnections: 1`, sin habilitación por tenant, un campo `apiKey` (`Secret`, requerido, ≤ 256, sin espacios, `revealSuffix: 4`). El puerto de Ai es `IAiProviderCredentials` y su adaptador en Bootstrapper no cambia de forma. | Mismo razonamiento del spec: cifrado AES-GCM, re-cifrado, `NeedsAttention`, auditoría y «el secreto nunca vuelve», gratis. |
| C-2 (reemplaza D-A24, resuelve P-6) | `AnthropicConnectionTester` llama **`GET /v1/models`** con la clave (no consume tokens) y después `GET /v1/models/{id}` del modelo predeterminado de `Ai:Models`. 401 → `credentials_rejected`; 403 → `permission_denied`; 404 del modelo → `model_unavailable` (la clave sirve, pero su organización no tiene ese modelo); 429/5xx/529/red → `unreachable`. El saldo **no** se puede verificar sin gastar: un 402 `billing_error` sólo se ve en la primera llamada real (§6.7). | Validar sin costo para el tenant y distinguir «clave mala» de «modelo no habilitado». |
| C-3 (reemplaza D-A26) | Lista curada `Ai:Models`, con `effort` por modelo además de `id`, `label`, `description`, `isDefault`, `available`: **`claude-opus-5-5`** (predeterminado), `claude-sonnet-5-5`, `claude-haiku-5-5`. Los ids se escriben exactos, sin sufijo de fecha. | Opus 5.5 es el modelo por defecto de Anthropic hoy. Que un tenant elija uno más barato es **su** decisión (D-4a), no del backend: por eso los tres están en la lista y ninguno se cambia solo. |
| C-4 (complementa D-A25) | `ai.usage_records` suma `cache_creation_input_tokens`, `cache_read_input_tokens` y `served_model` (el modelo que de verdad respondió, de `response.Model`: con el respaldo de C-9 puede no ser el pedido). | Sin el caché, el conteo de entrada no explica la factura del tenant; sin `served_model`, un respaldo se registra como el modelo pedido. |
| C-5 | **BYOK se conserva** (spec de producto D-4): cada tenant paga su propia cuenta de Anthropic. Una clave de plataforma (QEP paga y cobra por consumo) es una decisión del owner que **no** toma este plan: queda como P-13. | El spec de producto no se reabre acá. El diseño admite las dos: sólo cambia el adaptador de `IAiProviderCredentials`. |

### Llamada al modelo (resuelve P-1)

| # | Decisión | Por qué |
| --- | --- | --- |
| C-6 | **Messages API sin estado**, `client.Beta.Messages.Create` **sin streaming**. El historial lo arma QEP en cada llamada (D-A28); no se usan *message threads* ni Managed Agents. | La API es sin estado: no queda conversación guardada en la cuenta del tenant (lo que el spec pedía apagar). Sin streaming porque la respuesta va a WhatsApp entera, no se pinta a medida que llega; con `MaxTokens` de 16.000 cabe en el timeout. El endpoint beta es por C-9. |
| C-7 | **Pensamiento:** no se manda `Thinking` (los tres modelos corren adaptativo por defecto; en Opus 5.5 apagarlo es un 400). La profundidad se controla sólo con `OutputConfig.Effort`, que se manda **siempre explícito** desde `Ai:Models` (Opus 5.5 y Haiku 5.5 tienen `medium` por defecto, Sonnet 5.5 `high`). Valor inicial `medium` para los tres hasta que el eval de AI-B9 diga otra cosa. | Opus 5.5 rechaza `{type: "disabled"}` y `budget_tokens`. El esfuerzo es la palanca de costo y latencia. |
| C-8 | **Herramientas con `strict: true`** y `additionalProperties: false`; `tool_choice` **`auto`** (o ausente). `send_reply` y `handoff_to_human` siguen siendo las herramientas finales del spec, pero una vuelta que termina en `end_turn` con texto y sin herramienta final **se acepta como `send_reply` sin `sourceIds`**. | En Opus 5.5 y Sonnet 5.5 forzar herramienta (`any`/`tool`) es un 400, así que «siempre termina en herramienta final» no se puede garantizar; tratar el texto suelto como respuesta evita un reintento caro. Con `answerOnlyFromSources`, la pérdida de `sourceIds` se ve en la actividad del agente. |
| C-9 | **Respaldo por rechazo:** beta `server-side-fallback-2026-07-01` con `fallbacks: "default"` (C#: `new Default()`) en Opus 5.5 y Sonnet 5.5. Haiku 5.5 no tiene respaldo del lado del servidor. Un `stop_reason == "refusal"` que llega igual → traspaso `AgentFailed` con `ai.provider.refused` y la categoría de `stop_details` en el evento interno; **no** cuenta como fallo de la clave. | Un rechazo de un clasificador de seguridad no debe dejar al cliente sin respuesta: el respaldo la contesta dentro de la misma llamada y, si no se puede, la conversación va a un humano. |
| C-10 | **Bucle append-only.** Dentro de un trabajo, cada respuesta del asistente se devuelve **completa** (texto, `thinking` con su `Signature`, `tool_use`) en la siguiente llamada; todos los `tool_result` de una vuelta van en **un solo** mensaje `user`, con `is_error: true` el que falló. Entre trabajos no se reenvían bloques de pensamiento: el historial se rearma desde `messaging.messages` como texto. | Los bloques de pensamiento sólo son válidos sin modificar y en la misma conversación; un arnés que edita el historial los invalida. Separar los resultados en varios mensajes enseña al modelo a dejar de llamar herramientas en paralelo. |
| C-11 | **La petición siempre termina en un turno `user`.** Turnos del cliente seguidos se juntan en un solo mensaje `user` con un bloque de texto por mensaje. El **saludo** (turno `Greeting`) **no** va como mensaje `assistant` cuando es el último: va en un mensaje de sistema a mitad de conversación (`{"role":"system"}` dentro de `messages`) después del primer turno del cliente, «Ya enviaste este saludo automático: «…». No lo repitas.». | Un `assistant` al final es *prefill*, y los tres modelos responden 400. Con el saludo en el orden real (cliente → saludo), el primer trabajo de cada sesión caería ahí. |
| C-12 | **Caché de prompt.** Orden fijo: `tools` (orden determinista por nombre) → `system` [instrucciones fijas de QEP + `instructions` + `tone` + FAQ + respuestas rápidas ordenadas por id] con **un** `cache_control` explícito al final del `system` → historial, con caché automático de nivel superior para la cola. Lo que cambia en cada llamada (fecha y hora del tenant, minutos que le quedan a la ventana) va **después** del último punto de caché, en un mensaje de sistema a mitad de conversación. TTL de 5 minutos. | El bucle de herramientas hace varias llamadas en segundos con el mismo prefijo; un `DateTime.Now` dentro del `system` invalidaría todo en cada llamada sin error. Se verifica con `CacheReadInputTokens > 0` en una prueba (AI-B9). |
| C-13 (concreta D-A17 y D-A20) | `AnthropicClient` con **`MaxRetries = 0`** y `Timeout` = `Ai:Model:CallTimeout` (45 s). Los reintentos los hace el worker (D-A17), respetando `retry-after`. `MaxTokens` 16.000 (`Ai:Model:MaxOutputTokens`). | Con los reintentos del SDK (2 por defecto) el tiempo real es `timeout × 3`, y el trabajo pasaría su tope de 120 s y su lease sin que el worker lo sepa. |
| C-14 | **Clave por petición:** un `AnthropicClient` por trabajo, construido con `ApiKey` de la credencial resuelta y descartado al terminar; nunca uno global con clave, nunca la clave en configuración. Si el SDK acepta un `HttpClient` propio, se usa el nombrado `ai.anthropic` con `RemoveAllLoggers()` y User-Agent de QEP; si no, se deja el suyo (la instrumentación HTTP de OpenTelemetry sólo registra la URL). Se verifica en la Tarea 5.1. | BYOK: cada tenant, su clave. Mismo estándar del bloque HTTP (D-H5). |
| C-15 | **Entrantes que no son texto** (imagen, audio, video, documento, `Unsupported`): el MVP los pone en el historial como texto fijo («[El cliente envió una imagen]», con el pie de foto si lo trae). Leer la imagen o el PDF con visión queda **fuera** (§14). | Mantiene el MVP en texto, como el spec, sin que el agente ignore en silencio un mensaje que sí llegó. |

### Errores del proveedor (reemplaza la tabla de §6.7)

| Respuesta de Anthropic | Excepción del SDK (`Anthropic.Exceptions`) | Reintento | Efecto |
| --- | --- | --- | --- |
| Timeout, red | (conexión) | sí (D-A17) | Agotado → `ProviderFailed` `ai.provider.unavailable`. |
| 500 `api_error`, 529 `overloaded_error` | `Anthropic5xxException` y la de 529 | sí | Agotado → `ProviderFailed` `ai.provider.unavailable`. |
| 429 `rate_limit_error` | `AnthropicRateLimitException` | sí, con `retry-after` | Agotado → `ProviderFailed` `ai.provider.rate_limited`. **Siempre es tasa:** en Anthropic el 429 no significa cuota. |
| 402 `billing_error` | (por `type`) | no | `ProviderFailed` `ai.provider.quota_exhausted`; `issue` bloqueante en la credencial (como el `insufficient_quota` del spec). |
| 401 `authentication_error` | (por `type`) | no | `ProviderFailed` `ai.provider.credentials_rejected`; conexión a `NeedsAttention`; `issue` bloqueante. |
| 403 `permission_error` | (por `type`) | no | `ProviderFailed` `ai.provider.permission_denied`; `issue` bloqueante. |
| 404 `not_found_error` del modelo | (por `type`) | no | `AgentFailed` `ai.provider.model_unavailable`; `issue` en el agente. |
| 400 `invalid_request_error`, 413 | (por `type`) | no | `AgentFailed` `ai.agent.request_invalid`: es un bug de QEP, se loguea el `request_id` (nunca el cuerpo). |
| 200 con `stop_reason = refusal` | — | no (ya pasó por C-9) | `AgentFailed` `ai.provider.refused`. |
| 200 con `stop_reason = max_tokens` | — | 1 vez | Cuenta para `maxConsecutiveFailures`. |

Se clasifica por el `type` del error (`"billing_error"`, `"authentication_error"`…), no por string del mensaje. Los
nombres exactos de las excepciones se confirman contra el compilador en la Tarea 5.2: si una no existe como clase
propia, se cae al `type` de `AnthropicApiException`.

## Decisiones pendientes del owner

Las del spec siguen abiertas salvo P-1 y P-6 (resueltas arriba). Se suman:

| # | Pregunta | Por qué importa |
| --- | --- | --- |
| P-13 | ¿BYOK (cada tenant trae su clave de Anthropic, como dice el spec de producto) o una clave de plataforma de QEP con cobro por consumo? | Cambia sólo el adaptador de `IAiProviderCredentials` y la pantalla de Integraciones, pero con clave de plataforma QEP paga la factura y necesita los topes de P-11 como obligatorios, no opcionales. |
| P-14 (complementa P-7) | Los términos de tratamiento de datos ahora son los de Anthropic: con BYOK, el historial del cliente final viaja a la cuenta del tenant en Anthropic. ¿Basta con los términos del tenant o QEP tiene que avisar o pedir consentimiento? | Legal, no técnico. Bloquea AI-B6 (el que enciende canales reales), no los anteriores. |
| P-15 | ¿Se muestra un costo estimado en pesos o dólares junto a los tokens? Exigiría una tabla de precios por modelo en `Ai:Models` que alguien mantiene. | El spec dice «se muestra, no se cobra», en tokens. |

## Review Focus

1. El worker nunca llama al modelo con una transacción abierta ni con el lock de la conversación (D-A13).
2. La clave no aparece en logs, auditoría, `ai.*`, trazas ni respuestas (prueba con el host en `Trace`, como
   `TheKeyNeverAppearsInLogsAuditOrHttpCalls`).
3. Ninguna petición termina en `assistant` (C-11) y el bucle es append-only (C-10).
4. Todo error de §«Errores del proveedor» termina en traspaso: el cliente nunca se queda sin respuesta y sin humano.
5. La suite de Automations pasa sin cambios de comportamiento tras AI-B3 (salvo «sin ocupante no encola»).

---

## Tareas

Los slices son los de §13 del spec; las tareas con «Claude» en el título son las que este plan cambia. Las demás se
construyen como dice el spec y sólo se listan.

### Task 1 (AI-B1): Módulo `ai`, permisos y proyecto vacío

- [ ] RED: `TenantModuleKeyTests` espera `ai` en `All`, con dependencia `[Ai] = [Messaging]` y fuera de
  `DefaultForNewTenants`.
- [ ] GREEN: `Define("ai")` en `TenantModuleKey.cs`, `All`, `Dependencies`. Migración Tenancy `AddAiModuleKey`.
- [ ] Permisos `ai.agent.read|manage` en `AiPermissions`: constante, `PermissionDefinition`, rol admin por defecto **y**
  política en `AddAuthorization` (sin la política el síntoma es 500, no 403). `CompositionRootTests` los amarra.
- [ ] Proyectos `Modules.Ai.{Domain,Application,Infrastructure,Api}` vacíos, `AiLayerTests` que copia
  `AutomationsLayerTests`, suma `Modules.Automations` a los prohibidos **y** prohíbe `Anthropic` fuera de
  Infrastructure.
- [ ] Opciones `Ai:Models` (C-3) validadas al arrancar (`ValidateOnStart`): un solo `isDefault`, ids no vacíos,
  `effort` ∈ `low|medium|high|xhigh|max`. `GET /api/v1/tenants/{tenantId:guid}/ai/models`.
- [ ] Commit `feat(ai): add ai module key, permissions and curated Claude model list`.

### Task 2 (AI-B2): Proveedor `anthropic` en Integrations — Claude

- [ ] RED: `IntegrationProvidersTests` espera `anthropic` en `All`, categoría `Ai`, `maxConnections: 1`, campo
  `apiKey` secreto con `revealSuffix: 4`.
- [ ] GREEN: `IntegrationProviders.Anthropic` (C-1); migración `AddAnthropicProvider` (CHECK del proveedor y columna
  `hint`). `hint` en `FieldDefinition` y `SecretStateResponse` (D-A23).
- [ ] RED/GREEN: `AnthropicConnectionTester : IProviderConnectionTester` (C-2) contra un servidor HTTP falso: 200 → ok;
  401 → `credentials_rejected`; 403 → `permission_denied`; 404 del modelo → `model_unavailable`; 429, 529 y red →
  `unreachable`. El probador usa el SDK sólo en Infrastructure de Integrations **o** `HttpClient` directo con
  `x-api-key` y `anthropic-version`: se decide en esta tarea con el criterio de no duplicar el paquete en dos módulos.
  Recomendado: `HttpClient` directo (son dos `GET`), para que el SDK viva sólo en Ai.
- [ ] `IAiProviderCredentials` (Ai.Application) + `AiProviderCredentials` (Bootstrapper) sobre `ListActiveAsync` y
  `ResolveAsync`; `MarkNeedsAttentionAsync(connectionId, code)`.
- [ ] `GET /ai/settings`: estado de la credencial (`configured`, `hint`, `issue`), sin la clave.
- [ ] Commit `feat(integrations): add anthropic provider with key tester`.

### Task 3 (AI-B3): Ocupación neutral `messaging.connection_bots`

Como §4.3 y D-A2 del spec, sin cambios. Incluye el backfill desde `automations.chatbot_connections`, el `409
connection_taken` con `occupant` y la regresión completa de Automations.

### Task 4 (AI-B4): Agregado `Agent`, borrador, publicar y conexiones

Como §4.6, §4.7 y §5 del spec. Cambia sólo la validación del modelo: `config.model` contra `Ai:Models` (C-3) y
publicar exige conexión `anthropic` `Active` (`ai.provider.not_configured`, D-A8).

### Task 5 (AI-B5): `ClaudeModelClient`, herramientas de lectura, playground y consumo — Claude

**5.1 — Paquete y verificación del SDK (spike corto, sin código de producción)**

- [ ] `Anthropic` en `Directory.Packages.props` con versión exacta; referencia sólo desde
  `Modules.Ai.Infrastructure.csproj`. `dotnet restore --force-evaluate` y commit de los lock files en el mismo commit.
- [ ] Contra el SDK instalado (`strings` sobre el `.dll` o el compilador, sin sondas de reflexión), confirmar y anotar
  en el reporte: si `AnthropicClient` acepta un `HttpClient` (C-14); `MaxRetries` y `Timeout`; nombres de
  `ThinkingBlockParam`/`Signature`, `ToolResultBlockParam.IsError`, `Tool.Strict`, `CacheControlEphemeral`,
  `OutputConfig.Effort`, `Fallbacks`/`Default` en el namespace beta; `Usage.CacheReadInputTokens`; `StopDetails`.
- [ ] Commit `build(ai): add Anthropic SDK and regenerate lock files`.

**5.2 — Puerto y adaptador**

- [ ] `IAiModelClient.CompleteAsync(AiModelRequest, ct) → AiModelResponse` en Application, con tipos propios (spec §9):
  `AiModelRequest(Model, Effort, SystemPrompt, VolatileContext, Messages, Tools, MaxOutputTokens)`,
  `AiModelResponse(Blocks, StopReason, Usage(Input, Output, CacheCreation, CacheRead), ServedModel, RequestId)` y errores
  tipados (`ProviderUnavailable`, `RateLimited(RetryAfter?)`, `QuotaExhausted`, `CredentialsRejected`,
  `PermissionDenied`, `ModelUnavailable`, `Refused(Category)`, `InvalidRequest`, `InvalidResponse`). Los bloques del
  asistente se guardan **opacos** para devolverlos tal cual en la vuelta siguiente (C-10).
- [ ] RED: `ClaudeModelClientTests` contra un servidor HTTP falso que captura el cuerpo enviado:
  - sin `thinking` en el cuerpo, `output_config.effort` presente (C-7);
  - `tools[*].strict == true` y sin `tool_choice` forzado (C-8);
  - `cache_control` en el último bloque de `system` y la fecha **fuera** del `system` (C-12);
  - último mensaje con rol `user` o `system`, nunca `assistant` (C-11);
  - `fallbacks: "default"` y el header beta en Opus y Sonnet, ausentes en Haiku (C-9);
  - cada fila de la tabla de errores mapea a su error tipado; un 429 trae `RetryAfter`;
  - el `x-api-key` es el de la credencial de la petición y dos clientes de tenants distintos no comparten clave.
- [ ] GREEN: `ClaudeModelClient` en `Modules.Ai.Infrastructure/Model/`. Comentario en el adaptador con el porqué de C-8,
  C-11 y C-13: son los tres que el siguiente va a querer «simplificar».
- [ ] Commit `feat(ai): add Claude model client behind IAiModelClient`.

**5.3 — Armado del contexto**

- [ ] RED: `AgentPromptBuilderTests`: instrucciones fijas de QEP primero y en español; `instructions`, `tone`, FAQ y
  respuestas rápidas en orden estable (dos llamadas con la misma versión producen `system` idéntico byte a byte);
  turnos del cliente seguidos juntos (C-11); saludo como mensaje de sistema (C-11); entrantes no texto con su
  marcador (C-15); recorte a los últimos 30 turnos o `Ai:Model:MaxContextTokens`.
- [ ] GREEN: `AgentPromptBuilder` en Application (sin tipos del SDK).
- [ ] Commit `feat(ai): build agent prompt with stable cacheable prefix`.

**5.4 — Bucle de herramientas, consumo y playground**

- [ ] Herramientas de lectura `search_catalog` y `get_order_status` (spec §8.2) con sus puertos (§8.3); `send_reply` y
  `handoff_to_human` como finales. Esquemas JSON con `additionalProperties: false` y todos los campos en `required`.
- [ ] RED: `AgentTurnRunnerTests` con un `IAiModelClient` falso: varias llamadas a herramientas en una vuelta → un solo
  mensaje de resultados; herramienta que falla → `is_error`; topes D-A20 (6 llamadas al modelo, 8 a herramientas) →
  `AgentFailed`; texto suelto con `end_turn` → respuesta sin `sourceIds` (C-8); `max_tokens` → un reintento;
  `usage_records` con caché y `served_model` por **cada** llamada, también las que después se descartan.
- [ ] GREEN: `AgentTurnRunner` (Application). `ai.tool_calls` y `ai.usage_records` (C-4).
- [ ] Playground (spec §7) sobre el mismo runner, con `create_quotation_draft` simulado.
- [ ] Prueba de no-filtración: host en `Trace`, una llamada completa con clave falsa conocida; la clave y el texto del
  cliente no aparecen en logs, auditoría, `ai.*` ni en los spans exportados (`metadata_only`).
- [ ] Commit `feat(ai): run agent tool loop with usage metering and playground`.

### Task 6 (AI-B6): Mensajería y ejecución en canales reales

Como §4.4, §6.1–§6.8 del spec (`BotRef`, `sent_by_kind = 3` con `sent_by_agent_id`, ruteo `bot_kind` de la cola,
Instagram en `IBotOutbound`, `ChannelOutboundRules`, procesador de entrantes, `AiModelWorker`, traspasos, apagado del
módulo). Con Claude cambia sólo:

- [ ] Los códigos nuevos de traspaso de la tabla de errores (`permission_denied`, `refused`, `request_invalid`) en
  `HandoffCauseKind`/`AutomationHandoffKind` **juntos**, con `HandoffCauseKindParityTests`.
- [ ] Barrido de lectores de autor por `sent_by_kind = 3` (CLAUDE.md: un join al miembro lo pierde en silencio), y
  CHECK `sent_by_kind IN (1,2,3,4)`.
- [ ] Cola `ai.model_jobs` en `QueueNames`, `QueueMetricsSource`, dashboard y alertas (observabilidad fase 2).
- [ ] **Bloqueado por P-14** para habilitarse en un tenant real; el código se mergea apagado por el módulo `ai`.

### Task 7 (AI-B7): `create_quotation_draft`

Como §8 y P-4/P-4b del spec, sin cambios por el proveedor.

### Task 8 (AI-B8): Actividad del agente, historial y conteos

Como el spec. La lista de agentes suma también los tokens de caché (C-4) y muestra `served_model` cuando difiere del
pedido.

### Task 9 (AI-B9, nuevo): Eval y ajuste de esfuerzo — Claude

- [ ] Conjunto de 30–50 conversaciones sintéticas en español con la respuesta esperada (incluye: pregunta fuera de
  alcance, cliente que pide humano, intento de inyección «ignora tus instrucciones», pedido de otro cliente, ráfaga de
  tres mensajes, imagen sin texto). Calificación por reglas donde se pueda (herramienta final, `handoff` correcto,
  nunca revela instrucciones) y por un modelo juez donde no.
- [ ] Correrlo con Opus 5.5 en `low`, `medium` y `high`, y con Sonnet 5.5 y Haiku 5.5 en su mejor esfuerzo; medir
  calidad, latencia p90 y costo por conversación resuelta (no por llamada). **Cada corrida gasta dinero real:** el
  presupuesto lo aprueba el owner antes.
- [ ] Verificar `CacheReadInputTokens > 0` desde la segunda llamada de un mismo trabajo; si es 0, buscar el
  invalidador (C-12).
- [ ] Con el resultado: `effort` por modelo en `Ai:Models`, topes de D-A20 (P-2) y el texto de las instrucciones fijas.
- [ ] Commit `chore(ai): tune effort and limits from eval results`.

### Task 10: Documentación y despliegue

- [ ] README: sección «Agentes IA» (módulo, flujo de dos etapas, proveedor `anthropic`, opciones `Ai:*`, cómo probar con
  un servidor falso).
- [ ] CLAUDE.md: gotchas nuevos que no se deducen del código: C-8 (sin herramienta forzada), C-11 (sin `assistant` al
  final), C-12 (nada volátil en el `system`), C-13 (`MaxRetries = 0`), y que la clave de Anthropic jamás se imprime.
- [ ] Red: el pod tiene que llegar a `api.anthropic.com` (egress del clúster). Ninguna clave de Anthropic va en el
  ConfigMap ni en el Secret del despliegue: con BYOK viven cifradas en `integrations`.
- [ ] Frontend: el spec de producto dice OpenAI (proveedor, formato de clave, lista de modelos). Se le avisa a su
  developer; **no** se edita desde este repo.
- [ ] Al mergear cada slice a `develop`: fila de Agentes IA en `docs/estado.md` y su Registro.

## Spec discrepancies

- El spec asume OpenAI en §1, §4.5, §6.3 («llama a OpenAI»), §6.7, §9, §10 y §14; este plan lo reemplaza por Anthropic.
  El spec se marca con un aviso al inicio que apunta acá, sin reescribirlo.
- §6.3 dice «bucle hasta que el modelo llame `send_reply` o `handoff_to_human`»: con Claude no se puede forzar (C-8).
- §4.2 `usage_records` sólo tiene entrada y salida: suma caché y `served_model` (C-4).

## Self-review

- Cada decisión que contradice el spec nombra la que reemplaza.
- Las afirmaciones sobre la API de Anthropic (errores 402/429, sin *prefill*, sin herramienta forzada, pensamiento
  obligatorio en Opus 5.5, caché, respaldo por rechazo) salen de la referencia de la API vigente al 2026-11-12; los
  nombres de tipos del SDK de C# se confirman contra el compilador en la Tarea 5.1, no se suponen.

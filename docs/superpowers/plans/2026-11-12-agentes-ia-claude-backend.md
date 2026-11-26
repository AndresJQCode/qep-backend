# Agentes IA con proveedor elegible (Claude y OpenAI) — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un tenant cree **agentes IA** que atienden las conversaciones de Mensajería (WhatsApp e Instagram) y que
**elija, por agente, el proveedor del modelo**: **Anthropic (Claude)** u **OpenAI**, cada uno con la clave de su propia
cuenta. Los agentes usan herramientas propias de QEP (catálogo, estado de pedidos, borrador de cotización, traspaso a
humano), sin canvas y sin bloquear el motor de chatbots.

**Architecture:** Es el spec [`2026-10-29-agentes-ia-design.md`](../specs/2026-10-29-agentes-ia-design.md) con un cambio:
el spec tenía un solo proveedor (OpenAI) y este plan admite **dos**, elegidos por agente. Todo lo demás del spec se
conserva tal cual: módulo nuevo `Ai` (D-A1), ocupación neutral de la conexión en Mensajería (§4.3), ejecución en dos
etapas con cola de trabajos de modelo fuera del lock y de la transacción (D-A13), herramientas con principal de servicio
(§8), playground (§7) y registro de consumo (D-A27). El spec ya había dejado el modelo de datos neutral
(`provider varchar(24)`, puerto `IAiModelClient` con tipos propios, D-A25); este plan lo usa de verdad:

- Application sólo conoce `IAiModelClient` y un `AiProvider` (`anthropic` | `openai`). Un `IAiModelClientResolver`
  devuelve el adaptador del proveedor de la versión del agente.
- Infrastructure tiene un adaptador por proveedor: `ClaudeModelClient` (SDK oficial de C# `Anthropic`, Messages API) y
  `OpenAiModelClient` (API y SDK según P-1, que sigue abierta **sólo para OpenAI**).
- Cada proveedor es una conexión distinta de Integrations; el tenant puede tener las dos y repartir sus agentes.
- Las reglas que dependen del proveedor (forma del historial, caché, herramientas forzadas, respaldo, errores) viven
  **dentro** de su adaptador; el armado del contexto, el bucle de herramientas, los topes y el consumo son comunes.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3,
Testcontainers.PostgreSql, `WebApplicationFactory<Program>`, paquete NuGet `Anthropic` (13.x); el cliente de OpenAI se
decide en P-1.

**Spec:** [`docs/superpowers/specs/2026-10-29-agentes-ia-design.md`](../specs/2026-10-29-agentes-ia-design.md). Este plan
**reemplaza** §4.5, §9, D-A12, D-A22, D-A24, D-A26 y la tabla de errores de §6.7; **resuelve** P-6 y la parte de P-1
que toca a Anthropic, y parte de P-2. Spec de producto (frontend, sólo contexto):
`qep-frontend/docs/superpowers/specs/2026-10-28-agentes-ia-design.md`.

---

## Antes de empezar

- Rama propia desde `develop` (`feat/agentes-ia-<slice>`), una por slice. Antes de **cada** commit,
  `git branch --show-current`; si dice `main`, frena y pregunta.
- Todo comando para el developer va en PowerShell, uno por línea, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. No lo detengas:
  avísale al developer.
- Pruebas de integración: Docker arriba y `$env:Seed__Enabled = "false"` en la misma sesión.
- **Nunca** se imprime una clave de proveedor (`sk-ant-…`, `sk-…`): ni en logs, ni en pruebas, ni al verificar que una
  conexión existe. Las pruebas usan claves falsas contra servidores HTTP falsos.
- `docs/estado.md` **no** se toca en las ramas de feature: se actualiza en `develop` al mergear cada slice.

## Global Constraints

- **Dos proveedores, elegidos por agente:** `anthropic` y `openai`. Agregar un tercero es: una entrada en `AiProvider`,
  un proveedor en Integrations, un probador, un adaptador de `IAiModelClient` y su sección en `Ai:Providers`. Nada en
  Domain ni Application pregunta por un proveedor concreto (`AiLayerTests` lo amarra: ningún `switch` sobre
  `AiProvider` fuera de Infrastructure y del resolvedor).
- **Ningún SDK de proveedor sale de `Modules.Ai.Infrastructure`.** `AiLayerTests` prohíbe `Anthropic*` y el paquete de
  OpenAI en Domain y Application, igual que prohíbe EF Core.
- **Agregar un paquete toca los lock files.** Entra en `Directory.Packages.props` y los `packages.lock.json` se regeneran
  con `dotnet restore --force-evaluate` y se commitean **junto** con el props; sin eso el `docker build`
  (`--locked-mode`) muere con `NU1004`. Versión exacta, la última estable con al menos dos semanas de publicada.
- **Contenido fuera de logs y de trazas (D-A29).** El SDK de Anthropic emite spans de OpenTelemetry
  (`com.anthropic.sdk.csharp*`) en `metadata_only` por defecto; se fija explícito y una prueba lo amarra. El cliente de
  OpenAI, el que sea, cumple lo mismo y se prueba igual.
- **Sin respaldo entre proveedores.** Si el proveedor de un agente falla, la conversación pasa a un humano; nunca se
  reintenta con el otro proveedor (C-20).
- **Comentarios en español, tuteando**; códigos de error, rutas, permisos y enums quedan como contrato.
- **TDD:** RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- **Commits:** conventional commits, sin atribución de IA, `git commit -m "…" -- <rutas>`.

## Decisiones de este plan (lo que cambia del spec)

Cada fila reemplaza la decisión del spec que nombra. Donde no se nombra una decisión, rige el spec.

### Elección del proveedor

| # | Decisión | Por qué |
| --- | --- | --- |
| C-16 (reemplaza D-A12) | **El proveedor es del agente, junto con el modelo:** `config.model` pasa a ser `{ provider, id }` (`provider` ∈ `anthropic` \| `openai`). Se valida contra la lista curada al guardar (aviso) y al publicar (error `ai.agent.model_unavailable`). Un modelo retirado de la lista sigue en el agente con `modelRetired: true` (D-A12 se conserva en eso). | El modelo ya era del agente (D-A12); el proveedor se sigue de él. Por agente y no por tenant permite probar Claude en un agente mientras otro sigue en OpenAI, y comparar en el playground. |
| C-17 | **Cambiar de proveedor es cambiar el borrador y publicar.** La versión publicada guarda `provider` y `model` (columnas de `ai.agent_versions` del spec) y la sesión termina con la versión con que empezó (D-A5): una conversación en curso **nunca** cambia de proveedor a mitad. | Un historial armado para un proveedor se manda entero al otro como texto sin problema, pero los topes, el consumo y la actividad de una sesión quedan atados a un solo proveedor, que es lo que el tenant va a leer. |
| C-18 (reemplaza D-A8) | **Publicar exige la conexión del proveedor del agente** en `Active`: 422 `ai.provider.not_configured` con `provider` en el detalle. Una conexión de Anthropic no habilita un agente de OpenAI ni al revés. | Cada proveedor es una cuenta y una clave distintas. |
| C-19 (concreta §6.7 y §10) | **El `issue` bloqueante es por proveedor, no por tenant.** Un 402 de Anthropic deja sin atender las conversaciones nuevas de los agentes `anthropic`; los `openai` siguen. Quitar la conexión de un proveedor deja a sus agentes publicados sin tomar conversaciones (`issue` `not_configured`), no a los del otro. | El saldo o la clave de una cuenta no dicen nada de la otra. |
| C-20 | **Sin respaldo entre proveedores.** Un error agotado del proveedor de un agente termina en traspaso (§«Errores del proveedor»), nunca en una llamada al otro proveedor. El respaldo por rechazo de Anthropic (C-9) es **dentro** de Anthropic. | Mandar el historial de un cliente a una cuenta que el tenant no eligió para ese agente sería un tratamiento de datos en una cuenta que el tenant no eligió para ese agente (P-14 resuelta: el tratamiento va por la cuenta que el tenant elige), y le gastaría saldo de esa cuenta sin aviso. |

### Proveedor, clave y modelos

| # | Decisión | Por qué |
| --- | --- | --- |
| C-1 (reemplaza D-A22 y §4.5) | **Dos proveedores en Integrations**, con la misma forma: `anthropic` («Anthropic (Claude)») y `openai` («OpenAI»). Categoría `IntegrationCategory.Ai` (ya existe y nadie la usa), consumidor `[TenantModuleKeys.Ai]`, onboarding `Form`, `maxConnections: 1` **cada uno**, sin habilitación por tenant, un campo `apiKey` (`Secret`, requerido, ≤ 256, sin espacios, `revealSuffix: 4`). El puerto de Ai es `IAiProviderCredentials.ResolveAsync(tenantId, provider)`. | Mismo razonamiento del spec: cifrado AES-GCM, re-cifrado, `NeedsAttention`, auditoría y «el secreto nunca vuelve», gratis, ahora para cada proveedor. |
| C-2 (reemplaza D-A24, resuelve P-6 para Anthropic) | Un probador por proveedor. `AnthropicConnectionTester` llama **`GET /v1/models`** con la clave (no consume tokens) y después `GET /v1/models/{id}` del modelo predeterminado de Anthropic en `Ai:Providers`. 401 → `credentials_rejected`; 403 → `permission_denied`; 404 del modelo → `model_unavailable`; 429/5xx/529/red → `unreachable`. El saldo **no** se verifica sin gastar: un 402 `billing_error` se ve en la primera llamada real. `OpenAiConnectionTester`: endpoint y códigos según P-6, que sigue abierta para OpenAI. | Validar sin costo y distinguir «clave mala» de «modelo no habilitado». |
| C-3 (reemplaza D-A26) | Lista curada **por proveedor** en `Ai:Providers:{provider}:Models` (`id`, `label`, `description`, `isDefault`, `available`, `effort`), un predeterminado por proveedor y uno global (`Ai:DefaultProvider`). Valores iniciales: **anthropic** → `claude-opus-5-5` (predeterminado), `claude-sonnet-5-5`, `claude-haiku-5-5`; **openai** → los de D-4a (`gpt-6-luna` predeterminado, `gpt-6.1-terra`, `gpt-6.1-sol`). `GET /ai/models` responde agrupado por proveedor, con `configured` (si el tenant tiene la conexión `Active`) e `issue` por proveedor: la pantalla no tiene que cruzar con Integraciones para saber qué se puede elegir (backend for frontend). | Cambiar o retirar un modelo es configuración, no despliegue del SPA. Que el tenant elija un modelo más barato es **su** decisión (D-4a). |
| C-4 (complementa D-A25 y D-A27) | `ai.usage_records` conserva `provider` y suma `cache_creation_input_tokens` y `cache_read_input_tokens` (nullable: un proveedor que no los reporta deja `NULL`, no `0`) y `served_model` (el modelo que de verdad respondió; con el respaldo de C-9 puede no ser el pedido). La lista de agentes suma el mes **por proveedor**. | Con dos cuentas, un total mezclado no se puede contrastar con ninguna factura. Un `0` diría «no hubo caché» cuando el dato no existe. |
| C-5 | **BYOK** (spec de producto D-4, confirmado por el owner el 2026-11-12, P-13): cada tenant usa y paga **su propia cuenta** del proveedor que elige. QEP no tiene clave de plataforma ni cobra consumo. | El adaptador de `IAiProviderCredentials` sólo resuelve conexiones del tenant; no hay configuración de clave en la plataforma. |

### Llamada al modelo con Anthropic (resuelve P-1 para Anthropic)

Todas estas reglas viven **dentro** de `ClaudeModelClient`. El adaptador de OpenAI tendrá las suyas (P-1).

| # | Decisión | Por qué |
| --- | --- | --- |
| C-6 | **Messages API sin estado**, `client.Beta.Messages.Create` **sin streaming**. El historial lo arma QEP en cada llamada (D-A28); no se usan *message threads* ni Managed Agents. | No queda conversación guardada en la cuenta del tenant. Sin streaming porque la respuesta va a WhatsApp entera; con `MaxTokens` de 16.000 cabe en el timeout. El endpoint beta es por C-9. |
| C-7 | **Pensamiento:** no se manda `Thinking` (los tres modelos corren adaptativo por defecto; en Opus 5.5 apagarlo es un 400). La profundidad se controla con `OutputConfig.Effort`, **siempre explícito** desde la lista curada (Opus 5.5 y Haiku 5.5 tienen `medium` por defecto, Sonnet 5.5 `high`). Valor inicial `medium` hasta que el eval de AI-B9 diga otra cosa. | Opus 5.5 rechaza `{type: "disabled"}` y `budget_tokens`. El esfuerzo es la palanca de costo y latencia. |
| C-8 | **Herramientas con `strict: true`** y `additionalProperties: false`; `tool_choice` **`auto`**. `send_reply` y `handoff_to_human` siguen siendo las finales del spec, pero una vuelta que termina en `end_turn` con texto y sin herramienta final **se acepta como `send_reply` sin `sourceIds`**. El bucle común recibe ese caso ya normalizado por el adaptador. | En Opus 5.5 y Sonnet 5.5 forzar herramienta (`any`/`tool`) es un 400. |
| C-9 | **Respaldo por rechazo dentro de Anthropic:** beta `server-side-fallback-2026-07-01` con `fallbacks: "default"` (C#: `new Default()`) en Opus 5.5 y Sonnet 5.5; Haiku 5.5 no lo tiene. Un `stop_reason == "refusal"` que llega igual → traspaso `AgentFailed` con `ai.provider.refused` y la categoría de `stop_details` en el evento interno. | Un rechazo de un clasificador no debe dejar al cliente sin respuesta. |
| C-10 | **Bucle append-only.** Dentro de un trabajo, cada respuesta del asistente se devuelve **completa** (texto, `thinking` con su `Signature`, `tool_use`) en la siguiente llamada: el bucle común la guarda como bloques **opacos** del proveedor y el adaptador la reenvía tal cual. Todos los `tool_result` de una vuelta van en **un solo** mensaje `user`, con `is_error: true` el que falló. Entre trabajos no se reenvían bloques de pensamiento. | Los bloques de pensamiento sólo valen sin modificar. Separar los resultados enseña al modelo a no llamar herramientas en paralelo. |
| C-11 | **La petición siempre termina en `user`.** Turnos del cliente seguidos se juntan en un mensaje `user`. El **saludo** ya enviado viaja en el `AiModelRequest` como dato aparte (`GreetingSent`), y el adaptador de Anthropic lo pone en un mensaje de sistema a mitad de conversación después del primer turno del cliente, nunca como `assistant` final. | Un `assistant` al final es *prefill* y responde 400. Ponerlo en el adaptador deja al de OpenAI elegir su forma. |
| C-12 | **Caché de prompt.** `tools` (orden por nombre) → `system` estable con **un** `cache_control` al final → historial con caché automático de nivel superior. Lo volátil (fecha y hora del tenant, minutos de ventana) va en `AiModelRequest.VolatileContext` y el adaptador lo pone **después** del último punto de caché. TTL 5 minutos. | Un `DateTime.Now` en el `system` invalidaría el caché en cada llamada sin error. Se verifica con `CacheReadInputTokens > 0` (AI-B9). |
| C-13 (concreta D-A17 y D-A20) | `AnthropicClient` con **`MaxRetries = 0`** y `Timeout` = `Ai:Model:CallTimeout` (45 s); `MaxTokens` 16.000. Los reintentos los hace el worker, igual para los dos proveedores (D-A17). | Con reintentos del SDK el tiempo real es `timeout × 3` y el trabajo pasa su tope y su lease sin saberlo. La misma regla aplica al cliente de OpenAI. |
| C-14 | **Clave por petición:** un cliente por trabajo con la clave de la credencial resuelta, descartado al terminar; nunca un cliente global con clave. Si el SDK acepta un `HttpClient`, se usa el nombrado `ai.anthropic` con `RemoveAllLoggers()`. Se verifica en la Tarea 5.1. | BYOK: cada tenant, su clave. Mismo estándar del bloque HTTP (D-H5). |
| C-15 | **Entrantes que no son texto** van al historial como texto fijo («[El cliente envió una imagen]», con el pie si lo trae), igual para los dos proveedores. Visión queda **fuera**. | MVP en texto sin ignorar en silencio un mensaje. |

### Errores del proveedor (reemplaza la tabla de §6.7)

Los **errores tipados** de `IAiModelClient` y sus efectos son comunes; cada adaptador traduce los de su API.

| Error tipado | Reintento | Efecto | Anthropic | OpenAI |
| --- | --- | --- | --- | --- |
| `ProviderUnavailable` | sí (D-A17) | Agotado → `ProviderFailed` `ai.provider.unavailable` | timeout, red, 500 `api_error`, 529 `overloaded_error` | P-1 |
| `RateLimited(RetryAfter?)` | sí, con `retry-after` | Agotado → `ProviderFailed` `ai.provider.rate_limited` | 429 `rate_limit_error` (siempre tasa, nunca cuota) | P-1 |
| `QuotaExhausted` | no | `ProviderFailed` `ai.provider.quota_exhausted`; `issue` bloqueante **del proveedor** (C-19) | 402 `billing_error` | `insufficient_quota` (forma exacta: P-1) |
| `CredentialsRejected` | no | `ProviderFailed` `ai.provider.credentials_rejected`; conexión a `NeedsAttention`; `issue` bloqueante del proveedor | 401 `authentication_error` | P-1 |
| `PermissionDenied` | no | `ProviderFailed` `ai.provider.permission_denied`; `issue` bloqueante del proveedor | 403 `permission_error` | P-1 |
| `ModelUnavailable` | no | `AgentFailed` `ai.provider.model_unavailable`; `issue` en el agente | 404 `not_found_error` del modelo | P-1 |
| `InvalidRequest` | no | `AgentFailed` `ai.agent.request_invalid`; se loguea el id de la petición, nunca el cuerpo | 400 `invalid_request_error`, 413 | P-1 |
| `Refused(Category)` | no | `AgentFailed` `ai.provider.refused` | 200 con `stop_reason = refusal` (ya pasó por C-9) | P-1 |
| `InvalidResponse` | 1 vez | Cuenta para `maxConsecutiveFailures` | 200 con `stop_reason = max_tokens` | P-1 |

El evento interno del traspaso lleva siempre el `provider`. Anthropic se clasifica por el `type` del error, no por el
texto; los nombres de las excepciones del SDK se confirman contra el compilador en la Tarea 5.1.

## Decisiones pendientes del owner

Las del spec siguen abiertas salvo P-6 para Anthropic, la parte de P-1 que toca a Anthropic y P-7 (resuelta con P-14, abajo). Del owner no queda ninguna de las nuevas:

| # | Pregunta | Por qué importa |
| --- | --- | --- |
| — | Ninguna. P-1 de OpenAI no es del owner: es técnica y se resuelve en la Tarea 6 (ver abajo). | |

### Resueltas por el owner (2026-11-12)

- **P-13 → BYOK.** El tenant usa su propia cuenta del proveedor (C-5). No hay clave de plataforma.
- **P-14 (y P-7 del spec) → la cuenta es del tenant.** El historial del cliente final viaja a la cuenta del tenant en el
  proveedor que el tenant eligió para el agente, bajo los términos entre el tenant y ese proveedor. QEP no agrega aviso
  ni consentimiento propio. Ya no bloquea encender canales reales.
- **P-16 → no.** Ningún tenant queda restringido a un proveedor: todo tenant con el módulo `ai` puede conectar los dos.
  Los proveedores de Integrations quedan con `requiresTenantEnablement: false`.
- **P-15 → no.** No se muestra costo estimado: el consumo se muestra sólo en tokens, por proveedor. No hay tabla de
  precios en `Ai:Providers`.

### Técnica, sin decisión del owner

- **P-1 (sólo OpenAI)** es cómo se habla con OpenAI por dentro: cuál de sus dos APIs de chat, si con su SDK o con HTTP
  directo, y cómo se leen sus errores y su consumo. No cambia nada que vea el tenant. La resuelve quien construya la
  Tarea 6, leyendo la documentación oficial de OpenAI, y deja la respuesta anotada en el spec.

## Review Focus

1. El worker nunca llama al modelo con una transacción abierta ni con el lock de la conversación (D-A13).
2. Las claves de los dos proveedores no aparecen en logs, auditoría, `ai.*`, trazas ni respuestas (prueba con el host en
   `Trace`, como `TheKeyNeverAppearsInLogsAuditOrHttpCalls`, una por proveedor).
3. Un agente nunca llama al proveedor de otro agente: el resolvedor toma el proveedor **de la versión de la sesión**, no
   del borrador ni del tenant (C-17, C-20).
4. El `issue` de un proveedor no frena a los agentes del otro (C-19).
5. Ninguna petición a Anthropic termina en `assistant` (C-11) y el bucle es append-only (C-10).
6. Todo error de la tabla termina en traspaso: el cliente nunca se queda sin respuesta y sin humano.
7. La suite de Automations pasa sin cambios de comportamiento tras AI-B3 (salvo «sin ocupante no encola»).

---

## Tareas

Los slices son los de §13 del spec, con el proveedor elegible y el adaptador de OpenAI separado del de Anthropic para
que el primero no espere a P-1. Las tareas que no cambian se construyen como dice el spec y sólo se listan.

### Task 1 (AI-B1): Módulo `ai`, permisos, proveedores y lista curada — hecha

- [ ] RED: `TenantModuleKeyTests` espera `ai` en `All`, con dependencia `[Ai] = [Messaging]` y fuera de
  `DefaultForNewTenants`.
- [ ] GREEN: `Define("ai")` en `TenantModuleKey.cs`, `All`, `Dependencies`. Migración Tenancy `AddAiModuleKey`.
- [ ] Permisos `ai.agent.read|manage` en `AiPermissions`: constante, `PermissionDefinition`, rol admin por defecto **y**
  política en `AddAuthorization` (sin la política el síntoma es 500, no 403). `CompositionRootTests` los amarra.
- [ ] Proyectos `Modules.Ai.{Domain,Application,Infrastructure,Api}` vacíos, `AiLayerTests` que copia
  `AutomationsLayerTests`, suma `Modules.Automations` a los prohibidos **y** prohíbe los SDK de proveedor fuera de
  Infrastructure.
- [ ] `AiProvider` en Domain (`anthropic`, `openai`; persistido como texto).
- [ ] Opciones `Ai:Providers` y `Ai:DefaultProvider` (C-3) con `ValidateOnStart`: un `isDefault` por proveedor, ids no
  vacíos ni repetidos dentro de un proveedor, `effort` válido, `DefaultProvider` presente en la lista.
- [ ] `GET /api/v1/tenants/{tenantId:guid}/ai/models` agrupado por proveedor, con `configured` e `issue` (el adaptador
  de credenciales llega en la Tarea 2; hasta entonces `configured: false`).
- [ ] Commit `feat(ai): add ai module key, permissions and per-provider model list`.

### Task 2 (AI-B2): Proveedor `anthropic` en Integrations y `GET /ai/settings` — hecha

Construida en `feat/agentes-ia-modulo`. Cambia respecto de lo planeado:

- **OpenAI no entra todavía en Integrations.** Un probador que no puede verificar la clave dejaría guardar una conexión que nadie
  usa; el proveedor `openai` llega en la Tarea 6, con su probador real. Mientras tanto `GET /ai/settings` lo lista con
  `connectable: false` y `GET /ai/models` con sus modelos no disponibles.
- **El final visible de la clave (D-A23) se calcula al leer**, de la clave descifrada (`FieldDefinition.RevealSuffix`,
  `SecretStateResponse.hint`), sin columna `hint` en claro: no hay otra copia que borrar ni que re-cifrar. Con menos del doble de
  caracteres no se muestra nada.
- **El probador sólo llama `GET /v1/models`** (`anthropic-version: 2026-10-21`): 2xx Ok; 401 y 403 rechazada; 429, 5xx, 529, 3xx,
  timeout y red «no pude verificar». No consulta el modelo predeterminado: Integrations no ve la lista de Ai, y un modelo sin
  acceso llega como `ModelUnavailable` en la primera llamada (Tarea 5).
- **`GET /ai/settings`** responde por proveedor `{ provider, connectable, connectionId, status }`. El `issue` por proveedor
  (C-19) llega con la Tarea 7, que es la que lo produce. `MarkNeedsAttentionAsync` llega con la Tarea 5, que es su primer
  consumidor.

- [x] Proveedor `anthropic` (categoría `Ai`, consumidor `ai`, `maxConnections: 1`, `apiKey` con `revealSuffix: 4`) y migración
  `AddAnthropicProvider`.
- [x] `AnthropicConnectionTester` con su cliente sin redirecciones ni loggers.
- [x] `IAiProviderCredentials` (`ResolveAsync`, `DescribeAsync`) + `AiProviderCredentials` en Bootstrapper.
- [x] `GET /api/v1/tenants/{tenantId}/ai/settings`.

### Task 3 (AI-B3): Ocupación neutral `messaging.connection_bots` — hecha

Construida en `feat/agentes-ia-modulo` (`8051922a`). Cambia respecto del spec (§4.3, D-A2):

- **La ocupación se sincroniza en la misma transacción que el guardado del chatbot, no con compensación ni barrido.**
  `AutomationsUnitOfWork` abre su transacción si no hay una, guarda, y por cada tenant con cambios en `Chatbot` o
  `ChatbotConnection` llama a `IChatbotConnectionSlots.SyncChatbotsAsync` (SQL crudo en Bootstrapper,
  `ChatbotConnectionSlots`): borra las filas `kind = 1` vencidas, inserta o renombra las de los chatbots publicados con
  `ON CONFLICT (connection_id)` sin pisar a otro ocupante, y devuelve los choques. Un choque deshace todo y responde
  `409 automations.chatbot.connection_taken` con `occupant { kind, id, name }` (`ConnectionTakenException`, por
  `IHasProblemExtensions`). No hay estado intermedio que reconciliar.
- **`ResourceConflictException` dejó de ser `sealed`** para que `ConnectionTakenException` la extienda y conserve el 409.
- **`servedBy` en `GET /automations/connections` queda para la Tarea 4**, cuando existe el agente que lo llena.

- [x] `messaging.connection_bots` (PK `connection_id`, `CK_connection_bots_kind`, `IX_connection_bots_tenant_bot`) y migración
  `AddConnectionBots` con backfill desde `automations.chatbot_connections` (protegido con `to_regclass`).
- [x] Borrador no ocupa; publicar ocupa; pausar conserva; borrar libera; mover y renombrar siguen; tomar la conexión mueve
  la fila; publicar sobre una conexión de un agente da 409 con el agente (`ChatbotsApiTests.ConnectionSlots`).
- [x] Regresión: Architecture 200, Automations unit 690, Bootstrapper unit 534, Automations integración 301, Messaging
  integración 393, todas en verde; `TheMigrationBackfillsTheChatbotsAlreadyPublished` en verde.

### Task 4 (AI-B4): Agregado `Agent`, borrador, publicar y conexiones — hecha

Construida en `feat/agentes-ia-modulo`. Esquema `ai` con `agents`, `agent_versions` y `agent_connections` (migración `AddAi`); las
demás tablas de §4.2 llegan con las tareas que las usan. Rutas de §5: `GET /connections`, `GET|POST /agents`,
`GET|PUT|DELETE /agents/{id}`, `PUT /agents/{id}/draft`, `POST /agents/{id}/{publish,pause,resume}`. Cambia respecto de lo planeado:

- **La ocupación se sincroniza en la misma transacción que el agente**, como en la Tarea 3 (`AiUnitOfWork` +
  `IAgentConnectionSlots`, SQL en `src/Bootstrapper/AgentConnectionSlots.cs` con `bot_kind = 2`). Sin compensación ni barrido. Un
  agente ocupa sus conexiones desde que se publica y las conserva en pausa (D-A10); quitar una conexión o borrar el agente la libera.
- **Un modelo fuera de la lista no tiene código propio de 422:** es el problema `model_unavailable` (aviso al guardar, error al
  publicar), así que publicar responde `ai.agent.invalid` con `errors.model`, igual que cualquier otro problema del borrador. Un
  proveedor desconocido es `model_provider_unknown` en `model.provider`.
- **Orden al publicar:** primero los problemas del borrador (`ai.agent.invalid`), después la cuenta del proveedor del agente
  (`ai.provider.not_configured` con `provider` en las extensiones). La conexión del otro proveedor no habilita (C-18), probado con
  OpenAI habilitado por configuración en la prueba.
- **Los topes del `config` son 422 `validation.failed`** con el campo (`Config.Tone`, `Config.Handoff.MaxRepliesWithoutHuman`…), no
  problemas del borrador. El tope de 256 KB del documento no se mide aparte: los topes de cada campo lo acotan (≈130 KB).
- **`GET /automations/connections` muestra también a un agente:** `servedBy` sale de `messaging.connection_bots` y suma `kind` e `id`.
  `chatbotId` se conserva, con el id del bot sea cual sea, porque el frontend actual lo exige como texto y lo compara con el chatbot
  abierto: con un agente, la conexión se ve ocupada sin cambiar nada en la pantalla.
- **Quedan para después:** el 409 `has_live_sessions` al borrar (Tarea 7, con las sesiones); el consumo del mes y la actividad del día
  en la lista (Tareas 5 y 9); el aviso de `tools.quotationDraft` sin asesor (P-8 del spec, sin resolver); Instagram con
  `botSupported: true` (Tarea 7, con su salida de bot).
- **Duplicar copia el borrador, nunca las conexiones**, que al publicarse chocarían con las del original.

- [x] RED/GREEN `AgentConfigValidatorTests` y `AgentTests` (dominio).
- [x] RED/GREEN `AgentsApiTests` (14): borrador nuevo con el modelo predeterminado, nombre repetido, `If-Match`, topes, publicar con
  errores, sin cuenta del proveedor, con la cuenta del otro proveedor, modelo retirado, publicar ocupa y guarda proveedor y modelo,
  409 con el ocupante, pausar, renombrar, quitar y borrar, conexiones ajenas y de Instagram, lista y duplicado, permisos y módulo.
- [x] `ConnectionsApiTests.AConnectionHeldByAnAgentShowsTheAgent` (Automations).

### Task 5 (AI-B5): Núcleo común y adaptador de Claude

**5.1 — Paquete y verificación del SDK de Anthropic (spike corto, sin código de producción)**

- [ ] `Anthropic` en `Directory.Packages.props` con versión exacta; referencia sólo desde
  `Modules.Ai.Infrastructure.csproj`. `dotnet restore --force-evaluate` y commit de los lock files en el mismo commit.
- [ ] Contra el SDK instalado (`strings` sobre el `.dll` o el compilador, sin sondas de reflexión), confirmar y anotar
  en el reporte: si `AnthropicClient` acepta un `HttpClient` (C-14); `MaxRetries` y `Timeout`; nombres de
  `ThinkingBlockParam`/`Signature`, `ToolResultBlockParam.IsError`, `Tool.Strict`, `CacheControlEphemeral`,
  `OutputConfig.Effort`, `Fallbacks`/`Default` en el namespace beta; `Usage.CacheReadInputTokens`; `StopDetails`; las
  excepciones de `Anthropic.Exceptions`.
- [ ] Commit `build(ai): add Anthropic SDK and regenerate lock files`.

**5.2 — Puerto común y resolvedor**

- [ ] `IAiModelClient.CompleteAsync(AiModelRequest, ct) → AiModelResponse` en Application, con tipos propios:
  `AiModelRequest(Model, Effort, SystemPrompt, GreetingSent?, VolatileContext, Messages, PriorAssistantBlocks, Tools,
  MaxOutputTokens)` y `AiModelResponse(Text?, ToolCalls, OpaqueAssistantBlocks, StopReason, Usage(Input, Output,
  CacheCreation?, CacheRead?), ServedModel, RequestId)`, y los errores tipados de la tabla.
- [ ] `IAiModelClientResolver.For(AiProvider) → IAiModelClient` en Application; implementación en Infrastructure con
  servicios por clave (`AddKeyedScoped`). Un proveedor sin adaptador registrado lanza al arrancar, no en un trabajo.
- [ ] RED: `AiModelClientResolverTests` (cada proveedor de `AiProvider` tiene adaptador; el de OpenAI es un doble hasta
  la Tarea 6) y `CompositionRootTests`.
- [ ] Commit `feat(ai): provider-neutral model client port and resolver`.

**5.3 — Adaptador de Claude**

- [ ] RED: `ClaudeModelClientTests` contra un servidor HTTP falso que captura el cuerpo enviado:
  - sin `thinking`, con `output_config.effort` (C-7);
  - `tools[*].strict == true` y sin `tool_choice` forzado (C-8); `end_turn` con texto → `send_reply` normalizado;
  - `cache_control` en el último bloque de `system`, `VolatileContext` **fuera** del `system` (C-12);
  - último mensaje `user` o `system`, nunca `assistant`, también con `GreetingSent` (C-11);
  - `fallbacks: "default"` y el header beta en Opus y Sonnet, ausentes en Haiku (C-9);
  - `PriorAssistantBlocks` se reenvían byte a byte (C-10);
  - cada fila de la columna Anthropic de la tabla de errores mapea a su error tipado; un 429 trae `RetryAfter`;
  - el `x-api-key` es el de la credencial de la petición; dos tenants no comparten clave.
- [ ] GREEN: `ClaudeModelClient` en `Modules.Ai.Infrastructure/Model/Anthropic/`. Comentario con el porqué de C-8,
  C-11 y C-13: son los tres que el siguiente va a querer «simplificar».
- [ ] Commit `feat(ai): add Claude model client`.

**5.4 — Armado del contexto (común)**

- [ ] RED: `AgentPromptBuilderTests`: instrucciones fijas de QEP primero y en español; `instructions`, `tone`, FAQ y
  respuestas rápidas en orden estable (misma versión → `SystemPrompt` idéntico byte a byte); turnos del cliente seguidos
  juntos; saludo en `GreetingSent`, no en `Messages`; fecha y ventana en `VolatileContext`; entrantes no texto con su
  marcador (C-15); recorte a 30 turnos o `Ai:Model:MaxContextTokens`.
- [ ] GREEN: `AgentPromptBuilder` en Application (sin tipos de ningún SDK).
- [ ] Commit `feat(ai): build provider-neutral agent prompt`.

**5.5 — Bucle de herramientas, consumo y playground (común)**

- [ ] Herramientas de lectura `search_catalog` y `get_order_status` (spec §8.2) con sus puertos (§8.3); `send_reply` y
  `handoff_to_human` como finales. Esquemas JSON neutrales con `additionalProperties: false` y todos los campos en
  `required`; cada adaptador los traduce.
- [ ] RED: `AgentTurnRunnerTests` con un `IAiModelClient` falso: el runner pide al resolvedor **el proveedor de la
  versión**; varias llamadas a herramientas en una vuelta → un solo mensaje de resultados; herramienta que falla →
  `is_error`; topes D-A20 → `AgentFailed`; `InvalidResponse` → un reintento; `usage_records` con `provider`, caché y
  `served_model` por **cada** llamada, también las descartadas; un `QuotaExhausted` deja el `issue` sólo en ese
  proveedor (C-19).
- [ ] GREEN: `AgentTurnRunner` (Application). `ai.tool_calls` y `ai.usage_records` (C-4).
- [ ] Playground (spec §7) sobre el mismo runner con el proveedor del **borrador**, `create_quotation_draft` simulado.
  Exige la conexión de ese proveedor (C-18).
- [ ] Prueba de no-filtración para Anthropic: host en `Trace`, clave falsa conocida; la clave y el texto del cliente no
  aparecen en logs, auditoría, `ai.*` ni spans (`metadata_only`).
- [ ] Commit `feat(ai): run agent tool loop with per-provider usage metering and playground`.

### Task 6 (AI-B5b, nuevo): Adaptador de OpenAI

**Empieza resolviendo P-1 y P-6 para OpenAI (técnicas, sin el owner).** No bloquea a ningún otro slice: hasta que llegue, `GET /ai/models` marca
`openai` con `available: false` en sus modelos y publicar un agente de OpenAI responde `ai.agent.model_unavailable`.

Hecha en `feat/agentes-ia-claude`. P-1 y P-6 quedaron resueltas en el spec, §9.1 (O-1..O-10, con las fuentes). Cambia respecto de
lo planeado:

- **Sin paquete:** P-1 eligió la Responses API con `HttpClient` directo (O-2), así que no hay lock files que regenerar. La columna
  OpenAI de la tabla de errores es O-8 y O-9.
- **OpenAI entra en Integrations en esta tarea** (proveedor `openai`, migración `AddOpenAiProvider`, `OpenAiConnectionTester`), no
  en la Tarea 2: allá se dejó fuera para no ofrecer una conexión que nadie podía usar. `GET /ai/settings` lo lista con
  `connectable: true` y sus modelos quedan `available: true` en `appsettings.json`, con la descripción de D-4a.
- **El esfuerzo de OpenAI es opcional** (O-6): la lista inicial no lo fija y se manda el default del modelo hasta que AI-B9 diga otra
  cosa.

- [x] Resolver P-1 contra la documentación oficial de OpenAI (no de memoria) y anotar la respuesta en el spec.
- [x] Paquete (si P-1 elige SDK) con lock files regenerados en el mismo commit. No aplica: sin SDK.
- [x] RED/GREEN: `OpenAiModelClientTests` con las mismas exigencias que `ClaudeModelClientTests` que apliquen
  (herramientas, `VolatileContext` fuera del prefijo estable, errores tipados de la columna OpenAI, clave por
  petición, sin estado del lado del proveedor) y las propias que P-1 determine.
- [x] `OpenAiConnectionTester` real (reemplaza el `unverified` de la Tarea 2).
- [x] Prueba de no-filtración para OpenAI (`OpenAiNoLeakTests`).
- [x] Commit `feat(ai): add OpenAI model client`.

### Task 7 (AI-B6): Mensajería y ejecución en canales reales

Como §4.4, §6.1–§6.8 del spec (`BotRef`, `sent_by_kind = 3` con `sent_by_agent_id`, ruteo `bot_kind` de la cola,
Instagram en `IBotOutbound`, `ChannelOutboundRules`, procesador de entrantes, `AiModelWorker`, traspasos, apagado del
módulo). Cambia:

Hecha en `feat/agentes-ia-claude` (`40f6ec87`, `528a3c9d`, `1ddaccfc`), con build en verde y **pruebas escritas sin correr** (las corre el
owner). Cambia respecto de lo planeado:

- **El `HandoffCauseKind` espejo vive en Ai** (`Modules.Ai.Application.HandoffCauseKind`, los seis kinds de agente de §4.4) y la prueba de
  paridad compara Automations ∪ Ai con `AutomationHandoffKind`; los códigos `permission_denied`, `refused` y `request_invalid` viajan en
  `Code` y la prueba exige que todo `AgentHandoffCodes.All` sobreviva la limpieza del hilo. Los topes de D-A20 y la respuesta inválida usan
  los códigos que ya mostraba el playground (`invalid_response`, `model_calls_exceeded`, `tool_calls_exceeded`, `job_timed_out`) más
  `max_replies_without_human` y `send_failed`.
- **El trabajo de una sesión en reintento o con la respuesta guardada sigue `Running` con el lease acortado**, no vuelve a `Pending`: el
  siguiente trabajo de la sesión puede estar ya pendiente y el índice único `IX_model_jobs_session_open` chocaría.
- **Los nombres de agentes los resuelve el mismo puerto de los chatbots** (`IAutomationsChatbotNames`, adaptador que ahora lee también Ai):
  `MessageDto.sentByAgent` y `ConversationSummary.automationBot` salen de ese diccionario.
- **Una conversación sin ocupante en `connection_bots` ya no encola** (`AutomationInboxTests` siembra un ocupante chatbot).
- **Dashboard y alertas no están en este repo** (`k8s/platform/dashboards/...` vive en el repo de plataforma): falta sumar `ai_model` y las
  series `qep.ai.model.calls` / `qep.ai.model.errors` allá.
- **`issue` en `GET /ai/settings`**: sigue siendo el `status` de la conexión (`NeedsAttention`); el código del fallo no se expone porque
  `ConnectionListing` de Integrations no lo trae.

- [x] El procesador de entrantes revisa el `issue` **del proveedor del agente** de la conexión (C-19) antes de abrir
  sesión.
- [x] Los códigos nuevos de traspaso (`permission_denied`, `refused`, `request_invalid`) en
  `HandoffCauseKind`/`AutomationHandoffKind` **juntos**, con `HandoffCauseKindParityTests`; el evento lleva `provider`.
- [x] Barrido de lectores de autor por `sent_by_kind = 3` (un join al miembro lo pierde en silencio), y CHECK
  `sent_by_kind IN (1,2,3,4)`.
- [ ] Cola `ai.model_jobs` en `QueueNames`, `QueueMetricsSource`, dashboard y alertas; métricas de llamadas y errores
  etiquetadas por `provider`. Hecho en este repo (`QueueNames`, `QueueMetricsSource`, su prueba y el meter `Qep.Ai`); **faltan dashboard y
  alertas en el repo de plataforma**.
- [x] El código se mergea apagado por el módulo `ai` (fuera de `DefaultForNewTenants`); se enciende tenant por tenant.

### Task 8 (AI-B7): `create_quotation_draft` — hecha

Como §8 y P-4/P-4b del spec, sin cambios por el proveedor. Hecha en `feat/agentes-ia-claude` con build en verde y **pruebas escritas sin
correr** (las corre el owner). Cambia respecto de lo planeado:

- **La «empresa facturadora» de P-4 es la cuenta de cobro entera** (`config.quotationDraft.billingAccount { companyId, bankName,
  accountNumber, currency }`): una cotización no guarda la empresa sola sino una de sus cuentas (`QuotationBillingAccount`), y elegir una
  cuenta de la empresa por regla sería inventar. La asesora va en `config.quotationDraft.advisorMemberId`. Los dos son nullable para que un
  `config` guardado antes se siga leyendo.
- **Validación (D-A7):** con `tools.quotationDraft`, sin asesora → `quotation_advisor_missing` y sin cuenta → `quotation_billing_account_missing`
  (errores); una asesora que ya no puede cotizar → `quotation_advisor_cannot_quote` como **aviso** («bloquea la herramienta con aviso»): al
  correr, la herramienta responde `quotation_advisor_unavailable` al modelo sin escribir. «Puede cotizar» = membresía activa con
  `quotations.quotation.manage` (`AiQuotationAdvisors`, misma regla que `MessagingAssignees`). El editor suma `quotationAdvisors`.
- **Quotations:** `IExternalQuotationWriter` / `ExternalQuotationService` (asesora explícita, validador y armado de la creación, historial sin
  miembro, auditoría `PublishByIntegration`), columna `external_key` con `IX_quotations_tenant_external_key` (parcial, traducido por nombre a
  `quotation.quotation.external_key_taken`) y migración `AddQuotationExternalKey`. El historial por línea no se escribe: audita con la persona
  que edita y aquí no hay ninguna.
- **El traspaso lo decide la herramienta, no el modelo:** creado el borrador, el turno termina con `AgentRequested` + `quotation_draft_created`
  hacia la asesora de la cotización (P-4b); sin cliente o con uno que Quotations no deja cotizar (`client_not_found`, `client_incomplete`,
  `client_cuc_missing`, `client_inactive`), con `quotation_customer_not_ready` hacia el destino configurado (P-4). Otro rechazo de Quotations
  vuelve al modelo con su código. El playground simulado traspasa igual.
- **Un traspaso con borrador escrito no se descarta por un turno nuevo del cliente** (D-A14): el siguiente trabajo crearía otro borrador.
- `ai.quotation_draft.created` se audita con actor `AiAgent` en el commit corto del traspaso.

- [x] RED/GREEN `CreateQuotationDraftToolTests`, `AgentConfigValidatorTests`, `AgentEditorBuilderTests`, `AgentSessionProcessorTests` (Ai),
  `AiQuotationDraftsTests` (Bootstrapper), `ExternalQuotationServiceTests` (Quotations, integración).

### Task 9 (AI-B8): Actividad del agente, historial y conteos — hecha

Como el spec. La actividad de cada respuesta muestra proveedor y `served_model`; la lista de agentes suma tokens por
proveedor, con caché (C-4).

Hecha en `feat/agentes-ia-claude`, sin migración, con build en verde y **pruebas escritas sin correr**. Cambia respecto de lo planeado:

- **`GET /ai/conversations/{conversationId}/agent-activity`**: las sesiones de la conversación y, por cada **trabajo** (respuesta, traspaso o
  descarte, porque todos gastaron tokens), `provider` de la versión, `servedModels` (sin repetir: con C-9 pueden ser dos), consumo con caché,
  mensajes enviados, herramientas y fuentes **resueltas** (`faq` con la pregunta, `tool` con el nombre, `quickReply` con el atajo de hoy o
  `null` si se borró). Permiso `ai.agent.read`; **P-10 sigue abierta** (si también la ve todo asesor de la conversación). Una conversación de
  otro tenant responde vacía tras el 403 de ruta.
- **`GET /ai/sessions?agentId=&from=&to=`**: de la más reciente a la más vieja, `from` inclusivo y `to` exclusivo sobre el inicio; `to ≤ from`
  es 422. El spec no pagina: se devuelven las **200 más recientes** (`ListAgentSessionsHandler.MaxSessions`). `replies` cuenta trabajos con
  respuesta, no mensajes.
- **Lista de agentes:** cada agente trae `usageThisMonth` (por proveedor, mes del reloj del tenant, **incluye el playground**, que gasta la
  misma clave) y `today { handled, handedOff, quotationDrafts }` de las sesiones que empezaron hoy; `handedOff` cuenta `HandedOff`, `Failed` y
  `WindowClosed` (los caminos de traspaso), no `Interrupted`. La respuesta suma además `usageThisMonth` del tenant por proveedor (D-A27, «el
  total de la clave»). La caché suma `null` sólo cuando ninguna llamada la reportó.
- `GET /ai/settings` sigue sin `usageThisMonth` (el spec lo lista; esta tarea no lo pedía).

- [x] RED/GREEN `AgentActivityHandlersTests` (Ai unit) y `AgentActivityApiTests` (Ai integración).

### Task 10 (AI-B9, nuevo): Eval, ajuste de esfuerzo y comparación de proveedores

Arnés hecho en `feat/agentes-ia-claude`, con build en verde y **sin correr** (cada corrida gasta dinero real). Cambia respecto de lo
planeado:

- **Es una consola, no un proyecto de pruebas:** `tests/Modules/Ai/Modules.Ai.Eval` (`OutputType Exe`, `IsTestProject false`). `dotnet test`
  la salta, así que no corre en la suite ni en ningún pipeline; está en `Backend.slnx` sólo para que el build la compile. Para llamar a un
  proveedor exige `--confirm-spend`, un tope duro `--max-model-calls` y la clave en `QEP_EVAL_ANTHROPIC_API_KEY` /
  `QEP_EVAL_OPENAI_API_KEY`; `--dry-run` valida el conjunto sin red. Cómo correrlo: README, «Agentes IA», «Eval».
- **43 casos** en `eval-set.json` (catorce categorías, las seis del plan incluidas), contra una ferretería ficticia en memoria
  (`EvalFixture`): catálogo y pedidos falsos, `create_quotation_draft` simulado (modo playground).
- **Los adaptadores salen de `AddAiInfrastructure`**, el mismo cableado de producción (clientes nombrados sin loggers ni redirecciones,
  límites de `Ai:Model`); la base nunca se abre.
- **El juez es un gancho** (`IEvalJudge`, `ModelJudge` sobre `IAiModelClient`, `--judge`): corre sólo en los casos con rúbrica que pasaron
  las reglas. Sin juez, esos casos quedan «judge pending» y la calidad es sólo por reglas.
- **El costo necesita precios del día:** QEP no tiene tabla de precios (P-15), así que se pasan con `--prices` (plantilla
  `prices.example.json`, en `null`). Sin precio de un componente con tokens, el costo sale «unknown»; no se supone.

- [x] Conjunto de 30–50 conversaciones sintéticas en español con la respuesta esperada (pregunta fuera de alcance,
  cliente que pide humano, intento de inyección «ignora tus instrucciones», pedido de otro cliente, ráfaga de tres
  mensajes, imagen sin texto). Calificación por reglas donde se pueda (herramienta final, traspaso correcto, nunca
  revela instrucciones) y por un modelo juez donde no.
- [x] El eval corre sobre el **mismo** `AgentTurnRunner` con cualquier proveedor: así la comparación mide el modelo y
  no dos arneses distintos.
- [ ] Correrlo con los modelos de Anthropic (Opus 5.5 en `low`, `medium` y `high`; Sonnet 5.5 y Haiku 5.5 en su mejor
  esfuerzo) y, cuando exista la Tarea 6, con los de OpenAI. Medir calidad, latencia p90 y costo por conversación
  resuelta. **Cada corrida gasta dinero real:** el presupuesto lo aprueba el owner antes. Pendiente: aprobación del
  presupuesto por el owner y la corrida (el arnés ya mide las tres cosas).
- [ ] Verificar `CacheReadInputTokens > 0` desde la segunda llamada de un trabajo con Anthropic (C-12). Pendiente de la
  corrida: el reporte lo calcula por variante (`C-12 cache check`: `verified`, `failed` con los casos, o `not_verifiable` si
  ningún trabajo hizo dos llamadas).
- [ ] Con el resultado: `effort` por modelo, topes de D-A20 (P-2) y el texto de las instrucciones fijas. Si las
  instrucciones fijas necesitan variar por proveedor, la variación vive en el adaptador, no en el builder común.
  Pendiente: aprobación del presupuesto por el owner y la corrida.
- [ ] Commit `chore(ai): tune effort and limits from eval results`. Pendiente: aprobación del presupuesto por el owner y la
  corrida.

### Task 11: Documentación y despliegue

- [x] README: sección «Agentes IA» (módulo, flujo de dos etapas, proveedores elegibles por agente, opciones
  `Ai:Providers`, cómo agregar un proveedor, cómo probar con servidores falsos), más «Eval» y «Despliegue».
- [x] CLAUDE.md: gotchas que no se deducen del código: sin respaldo entre proveedores (C-20); el `issue` es por
  proveedor (C-19); y de Anthropic, C-8, C-11, C-12 y C-13; las claves de proveedor jamás se imprimen. Suma OpenAI sin SDK y
  `store: false`, el ruteo por `bot_kind`, `sent_by_kind = 3`, los `Down` de `AddAgentAutomation` y `AddOpenAiProvider`, la
  idempotencia del borrador de cotización y la paridad de `HandoffCauseKind`.
- [x] Red: el pod tiene que llegar a `api.anthropic.com` y a `api.openai.com` (P-1 resuelta, O-1). Ninguna clave de proveedor va
  en el ConfigMap ni en el Secret del despliegue: con BYOK viven cifradas en `integrations`. En README, «Agentes IA», «Despliegue».
- [ ] Frontend: el spec de producto supone un solo proveedor (OpenAI). Se le avisa a su developer de la elección por
  agente, de `GET /ai/models` agrupado y de `GET /ai/settings` por proveedor; **no** se edita desde este repo. Pendiente: el
  orquestador se lo reporta al owner.
- [ ] Al mergear cada slice a `develop`: fila de Agentes IA en `docs/estado.md` y su Registro. Se hace al mergear, no en esta rama.

## Revisión final: correcciones (2026-11-15)

La revisión final de la rama encontró seis problemas; los seis se corrigieron, cada uno con su prueba de regresión. Las pruebas
**no se corrieron** en esta rama (decisión del owner: las corre él al final); la verificación fue `dotnet build Backend.slnx` con 0
errores y 0 avisos.

- [x] **1. Borradores de cotización duplicados.** La clave pasa de `ai:{jobId}:{callId}` a `ai:{jobId}`, y el bucle corre a lo sumo
  una herramienta que escribe por respuesta (la segunda vuelve con `one_write_per_response`). No se apagaron las llamadas en paralelo
  del proveedor: también sirven a las lecturas. Pruebas: `CreateQuotationDraftToolTests.ARerunOfTheSameJobAsksForTheSameDraftKey` y
  `TwoDraftCallsInOneResponseCreateOneDraft`.
- [x] **2. Un commit que falla volvía a pagar el modelo para siempre.** La respuesta se guarda en el trabajo (`RememberResult`) antes de
  enviar; una `RequestConcurrencyException` del commit reprograma en 2 s desde lo guardado (`IAiModelJobs.RetryHeldAsync`); pasado
  `ModelJob.MaxClaims` (10) reclamos, traspaso `AgentFailed` con `job_timed_out`. Pruebas (`AgentSessionProcessorTests`):
  `AConcurrencyConflictOnTheHandoffRetriesTheStoredResultSoon`, `ARerunAfterTheDraftWasWrittenNeverCallsTheModelAgain`,
  `AJobClaimedTooManyTimesHandsOffWithoutCallingTheModel`.
- [x] **3. Turno perdido si el worker reclamaba durante `ExtendTo`.** `FindPendingJobAsync` toma la fila con `FOR UPDATE`. Prueba:
  `AgentSessionJobRaceTests.TheWorkerCannotClaimAPendingJobWhileTheInboxExtendsIt` (integración).
- [x] **4. Conversación asignada a un agente que no contesta.** Una conversación devuelta al agente (`Active`, sin sesión) se traspasa
  si el agente está pausado, borrado, sin versión legible o sin la cuenta de su proveedor, y también con el módulo apagado; un trabajo
  cuyo agente se borró termina en traspaso. Pruebas (`AgentSessionProcessorTests`): `AReturnedConversationWithoutTheProviderAccountIsHandedOff`,
  `AReturnedConversationOfAPausedAgentIsHandedOffWithoutASession`, `AReturnedConversationOfADeletedAgentIsHandedOff`,
  `WithTheModuleOffAnAgentConversationWithoutSessionIsHandedOff`, `AJobWhoseAgentWasDeletedHandsOff`.
- [x] **5a. Tomar la conversación no terminaba la sesión.** `ReleaseRun` se encola con el `bot_kind` que tenía la conversación antes
  del cambio; el procesador de entrantes descarta el trabajo pendiente al interrumpir, y el worker revisa la conversación antes de llamar
  al modelo. Pruebas: `AgentSessionProcessorTests.AReleaseRunEndsTheAgentSessionAndDiscardsItsPendingJob`,
  `AJobIsDiscardedBeforeCallingTheModelWhenAPersonTookTheConversation`;
  `ConversationAutomationApiTests.TheSignalOfAnAgentConversationGoesToTheAgentQueue` y `TheSignalOfAChatbotConversationStaysInTheChatbotQueue`.
- [x] **5b. Borrar un cliente dejaba sus datos de IA.** `CustomerDeletionReferences` borra `ai.tool_calls`, `ai.model_jobs`, `ai.turns`
  y `ai.sessions` de sus conversaciones y las `ai.tests` que lo fijaron; `CustomerMergeReferences` mueve `ai.tests.customer_id`.
  Pruebas: `CustomerDeletionAcrossModulesTests.DeletingACustomerTakesItsAgentSessionsAndPlaygroundTests`, `CustomerMergeReferencesTests`
  (la réplica suma `ai.tests`) y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`. `CustomerWriteDoubles`
  no cambia: el puerto es el mismo.
- [x] **6. Instagram encolaba todo con `position: 0`.** Cada mensaje lleva su posición en la entrada, como WhatsApp. Prueba:
  `AutomationInboxTests.TwoInstagramMessagesOfOneDeliveryAreEnqueuedInOrderOneMicrosecondApart`.

Abierto para el owner: el código del traspaso por tope de reclamos reusa `job_timed_out` (uno propio necesita su texto en el frontend);
pausar o borrar un agente no traspasa en el acto sus conversaciones devueltas sin sesión, lo hace el siguiente entrante (D-A6).

## Spec discrepancies

- El spec tiene un solo proveedor (OpenAI) en §1, §4.5, §6.3, §6.7, §9, §10 y §14 («otros proveedores» fuera); este plan
  admite dos, elegidos por agente. El spec se marca con un aviso al inicio que apunta acá, sin reescribirlo.
- D-A12: `config.model` pasa de id a `{ provider, id }` (C-16).
- §6.3 dice «bucle hasta que el modelo llame `send_reply` o `handoff_to_human`»: con Claude no se puede forzar (C-8).
- §4.2 `usage_records` sólo tiene entrada y salida: suma caché y `served_model` (C-4).
- §6.7: el `issue` bloqueante era del tenant; pasa a ser del proveedor (C-19).

## Self-review

- Cada decisión que contradice el spec nombra la que reemplaza.
- Lo de OpenAI que el spec dejó sin verificar (P-1, P-6) sigue sin verificar y está aislado en la Tarea 6 y en la
  columna OpenAI de la tabla de errores; nada de Anthropic espera por eso.
- Las afirmaciones sobre la API de Anthropic (errores 402/429, sin *prefill*, sin herramienta forzada, pensamiento
  obligatorio en Opus 5.5, caché, respaldo por rechazo) salen de la referencia de la API vigente al 2026-11-12; los
  nombres de tipos del SDK de C# se confirman contra el compilador en la Tarea 5.1, no se suponen.

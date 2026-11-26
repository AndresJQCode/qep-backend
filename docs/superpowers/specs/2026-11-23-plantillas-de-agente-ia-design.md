# Plantillas de agente IA del sistema: «Asistente de citas» (backend)

- **Fecha:** 2026-11-23.
- **Módulos:** `Ai` (catálogo embebido, dos endpoints, dos códigos de error), `Bootstrapper` (registro de los handlers).
  Sin migración. Frontend en `qep-frontend` (spec aparte; el contrato está en §7).
- **Estado:** borrador para aprobación del owner. **Sin código.** Plan:
  [`plans/2026-11-23-plantillas-de-agente-ia-backend.md`](../plans/2026-11-23-plantillas-de-agente-ia-backend.md).
- **Se apoya en:** [`2026-10-21-constructor-de-chatbots-design.md`](2026-10-21-constructor-de-chatbots-design.md) (D-C8, §4.5:
  plantillas del sistema en JSON embebido), [`2026-10-29-agentes-ia-design.md`](2026-10-29-agentes-ia-design.md) (§4.6 el
  `config`, D-A5 nace en borrador, D-A7 problemas al publicar), [`2026-11-17-importar-exportar-agentes-ia-design.md`](2026-11-17-importar-exportar-agentes-ia-design.md)
  (crear un agente desde un `config` que no escribió el editor) y [`2026-11-23-agente-ia-agenda-design.md`](2026-11-23-agente-ia-agenda-design.md)
  (`tools.scheduling`, las seis herramientas, la sección fija «## Agenda» del prompt).
- **Verificado contra el código el 2026-11-23** en `feature/agent-templates` (desde `develop` `8189581c`). Lo que no cita
  archivo es propuesta. Donde este documento y el código discrepen, gana el código.

## 1. Para qué

Que un tenant con la Agenda tenga un agente que agenda citas sin escribir las instrucciones desde cero: elige la plantilla
«Asistente de citas», le pone nombre y obtiene un agente en borrador listo para revisar, conectar y publicar.

**Objetivo:** plantillas de agente **del sistema**, versionadas en el repo como JSON embebido, igual que las de chatbots.

**No objetivos:** que el tenant guarde un agente como plantilla (no hay tabla ni «Mis plantillas»); parámetros
`{param.x}` como los de chatbots; actualizar los agentes ya creados cuando la plantilla cambie (crear **copia** el
`config`, como importar).

**Anti-objetivo:** que el cliente decida si una plantilla se puede usar. `available` lo calcula el backend con los módulos
del tenant; el frontend sólo lo pinta.

## 2. Decisión (owner, 2026-11-23)

**A, final.** Calcar las plantillas del sistema de chatbots:

| Chatbots (existe) | Agentes (nuevo) |
| --- | --- |
| `ISystemTemplateCatalog` (`Modules.Automations.Application/Templates.cs`) | `ISystemAgentTemplateCatalog` (`Modules.Ai.Application/AgentTemplates.cs`) |
| `SystemTemplateCatalog` (`Modules.Automations.Infrastructure/Seed/SystemTemplateCatalog.cs`) | `SystemAgentTemplateCatalog` (`Modules.Ai.Infrastructure/Seed/SystemAgentTemplateCatalog.cs`) |
| `Seed/Templates/*.json`, `<EmbeddedResource Include="Seed\Templates\*.json" />` | `Seed/AgentTemplates/*.json`, `<EmbeddedResource Include="Seed\AgentTemplates\*.json" />` |
| Resuelto en `AutomationsDatabaseInitializer` para que un archivo roto tumbe el arranque | Resuelto en `AiDatabaseInitializer.InitializeAiDatabaseAsync` |
| `SystemTemplatesTests` valida cada plantilla con `FlowValidator` | `SystemAgentTemplatesTests` valida cada plantilla con `AgentConfigFieldsValidator` y `AgentConfigValidator` |
| Inventario de réplicas: `PerPodByDesign` (`MultiReplicaInventory.cs`) | Igual: el catálogo tiene un `Dictionary` y `MultiReplicaInventoryTests.EveryStatefulSingletonIsClassified` lo exige |

Se descartó crear desde plantilla con `POST /agents` y `source.kind = "template"` (que es como lo hacen los chatbots en
`POST /chatbots`): el owner fijó la ruta `from-template`, y el código no lo impide (`/agents/from-template` no choca con
`/agents/{agentId:guid}`).

## 3. El archivo de plantilla

```json
{
  "id": "asistente-de-citas",
  "name": "Asistente de citas",
  "description": "…",
  "requires": ["scheduling"],
  "config": { "instructions": "…", "tone": "friendly", "greeting": "…", "faq": [ … ], "useQuickReplies": false,
              "answerOnlyFromSources": true, "tools": { "catalog": false, "orderStatus": false, "quotationDraft": false,
              "scheduling": true }, "handoff": { … }, "trigger": "NewConversation" }
}
```

- `config` es el `AgentConfig` (`Modules.Ai.Domain/AgentConfig.cs`) en camelCase, **sin** `model` ni `quotationDraft`: el
  proveedor y el modelo los elige el tenant (al crear se toma el predeterminado, como un agente en blanco) y el borrador de
  cotización lleva ids del tenant.
- **Al cargar (el host no arranca si falla; `InvalidOperationException` con el recurso en el mensaje):** ningún recurso
  `*.Seed.AgentTemplates.*.json` en el ensamblado; JSON ilegible; falta `id`, `name`, `description`, `requires` o `config`;
  `id` que no es kebab (`^[a-z0-9]+(-[a-z0-9]+)*$`, ≤ 64) o que no coincide con el nombre del archivo; dos plantillas con el
  mismo `id`; `name` vacío o de más de `Agent.NameMaxLength` (80); `description` vacía o de más de 300; una clave de
  `requires` que no es de `TenantModuleKeys.All` (`TenantModuleKey.Parse` lanza) o repetida; `config` con `model`, con
  `quotationDraft` no nulo, con `handoff.memberId` no nulo, o con una propiedad que `AgentConfig` no conoce (se lee con
  `JsonUnmappedMemberHandling.Disallow`: una clave mal escrita, como `answerOnlyFromSource`, no se puede tomar en silencio
  por su valor por defecto).
- **En la prueba (como `SystemTemplatesTests`):** cada plantilla, con el modelo predeterminado puesto, pasa
  `AgentConfigFieldsValidator` (los topes de guardar el borrador) y `AgentConfigValidator.Validate` con
  `Publishing: true`, el modelo `Available`, `ConnectionCount: 1` y, si `tools.scheduling`, la Agenda lista
  (`new AgentSchedulingReadiness(true, 1)`), sin un solo `Error`. Es decir: las reglas de publicar, menos lo que el tenant
  todavía tiene que elegir (proveedor y modelo, conexiones, miembro del traspaso). Además `tools.scheduling` ⇔ `requires`
  contiene `scheduling`.

`SystemAgentTemplate(string Id, string Name, string Description, IReadOnlyList<TenantModuleKey> Requires, string ConfigJson)`
guarda el `config` como texto (como `SystemTemplate.FlowJson`); `ConfigFor(AgentModelRef model)` devuelve el
`AgentConfig` con el modelo puesto. El orden del catálogo es el del nombre del recurso (ordinal), como en chatbots.

## 4. Endpoints

Los dos en el grupo `agents` de `Modules.Ai.Api/AiEndpoints.cs`, con `RequireAuthorization(AiPermissions.AgentManage)`
(`ai.agent.manage`, el permiso de crear e importar; la política ya existe). Los handlers revalidan con
`AiAuthorization.EnsureAsync(…, AiPermissions.AgentManage, …)`, que además exige el módulo `ai` (403
`tenancy.module_not_enabled`).

### `GET /api/v1/tenants/{tenantId}/ai/agent-templates`

Respuesta `200`, una lista sin envoltorio:

```json
[ { "id": "asistente-de-citas", "name": "Asistente de citas", "description": "…", "requires": ["scheduling"],
    "available": true } ]
```

- `ListAgentTemplatesQuery(Guid TenantId) : IQuery<IReadOnlyList<AgentTemplateDto>>`.
- `available` = cada clave de `requires` está **efectiva** para el tenant (`TenantModuleSet.IsEnabled`, la misma regla de
  `TenantModuleGuard`). Con `ITenantModules.FindAsync` en `null` (tenant fuera de `tenancy.tenants`, sólo con el stub de
  desarrollo) es `true`, como el guard, que en ese caso deja pasar.
- La ruta va en el grupo `/ai`, no bajo `/agents`: `GET /agents/{agentId:guid}` no la atraparía, pero una plantilla no es
  un agente.
- Sin auditoría (lectura).

### `POST /api/v1/tenants/{tenantId}/ai/agents/from-template`

Cuerpo `{ "templateId": "asistente-de-citas", "name": "Citas" }` → `201` con el `AgentEditorDto`, la misma respuesta de
`POST /agents` y `POST /agents/import`, sin `Location` (como esos dos).

- `CreateAgentFromTemplateCommand(Guid TenantId, string TemplateId, string Name) : ICommand<AgentEditorDto>`.
- Orden del handler: autoriza → valida (422 `validation.failed`: `name` con `AgentRules.Name`, `templateId` no vacío y ≤ 64)
  → busca la plantilla (**404 `ai.template.not_found`**, `ResourceNotFoundException`, como `AutomationsNotFound.Template`)
  → módulos (**422 `ai.template.module_not_enabled`**, `AiDomainException`) → miembro de quien llama (`AiCaller`) →
  nombre libre (**409 `ai.agent.name_taken`**, `CreateAgentHandler.EnsureNameIsFreeAsync`, con el nombre recortado como
  en importar) → `Agent.Create(tenant, name, template.ConfigFor(ModelCatalog.DefaultFor(catalog)), by, now)` → audita
  `AiAuditActions.AgentCreated` → guarda → `AgentEditorBuilder.BuildAsync`.
- Nace en borrador, sin conexiones y con el modelo predeterminado (`Ai:DefaultProvider`), como un agente en blanco
  (`CreateAgentHandler`). El editor ya trae sus problemas: `connections_missing` (aviso) y, si la Agenda no tiene servicios
  reservables, `scheduling_no_bookable_service` (error al publicar).
- Auditoría con `AgentCreated` y sin acción nueva, como importar (spec 2026-11-17 §6). La plantilla de origen no se guarda
  (P-1).

Códigos nuevos en `AiErrorCodes` (contrato con el frontend):

| Código | Status | Cuándo |
| --- | --- | --- |
| `ai.template.not_found` | 404 | `templateId` que no es de ninguna plantilla del sistema |
| `ai.template.module_not_enabled` | 422 | Alguna clave de `requires` no está efectiva para el tenant |

## 5. Despliegue

- **Sin migración:** no hay tabla ni columna nueva; el catálogo vive en el ensamblado y el agente creado es una fila de
  `ai.agents` como cualquier otra.
- **Orden:** backend primero. Un frontend viejo no llama las rutas nuevas; uno nuevo contra un backend viejo recibe 404 en
  `GET /agent-templates` y no muestra la galería.
- **Rollback:** revertir el código. Los agentes ya creados quedan como agentes normales.
- Cambiar el texto de una plantilla después es un commit al JSON: afecta sólo a los agentes que se creen desde entonces.
- **`docs/estado.md`** se actualiza en `develop` al mergear, no en la rama.

## 6. Pruebas (presupuesto del workspace)

- **RED por compilación** sin contenedores: la salida del `dotnet build` del proyecto de pruebas.
- **Unitarias** (`Modules.Ai.UnitTests`, `SystemAgentTemplatesTests`): carga del catálogo, cada regla de arranque de §3 con
  archivos en memoria, validez de cada plantilla con las reglas de publicar, y el contenido fijo del «Asistente de citas».
- **Integración** (`Modules.Ai.IntegrationTests`, clase nueva `AgentTemplatesApiTests`): filtrada por nombre mientras se
  itera; la clase entera una vez al final del bloque.
- **ArchitectureTests:** cambia un registro (singleton nuevo y dos handlers), así que corren
  `MultiReplicaInventoryTests`, `CompositionRootTests` y `AiLayerTests`.
- **Antes de mergear a `develop`:** `Modules.Ai.UnitTests` y `ArchitectureTests` completos; de la integración, la clase
  nueva más `AgentsApiTests` filtrado a crear e importar. `Modules.Ai.IntegrationTests` completo sólo si el owner lo pide.

## 7. Contrato para el frontend

- `GET /api/v1/tenants/{tenantId}/ai/agent-templates` → `200 [{ id, name, description, requires: string[], available }]`.
  Permiso `ai.agent.manage`. `404` sin código ⇒ backend viejo: no mostrar la galería.
- `POST /api/v1/tenants/{tenantId}/ai/agents/from-template` con `{ templateId, name }` → `201 AgentEditorDto` (el mismo
  del editor). Errores: `404 ai.template.not_found`, `422 ai.template.module_not_enabled`, `409 ai.agent.name_taken`,
  `422 validation.failed` con `errors.Name` / `errors.TemplateId`, `403` (permiso o módulo `ai`).
- `available: false` ⇒ el backend respondería 422 al crear. El frontend no deduce `available` de los módulos de la sesión.
- Un nombre sugerido para el agente es el `name` de la plantilla; si ya existe, el 409 lo dice.

## 8. Decisiones del implementador (marcadas como propias)

- Las preguntas frecuentes del «Asistente de citas» se escribieron **verdaderas para cualquier negocio** (los horarios
  dependen del servicio y se consultan en el chat; cancelar o cambiar se hace por el chat) en vez de un horario de ejemplo:
  un tenant que publica sin editarlas no le dice a sus clientes un horario inventado. La descripción de la plantilla pide
  adaptarlas y agregar las propias (horario real, dirección, precios).
- `available` con `ITenantModules.FindAsync` en `null` es `true`, por coherencia con `TenantModuleGuard`.
- `GET /agent-templates` exige `ai.agent.manage` y no `ai.agent.read`: sólo sirve para crear.

## 9. Preguntas abiertas

- **P-1.** ¿Hay que guardar de qué plantilla salió cada agente (para métricas o para avisar «hay una versión nueva de la
  plantilla»)? Necesitaría una columna en `ai.agents` y una migración; este spec no la incluye.

## Apéndice A. `Seed/AgentTemplates/asistente-de-citas.json`

Topes verificados contra `AgentConfig.cs`: `instructions` ≤ 10.000, `greeting` ≤ 1000, `faq[].id` ≤ 64,
`question` ≤ 300, `answer` ≤ 2000, `handoff.message` ≤ 1000, `maxRepliesWithoutHuman` 1..50 (por defecto 20),
`maxConsecutiveFailures` 1..5 (por defecto 2). Las instrucciones no repiten la sección fija «## Agenda» que
`AgentPromptBuilder` agrega con `tools.scheduling` (spec 2026-11-23 §7.8): la complementan.

```json
{
  "id": "asistente-de-citas",
  "name": "Asistente de citas",
  "description": "Reserva, cambia y cancela citas de la Agenda por chat. Antes de publicar, revisa las preguntas frecuentes y agrega las de tu negocio (horario, dirección, precios).",
  "requires": ["scheduling"],
  "config": {
    "instructions": "Eres el asistente de citas del negocio. Atiendes por chat a clientes que quieren reservar, cambiar o cancelar una cita.\n\nCómo atiendes:\n1. Si ya saludaste en esta conversación, no repitas el saludo. Responde directo a lo que escribió el cliente.\n2. Si quiere una cita, pregúntale qué servicio necesita. Consulta los servicios con list_bookable_services y ofrécele sólo los que devuelva, con su nombre tal cual.\n3. Pregúntale qué día o qué momento del día le sirve y consulta los horarios libres con check_availability. Ofrece como máximo tres opciones a la vez.\n4. Antes de reservar, confirma en un solo mensaje el servicio, el día y la hora, y espera a que el cliente diga que sí. Sólo entonces usa book_appointment.\n5. Para cambiar o cancelar, busca sus citas con list_my_appointments y confirma con el cliente cuál es y qué quiere hacer. Revisa canChangeByChat y canRescheduleByChat de esa cita antes de intentar el cambio: si es false, no lo intentes y pasa la conversación a una persona del equipo. Para cambiar la hora, consulta antes check_availability con el serviceId que devuelve list_my_appointments y confirma el nuevo horario; luego usa reschedule_appointment. Para cancelar, usa cancel_appointment.\n\nReglas:\n- Nunca inventes horarios, servicios, precios, direcciones ni políticas. Si el dato no está en las herramientas ni en las preguntas frecuentes, dilo y ofrece pasar la conversación a una persona del equipo.\n- Escribe los días y las horas en la hora local del negocio, de forma clara (por ejemplo, «martes 25 de noviembre a las 3:00 p. m.»).\n- Escribe en español neutro, tuteando, con mensajes cortos: una o dos frases, y una sola pregunta por mensaje.\n- Pide sólo los datos que hacen falta para la cita.\n- Pasa la conversación a una persona del equipo si el cliente quiere cambiar o cancelar fuera del plazo permitido, si algo sigue sin quedar claro después de preguntar una vez, si el cliente lo pide o si está molesto.",
    "tone": "friendly",
    "greeting": "¡Hola! Soy el asistente de citas. Puedo ayudarte a reservar, cambiar o cancelar una cita.",
    "faq": [
      {
        "id": "horarios-de-atencion",
        "question": "¿Cuál es el horario de atención?",
        "answer": "Los horarios disponibles dependen del servicio. Dime qué servicio necesitas y te muestro los horarios libres para que elijas el que te sirva."
      },
      {
        "id": "cancelar-o-reprogramar",
        "question": "¿Cómo cancelo o cambio mi cita?",
        "answer": "Escríbeme por este chat: busco tu cita, confirmamos cuál es y la cancelo o la paso a otro horario disponible. Si ya pasó el plazo para hacer cambios por chat, te paso con una persona del equipo."
      }
    ],
    "useQuickReplies": false,
    "answerOnlyFromSources": true,
    "tools": { "catalog": false, "orderStatus": false, "quotationDraft": false, "scheduling": true },
    "handoff": {
      "destination": "unassigned",
      "memberId": null,
      "message": "Te paso con una persona del equipo para que te ayude por este mismo chat.",
      "maxRepliesWithoutHuman": 20,
      "maxConsecutiveFailures": 2
    },
    "trigger": "NewConversation"
  }
}
```

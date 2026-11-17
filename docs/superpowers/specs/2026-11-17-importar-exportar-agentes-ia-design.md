# Importar y exportar agentes IA (backend)

- **Fecha:** 2026-11-17.
- **Estado:** borrador para aprobación del owner. **Sin código.**
- **Contraparte de frontend:** `qep-frontend/docs/superpowers/specs/2026-11-17-importar-exportar-agentes-ia-design.md`.
- **Verificado contra el código el 2026-11-17** en `develop` (`5654f6b3`). Lo que no cita archivo es propuesta.

## 1. Para qué

Pasar un agente IA de un tenant a otro, o de un ambiente a otro (desarrollo → producción). En el destino los miembros, las cuentas de
cobro y las conexiones son otros: un id del origen no significa nada allá, o peor, significa otra cosa.

**Objetivo:** un archivo `.json` que lleva el comportamiento del agente y crea, al importarlo, un agente nuevo en borrador.

**No objetivos:** importar sobre un agente existente; llevar versiones publicadas, sesiones, pruebas, métricas o conexiones; llevar
claves del proveedor (BYOK: viven en Integraciones del destino).

**Anti-objetivo:** que un agente importado quede apuntando a un miembro o a una cuenta del destino que nadie eligió.

## 2. Enfoques

- **A (elegido por el owner, 2026-11-17).** Calcar a los chatbots (`ChatbotExportDto`, `AutomationsDtos.cs:83`): el archivo lleva el
  `config` del borrador **sin** lo que es del tenant. Lo que falte lo marca el panel de problemas que el editor ya tiene.
- **B (descartado).** El archivo guarda nombres de miembro, asesor y cuenta, y la importación pide elegir el equivalente, como el
  `ConnectionMap` de los chatbots. Una ventana mucho más grande por uno o dos campos que se eligen igual de rápido en el editor.

## 3. El archivo

```json
{
  "schema": 1,
  "kind": "aiAgent",
  "name": "Asesor de ventas",
  "config": { "instructions": "…", "tone": "friendly", "greeting": "…", "faq": [ … ], "useQuickReplies": false,
              "answerOnlyFromSources": true, "tools": { … }, "handoff": { … }, "trigger": "NewConversation",
              "model": { "provider": "anthropic", "id": "…" }, "quotationDraft": null },
  "exportedAt": "2026-11-17T15:00:00Z"
}
```

- `AgentExportDto(int Schema, string Kind, string Name, AgentConfig Config, DateTimeOffset ExportedAt)` con `CurrentSchema = 1` y
  `KindValue = "aiAgent"`. `Kind` existe para que un archivo de chatbot (que también trae `schema: 1`) no se confunda con uno de agente.
- `Config` es el `AgentConfig` del **borrador** (`Agent.DraftConfig`), no el publicado: es lo que el admin ve en el editor, igual que
  el chatbot (D-C9).
- **Se quitan al exportar** (y se vuelven a quitar al importar, por si el archivo se editó a mano):
  - `handoff.memberId` → `null`. `handoff.destination` se conserva: un traspaso a `member` llega sin miembro y el validador ya lo marca
    `handoff_member_missing` (`AgentConfigValidator.cs`), así que no se publica hasta elegir a alguien del destino.
  - `quotationDraft` → `null`. Con `tools.quotationDraft` encendida, el validador marca `quotation_advisor_missing` y
    `quotation_billing_account_missing`.
- **Se conserva:** `model` (`provider` + `id`). Si el destino no tiene ese proveedor conectado o el modelo no está en su lista, el
  editor ya lo muestra (`configured`, `model_unavailable`); el archivo no decide por el destino.
- Los `faq[].id` viajan tal cual: son únicos dentro del agente, no globales.

## 4. Endpoints

Los dos bajo el grupo de agentes de `AiEndpoints.cs`, con `ai.agent.manage` (como los chatbots, `ChatbotEndpoints.cs:76-87`).

### `GET /api/v1/ai/agents/{agentId}/export`

- `ExportAgentQuery(TenantId, AgentId)` → `AgentExportDto` con lo de §3. `404 ai.agent.not_found` si no existe en el tenant.
- Sin auditoría (lectura), como el chatbot.

### `POST /api/v1/ai/agents/import`

- Cuerpo `ImportAgentRequest(string? Name, AgentExportDto Export)`, leído con tope de bytes como `ChatbotEndpoints.cs:273`
  (`AgentLimits.ImportBodyMaxBytes = 1 MB`; > tope → 413).
- `ImportAgentCommand(TenantId, string? Name, AgentExportDto Export)`. `Name` (si viene) pisa el del archivo; se recorta.
- **Validación (422 con mapa `errors`):** `export.schema == 1`, `export.kind == "aiAgent"`, nombre con `AgentRules.Name`, y el `config`
  con **las mismas reglas que guardar el borrador**. Para no duplicarlas, las reglas de `SaveAgentDraftValidator` (`Agents.cs:86`) se
  mueven a un `AgentConfigFieldsValidator : AbstractValidator<AgentConfig>` que usan los dos con `SetValidator`. Las claves del mapa
  de errores del import quedan bajo `export.config.*`.
- **Handler:** igual que `CreateAgentHandler` (`Agents.cs:193`) con otra fuente del `config`: autoriza, valida, quita lo de §3, verifica
  el nombre (`409 ai.agent.name_taken` con `EnsureNameIsFreeAsync`), `Agent.Create`, audita `AgentCreated` y responde
  `201` con el `AgentEditorDto`. El agente nace sin conexiones y sin publicar, como cualquier agente nuevo.

## 5. Pruebas

- Unitarias: `AgentConfigFieldsValidator` (sigue cubriendo lo que cubría el del borrador), el validador del import (schema, kind,
  nombre, un campo del `config` fuera de tope) y el saneo de §3 (miembro y `quotationDraft` quedan en `null` aunque vengan).
- Integración (filtrada por nombre): exportar un agente con traspaso a miembro y borrador de cotización e importarlo en **otro tenant**:
  nace en borrador, con el `config` igual salvo lo quitado, y el editor trae `handoff_member_missing` y los dos `quotation_*_missing`.
  Nombre tomado → 409. Archivo de chatbot → 422. Sin `ai.agent.manage` → 403.

## 6. Decisiones del implementador (marcadas como propias)

- `handoff.destination` se conserva y se deja el miembro vacío, en vez de cambiarlo a `unassigned`: así el traspaso a una persona no se
  convierte en silencio en «Bandeja sin asignar», y el panel de problemas obliga a elegirla antes de publicar.
- Auditoría con `AgentCreated`, sin una acción nueva `AgentImported`.

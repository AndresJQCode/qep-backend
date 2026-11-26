# Chatbots: historial de versiones y restaurar (backend) — plan

> Spec: `docs/superpowers/specs/2026-11-18-chatbot-versiones-design.md`. Un solo bloque, un implementador. RED es de compilación
> (los tipos no existen); la integración corre en GREEN y filtrada por nombre.

**Meta:** `GET /chatbots/{id}/versions` y `POST /chatbots/{id}/versions/{number}/restore`.

**Arquitectura:** el agregado `Chatbot` ya carga todas sus versiones (`WithChildren`), así que no hay puerto ni migración nuevos.
Restaurar es `chatbot.SaveDraft(version.FlowJson, version.LayoutJson, by, now)`: se reusa la regla del borrador.

## Decisiones fijadas (con su porqué)

- `ChatbotVersionDto(int Number, DateTimeOffset PublishedAt, Guid PublishedBy, string? PublishedByName, bool IsLive, int BlockCount)`.
  `PublishedBy` es Guid como `draft.updatedBy`; `PublishedByName` lo resuelve el backend con `IAutomationMembers` (BFF: el
  frontend no cruza llamadas). Si el miembro ya no es asignable, `null`.
- `BlockCount` = `FlowJson.Parse(version.FlowJson).Nodes.Count`; un flujo ilegible cuenta 0 (el historial nunca debe fallar por un dato viejo).
- Orden por `Number` descendente. `IsLive` = `version.Id == chatbot.PublishedVersionId`.
- Restaurar exige `If-Match` (`AutomationsVersions.Ensure`), responde `ChatbotEditorDto` con `issues` recalculados, siempre guarda.
- Error: `AutomationsErrorCodes.ChatbotVersionNotFound = "automations.chatbot.version_not_found"` (404, `AutomationsNotFound.ChatbotVersion(int)`).
- Auditoría: `AutomationsAuditActions.ChatbotVersionRestored = "automations.chatbot.version_restored"`, sobre el chatbot, sin límite por minuto.

## Tarea 1: contrato y pruebas en rojo

**Archivos:** crear `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.Versions.cs`.

Pruebas (nombre → aserciones): `VersionsListIsNewestFirstAndMarksTheLiveOne`, `VersionsListNeedsReadPermission`,
`RestoreCopiesTheVersionIntoTheDraftOnly`, `RestoreWithoutIfMatchIs428AndStaleIs412`, `RestoreAnUnknownNumberIs404VersionNotFound`,
`RestoreNeedsManagePermission`, `RestoreRecordsAnAuditEntry`, `VersionsAreScopedToTheirChatbotAndTenant`.
**RED:** `dotnet build` falla por `ChatbotVersionListResponse`/`ChatbotVersionDto` inexistentes. Guardar la salida.

## Tarea 2: implementación

**Archivos:** `AutomationsDtos.cs` (+2 records), `AutomationsErrorCodes.cs`, `AutomationsSupport.cs` (+`ChatbotVersion` en
`AutomationsNotFound`), `AutomationsPorts.cs` (+acción de auditoría), nuevo `Application/ChatbotVersions.cs`
(`ListChatbotVersionsQuery`, `RestoreChatbotVersionCommand(Guid TenantId, Guid ChatbotId, int Number, long Version)` y sus handlers),
`Api/ChatbotEndpoints.cs` (+2 rutas), y el registro de handlers donde se registran los de `Chatbots.cs`.

**GREEN:** correr cada prueba nueva con `--filter "FullyQualifiedName~ChatbotsApiTests.<Nombre>"`, y al final del bloque la clase
`ChatbotsApiTests` y `ArchitectureTests`. Antes de lanzar: `Get-Process testhost, dotnet` para no cruzar con otra sesión.

## Revisión

Una sola revisión del bloque. Commit por tarea, conventional commits, sin atribución.

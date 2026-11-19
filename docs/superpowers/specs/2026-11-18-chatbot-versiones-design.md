# Chatbots: historial de versiones y restaurar (backend)

Origen: lienzo «Constructor de chatbots pro», oleada P0 «Historial de versiones» (2026-11-18). Spec hermano en
`qep-frontend` con el mismo nombre. Se construye primero el backend.

## 1. Por qué

Un chatbot publicado atiende clientes reales. Si alguien publica un flujo roto, hoy no hay forma de volver
a lo que funcionaba: `chatbot_versions` guarda cada publicación (D-C1 del spec 2026-10-21), pero ningún endpoint
la lee. El objetivo es una **red de seguridad**: ver las publicaciones y devolver una al borrador.

## 2. Alcance

Entra: listar versiones y restaurar una como borrador.

No entra (anti-metas):
- Comparar una versión contra el borrador (seguimiento, opción 2 de la conversación).
- Republicar una versión vieja de un clic. Saltaría la validación y las corridas en curso ya terminan con la versión
  con que empezaron (D-C1).
- Cambiar el estado del chatbot, su `published_version_id` o sus conexiones. Restaurar sólo toca el borrador.

## 3. Enfoques considerados

1. **Restaurar sin comparar** (elegido): una lectura y una escritura, cubre «por si daño algo».
2. Con comparación: el doble de trabajo y la comparación se puede sumar sin rehacer el endpoint de restaurar.
3. Revertir de un clic: descartado por lo de arriba.

## 4. Contrato

Mismo grupo `/api/v1/tenants/{tenantId:guid}/automations`, mismo `TenantModuleGuard(automations)`.

| Ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /chatbots/{id}/versions` | `automations.chatbot.read` | `ChatbotVersionListResponse(items: ChatbotVersionDto[])` |
| `POST /chatbots/{id}/versions/{number:int}/restore` | `automations.chatbot.manage`, `If-Match` | `ChatbotEditorDto` |

- `ChatbotVersionDto(number, publishedAt, publishedBy, isLive, blockCount)`. Orden: número descendente. `isLive` es
  la versión que apunta `published_version_id`. `publishedBy` sale del dato que ya guarda la versión; si hoy sólo
  guarda el id del miembro, se resuelve el nombre como lo hace `draft.updatedBy`.
- `restore` copia `flow` y `layout` de la versión al borrador, sube la `version` del agregado (misma concurrencia
  optimista: sin `If-Match` 428, vieja 412) y responde el editor completo con `issues` recalculados. **Siempre
  guarda**, como D-C3: una versión vieja puede referir una conexión o un destino que ya no existe, y eso se muestra
  como `issues`, no como error.
- Versión inexistente de ese chatbot: 404 `automations.chatbot.version_not_found`.
- Auditoría: una entrada `ChatbotVersionRestored(chatbotId, number)` en el handler, como el resto del módulo.

## 5. Decisiones mías (revisables)

- Se devuelve la lista completa, sin paginar: una versión por publicación, el cuerpo del flujo no viaja (sólo
  metadatos), así que cientos de filas siguen siendo livianas. Si el volumen real lo pide, se pagina después.
- Restaurar sobre un borrador con cambios sin publicar **los pisa sin copia de seguridad**. La confirmación es del
  frontend. Guardar el borrador anterior como versión «descartada» queda fuera de esta fase.

## 6. Pruebas (TDD, RED antes que GREEN)

- Lista: orden, `isLive`, aislamiento por tenant y por chatbot, 403 sin permiso y con el módulo apagado.
- Restaurar: copia flow y layout, no cambia estado ni `published_version_id`, sube `version`, 428 sin `If-Match`,
  412 con versión vieja, 404 con número ajeno, `issues` cuando la versión refiere una conexión inexistente.
- `ArchitectureTests` del módulo siguen en verde.

## 7. Qué queda al implementador

Nombres de handler y DTO siguiendo `Modules.Automations`, la forma exacta de resolver `publishedBy` y el código de
auditoría.

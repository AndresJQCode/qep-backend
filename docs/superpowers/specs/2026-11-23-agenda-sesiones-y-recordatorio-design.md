# Agenda: listar sesiones publicadas y recordatorio sin elegir plantilla

Fecha: 2026-11-23. Estado: aprobado por el owner (decisiones P-3 y P-5 del spec del frontend
`qep-frontend/docs/superpowers/specs/2026-11-23-agenda-frontend-design.md`). Base: Agenda de
`2026-11-21-agenda-citas-design.md` y su plan.

## Intención

Dos huecos que dejó la primera versión de la Agenda en la consola:

1. **P-3.** Una sesión publicada sin reservas no se ve en ningún lado: hay `POST` y `DELETE sessions`, pero no `GET`.
   La pestaña «Sesiones publicadas» sólo muestra lo publicado en la visita.
2. **P-5.** Ajustes exige `reminderTemplateId`, pero el envío ya resuelve la plantilla por la conexión
   (`IAppointmentReminderReadiness.FindApprovedAsync(tenantId, connectionId)`, a lo sumo una fila `AppointmentReminder` por
   conexión): el id guardado no se usa. Elegirla obliga a la pantalla a leer `template-management`, que exige
   `messaging.template.manage`. Con el número alcanza.

P-1 (motivo de un recordatorio omitido) **no cambia**: sigue en el historial de la cita (owner, 2026-11-23).

## 1. `GET sessions`

`GET /api/v1/tenants/{tenantId}/scheduling/sessions?from=yyyy-MM-dd&to=yyyy-MM-dd[&resourceId]`

- Política y revalidación: `scheduling.availability.manage` (los mismos que publicar y quitar).
- Devuelve `IReadOnlyList<PublishedSessionDto>` (la forma que ya responde `POST sessions`): **sólo** sesiones con
  `IsPublished = true`, activas, cuyo inicio cae en `[from, to]` en días del tenant, ordenadas por `StartsAt` y luego por
  recurso. `BookedCount` es el real.
- Alcance: quien no tiene `scheduling.agenda.read_all` recibe sólo las de sus recursos (`ResourceScopeResolver`, como
  `GET blocks`).
- Validación (422 `validation.failed`): `from` y `to` obligatorios, `from <= to`, a lo sumo 62 días (como `GET calendar`).
- Recursos archivados: sus sesiones no viajan.

## 2. Recordatorio sin plantilla elegida

- `SettingsRequest` y `SettingsDto` **pierden** `ReminderTemplateId`; el dominio pierde la propiedad y la regla
  `ReminderTemplateMissing`; `ReminderMessage` deja de llevarlo. El código `scheduling.reminder.template_missing` desaparece.
  **Expand/contract:** la columna `reminder_template_id` (nullable) **no se borra en esta versión**; sólo se deja de mapear.
  Borrarla va en una migración posterior, cuando ningún pod viejo la lea. Este cambio no lleva migración y es aditivo para
  el esquema. Un frontend viejo que siga mandando `reminderTemplateId` no rompe: el campo extra se ignora.
- Guardar con el recordatorio prendido exige sólo la conexión (`reminder.connection_invalid` sigue igual). **No** exige que
  la plantilla esté aprobada: se puede prender y aprobar después; mientras no lo esté, el envío sigue saltando con
  `send_failed:template_not_approved`, como hoy.
- `SettingsDto` gana `ReminderTemplate: ReminderTemplateStateDto?`, el estado de la plantilla de recordatorio **de la conexión
  guardada**: `null` si no hay conexión guardada; si la hay, `{ Status, StatusReason }` con `Status` = el de la fila
  `AppointmentReminder` de esa conexión (`APPROVED`, `PENDING`, `REJECTED`…, con el nombre de Meta como en
  `template-management`) o `Missing` si no existe la fila. Se resuelve por un puerto de Scheduling con adaptador en
  Bootstrapper hacia Mensajería (forma de `IAppointmentReminderReadiness`). Sin el módulo `messaging`, `Missing`.
- `PUT settings` responde el mismo `SettingsDto` con el estado ya resuelto.

## Fuera de alcance

Cambiar cómo se envía el recordatorio, el motivo en el DTO de la cita (P-1), validar la aprobación al guardar.

## Pruebas

Integración filtrada por nombre mientras se itera; la clase de Scheduling tocada una vez por bloque; la suite de los
proyectos de Scheduling (y ArchitectureTests si se agrega un puerto) sólo antes de mergear. Nunca dos corridas de
integración a la vez en la máquina.

# Cumpleaños RRHH: el ajuste trae candidatos y conexiones (BFF)

Fecha: 2026-11-27. Tarjeta: <https://trello.com/c/pjzEGHMV> (pedida por el owner el 2026-11-27).
Amplía el spec [2026-11-26-hr-birthday-notices-design.md](2026-11-26-hr-birthday-notices-design.md) §5.1.

## Problema

La pantalla «Cumpleaños» de `/settings/hr` hace cuatro llamadas para pintarse: el ajuste, `GET /memberships`
(`advisorship.read`), `GET /messaging/connections` (`messaging.conversation.read`) y la gestión de plantillas de cada conexión
(`messaging.template.manage`). Exige permisos que nada tienen que ver con RRHH, y la lista de miembros no dice quién tiene
`hr.employee.read`, así que se puede elegir a alguien que el PUT rechaza con 422 `hr.birthday_notice.manager_without_access`.

## Contrato

Dos campos nuevos, al final de `BirthdayNoticesDto`, en la respuesta de `GET /api/v1/tenants/{tenantId}/hr/birthday-notices` y
de su `PUT`. El cuerpo del PUT, los permisos, las rutas y los códigos de error no cambian.

```jsonc
{
  // … managers, leadDays, whatsAppEnabled, whatsAppConnectionId, emailEnabled, version (sin cambios)
  "candidates": [ { "memberId": "…", "displayName": "Ana Gerente" } ],   // o null
  "connections": [ { "connectionId": "…", "name": "Ventas", "phoneNumber": "+57 300…", "templateStatus": "Approved" } ]
}
```

- **`candidates`:** las membresías **activas** cuyo rol concede `hr.employee.read`, por nombre y luego por id. Es exactamente el
  conjunto que el PUT acepta como encargado (`EnsureManagersAsync`). Viaja **`null`** cuando quien pide no tiene
  `hr.settings.manage`: sin ese permiso la pantalla es de sólo lectura y no necesita a quién elegir, así que no se le entrega la
  lista de quiénes ven RRHH. La respuesta del PUT siempre la trae (el PUT exige `hr.settings.manage`).
- **`connections`:** las conexiones de WhatsApp activas del tenant (Integrations filtra estado, visibilidad y habilitación del
  proveedor, como `HrWhatsAppConnections.IsWhatsAppConnectionAsync`), por nombre sin distinguir mayúsculas y luego por id, cada una
  con el estado de su plantilla `HrBirthdayNotice`. Viaja a cualquiera con `hr.employee.read`: la pantalla de sólo lectura también
  muestra la conexión elegida. Lista vacía si no hay conexiones.
  - `phoneNumber`: el número visible de la conexión, `null` si no tiene.
  - `templateStatus`: `Approved` | `Pending` | `Rejected` | `Missing`, igual que en el ajuste de avisos de Soporte
    (`SupportTaskNoticeConnection`): `APPROVED` → `Approved`; `PENDING` o `IN_APPEAL` → `Pending`; sin fila → `Missing`; cualquier
    otro estado de Meta → `Rejected`. Es contrato con la pantalla: no se renombra.

Un frontend viejo ignora los campos nuevos. Con ellos, el frontend deja de hacer las llamadas 2 a 4 (tarjeta `[frontend]` aparte).

## Diseño

- `IHrWhatsAppConnections` (puerto de Hr) gana `ListAsync(tenantId)`, que devuelve `HrWhatsAppConnection(ConnectionId, Name,
  PhoneNumber, TemplateStatus)`. Los valores de `TemplateStatus` son constantes de `HrBirthdayNoticeTemplateStatuses` en Hr. El
  adaptador de Bootstrapper (`HrWhatsAppConnections`) lo resuelve con `IIntegrationConnections.ListResolvedActiveAsync` y
  `IHrBirthdayNoticeTemplateReadiness.FindStatusAsync`, calco de `SupportTaskNoticeChannels`. Hr sigue sin referenciar Messaging
  ni Integrations.
- `BirthdayNoticesComposer.ComposeAsync` recibe si incluye candidatos. Calcula `ListWithPermissionAsync(hr.employee.read)` una
  sola vez para los candidatos y para `hasHrAccess`. Los candidatos y las conexiones viajan también cuando no hay ajuste guardado:
  es justo cuando la pantalla los necesita para configurar la primera vez.

## Fuera de alcance

El PUT, los códigos de error, la campanita y el envío del aviso.

## Decisiones pendientes del owner

Ninguna. `candidates: null` sin `hr.settings.manage` lo dejó la tarjeta a quien implementa.

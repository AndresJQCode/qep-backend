# Soporte (PQRSF) — PIN nuevo para un ticket anónimo desde la consola · diseño

> **Estado:** aprobado por el owner 2026-11-16 (decisiones P-1…P-6 abajo).
> **Construye sobre:** fase 4a en `develop` (alta manual anónima con `IssuedPin`, plan
> `2026-11-15-support-phase-4a-management-and-deadlines-backend.md`, G3) y fase 3 (consulta pública por número + PIN,
> bloqueo por intentos, reenvío público del PIN).
> **Consola:** spec `qep-frontend/docs/superpowers/specs/2026-11-16-soporte-fase-4a-consola-design.md`.

## 1. Problema

Un ticket anónimo (del portal o radicado a mano) no tiene correo ni hilo: su PIN se muestra una sola vez y el reenvío
público (`POST /public/support/{slug}/tickets/resend-pin`) nunca envía nada para él (`ResendPublicPin.cs:61`,
`HasDestination` es `false`). Si la persona lo pierde, o queda bloqueada por intentos fallidos, no vuelve a leer su
caso ni la respuesta, que para un anónimo sólo se publica en la página de consulta. Hoy no hay salida.

## 2. Decisiones del owner (2026-11-16)

| # | Decisión |
| --- | --- |
| P-1 | `POST /api/v1/tenants/{tenantId}/support/tickets/{ticketId}/pin`, permiso `support.ticket.manage`, `If-Match`. **Sólo anónimos**: un ticket con solicitante o cliente → `422 support.ticket.pin_not_anonymous` (ellos usan el reenvío público, que va a su correo u hilo). Responde 200 con el detalle y `IssuedPin` una sola vez. |
| P-2 | Generar un PIN nuevo desde la consola **desbloquea**: contador de fallos en cero y sin `LockedUntil`. El asesor es la verificación humana y los intentos viejos se hicieron contra un PIN que ya no existe. (El reenvío público sigue sin desbloquear.) |
| P-3 | Cualquier estado, cerrado incluido: es acceso para leer, no edición del caso. |
| P-4 | Traza: evento `PinReissued` (actor = quien lo generó, sin detalles). Auditoría `support.ticket.pin_reissued`. Ninguno lleva el PIN. |
| P-5 | En la consola no se pide el PIN anterior: un botón genera el nuevo. |
| P-6 | El detalle suma `ConsultUrl` (`string?`, último campo): viene **sólo** junto con `IssuedPin` (201 del alta anónima y 200 de esta ruta), armado por `SupportOpeningSender.ConsultUrl`. En toda otra lectura es `null`. |

## 3. Contrato

```
POST /api/v1/tenants/{tenantId:guid}/support/tickets/{ticketId:guid}/pin
If-Match: "<version del ticket>"
(sin cuerpo)

200 SupportTicketDetailDto  { …, issuedPin: "123456", consultUrl: "https://…/{slug}/consultar?radicado=SOP-000123" }
```

| Caso | Respuesta |
| --- | --- |
| Sin `support.ticket.manage` | 403 |
| Ticket inexistente o de otro tenant bajo mi ruta | 404 `support.ticket.not_found` |
| Sin `If-Match` | 428 `precondition.if_match_required` |
| Versión vieja | 412 `concurrency.conflict` |
| Ticket no anónimo | 422 `support.ticket.pin_not_anonymous` |
| Ticket sin fila de acceso (no debería pasar) | 422 `support.ticket.pin_not_anonymous` |

- Sube la `version` del ticket (el evento es parte del agregado), así un segundo clic con la versión vieja da 412 y no
  genera dos PIN seguidos sin que la pantalla se entere.
- Después del commit: `ISupportRealtime.TicketChangedAsync`.
- `SupportTicketEventDto` no suma campos: `kind: "PinReissued"` y el `actor`.

## 4. Lo que no cambia

- Reenvío público del PIN, su enfriamiento y su regla de no desbloquear.
- `ConsultAccess` del detalle: `lastSentAt`, `channel` y `failureCode` no se tocan (esta ruta no envía nada).
- El PIN nunca va a logs, auditoría, traza ni a otra lectura.

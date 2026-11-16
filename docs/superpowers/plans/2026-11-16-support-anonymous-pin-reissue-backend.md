# Soporte (PQRSF) — PIN nuevo para un ticket anónimo, backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development o superpowers:executing-plans.
> Un solo bloque, un implementador, una revisión. Steps use checkbox (`- [ ]`).

**Goal:** que el asesor genere desde la consola un PIN nuevo para un ticket anónimo, lo vea una vez junto con el enlace
de la página de consulta, y que eso desbloquee la consulta y quede en la traza.

**Architecture:** todo en el módulo Support. Sin migración de esquema: `PinReissued` se persiste por nombre (valor nuevo
al final del enum) y el acceso ya tiene `pin_hash`, `pin_issued_at`, `failed_attempts` y `locked_until`.

**Spec:** [`docs/superpowers/specs/2026-11-16-support-anonymous-pin-reissue-design.md`](../specs/2026-11-16-support-anonymous-pin-reissue-design.md).

## Antes de empezar

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-anonymous-pin`,
  rama `feature/support-anonymous-pin`. Rutas absolutas siempre. `git branch --show-current` antes de cada commit.
- Conventional commits en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni atribución de IA**.
- **Pruebas (owner):** se escriben y **no se corren**. RED = `dotnet build` del proyecto de pruebas fallando por
  compilación; GREEN = `dotnet build` de la solución sin errores ni avisos. Salidas literales al reporte, más la lista
  exacta de `--filter` para que el owner corra después.
- Si `Api.exe` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- `docs/estado.md` no se toca en esta rama.
- Otra sesión planea `feature/support-phase-4b` (sólo docs al 2026-11-16); si agrega valores al enum de eventos, el
  merge choca al final de `SupportTicketEventKind`: se resuelve dejando los dos.

## Global Constraints

- Mismas de la fase 4a: tenant revalidado en el handler (`SupportAuthorization.EnsureAsync`), auditoría en la misma
  transacción, realtime después del commit, `SupportDomainException` → 422, ningún status a mano.
- **Ningún log, auditoría ni evento lleva el PIN.**
- `SupportLayerTests` no se afloja.

## Review Focus

1. El PIN en claro sólo existe en la respuesta de esta ruta y del alta anónima; `ConsultUrl` igual. Prueba: un `GET`
   del detalle después trae los dos en `null`.
2. Desbloqueo: tras 5 fallos (bloqueado), generar PIN nuevo y consultar con él da 200 de inmediato.
3. El PIN viejo deja de servir; el nuevo sirve.
4. No anónimo → 422 `support.ticket.pin_not_anonymous`, sin evento ni auditoría.

## Bloque único

### Task 1: dominio

**Archivos:** `Modules.Support.Domain/SupportTicketAccess.cs`, `SupportTicketEventKind.cs`, `SupportTicket.cs`,
`SupportErrorCodes.cs` (+ pruebas unitarias del dominio).

- `SupportTicketAccess.ReissueByAgent(string pinHash, DateTimeOffset now)`: fija hash y `PinIssuedAt`, pone
  `FailedAttempts = 0` y `LockedUntil = null`. Comentario: a diferencia de `Reissue`, ésta desbloquea (P-2).
- `SupportTicketEventKind.PinReissued` **al final**.
- `SupportTicket.RecordPinReissued(SupportMemberId actor, DateTimeOffset now)`: exige `IsAnonymous` (si no,
  `SupportDomainException(SupportErrorCodes.PinNotAnonymous)`), agrega el evento y sube la versión como cualquier
  otra escritura. Sin guarda de estado (P-3).
- `SupportErrorCodes.PinNotAnonymous = "support.ticket.pin_not_anonymous"`.

- [ ] RED (unitarias que no compilan) → GREEN (`dotnet build`) → commit `feat(support): let an agent reissue an anonymous ticket PIN in the domain`.

### Task 2: caso de uso, endpoint y contrato

**Archivos:** `Modules.Support.Application/Tickets/ReissueTicketPin.cs` (nuevo), `SupportDtos.cs`,
`SupportTicketDtoComposer.cs`, `Tickets/CreateTicket.cs`, `SupportAuditActions.cs`, `Modules.Support.Api/SupportEndpoints.cs`,
registro DI, pruebas de integración (`SupportTicketsApiTests` o `SupportPinReissueApiTests` nueva).

- `ReissueTicketPinCommand(tenantId, ticketId, expectedVersion)` con handler:
  1. `EnsureAsync(Manage)`; 2. cargar ticket (404/412 como `SupportTicketActions`); 3. cargar su `SupportTicketAccess`
  (sin fila → `PinNotAnonymous`); 4. `ticket.RecordPinReissued(actor, now)`; 5. `pin = SupportPin.Generate()`,
  `access.ReissueByAgent(pinHasher.Hash(pin), now)`; 6. auditoría `support.ticket.pin_reissued` con campos
  `["pinIssuedAt"]`; 7. guardar y commit; 8. `realtime.TicketChangedAsync(…, CancellationToken.None)`; 9. detalle
  `with { IssuedPin = pin, ConsultUrl = SupportOpeningSender.ConsultUrl(publicOptions, slug, ticket.DisplayNumber) }`.
  El slug se obtiene igual que en `CreateTicket`.
- `SupportTicketDetailDto` suma `string? ConsultUrl = null` como **último** campo. El composer lo deja `null`.
- `CreateTicket`: el 201 anónimo suma `ConsultUrl` junto a `IssuedPin` (P-6).
- Endpoint: `MapTicketPost(group, "pin", ReissuePinAsync)` con `RequireVersion(httpContext, "ticket")`, documentado
  200/403/404/412/422/428.
- Pruebas de integración: los cuatro focos de revisión, más 428 sin `If-Match`, 412 versión vieja, 403 lector, evento
  `PinReissued` en la traza con actor, `ConsultUrl` en el 201 anónimo y `null` en el no anónimo.

- [ ] RED → GREEN → commit `feat(support): reissue an anonymous ticket PIN from the console`.

### Cierre

- [ ] `dotnet build` de la solución sin avisos. Lista de `--filter` para el owner. Una revisión del bloque.

## Contrato para las pantallas (BFF)

| Campo / ruta | Uso en la consola |
| --- | --- |
| `POST …/tickets/{id}/pin` | Botón «Generar PIN nuevo» del panel de consulta, sólo con `isAnonymous` y `manage`. |
| `issuedPin`, `consultUrl` | Diálogo de entrega (el mismo del alta anónima). |
| evento `PinReissued` | «Generó un PIN nuevo». |
| `support.ticket.pin_not_anonymous` | «Este ticket tiene a quién enviarle el PIN: se reenvía desde el portal.» |

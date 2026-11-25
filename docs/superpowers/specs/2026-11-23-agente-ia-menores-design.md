# Agente IA: dos menores (cambio de hora simulado y plantilla de origen) (backend)

- **Fecha:** 2026-11-23.
- **Módulos:** `Scheduling` (una comprobación nueva en `IChannelScheduling`), `Ai` (puerto, herramienta simulada, columna, DTO),
  `Bootstrapper` (adaptador `AiScheduling`). Una migración aditiva en `ai.agents`. Frontend en `qep-frontend`
  (`docs/superpowers/specs/2026-11-23-agente-ia-menores-frontend-design.md`).
- **Estado:** aprobado por el owner («trabaja los menores»). **Sin código.** Plan:
  [`plans/2026-11-23-agente-ia-menores-backend.md`](../plans/2026-11-23-agente-ia-menores-backend.md).
- **Se apoya en:** [`2026-11-23-agente-ia-agenda-design.md`](2026-11-23-agente-ia-agenda-design.md) (§7.7 herramientas simuladas, D-7,
  D-15) y [`2026-11-23-plantillas-de-agente-ia-design.md`](2026-11-23-plantillas-de-agente-ia-design.md) (§4, P-1).
- **Verificado contra el código el 2026-11-23** en `feature/agent-minors` (desde `develop` `24be6ebd`). Donde este documento y el
  código discrepen, gana el código.

## M1. El cambio de hora simulado cuenta el cupo de la propia cita

### Hoy

`SimulatedRescheduleAppointmentTool` (`Modules.Ai.Application/AgentSchedulingTools.cs`), con un id real, lee la cita y comprueba el
inicio nuevo con `IAiScheduling.PreviewBookingAsync`, que en el adaptador (`src/Bootstrapper/AiScheduling.cs`) llama
`IChannelScheduling.CheckStartAsync`. Esa comprobación es la de **reservar**: no conoce la cita, así que su propia sesión ocupa el
rango. El cambio de hora real (`AppointmentRescheduling.RescheduleAsync`) hace cuatro cosas que la vista previa no hace:

1. Candidatos sólo de la **misma sede** de la cita (`sameLocation`).
2. Quita de las sesiones la sesión ad hoc que sólo tiene el cupo de esta cita
   (`session.Id == oldSession.Id && session.BookedCount == 1 && !session.IsPublished`): se libera antes de tomar la nueva.
3. Prefiere el recurso pedido o, si no, **el de la cita** (`preferred: resourceId ?? appointment.ResourceId`).
4. Aplica el plazo del cliente y `bookableByChannel` (`EnsureClientMayMove`, `byClient: true`); y en el canal, la misma hora y el
   mismo recurso responden `AlreadyDone` sin tocar nada (`ChannelScheduling.RescheduleAsync`).

Resultado: en el playground, mover una cita a un inicio que se solapa con su propio rango (un servicio con margen, o un inicio
siguiente más corto que la duración) responde `slot_taken`, y en producción el mismo cambio sale bien. El modelo aprende a no
ofrecerlo, o le pasa la conversación a una persona.

### Decisión

La comprobación vive en **Scheduling**, al lado del núcleo real, no en Ai ni en el adaptador:

- `AppointmentRescheduling` gana `CheckAsync`, sin candado ni escritura, con los mismos pasos que `RescheduleAsync` antes del
  candado y la misma exclusión de la sesión que se libera. Los dos comparten dos métodos privados extraídos del cuerpo actual
  (sin cambiar su orden ni su lógica): así la vista previa no puede divergir del real sin que lo vea quien toque uno.
- `IChannelScheduling.CheckRescheduleAsync(tenantId, customerId, appointmentId, localStartsAt, resourceId)` carga la cita **del
  cliente** (`LoadOwnAsync`: ajena o inexistente es el mismo 404 `appointment_not_found`), responde `AlreadyDone` con la misma regla
  que `RescheduleAsync` y, si no, devuelve lo que dejaría el cambio. El estado es el **de la cita** (cambiar la hora no cambia el
  estado), no el de `confirmationMode`.
- `IAiScheduling.PreviewRescheduleAsync(caller, appointmentId, localStartsAt, resourceId)`: el adaptador resuelve el cliente como
  `RescheduleAsync` (`ReadingCustomerAsync`; sin cliente, `appointment_not_found`) y traduce con `GuardAsync`.
- La herramienta simulada sigue leyendo la cita (para `IsOpen` y `CanRescheduleByChat`, que deciden el traspaso antes de comprobar
  nada) y cambia `PreviewBookingAsync` por `PreviewRescheduleAsync`. `alreadyDone` sale de la comprobación en vez de ir fijo en
  `false`. `PreviewBookingAsync` sigue siendo la de la reserva simulada.

**Descartado:** un `excludeAppointmentId` opcional en `CheckStartAsync`. Haría que una comprobación sin cliente acepte un id de cita
(habría que agregarle el cliente para no confirmar ids ajenos), y seguiría sin la sede, el recurso preferido ni el plazo: la vista
previa seguiría sin decir lo mismo que el real.

**No cambia:** `AppointmentRescheduling.RescheduleAsync` (sólo se extrae código, mismo orden, mismo candado, misma transacción),
`CheckStartAsync`, la reserva real ni la simulada, el id `PRUEBA-…` (se sigue dando por encontrado sin comprobar).

## M2. El agente guarda de qué plantilla salió

Cierra la P-1 del spec de plantillas para lo que pidió el owner: ver en el editor de qué plantilla salió un agente. Métricas y
«hay una versión nueva de la plantilla» siguen fuera.

- **Columna:** `ai.agents.source_template_id`, `varchar(64)` (`SystemAgentTemplate.IdMaxLength`), `NULL`. Sin FK (la plantilla vive
  en el ensamblado, no en una tabla), sin índice (nadie filtra por ella), **sin backfill**: los agentes ya creados desde plantilla
  quedan en `NULL`; no hay forma honesta de saber de cuál salieron.
- **Quién la escribe:** sólo `CreateAgentFromTemplateHandler`, con `template.Id`. Nace con el agente y no cambia: no hay método
  para cambiarla ni para borrarla.
- **Quién no:** crear en blanco, **duplicar** e importar dejan `NULL`. Duplicar copia el borrador, no el origen: el duplicado es
  otro agente que nadie creó desde la plantilla, y si la heredara el editor diría «Creado desde la plantilla…» de algo que el tenant
  hizo a mano a partir de otro agente. Importar no la recibe porque el archivo no la lleva.
- **Exportar no la lleva:** `AgentExportDto(Schema, Kind, Name, Config, ExportedAt)` no cambia. El archivo es portable entre
  tenants y entornos, y el origen es un dato del agente, no de su configuración.
- **Contrato:** `AgentEditorDto` suma al final `string? SourceTemplateId` y `string? SourceTemplateName` (en JSON
  `sourceTemplateId`, `sourceTemplateName`). `AgentEditorBuilder` resuelve el nombre **al leer** con
  `ISystemAgentTemplateCatalog.Find`: `null` si el agente no salió de una plantilla o si esa plantilla ya no existe (se borró o se
  renombró su id). El nombre es el de hoy de la plantilla, no el del día en que se creó el agente.
- **Sólo el editor, no la lista:** `AgentSummaryDto` no cambia. La lista (`agents-list-page.tsx`) muestra estado, modelo,
  conexiones, consumo y chatbots que lo usan: datos para operar; de dónde salió es contexto de edición. Backend for Frontend: el
  campo viaja donde una pantalla lo pinta.
- **Auditoría:** sin acción nueva. `AgentCreated` sigue siendo la entrada; la columna no se edita.

## Despliegue

- **Migración aditiva** (expand): una columna nullable. Un pod viejo no la conoce, no la lee y no la escribe; sus agentes nuevos
  quedan en `NULL`, que es lo mismo que «sin plantilla». No hace falta corte.
- **Orden:** backend primero. Un frontend viejo ignora los dos campos; uno nuevo contra un backend viejo los recibe ausentes y no
  muestra nada.
- **Rollback:** revertir el código. La columna puede quedarse (nadie la lee); quitarla es otra migración (contract), aparte.
- **`docs/estado.md`** se actualiza en `develop` al mergear.

## Pruebas (presupuesto del workspace)

- **RED por compilación** sin contenedores: miembros nuevos del puerto, del DTO y de `Agent.Create`.
- **Unitarias:** `Modules.Ai.UnitTests` (herramienta simulada, `AgentEditorBuilder`) y `Bootstrapper.UnitTests`
  (`AiSchedulingTests`, el adaptador).
- **Integración, filtrada por nombre mientras se itera:** `SchedulingChannelServiceTests` (la comprobación nueva y la paridad con el
  real), `AgentTemplatesApiTests` (columna, contrato, duplicar, importar, exportar). Al final del bloque: esas dos clases enteras.
- **ArchitectureTests:** no corren; ningún registro cambia (`AgentEditorBuilder` ya es scoped y `ISystemAgentTemplateCatalog` ya es
  singleton; `AiScheduling` y `ChannelScheduling` siguen registrados igual).
- **Antes de mergear a `develop`:** `Modules.Ai.UnitTests`, `Bootstrapper.UnitTests`, y las dos clases de integración de arriba.

## Preguntas abiertas

- **P-1.** ¿Mostrar el origen también cuando la plantilla ya no existe (con el id crudo)? Este spec dice que no: un id como
  `asistente-de-citas` no le dice nada al tenant.

# Eliminar un contrato de servicio — diseño

Fecha: 2026-11-19. Módulo: `ServiceContracts` (esquema `billing`). Frontend: «Contratos de servicio».

## Decisión del owner

Un contrato de servicio se puede **eliminar siempre**, en cualquier estado (`Active`, `Paused`, `Cancelled`) y aunque tenga
cobros pagados. Es un borrado duro: el historial se pierde. La pantalla lo advierte y pide escribir el nombre del contrato antes
de enviar; el backend no valida ese nombre.

## Contrato HTTP

`DELETE /api/v1/tenants/{tenantId:guid}/billing/services/{serviceId:guid}`

- Política `billing.service.manage` (`ServiceContractsPermissions.Manage`); el handler revalida tenant y permiso.
- Exige `If-Match` con la versión cargada, igual que `/pause`, `/resume` y `/cancel`: sin cabecera, `428`
  (`precondition.if_match_required`); versión vieja, `412` (`concurrency.conflict`, por `ServiceVersion.EnsureCurrent`).
- Servicio inexistente o de otro tenant: `404` `billing.service.not_found`.
- Éxito: `204 No Content`, sin cuerpo.

## Qué se borra

Todo en una sola transacción (`IServiceContractsUnitOfWork.SaveChangesAsync`):

| Tabla | Filas |
| --- | --- |
| `billing.services` | el servicio |
| `billing.charges` | todos sus cobros, de cualquier estado |
| `billing.reminder_steps` | los pasos de aviso de esos cobros |
| `billing.service_price_changes` | su historial de precios |

El esquema `billing` no declara FKs entre `services`, `charges` y `service_price_changes`; la única FK real es
`reminder_steps.message_id → customer_messages.id`. Por eso no hace falta migración ni cascada: el handler marca las filas en el
change tracker y salen juntas. `customer_messages` **no** se toca: es un mensaje por cliente y día (`tenant_id, customer_id,
run_date`), no por cobro, y puede cubrir cobros de otros contratos.

Referencias externas por id de servicio o cobro: ninguna. Los únicos módulos que leen `billing.*` son los de clientes
(`CustomerDeletionReferences`, `CustomerMergeReferences`), y lo hacen por `customer_id`: borrar un contrato sólo quita un motivo
de bloqueo al borrar el cliente.

## Auditoría

`billing.service.deleted` (`ServiceContractsAuditActions.ServiceDeleted`), recurso `service`, id del servicio, publicada en la
misma transacción que el borrado.

## Concurrencia

Servicio y cobros llevan token de concurrencia: si el worker cambia uno entre la lectura y el guardado, el borrado falla con
`concurrency.conflict` y se reintenta. Un cobro que el worker inserte entre la lectura y el guardado quedaría sin servicio; es una
carrera acotada a un servicio `Active` justo en su tick diario y se deja como riesgo conocido.

## Pruebas

- Unitaria: `DeleteServiceValidatorTests`.
- Integración (`ServiceContractApiTests`, prefijo `Delete`): 204 con filas desaparecidas y las de otro contrato intactas,
  cancelado, 428/412, 404 de tenant ajeno e inexistente, 403 sin `manage`, y auditoría.

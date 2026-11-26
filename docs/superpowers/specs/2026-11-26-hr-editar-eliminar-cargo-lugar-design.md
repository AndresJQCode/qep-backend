# Editar y eliminar registros de «Cargo y lugar de trabajo» (Hr)

Tarjeta: <https://trello.com/c/W4Ijr9DW>. Desbloquea <https://trello.com/c/0dhhZPZV> (frontend).
Verificado sobre `develop` en `4dc94b00c`. Estado: **por aprobar**.

## Para qué

Un registro mal cargado hoy no se puede corregir: sólo se agrega otro posterior (`AddAssignment`) o se cambia su PDF.

## Decisiones

Del owner (2026-11-26):

- Un registro que ya rigió en una nómina `Approved` o `Closed` **no se edita ni se elimina**. Primero se reabre el período.

Propuestas que se aprueban con este documento:

1. La fecha de vigencia se puede editar, salvo la del primer registro (es el inicio del contrato).
2. El primer registro se edita pero no se elimina.
3. Los demás se editan y eliminan sólo si el historial resultante sigue siendo válido.
4. En contratos terminados no se permite, igual que «Editar» salario.
5. Eliminar registros de salario queda fuera.
6. Editar un registro conservando un cargo, área o centro que se archivó después se permite; elegir uno archivado
   distinto, no.

## Contrato HTTP

Permiso `hr.contract.manage`, `If-Match` con `employee.version`, respuesta `EmployeeDetailDto`.

| Ruta (bajo `.../contracts/{contractId}`) | Cuerpo |
| --- | --- |
| `PUT /assignments/{recordId}` | `{ effectiveDate, positionId, areaId, workplaceId, confirmRewrite }` |
| `DELETE /assignments/{recordId}` | sin cuerpo |

`areaId` es opcional. El PDF se sigue cambiando con `PUT /assignments/{recordId}/document`.

`AssignmentRecordDto` suma `canEdit` y `canDelete`:

- `canEdit`: el contrato está activo y ninguna nómina `Approved` o `Closed` usó los días que rige el registro.
- `canDelete`: `canEdit`, no es el primero y el historial sin él sigue siendo válido.

`canEdit` en `true` no garantiza cualquier cambio: mover la fecha puede alcanzar días de otra nómina y se rechaza.
Los dos campos no miran permisos, como el resto de la ficha.

### Errores

Validación: 422 `validation.failed` con `effectiveDate`, `positionId`, `areaId`, `workplaceId`, como el alta
(incluye fecha posterior a la terminación y centro de otra empresa).

| Código | Estado | Cuándo | Nuevo |
| --- | --- | --- | --- |
| `hr.assignment.not_found` | 404 | el registro no es del contrato | no |
| `hr.contract.not_active` | 422 | contrato terminado | no |
| `hr.assignment.rewrite_unconfirmed` | 422 | `PUT` sin `confirmRewrite: true` | sí |
| `hr.assignment.first_record_date_fixed` | 422 | cambiar la fecha del primero | sí |
| `hr.assignment.first_record_not_deletable` | 422 | eliminar el primero | sí |
| `hr.assignment.effective_date_out_of_order` | 422 | la fecha nueva no queda entre el registro anterior y el siguiente | sí |
| `hr.assignment.unchanged` | 422 | dos registros vecinos quedarían iguales | no |
| `hr.assignment.site_change_not_first_of_month` | 422 | un cambio de lugar quedaría en un día distinto del 1 | no |
| `hr.assignment.used_by_payroll` | 422 | alcanza una nómina `Approved` o `Closed` | sí |

Concurrencia: la misma respuesta que las demás escrituras con `If-Match` de Hr.

## Reglas

**Vecinos.** Con los registros ordenados por fecha, se revisan los pares que el cambio toca (anterior–editado y
editado–siguiente al editar; anterior–siguiente al eliminar). En cada par el posterior cambia algo (sede, centro,
cargo o área) y, si cambia el lugar, su fecha es día 1. Las reglas viven en `EmploymentContract`
(`RewriteAssignment`, `RemoveAssignment`) y las reutiliza `canDelete`.

**Nómina.** Un registro rige desde su fecha hasta el día anterior al siguiente (o el fin del contrato; sin fin, abierto).
Al editar, el rango alcanzado va desde la menor entre la fecha vieja y la nueva hasta el fin del rango que resulte
mayor. Se rechaza si se cruza con un período `Approved` o `Closed` de la empresa del contrato **que tenga resultado
guardado de ese empleado**: un excluido no bloquea. Un período `Liquidated` no bloquea; la huella ya marca que la fuente
cambió y no deja aprobarlo sin reliquidar. `Reopened` y `Draft` no bloquean.

**Rastro.** Dos acciones nuevas en el historial de actividad (`AssignmentRewritten`, `AssignmentDeleted`, categoría
`Assignment`), con etiqueta, cambios antes y después, y auditoría `hr.contract.assignment_rewritten` y
`hr.contract.assignment_deleted`. La restricción `CK_employee_activity_action` se amplía con una migración.

**PDF (decisión del owner, 2026-11-26: aviso por outbox con reintento).** Al eliminar un registro con PDF, Hr escribe en
`platform.outbox_messages`, en la misma transacción, el evento `hr.documents.released.v1` (`{ tenantId, fileIds }`).
Storage lo consume con su inbox (`storage.hr-document-release`), como la liberación de imágenes de producto: pregunta a
las sondas si algún registro de Hr todavía usa el archivo; si ninguno, borra el objeto de R2, marca el recurso `Purged`
y audita `storage.file.purged`. Si algo falla, no marca el inbox y reintenta en el tick siguiente. Sin sondas no borra
nada. El consumidor sólo purga archivos `HrDocument`.

El fallo queda en el log (`Error`, con el id del mensaje). La cola `outbox` no lo cuenta (mira `processed_at`, que marca
el despachador de plataforma), así que `QueueMetricsSource` suma la cola `hr_document_release`: avisos del evento que
todavía no están en el inbox de Storage, con la edad del más viejo y `retrying` en cero (no se cuentan intentos). Sin
tabla nueva. Sale en Grafana y en la vista de plataforma (<https://trello.com/c/CN1QTtcc>).

## Enfoques considerados (nómina)

- **A. Bloquear (elegido).** El historial siempre cuadra con lo pagado. Cuesta un puerto de lectura nuevo.
- **B. Permitir.** Más simple, pero el historial puede contradecir la nómina cerrada y reabrirla cambia el cálculo.

## Enfoques considerados (borrado del PDF)

- **A. Aviso por outbox con reintento (elegido).** El borrado no se pierde. Cuesta un consumidor y un worker nuevos.
- **B. Barrido diario de PDF sin referencia.** Cubre además los reemplazados, pero una referencia omitida borra un
  documento laboral válido.
- **C. Borrar tras la transacción y sólo registrar el fallo.** Deja huérfanos.

## Arquitectura (requiere tu autorización)

Hr no referencia a Payroll. Se agrega un puerto en `Modules.Hr.Application` (períodos congelados de un empleado:
empresa, inicio y fin), otro de lectura en `Modules.Payroll.Application` (EF sobre `periods` y `employee_results`) y el
adaptador en Bootstrapper, como `LegalParameterUsage`. No agrega referencias entre módulos. La ficha del empleado hace
una consulta más.

Para el PDF: Hr publica en el outbox de plataforma, Storage suma el consumidor y su worker, y Hr suma su sonda
(`IFileReferenceProbe`) en Infrastructure, como las de Catalog, Quotations y Support.

## Fuera de alcance

- `AddAssignment` y el salario no miran la nómina: sus reglas no cambian.
- Reemplazar o quitar un PDF sigue dejando el archivo anterior en R2.
- Una aprobación de nómina simultánea con la edición puede ganar por milisegundos; no se agrega candado.

## Pruebas

Unitarias de dominio (vecinos, primer registro, contrato terminado, `canDelete`) y de aplicación (bloqueo por nómina,
aviso del PDF, actividad). Integración: las dos rutas, los códigos, el puerto de Payroll contra el esquema migrado, la
migración y el consumidor de Storage (borra, conserva si hay referencia y reintenta si R2 falla).

## Documentos a actualizar

`2026-11-20-rrhh-fase-1-design.md` («nada se sobreescribe salvo Editar»), `README.md` (rutas) y `docs/estado.md` (en
`develop`, al mergear).

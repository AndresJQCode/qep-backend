# Editar y eliminar registros de «Cargo y lugar de trabajo» (Hr)

Tarjeta: <https://trello.com/c/W4Ijr9DW>. Desbloquea <https://trello.com/c/0dhhZPZV> (frontend).
Verificado sobre `develop` en `4dc94b00c` y, tras la revisión independiente, en `5e370555b`. Estado: **por aprobar**.

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
- `canDelete`: `canEdit`, no es el primero y el historial sin él sigue siendo válido. Es una consulta pura del agregado
  (`EmploymentContract.CanRemoveAssignment`, nueva) con las mismas reglas de vecinos que `RemoveAssignment`: no se
  calcula mutando y capturando la excepción.

`canEdit` en `true` no garantiza cualquier cambio: mover la fecha puede alcanzar días de otra nómina y se rechaza.
Los dos campos no miran permisos, como el resto de la ficha.

### Errores

Validación: 422 `validation.failed` con `effectiveDate`, `positionId`, `areaId`, `workplaceId`, como el alta
(incluye la fecha posterior a la terminación).

| Código | Estado | Cuándo | Nuevo |
| --- | --- | --- | --- |
| `hr.assignment.not_found` | 404 | el registro no es del contrato | no |
| `hr.contract.not_active` | 422 | contrato terminado | no |
| `hr.assignment.workplace_other_company` | 422 | el centro es de otra empresa; trae el mapa `errors` con `workplaceId` (`ContractRules.EnsureWorkplaceOfCompany`, como el alta) | no |
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
(`RewriteAssignment`, `RemoveAssignment`) y `canDelete` las consulta sin mutar.

**Nómina.** Un registro rige desde su fecha hasta el día anterior al siguiente (o el fin del contrato; sin fin, abierto).
El rango alcanzado es:

- al editar, desde la menor entre la fecha vieja y la nueva hasta el fin del rango que resulte mayor;
- al eliminar, el rango que regía el registro eliminado (desde su fecha hasta el día anterior al siguiente, o el fin del
  contrato), que pasa a regir el anterior.

Se rechaza si se cruza con un período `Approved` o `Closed` de la empresa del contrato **que tenga resultado guardado de
ese empleado**: un excluido no bloquea. `Liquidated`, `Reopened` y `Draft` no bloquean.

**`Liquidated` y la huella.** La huella (`InputFingerprint`) cubre la entrada del motor. De los registros de cargo y
lugar, el motor sólo lee el vigente el último día cubierto (`PayrollEligibility`, `AssignmentAtEnd`), y de él sólo la
clase de riesgo y la pensión de alto riesgo del centro (`WorkplaceInfo`; el nombre se quita de la huella). Así que una
edición o un borrado marcan `sourceChanged` sólo si cambian esos dos datos para el último día cubierto. Cambiar el cargo,
el área, un centro por otro de la misma clase o un registro que no rige ese día no lo marca, y el cálculo guardado sigue
siendo correcto. Lo que sí queda viejo son las etiquetas que el resultado guarda al liquidar (cargo y «centro - sede»,
`LiquidatePeriod`), que salen así al aprobar. Bloquear sólo `Approved` y `Closed` es sano para el cálculo. Para las
etiquetas queda una **decisión pendiente del owner**: (a) aceptar que un `Liquidated` se apruebe con las etiquetas de su
liquidación (reliquidar las refresca), que es lo que este diseño supone; o (b) bloquear también `Liquidated` con
resultado del empleado.

**Rastro.** Dos acciones nuevas en el historial de actividad (`AssignmentRewritten`, `AssignmentDeleted`, categoría
`Assignment`), con etiqueta, cambios antes y después, y auditoría `hr.contract.assignment_rewritten` y
`hr.contract.assignment_deleted`. La restricción `CK_employee_activity_action` se amplía con una migración.

## PDF del registro eliminado

Decisión del owner (2026-11-26): aviso por outbox con reintento y purga inmediata.

**Evento.** Al eliminar un registro con PDF, Hr escribe en `platform.outbox_messages`, en la misma transacción,
`hr.employee.documents-released.v1` (`{ tenantId, fileIds }`), con la forma `<módulo>.<agregado>.<evento>.v1` de los
demás (`catalog.product.images-released.v1`, `customers.customer.media-released.v1`). Storage lo suma a
`StorageSubscribedEvents`.

**Referencias.** Storage pregunta por un puerto propio, `IHrDocumentReferences` (nuevo, en `Modules.Storage.Application`),
calco de `IChatbotMediaReferences`: devuelve el subconjunto de `fileIds` que el tenant todavía usa en Hr. Lo implementa un
adaptador en Bootstrapper (`HrDocumentReferences`, nuevo) sobre un puerto de lectura de Hr (`IHrDocumentUsage`, nuevo, en
`Modules.Hr.Application`, con EF en `Modules.Hr.Infrastructure`), como `ChatbotMediaReferences` usa `IBotMediaReferences`:
nadie lee columnas de Hr por nombre desde fuera. Mira las cinco tablas con `document_file_id`: `employment_contracts`,
`salary_records`, `assignment_records` y `contract_renewals` (acotadas al tenant por `employees.tenant_id`) y
`medical_exams` (por su `tenant_id`).

No se usan las sondas `IFileReferenceProbe`: `ProductImageReleaseProcessor` sólo se detiene si no hay **ninguna**, así
que una sonda de Hr que faltara dejaría purgar PDF vivos sin aviso. Falla cerrado: el puerto es una dependencia
obligatoria del procesador (no una lista); sin el adaptador registrado el procesador no se resuelve, el tick falla y no
se borra nada. Hr no suma `IFileReferenceProbe`.

**Consumidor.** `HrDocumentReleaseProcessor` (nuevo, consumidor `storage.hr-document-release`) sigue a
`CustomerMediaPurgeProcessor` de Messaging: lote de mensajes del evento sin fila terminada en el inbox o con el reclamo
vencido, reclamo por mensaje con una sentencia que suma `attempts` y fija `claimed_until`, y un scope por mensaje. Storage
no tiene esa pieza: se copian `ClaimedOutboxConsumer` y `StorageInboxClaims` (nuevos; ningún módulo referencia la
infraestructura de otro). Esperas entre intentos: 5 s, 30 s y 5 min, y desde ahí 5 min; nunca se abandona un mensaje. Una
falla se registra en `Warning` con el id del mensaje y el intento, y en `Error` desde el intento 3 (`ErrorAfterAttempts`,
el primero que deja la espera de 5 min por delante). Un payload mal formado se registra y se marca sin efecto.

Por cada id:

1. Carga el recurso por id **y** tenant, con candado de fila. Si no existe, no es `HrDocument` o no está `Available`, lo
   salta (y lo registra si es de otro dueño): sólo purga archivos `HrDocument`.
2. Vuelve a preguntar a `IHrDocumentReferences` por ese archivo, bajo el candado y justo antes de borrar. Si lo usan, lo
   deja.
3. Borra el objeto de R2, lo marca `Purged` (`FileResource.PurgeReleasedHrDocument`, nuevo, exige `HrDocument` y
   `Available`) y audita `storage.file.purged` como el sistema.

El inbox se marca en el mismo `SaveChanges` que el último archivo. Primero R2 y después la base, como
`ProductImageReleaseProcessor`: borrar una clave que no existe no falla, así que un guardado fallido sólo repite el
borrado.

**Worker.** `HrDocumentReleaseWorker` (nuevo) escucha `DoorbellQueues.Outbox` con
`DoorbellQueues.SafetyNet(DoorbellQueues.Outbox)` y `DoorbellPolling`, como `ChatbotMediaReleaseWorker`; sin
`PeriodicTimer`. Entra en `MultiReplicaInventory` como `Claimed` (evidencia: el reclamo de `StorageInboxClaims`) y, si el
procesador se registra como singleton con fábrica a su interfaz, también en `InterfaceFactories`, como el de Messaging.

**Sin gracia.** La purga es inmediata (decisión del owner). Un borrado equivocado no recupera el archivo: la fila de
Storage queda `Purged` y el objeto ya no está en R2.

**Observabilidad.** Sin métrica de cola, como ningún otro consumidor de liberación: estos borrados pendientes no aparecen
en la vista de colas de la plataforma ni en Grafana. Una falla sólo se ve en el log.

## Migraciones y despliegue

| Migración | Qué | Aditiva |
| --- | --- | --- |
| Hr | ampliar `CK_employee_activity_action` con las dos acciones nuevas | sí (ver efecto abajo) |
| Hr | índices parciales `IX_<tabla>_document_file_id` (`WHERE document_file_id IS NOT NULL`) en las cinco tablas | sí |
| Storage | `storage.inbox_messages`: `processed_at` pasa a nullable, `attempts integer NOT NULL DEFAULT 1` y `claimed_until` nullable; el `Down` borra las filas sin terminar antes de volver `processed_at` obligatorio, como `AddInboxClaims` de Identity | sí |

- **Storage con pods viejos.** Los cinco consumidores de hoy (`ProductImage…`, `TemplateHeaderImage…`, `ChatbotMedia…`,
  `PaymentProofDetach…`, `PaymentProofMove…`) sólo hacen el anti-join por su propio `consumer` e insertan filas con
  `processed_at`; ninguno lee esa columna ni filas de otro consumidor, y su fila toma `attempts = 1` por el default. Una
  fila reclamada sin terminar es sólo de `storage.hr-document-release`. Nada más en `src` lee `storage.inbox_messages`.
- **Efecto de ampliar la restricción.** Un pod viejo que lea el historial de actividad con una acción nueva falla al
  materializarla (`HasConversion<string>()` sobre un enum que no la tiene): durante el rollout esa lectura puede responder
  500 en el pod viejo. Se cierra al terminar el rollout y no afecta escrituras.
- Ninguna migración exige corte. Payroll no cambia de esquema.

## Enfoques considerados (nómina)

- **A. Bloquear (elegido).** El historial siempre cuadra con lo pagado. Cuesta un puerto de lectura nuevo.
- **B. Permitir.** Más simple, pero el historial puede contradecir la nómina cerrada y reabrirla cambia el cálculo.

## Enfoques considerados (borrado del PDF)

- **A. Aviso por outbox con reintento y puerto propio (elegido).** El borrado no se pierde y falla cerrado. Cuesta dos
  puertos y su adaptador, un consumidor con reclamo, un worker y una migración en Storage.
- **B. Sondas `IFileReferenceProbe`.** Menos piezas, pero una sonda faltante purga PDF vivos.
- **C. Barrido diario de PDF sin referencia.** Cubre además los reemplazados, pero una referencia omitida borra un
  documento laboral válido.
- **D. Borrar tras la transacción y sólo registrar el fallo.** Deja huérfanos.

## Arquitectura (requiere tu autorización)

Cada cambio, uno por uno:

1. **Nómina.** Un puerto en `Modules.Hr.Application` (períodos congelados de un empleado: empresa, inicio y fin), otro de
   lectura en `Modules.Payroll.Application` (EF sobre `periods` y `employee_results`) y el adaptador en Bootstrapper,
   como `LegalParameterUsage`. La ficha del empleado hace una consulta más.
2. **Referencias del PDF.** `IHrDocumentReferences` en `Modules.Storage.Application`, `IHrDocumentUsage` en
   `Modules.Hr.Application` con su implementación en `Modules.Hr.Infrastructure`, y `HrDocumentReferences` en
   Bootstrapper.
3. **Evento entre módulos.** Hr publica `hr.employee.documents-released.v1` en el outbox de plataforma y Storage lo
   consume.
4. **Storage.** `ClaimedOutboxConsumer`, `StorageInboxClaims`, `HrDocumentReleaseProcessor`, `HrDocumentReleaseWorker`,
   `FileResource.PurgeReleasedHrDocument` y la migración del inbox.
5. **`tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`** cambia (entrada del worker), y
   `.\scripts\test-feature.ps1` lo va a avisar como «Requiere autorización del owner».
6. **Contrato HTTP.** Las dos rutas y los dos campos de `AssignmentRecordDto`, que se aprueban con este documento.

No agrega referencias entre módulos: Bootstrapper ya referencia Hr y Storage (`HrDocumentStore`).

## Fuera de alcance

- `AddAssignment` y el salario no miran la nómina: sus reglas no cambian. En particular, «Editar» salario reescribe un
  monto que ya usó una nómina `Approved` o `Closed` sin revisarlo; se sigue en una tarjeta aparte.
- `DeleteMedicalExam` y los demás caminos que sueltan un PDF (reemplazarlo o quitarlo) siguen como hoy: el archivo queda
  en R2. Se sigue en <https://trello.com/c/HguYDih9>, que puede reusar este evento y `IHrDocumentReferences`.
- Una aprobación de nómina simultánea con la edición puede ganar por milisegundos; no se agrega candado.

## Pruebas

- **Unitarias de dominio:** vecinos, primer registro, contrato terminado y `canDelete` sin mutar.
- **Unitarias de aplicación:** rango alcanzado al editar y al eliminar, bloqueo por nómina, aviso del PDF y actividad.
- **Unitarias de Storage (procesador con dobles):** purga; conserva si la referencia aparece en la segunda pregunta;
  salta otro dueño, otro tenant y lo que no está `Available`; no marca si R2 o la pregunta fallan; payload mal formado;
  nivel del log según el intento.
- **Integración:** las dos rutas y sus códigos (incluido `hr.assignment.workplace_other_company` con su `errors`), el
  puerto de Payroll contra el esquema migrado, las tres migraciones y el consumidor contra Storage real (borra, conserva
  con referencia, reintenta tras una falla de R2 con `attempts` y `claimed_until`, y dos réplicas no purgan dos veces).
- **Composición:** resuelve `IHrDocumentReferences` del host real, comprueba que es `HrDocumentReferences` y la corre
  contra el esquema migrado con un PDF en cada una de las cinco tablas. `MultiReplicaInventoryTests` cubre el worker.

## Documentos a actualizar

- `2026-11-20-rrhh-fase-1-design.md` («nada se sobreescribe salvo Editar»).
- `README.md`: las rutas, el evento y el consumidor de Storage.
- Regla nueva `.claude/rules/rrhh.md`, con su fila en la tabla de `CLAUDE.md`: una tabla de Hr que guarde un
  `document_file_id` entra en `IHrDocumentUsage` y en la prueba de composición, o un PDF que todavía usa se purga al
  soltarlo desde otro registro.
- `docs/estado.md`, en `develop`, al mergear.

`sql-entre-modulos` no cambia: el adaptador no lee columnas de otro módulo por nombre.

# Exámenes médicos ocupacionales en RRHH — backend

Fecha: 2026-11-23. Estado: diseño, pendiente de aprobación del owner.
Contraparte: `qep-frontend/docs/superpowers/specs/2026-11-23-examenes-medicos-ocupacionales-design.md`.

## 1. Intención

Hoy el tenant controla los exámenes médicos laborales (ingreso, periódicos, egreso) en un Excel que alguien
mira a mano. Se quiere que QEP lleve ese control: registrar cada examen del empleado, saber a quién le toca el
periódico o el egreso, y **que el sistema avise** en la campana, sin que nadie tenga que acordarse de mirar.

Marco legal: Resolución 1843 de 2025 del Ministerio del Trabajo (deroga la 2346 de 2007 y la 1918 de 2009).

- Tipos de evaluación: preingreso, periódica (programada o por cambio de ocupación), egreso, post-incapacidad,
  retorno laboral y seguimiento.
- La periodicidad del periódico la fija el SG-SST de la empresa **según el riesgo del cargo**. Su tope es de 3 años,
  salvo justificación técnica. "Anual" es una práctica, no una obligación.
- **Egreso:** el empleador informa al trabajador, y el examen se hace dentro de los **5 días hábiles** siguientes a la
  terminación.
- Al empleador sólo llega el **concepto**, con restricciones y recomendaciones. Nunca la historia clínica. La norma
  ya no usa "no apto".

## 2. Objetivos, no-objetivos, anti-objetivos

**Objetivos**

- Registro de exámenes por empleado, con certificado PDF opcional.
- Periodicidad del examen periódico **por cargo**.
- Egreso pendiente que nace al terminar el contrato, con vencimiento a 5 días hábiles.
- Vista de control con el estado por empleado y los conteos calculados en el backend.
- Recordatorio en la campana: un resumen diario y un aviso inmediato de egreso.
- Importación del historial desde Excel.

**No-objetivos**

- Historia clínica, diagnósticos o resultados de laboratorio.
- Agenda con IPS u órdenes de servicio.
- Profesiograma.
- Exámenes de contratistas que no son empleados.
- Avisos por correo o WhatsApp.
- Vencimiento de restricciones temporales como alerta. Se guarda la fecha, pero no se avisa.

**Anti-objetivos (sería un fracaso aunque funcione)**

- Que restricciones o recomendaciones se vean con `hr.employee.read`. Son datos de salud: datos sensibles según la
  Ley 1581.
- Que el texto del concepto aparezca en la actividad del empleado, en la auditoría o en una notificación.
- Que la campana mande un aviso por cada empleado.

## 3. Restricciones

- Todo vive en el módulo `Hr` (enfoque A, elegido por el owner). Requiere el módulo `hr`.
- Hr no tiene outbox ni worker hoy. Hay que agregar los dos copiando a ServiceContracts
  (`ServiceContractsReminderWorker`, `TenantRunStore`, `DailyDigestStore`, y la proyección `*OutboxMessage` con
  `ExcludeFromMigrations`).
- Hr no puede referenciar Messaging ni Support. Los días hábiles entran por un puerto con adaptador en el
  Bootstrapper, como `SupportBusinessCalendarSource`.
- Las capas las verifica `ArchitectureTests`.

## 4. Enfoques considerados

| Enfoque | Qué gana | Qué cuesta | Decisión |
|---|---|---|---|
| **A. Dentro de `Hr`** | Reusa empleado, cargo, contrato, PDF y actividad | `Hr` crece | **Elegido por el owner** |
| B. Módulo SG-SST aparte | Se vende por separado y deja lugar a accidentes y ausentismo | Eventos entre módulos, copia de cargos y contratos | Descartado: demasiado costo para el alcance |
| C. "Documentos con vencimiento" genérico | Un mecanismo para exámenes, cursos y licencias | Pierde el egreso a 5 días hábiles, los tipos y las restricciones | Descartado: no sirve a la norma |

## 5. Modelo

### 5.1 `MedicalExam`

Agregado propio, con **versión propia**. No usa `employee.version`, para que registrar un examen no choque con
quien edita la ficha.

| Campo | Tipo | Regla |
|---|---|---|
| `Id` | `MedicalExamId` | |
| `TenantId`, `EmployeeId` | | El empleado debe existir en el tenant |
| `Type` | enum `MedicalExamType` | `PreEmployment`, `Periodic`, `OccupationChange`, `PostIncapacity`, `ReturnToWork`, `FollowUp`, `Exit` |
| `ExamDate` | `DateOnly` | No puede ser futura respecto del día del tenant |
| `Provider` | `string?` | IPS o médico; máx. 200 |
| `Restrictions` | `string?` | máx. 2000; vacío significa sin restricciones |
| `Recommendations` | `string?` | máx. 2000 |
| `RestrictionsUntil` | `DateOnly?` | Sólo con `Restrictions`; debe ser ≥ `ExamDate` |
| `DocumentFileId` | `Guid?` | PDF del certificado |
| `Version`, `CreatedAt`, `UpdatedAt` | | |

Tabla `hr.medical_exams`, con índice único `(TenantId, EmployeeId, Type, ExamDate)`. Sobre él se apoya la regla
"misma fila no se repite" de la importación. Duplicarlo a mano responde 422 `hr.medical_exam.duplicate`.

### 5.2 Periodicidad en el cargo

`Position.ExamFrequencyMonths : int?`, entre 1 y 36. `null` quiere decir que el cargo no lleva periódico.

Se escribe con un endpoint propio, `PUT /positions/{id}/exam-frequency`, y no con el `PUT /positions/{id}` de hoy.
Así un frontend viejo que manda sólo `name` no borra la periodicidad. Fuera de rango responde 422
`hr.settings.exam_frequency_invalid`. `PositionDto` suma `examFrequencyMonths`.

### 5.3 `ExitExamRequirement` (egreso pendiente)

| Campo | Regla |
|---|---|
| `EmployeeId`, `ContractId` | Uno por contrato terminado |
| `TerminatedOn` | La fecha de terminación del contrato |
| `DueDate` | 5 días hábiles **después** de `TerminatedOn` (§5.5) |
| `Status` | `Pending`, `Done` (con `ExamId`) o `NotAttended` (con `NotifiedOn` y `Notes?`) |

- **Cuándo nace:** con `TerminateContract`, en la misma unidad de trabajo. **No** nace al desactivar: un contrato
  `Closed` pasa a `Deactivated` y eso no es un retiro (decisión del builder, D-3).
- **`Pending` → `Done`:** al registrar o importar un examen `Exit` de ese empleado con `ExamDate ≥ TerminatedOn`.
  Si hay varios pendientes, cierra el más viejo.
- **`Pending` → `NotAttended`:** por `POST …/exit-exam/not-attended`, con
  `{ notifiedOn: DateOnly, notes?: string(500) }`. `notifiedOn` no puede ser futura. Es la constancia del aviso
  que la norma le pide al empleador.
- **Se recalcula después de cada escritura de exámenes** (crear, editar, borrar, importar) del empleado. Hay una sola
  regla, determinista:
  - Un requerimiento está `Done` si existe un examen `Exit` con `ExamDate ≥ TerminatedOn` que no esté ya asignado a
    otro requerimiento. Cada requerimiento toma el más viejo que califique.
  - Si deja de existir ese examen (se borró, se le cambió el tipo o la fecha), vuelve a `NotAttended` si tenía aviso
    registrado y a `Pending` si no.
  - `PUT` puede cambiar `Type` y `ExamDate`, y el recálculo cubre esos casos.
- Un `NotAttended` pasa a `Done` si después llega el examen, y conserva el aviso.
- Reincorporar al empleado no la toca.
- **Backfill en la migración:** los contratos `Terminated` cuyo `DueDate` calculado cae en hoy o después reciben su
  requerimiento `Pending`. Los más viejos no reciben nada (D-7).

### 5.4 Estado de control por empleado

El backend lo calcula. El navegador nunca lo deriva.

- **Universo:** empleados `Active`, más empleados `Retired` con un egreso `Pending`. `Deactivated` queda fuera.
- **Último examen:** el de mayor `ExamDate`, sea del tipo que sea.
- **Próximo periódico:** último examen + `ExamFrequencyMonths` del cargo de la asignación vigente del contrato
  activo. Sin cargo, o con `null`, no hay próximo.

Hay un solo estado por empleado. Se aplica el primero que cumpla:

1. `ExitPending`: tiene un egreso `Pending`. Lleva `dueDate` y `businessDaysLeft`; si ya venció, `overdue: true`.
2. `NoExams`: está activo y no tiene ningún examen.
3. `Overdue`: el próximo periódico es anterior a hoy.
4. `DueSoon`: el próximo periódico cae en los próximos **30 días** (`MedicalExamRules.DueSoonDays`).
5. `UpToDate`: hay próximo periódico y está lejos.
6. `NotRequired`: tiene exámenes y su cargo no lleva periódico.

"Hoy" es `TenantCalendar.Today`.

Un empleado **fuera del universo** sigue teniendo su pestaña y su `GET`. Pasa con un retirado con el egreso
cerrado o con uno desactivado. Para ellos `status` es `null` y `nextPeriodicDue` también. El historial se lee igual.

### 5.5 Días hábiles

Puerto `IHrBusinessCalendar` en `Modules.Hr.Application`, con adaptador en el Bootstrapper.

- Cuenta de lunes a viernes.
- Con país regional del tenant `CO`, salta además los festivos de `ColombianHolidays`. Es la misma regla de
  `SupportBusinessCalendarSource`, que se reutiliza o se extrae a un helper compartido del Bootstrapper.
- `DueDate` es el 5.º día hábil **estrictamente después** de `TerminatedOn`.

## 6. Permisos

Dos permisos nuevos, ambos con `RequiredModules: [ModuleKeys.Hr]` y sólo para `admin` por defecto:

- `hr.medical.read`: "Ver exámenes médicos".
- `hr.medical.manage`: "Gestionar exámenes médicos".

- Todo endpoint de exámenes exige además `hr.employee.read`, porque sin ficha no hay a quién colgarle nada.
- Escribir exige `manage` y `read`, la misma regla D-1 de RRHH.
- El conteo de `CompositionRootTests` pasa de 112 a 114. También se toca
  `TheHrPermissionsRequireTheHrModuleAndGoOnlyToAdmin`.

## 7. Contrato HTTP

Todas las rutas van bajo `/api/v1/tenants/{tenantId}/hr`. Toda escritura de un examen responde
`EmployeeMedicalExamsDto` entero (§7.1), y el frontend lo escribe en su caché.

| Método y ruta | Permiso | Notas |
|---|---|---|
| `GET /employees/{id}/medical-exams` | read | `EmployeeMedicalExamsDto` |
| `POST /employees/{id}/medical-exams` | manage | Body `MedicalExamRequest` |
| `PUT /employees/{id}/medical-exams/{examId}` | manage | `If-Match` con la versión del examen (428/412) |
| `DELETE /employees/{id}/medical-exams/{examId}` | manage | `If-Match` |
| `PUT /employees/{id}/medical-exams/{examId}/document` | manage | `{ fileId: Guid? }`; `null` quita el PDF |
| `POST /medical-exams/upload-session` | manage | La misma forma que `/files/upload-session`; PDF de hasta 10 MB (`HrDocumentPolicy`) |
| `GET /employees/{id}/medical-exams/{examId}/document` | read | URL firmada de 5 min |
| `POST /employees/{id}/exit-exam/not-attended` | manage | `{ notifiedOn, notes? }` sobre el egreso `Pending` más viejo; si no hay, 422 `hr.medical_exam.no_pending_exit` |
| `GET /medical-exams/overview?status=&search=&page=&pageSize=` | read | §7.2 |
| `PUT /positions/{id}/exam-frequency` | `hr.settings.manage` | `{ months: int? }` con `If-Match` del cargo; responde `PositionDto` |
| `GET /medical-exams/import/template` | manage | xlsx |
| `POST /medical-exams/import/preview` | manage | multipart `file`; no escribe nada |
| `POST /medical-exams/import` | manage | Mismo archivo; todo o nada |

La subida y la descarga de PDF tienen rutas propias para no abrir `/files/*`, que hoy exige permisos de contrato,
a quien sólo puede ver exámenes.

### 7.1 DTOs

```jsonc
// EmployeeMedicalExamsDto
{
  "status": "ExitPending|NoExams|Overdue|DueSoon|UpToDate|NotRequired" | null, // null = fuera del universo (§5.4)
  "nextPeriodicDue": "yyyy-MM-dd" | null,
  "examFrequencyMonths": 12 | null,          // del cargo vigente
  "exitRequirement": {
    "contractId": "…", "terminatedOn": "…", "dueDate": "…",
    "status": "Pending|Done|NotAttended",
    "businessDaysLeft": 3, "overdue": false,
    "notifiedOn": null, "notes": null, "examId": null
  } | null,                                  // el más reciente; null si nunca hubo
  "exams": [ {                               // de ExamDate más reciente al más viejo
    "id": "…", "type": "Periodic", "examDate": "…", "provider": null,
    "restrictions": null, "recommendations": null, "restrictionsUntil": null,
    "hasDocument": true, "version": 3
  } ]
}
// MedicalExamRequest
{ "type": "…", "examDate": "…", "provider": null, "restrictions": null,
  "recommendations": null, "restrictionsUntil": null }
```

### 7.2 Resumen de control

```jsonc
{
  "counts": { "ExitPending": 1, "NoExams": 2, "Overdue": 4, "DueSoon": 7, "UpToDate": 30, "NotRequired": 5 },
  "items": [ {
    "employeeId": "…", "fullName": "…", "documentType": "CC", "documentNumber": "…",
    "positionName": "Operaria" | null,
    "lastExam": { "type": "Periodic", "examDate": "…" } | null,
    "nextDue": "yyyy-MM-dd" | null,          // nextPeriodicDue, o dueDate del egreso
    "status": "…", "businessDaysLeft": null | 3
  } ],
  "page": 1, "pageSize": 25, "total": 7
}
```

- `status` es opcional y acepta **varios valores separados por coma**, por ejemplo `status=UpToDate,NotRequired`.
  Así el frontend agrupa pestañas sin filtrar en el navegador. Sin `status` se listan todos. Un valor desconocido
  responde 422 `hr.medical_exam.status_invalid`.
- `search` busca por nombre o documento.
- **Orden:** urgencia primero (estados en el orden de §5.4) y, dentro de cada estado, `nextDue` ascendente.
- Los `counts` ignoran `status` y `search`: son los totales del tenant.

### 7.3 Errores nuevos (422 salvo que se diga otro)

| Código | Cuándo |
|---|---|
| `hr.medical_exam.not_found` | 404 |
| `hr.medical_exam.duplicate` | Mismo empleado, tipo y fecha que un examen existente |
| `hr.medical_exam.future_date` | La fecha del examen es posterior a hoy |
| `hr.medical_exam.restrictions_until_invalid` | Hay fecha sin restricciones, o la fecha es anterior al examen |
| `hr.medical_exam.no_pending_exit` | No hay egreso `Pending` |
| `hr.settings.exam_frequency_invalid` | Fuera de 1..36 |

Los errores de validación de campo viajan en `errors` como en el resto de Hr.

## 8. Importación

Calca la de empleados: ClosedXML, puertos `IExcelMedicalExamImporter` e `IMedicalExamImportTemplateBuilder`, y
constantes en `MedicalExamImportColumns`. Hoja "Exámenes" más hoja "Valores válidos". Máximo 1000 filas y 5 MB.

| Columna | Obligatoria | Formato |
|---|---|---|
| TIPO DOCUMENTO | sí | La lista de tipos de empleados |
| NÚMERO DOCUMENTO | sí | Cruza con un empleado del tenant (cualquier estado) |
| TIPO DE EXAMEN | sí | Preingreso, Periódico, Cambio de ocupación, Post-incapacidad, Retorno laboral, Seguimiento, Egreso |
| FECHA DEL EXAMEN | sí | dd/mm/aaaa; no puede ser futura |
| IPS / MÉDICO | no | |
| RESTRICCIONES | no | Vacío significa sin restricciones |
| RECOMENDACIONES | no | |
| RESTRICCIONES HASTA | no | dd/mm/aaaa; sólo con restricciones |

- La vista previa devuelve las filas válidas y los errores por fila con código. Si hay un solo error, no se escribe
  nada.
- Una fila duplicada, contra la base o contra otra fila del archivo, es error de fila.
- Un `Egreso` importado no exige un contrato terminado: es historial. Si existe un egreso `Pending` que cumple la
  regla de §5.3, lo cierra.
- La importación escribe una sola entrada de auditoría con el conteo.

## 9. Recordatorios en la campana

### 9.1 Infraestructura nueva en Hr

- Proyección `HrOutboxMessage` sobre `platform.outbox_messages`, con `ExcludeFromMigrations`.
- `HrMedicalExamsReminderWorker` es un `BackgroundService` con `PeriodicTimer` de `Hr:SweepMinutes` (15). Recorre
  tenant por tenant:
  - corre sólo desde la hora local `Hr:SendHourLocal` (8), con `TenantCalendar`;
  - sólo si el tenant está activo y tiene el módulo `hr`;
  - hace una corrida por tenant y día, con su propio `TenantRunStore`;
  - y un claim por miembro y día, con su propio `DailyDigestStore`.
- `IHrMemberDirectory.ListWithPermissionAsync(tenant, permission)`: devuelve las membresías activas cuyo rol
  concede el permiso. El adaptador va en el Bootstrapper con `IRolePermissionChecker`, como
  `ServiceContractsMemberDirectory`.

### 9.2 Eventos

| Evento | Cuándo | Payload (camelCase) | Correlación |
|---|---|---|---|
| `hr.medical-exams.due-digest.v1` | Corrida diaria, por destinatario, sólo si `pendingCount > 0` | `{ tenantId, memberId, runDate, pendingCount, occurredAt }` | `{tenant}:{member}:{runDate}` |
| `hr.medical-exams.exit-required.v1` | En `TerminateContract`, misma transacción, un mensaje por destinatario | `{ tenantId, memberId, employeeId, employeeName, dueDate, occurredAt }` | `{tenant}:{member}:{contractId}` |

- **Destinatarios:** quienes tienen `hr.medical.manage`.
- **`exit-required`:** los destinatarios se resuelven antes del commit de `TerminateContract`. Sin destinatarios, la
  terminación sigue y no se escribe ningún mensaje. Si la consulta falla, falla la terminación, igual que cualquier
  otra lectura del handler. No hay terminación sin su aviso.
- **`pendingCount`:** `ExitPending + NoExams + Overdue + DueSoon`.
- Ningún payload lleva restricciones ni el tipo de examen.

### 9.3 Notifications

- `InAppNotificationKind` suma `HrMedicalExamsDue` y `HrExitExamRequired`.
- `KeysOf` suma `{ pendingCount, runDate }` y `{ employeeId, employeeName, dueDate }`.
- Hay un consumidor nuevo, `HrInAppConsumer` (`notifications.in-app.hr`). Usa los dedupe keys
  `hr-medical-exams:{runDate}` y `hr-exit-exam:{employeeId}:{dueDate}`.
- Se actualizan §6, §8.5, §11 y §12 del spec `2026-11-22-in-app-notifications-backend-design.md`.

## 10. Actividad y auditoría

- **Actividad del empleado:** se suma la categoría `Medical` con las acciones `MedicalExamRecorded`,
  `MedicalExamUpdated`, `MedicalExamDeleted` y `ExitExamNotAttended`. Los cambios registran **sólo tipo, fecha y
  si hay PDF**; nunca restricciones, recomendaciones ni proveedor.
- `GET …/activity` omite la categoría `Medical` a quien no tiene `hr.medical.read`.
- **Auditoría:** `hr.medical_exam.created|updated|deleted|document_updated|imported` y
  `hr.medical_exam.exit_not_attended`, con el mismo límite de contenido.
- Eliminar un empleado borra sus exámenes y sus egresos (D-9 de RRHH).

## 11. Lo que decide el builder

- Si `ExitExamRequirement` es entidad del agregado `Employee` o tabla aparte, siempre que se escriba en la misma
  unidad de trabajo que `Terminate`.
- Si el resumen de control se calcula con una consulta o con una vista. Tiene que paginar en la base: no puede
  traer a todos los empleados a memoria si un tenant tiene muchos.
- La extracción del calendario hábil a un helper compartido.

## 12. Decisiones registradas (owner o builder)

- **D-1 (owner):** periodicidad por cargo, no por empresa.
- **D-2 (owner):** recordatorio por la campana, con importación de historial.
- **D-3 (builder):** sólo la terminación crea un egreso pendiente; la desactivación no.
- **D-4 (builder):** el próximo periódico se calcula desde el último examen de cualquier tipo. Un post-incapacidad
  reinicia el conteo.
- **D-5 (builder):** "Por vencer" = 30 días.
- **D-6 (owner, 2026-11-23):** `NoExams` cuenta en la campana. El primer día, sin importar historial, el resumen
  dice "N exámenes pendientes" por todos los empleados. Es un solo aviso por persona y por día, y baja apenas se
  importa el historial.
- **D-7 (builder):** el backfill de egresos cubre sólo los contratos terminados que todavía están dentro del plazo.
- **D-8 (builder):** no hay interruptor propio. El worker respeta el módulo `hr` y el estado del tenant.

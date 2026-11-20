# Recursos humanos, fase 1: empleados y contratos

Fecha: 2026-11-20. Estado: diseño pendiente de aprobación. Sin código.
Repos: `qep-backend` (este documento, con el contrato HTTP) y `qep-frontend`
(`docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md`, pantallas).

## Intención

QCode lleva su gente en lulo-erp `v1` (NestJS, sin cambios desde junio de 2024). El owner quiere
apagar lulo-erp y, además, **vender el módulo a los tenants de QEP**. El trabajo va por fases:

- **Fase 1 (este documento):** paridad con lo que lulo-erp `v1` hace con los colaboradores:
  ficha, contrato laboral con historial de salario e importación por Excel. Los préstamos a
  empleados de `v1` no entran: el owner no los necesita en la fase 1 (2026-11-20).
- **Fase 2 (otro spec):** nómina, a partir del plan «Lulo Nómina» de `lulo-erp/v2/docs/` (motor de
  nómina, novedades, vacaciones, prestaciones, PILA, nómina electrónica DIAN, liquidación,
  autoservicio).
- **Proyecto aparte:** gestión de proyectos («Jira light») y el timer de horas facturables a
  clientes. El timer de `v1` cuelga de proyectos, tareas y facturas: no es RRHH y no entra acá.

Apagar lulo-erp exige la fase 1 de RRHH y el proyecto de gestión de proyectos.

## Objetivos

- Registrar empleados del tenant con sus datos personales, de pago y de seguridad social.
- Llevar el contrato laboral vigente, sus otrosíes y el historial de salario y cargo.
- Terminar, suspender y reactivar un contrato con motivo.
- Importar la planta desde un Excel (migración desde lulo-erp y alta de tenants nuevos).
- Vender el módulo por tenant (`hr`) con permisos propios.

## Fuera de alcance (fase 2 o nunca)

- Nómina, novedades, prestaciones, PILA, nómina electrónica, liquidación de contrato.
- Vacaciones, permisos, incapacidades y ausencias.
- Autoservicio del empleado (portal o WhatsApp) y certificados laborales.
- Reclutamiento, evaluaciones, capacitaciones, organigrama, SG-SST.
- Timer y horas (proyecto aparte).
- Préstamos a empleados. Cuando lleguen, el camino elegido es que `Loans` acepte un deudor
  `Employee` (enfoque A abajo), probablemente junto con el tipo «capital e intereses» y el
  descuento por nómina de la fase 2. `Loans` no se toca en este trabajo.
- Catálogos oficiales de EPS, AFP, ARL, cajas y fondos de cesantías: en fase 1 son texto libre
  (el catálogo llega con los parámetros legales de la fase 2).
- Migrar desde lulo-erp el historial de aumentos: se hace una sola
  vez por script SQL, fuera del producto (como la carga inicial de Préstamos).

## Lo que no debe pasar

- Que un empleado aparezca en Clientes, campañas o Mensajería. RRHH no comparte tabla con el CRM.
- Que alguien con permiso de ver empleados vea salario o cuenta bancaria sin el permiso de
  compensación.
- Que cambiar un salario borre el anterior: todo cambio después del inicio es un otrosí.
- Que el navegador calcule el salario vigente o el cargo vigente: los entrega el backend.
- Que exigir una membresía impida registrar a quien no entra a la consola.

## Enfoques considerados

| Enfoque | Qué cambia | Por qué sí / no |
| --- | --- | --- |
| **A. Módulo `Hr` con `Employee` propio; `Loans` acepta un deudor empleado (cuando haya préstamos a empleados)** | Módulo nuevo; más adelante, un tipo de deudor en `Loans` | **Elegido por el owner.** Datos de la gente aislados del CRM; la fase 2 puede descontar cuotas por nómina sabiendo que el deudor es empleado. |
| B. Módulo `Hr` y un cliente espejo por empleado para prestarle | `Loans` intacto | Descartado: el empleado aparece en el CRM; un asesor con permiso de clientes ve datos de un compañero. |
| C. Empleado como extensión de la membresía | Sin entidad nueva | Descartado: quien no entra a la consola (cajero, bodeguero) no podría existir. |

## Diseño

### Módulo y permisos

- Módulo de backend `Modules.Hr` (Domain, Application, Infrastructure, Api), esquema `hr`, con su
  `HrLayerTests.cs` en `tests/ArchitectureTests/`.
- Clave de tenant `TenantModuleKey.Hr = "hr"`, con su migración del `CK_tenant_modules_module_key`
  (como `AddLoansModuleKey`).
- Permisos (llamada del diseño, el owner puede corregir los nombres):
  - `hr.employee.read`: lista y ficha **sin** salario ni datos bancarios.
  - `hr.employee.manage`: crear, editar, archivar empleados e importar.
  - `hr.contract.read`: contrato, otrosíes, salario, historial y datos bancarios.
  - `hr.contract.manage`: crear contrato, otrosí, terminar, suspender, reactivar.
  - Roles por defecto: `owner` y `admin` con los cuatro; los demás roles, ninguno.
  - Cada `manage` exige también su `read` (el editor de roles no deja uno sin el otro): nadie
    crea un contrato que no puede ver.
  - Los datos bancarios no viajan en `PUT /hr/employees/{id}`: van por
    `PUT /hr/employees/{id}/payment` con `hr.contract.manage`. Así no hay campos que se ignoren en
    silencio según el permiso (llamada del diseño).
- Auditoría en el handler y outbox, como el resto de módulos.

### Empleado (`Employee`, agregado)

Une los campos de `v1` (`User` con rol Colaborador) y `v2` (`Collaborator`).

| Campo | Tipo | Reglas |
| --- | --- | --- |
| `id` | uuid | |
| `firstName`, `lastName` | string | requeridos |
| `countryCode` | string ISO-3166 | requerido; default país del tenant |
| `documentType` | string | mismo catálogo por país que Clientes |
| `documentNumber` | string | requerido; **único por tenant** entre empleados no archivados (índice único; traducir por nombre de índice) |
| `birthDate` | date? | |
| `phone` | string? E.164 | |
| `personalEmail` | string? | |
| `workEmail` | string? | |
| `address` | string? | |
| `cityId` | uuid? | catálogo de `Geography` (sólo si el país lo tiene); fuera de Colombia `cityName` libre, como las partes del pedido |
| `memberId` | uuid? | vínculo opcional a una membresía activa del tenant; único por tenant |
| `bankName`, `bankAccountType` (`Savings`/`Checking`), `bankAccountNumber` | string? | visibles sólo con `hr.contract.read` |
| `healthProvider` (EPS), `pensionFund`, `severanceFund`, `occupationalRiskProvider` (ARL), `compensationFund` | string? | texto libre en fase 1 |
| `status` | `Active` / `Inactive` / `Archived` | **derivado**: `Active` con contrato `Active` o `Suspended`; `Inactive` sin contrato vigente; `Archived` explícito |
| `version` | int | `If-Match` en `PUT` |

Archivar no borra: un empleado con contratos se conserva; archivar exige no tener
contrato vigente. Desarchivar existe.

### Contrato (`EmploymentContract`, entidad del agregado `Employee`)

Forma de `v2` (`Contract.cs`).

| Campo | Tipo | Reglas |
| --- | --- | --- |
| `contractType` | `Indefinite`, `FixedTerm`, `ServiceProvision`, `Temporary`, `Apprenticeship` | |
| `workSchedule` | `FullTime`, `PartTime`, `Hourly`, `Remote`, `Hybrid` | |
| `position` | string ≤ 120 | texto libre (en `v1` era un enum fijo, que no sirve para revender) |
| `startDate` | date | |
| `endDate` | date? | requerido para `FixedTerm`, `Temporary` y `Apprenticeship`; opcional en `ServiceProvision`; prohibido en `Indefinite`; ≥ `startDate` |
| `salary` | decimal > 0 | salario **inicial** |
| `currency` | string | default moneda del tenant |
| `documentFileId` | uuid? | PDF firmado vía `Storage` (sesión de subida, como los adjuntos) |
| `status` | `Active`, `Suspended`, `Terminated`, `Cancelled` | |
| `terminationReason` | `VoluntaryResignation`, `JustCauseDismissal`, `UnjustDismissal`, `ContractEnd`, `MutualAgreement`, `BreachOfDuties`, `Downsizing`, `CompanyClosure`, `Other` | requerido al terminar |
| `terminationDate`, `terminationNotes` | date?, string? | |

Reglas:

- Un empleado tiene **a lo sumo un** contrato `Active` o `Suspended`. Los terminados quedan como
  historial.
- `Cancelled` es «se creó por error»: sólo sin otrosíes. `Terminated` es fin real de la relación.
- Transiciones: `Active → Suspended → Active`, `Active|Suspended → Terminated`,
  `Active → Cancelled`. Ninguna sale de `Terminated` o `Cancelled`.
- El salario inicial y los datos del contrato sólo se editan sin otrosíes; después todo cambio es
  un otrosí.

### Otrosí (`ContractAddendum`)

| Campo | Tipo | Reglas |
| --- | --- | --- |
| `type` | `SalaryIncrease`, `PositionChange`, `ScheduleChange`, `Extension`, `Other` | |
| `effectiveDate` | date | ≥ `startDate` del contrato |
| `newSalary` | decimal? | requerido con `SalaryIncrease` (sirve también para una baja; el nombre es el de uso común) |
| `newPosition` | string? | requerido con `PositionChange` |
| `newWorkSchedule` | enum? | requerido con `ScheduleChange` |
| `newEndDate` | date? | requerido con `Extension` |
| `description` | string? ≤ 1000 | |
| `documentFileId` | uuid? | |

- `Extension` sólo aplica a contratos con `endDate`, y `newEndDate` debe ser posterior a la
  fecha fin vigente.
- «Hoy» es el día en la zona horaria del tenant (configuración regional existente).
- No se edita ni se borra un otrosí: se anula (`voided`, con motivo) y se crea otro. Mismo
  criterio que los abonos a capital de Préstamos.
- El backend entrega `current` (salario, cargo, jornada, fecha fin) tomando el último otrosí no
  anulado con `effectiveDate ≤ hoy` (día del tenant), y `salaryHistory` (inicial + otrosíes con
  salario) del más reciente al más viejo.
- Un otrosí con fecha futura existe y aparece como «programado».

### Importación por Excel

Patrón de la importación de Contratos de servicio: `POST /hr/employees/import/preview` sin
escribir, y `POST /hr/employees/import` confirma reenviando el mismo archivo. Columnas (las de
`v1`, más las nuevas): `NOMBRES`, `APELLIDOS`, `TIPO DE DOCUMENTO`, `NÚMERO DE DOCUMENTO`,
`FECHA DE NACIMIENTO`, `CELULAR`, `CORREO PERSONAL`, `CORREO CORPORATIVO`, `DIRECCIÓN`, `CIUDAD`,
`BANCO`, `TIPO DE CUENTA`, `CUENTA BANCARIA`, `EPS`, `PENSIÓN`, `CESANTÍAS`, `ARL`,
`CAJA DE COMPENSACIÓN`, `CARGO`, `TIPO DE CONTRATO`, `JORNADA`, `SALARIO`, `FECHA DE INGRESO`,
`FECHA DE RETIRO`. Cada fila crea empleado + contrato (terminado con `ContractEnd` si trae
fecha de retiro pasada). Un documento ya existente es error de fila, no actualización. La
plantilla se descarga de `GET /hr/employees/import/template`. Tope: 1000 filas.

Reglas de la importación (llamadas del diseño):

- **Todo o nada:** confirmar exige una vista previa sin errores; si una fila falla, no se escribe
  ninguna.
- Un documento repetido dentro del archivo es error de las dos filas.
- `TIPO DE DOCUMENTO`, `CIUDAD`, `TIPO DE CONTRATO`, `JORNADA` y `TIPO DE CUENTA` se comparan sin
  mayúsculas ni tildes contra la etiqueta en español que muestra la UI (p. ej. «Término fijo»,
  «Tiempo completo», «Ahorros»). La plantilla trae una hoja con los valores válidos.
- `FECHA DE RETIRO` pasada ⇒ contrato `Terminated`, `ContractEnd`, `terminationDate` = esa
  fecha. Futura ⇒ es el `endDate` del contrato, que queda `Active`.

### Contrato HTTP (prefijo `/api/v1`, header `X-Tenant-Id`)

| Método y ruta | Permiso | Notas |
| --- | --- | --- |
| `GET /hr/employees?search&status&page&pageSize` | `hr.employee.read` | `search` por nombre o documento; `items[]` con `current.position` y `contractStatus`; totales por estado en `summary`, que respetan `search` pero no `status` (para que los contadores de los filtros no cambien al elegir uno) |
| `PUT /hr/employees/{id}/payment` | `hr.contract.manage` | banco, tipo y número de cuenta; `If-Match` |
| `GET /hr/employees/{id}` | `hr.employee.read` | sin `bank*` ni `contract` si falta `hr.contract.read` (campos `null`, y `canReadContract: false`) |
| `POST /hr/employees` | `hr.employee.manage` | puede traer `contract` inicial (requiere además `hr.contract.manage`) |
| `PUT /hr/employees/{id}` | `hr.employee.manage` | `If-Match`; sin datos bancarios |
| `POST /hr/employees/{id}/archive` / `unarchive` | `hr.employee.manage` | `hr.employee.has_active_contract` (422) |
| `POST /hr/employees/{id}/contracts` | `hr.contract.manage` | `hr.contract.already_active` (409) |
| `PUT /hr/employees/{id}/contracts/{cid}` | `hr.contract.manage` | `If-Match`; `hr.contract.has_addenda` (422) |
| `POST …/contracts/{cid}/suspend` · `reactivate` · `terminate` · `cancel` | `hr.contract.manage` | `hr.contract.invalid_transition` (422) |
| `POST …/contracts/{cid}/addenda` | `hr.contract.manage` | |
| `POST …/contracts/{cid}/addenda/{aid}/void` | `hr.contract.manage` | motivo requerido |
| `POST /hr/files/upload-session` | `hr.contract.manage` | PDF ≤ 10 MB |
| `GET /hr/files/{fileId}` | `hr.contract.read` | URL firmada de descarga |
| `GET /hr/employees/import/template` · `POST …/import/preview` · `POST …/import` | `hr.employee.manage` + `hr.contract.manage` | `hr.import.*` (422) para errores de archivo |
| `GET /hr/members-available` | `hr.employee.manage` | membresías activas sin empleado vinculado |

Errores de validación: FluentValidation con mapa `errors`. Errores de dominio: `code` con
prefijo `hr.`, mapeados en `ApiExceptionHandler`. `hr.employee.document_taken` (409) por índice.

## Lo que queda al implementador

- Nombres de tablas, índices, handlers y DTOs internos.
- Detalle de la sesión de subida (reusar la infraestructura de `Storage`).
- Tamaño de página por defecto y orden de la lista (sugerido: apellido, nombre).
- El texto de los mensajes de error en español del frontend.

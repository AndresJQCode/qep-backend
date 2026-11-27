# Recursos humanos, fase 1: organización, empleados y contratos

Fecha: 2026-11-20 (reescrito el mismo día sobre `lulo-erp/v2/docs`; el mismo día suma el país del empleado y la empresa
empleadora, delta aprobado por el owner). Estado: alcance aprobado por el owner; sin código.
Repos: `qep-backend` (este documento) y `qep-frontend` (`docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md`,
pantallas). El contrato HTTP exacto vive en el plan de backend (`docs/superpowers/plans/2026-11-20-rrhh-fase-1-backend.md`,
«Contrato HTTP»).

## Fuente

La lógica de negocio sale de `lulo-erp/v2/docs`: la investigación de Aleluya (`research/00` a `05`) y el openspec
`lulo-nomina-platform`, sobre todo `specs/employee-and-contract-management`, `specs/organization-configuration` y
`specs/legal-parameters`. Cada regla cita su requisito (`FI-*`). **De lulo-erp no se toma código, modelo, nombres ni
patrones**, ni de v1 ni de v2 (decisión del owner, 2026-11-20): Aleluya es un SaaS especializado en nómina colombiana y su
modelo manda; la implementación sigue los patrones de QEP.

## Intención

QCode lleva su gente en lulo-erp y quiere apagarlo y, además, **vender el módulo `hr` a los tenants de QEP**. Por fases:

- **Fase 1 (este documento):** los datos maestros que la nómina necesita, sin calcular nada: configuración de la
  organización, ficha del empleado, contrato, historiales con fecha de vigencia, prórrogas, conversión, terminación e
  importación por Excel.
- **Fase 2:** nómina (motor, novedades, prestaciones, PILA, liquidación laboral), sobre lo que deja la fase 1.
- **Más adelante:** autoservicio del empleado, nómina electrónica DIAN, integraciones.
- **IA, después de la fase 1 (owner, 2026-11-20), cada una con su spec:** 1) herramientas de RRHH de sólo lectura para el
  copiloto existente, con los permisos del miembro; 2) asistente de migración que propone el mapeo de un Excel cualquiera a
  la plantilla de importación (v2/docs FI-AI-009), con la validación determinística de siempre. Las reglas legales y los
  avisos nunca dependen de un modelo (`payroll-assistive-intelligence`: la IA explica, no decide).

## Fuera de alcance de la fase 1

| Qué | Por qué espera |
| --- | --- |
| Cálculo de nómina, IBC, aportes, novedad VSP automática, efectos del subtipo de cotizante, reglas de tiempo parcial | Cálculo (fase 2). La fase 1 guarda el dato. |
| Suspensiones, licencias, incapacidades, vacaciones | Son novedades de nómina (fase 2). |
| Desactivación automática al vencer el contrato (FI-EMP-006) | Va atada al período de nómina, que no existe aún. La fase 1 avisa (ver Prórrogas). |
| Liquidación laboral (FI-SET-*) | Fase 2. Terminar el contrato en la fase 1 deja al empleado «Retirado»; liquidar será de la fase 2. |
| Carpetas de documentos visibles para el empleado (FI-EMP-004) | Necesitan el portal del empleado. La fase 1 adjunta PDFs a contrato y cambios. |
| Exportación con vacaciones acumuladas (FI-EMP-016) | Las vacaciones son de la fase 2. |
| Centros de costos, prefijo de nómina electrónica y código de sucursal PILA de la sede | Contabilidad, DIAN y PILA. |
| Tabla CIIU → clase de riesgo ARL automática | La tabla del Decreto 2012/2022 es un catálogo grande; en la fase 1 la clase se elige a mano y el CIIU se guarda como dato. |
| Catálogos oficiales de EPS, AFP, ARL, cajas y fondos | Texto libre en la fase 1; el catálogo llega con la fase 2. |
| Préstamos a empleados, timer, proyectos | Otros trabajos. |

## Lo que no debe pasar

- Que un empleado aparezca en Clientes, campañas o Mensajería: RRHH no comparte tabla con el CRM.
- Que alguien con permiso de ver empleados vea salario o cuenta bancaria sin el permiso de contratos.
- Que un aumento borre el salario anterior: un aumento es «Crear» un registro nuevo; «Editar» reescribe y exige confirmación
  explícita (FI-EMP-011).
- Que el navegador calcule el salario, el cargo o la sede vigentes, el aviso de vencimiento o el estado del empleado.
- Que exigir una membresía impida registrar a quien no entra a la consola.
- Que una prórroga viole el tope legal del término fijo (Ley 2466/2025): se bloquea, no se avisa.
- Que un contrato cambie de empresa empleadora: la empresa es fija; cambiar de empresa es terminar y reincorporar.
- Que un contrato use un centro de trabajo de una sede de otra empresa (ARL y PILA son por aportante).
- Que desactivar una empresa rompa algo: sus sedes y contratos siguen; sólo deja de ofrecerse para vínculos nuevos.
- Que se borre una empresa con sedes o contratos de RRHH: la base lo impide (422 `companies.company.in_use`).
- Que quien tiene permisos de RRHH necesite permisos de Empresas para elegir la empresa empleadora.

## Diseño

### Módulo y permisos

- Módulo `Modules.Hr` (Domain, Application, Infrastructure, Api), esquema `hr`, con su `HrLayerTests.cs`.
- Clave de tenant `TenantModuleKey.Hr = "hr"`, con su migración del `CK_tenant_modules_module_key`. **Depende de
  `companies`** (mismo mecanismo que `ServiceContracts` y `Loans` con `customers`): la empresa empleadora es una empresa del
  módulo de Empresas (`Company`, razón social propia del tenant con NIT). Sin `companies` prendido, `hr` queda apagado.
- RRHH lee las empresas por un puerto propio hacia Companies (como la caja de POS); no comparte tabla ni referencia el
  módulo para leer. La lista de empresas activas llega en la configuración de RRHH: quien tiene permisos `hr.*` puede no
  tener los de Empresas, y el frontend nunca llama la API de Companies. Sedes y contratos apuntan a la empresa con una clave
  foránea real (como la caja de POS), así que una empresa con sedes o contratos de RRHH no se puede borrar en Empresas; sí se
  puede desactivar.
- **Catálogo de países en Geografía.** El backend no tenía uno (Empresas, Tenancy y Mayorista sólo validan la forma del
  código). Geografía suma un catálogo de sólo lectura —código ISO 3166-1 alfa-2 y nombre en español— con los mismos 41
  países que hoy ofrece el frontend, expuesto en `GET /api/v1/countries` (`[{ "code": "CO", "name": "Colombia" }]`, por
  nombre, para cualquier usuario autenticado, como `/api/v1/departments`). RRHH valida el país del empleado contra él.
  Pasar Empresas, Tenancy y Mayorista a ese catálogo queda fuera de esta fase.
- Permisos:
  - `hr.employee.read`: lista, ficha, historial de acciones y configuración (lectura), **sin** salario ni banco.
  - `hr.employee.manage`: editar datos personales, de contacto y de seguridad social; desactivar; eliminar.
  - `hr.contract.read`: contratos, salarios, cargos y sedes con su historial, prórrogas y datos bancarios.
  - `hr.contract.manage`: crear empleados (el alta incluye el contrato), salario, cambios de cargo y sede, prórrogas,
    conversiones, terminar, reincorporar, banco e importar.
  - `hr.settings.manage`: sedes, centros de trabajo, áreas y cargos.
  - Roles por defecto: `admin` con los cinco; los demás, ninguno.
- Auditoría en el handler, como el resto de módulos.

### Configuración de la organización (FI-ORG-001 a 004, 009)

Debe existir antes de crear empleados (`organization-configuration`, «Áreas and Cargos», «Centros de Trabajo»): al menos
una empresa activa con una sede no archivada que tenga un centro de trabajo no archivado, y al menos un cargo no archivado.

Sedes y centros de trabajo **pertenecen a una empresa** (ARL y PILA son por aportante). Áreas y cargos son de todo el tenant.

**Caja de compensación (delta 2026-11-21).** Es el aporte parafiscal del 4 % del empleador (Ley 21/1982), no el salario; se afilia por sede porque las cajas se organizan por departamento. El campo de la sede es una **lista fija**: el backend la declara en código y `GET /hr/settings` la devuelve como `compensationFunds: string[]` para que el selector no la duplique. La API guarda el nombre tal cual figura en la lista y rechaza otro con 422 `errors.compensationFund`; `null` sigue valiendo (sede sin caja). **Sólo empresas de Colombia (2026-11-21, owner).** La caja de compensación es un aporte parafiscal colombiano: una sede cuya empresa no es de Colombia no tiene caja. `GET /hr/settings` trae `country` (ISO alfa-2, `CO` si la empresa no lo tiene) en cada empresa; al crear o editar una sede de otra empresa, un `compensationFund` no vacío es 422 `errors.compensationFund` («La caja de compensación sólo aplica a empresas de Colombia.») y `null` vale. La empresa de una sede es fija, así que el país que se revisa es el de la guardada. **Contenido de la lista (inicial, 42 cajas).** Sale del listado de cajas de la UGPP publicado el 14-01-2026 (`ugpp.gov.co/wp-content/uploads/2026/01/LISTADO-CAJAS-DE-COMPENSACION-2026.pdf`, vigencia de un año), que separa las cajas sin medida especial de las que están bajo intervención administrativa de la Superintendencia del Subsidio Familiar; **ambos grupos entran**, porque las intervenidas siguen operando y tienen empresas afiliadas. Se guarda el nombre corto de la tabla. El departamento lo dedujo del nombre de la caja, no de la tabla del PDF (salió desordenada al extraerla), y los NIT se leyeron del PDF en ese orden. **Verificar contra SuperSubsidio (`ssf.gov.co`) antes de la fase de nómina**: Rankia cuenta 43 cajas, así que puede faltar una, y el NIT importa cuando se genere la PILA. En la fase 1 la lista no calcula nada y corregirla es un cambio de una línea, sin migración.

Sin medida especial (30):

| Caja | Departamento | NIT |
| --- | --- | --- |
| CAFAMAZ | Amazonas | 800003122 |
| COMFAMA | Antioquia | 890900841 |
| CAJASAI | San Andrés y Providencia | 892400320 |
| COMBARRANQUILLA | Atlántico | 890102002 |
| CAJACOPI Atlántico | Atlántico | 890102044 |
| COMFAMILIAR Atlántico | Atlántico | 890101994 |
| COMFENALCO Cartagena | Bolívar | 890480023 |
| COMFABOY | Boyacá | 891800213 |
| CONFA | Caldas | 890806490 |
| COMFACASANARE | Casanare | 844003392 |
| COMFACAUCA | Cauca | 891500182 |
| COMFACESAR | Cesar | 892399989 |
| COMFACHOCÓ | Chocó | 891600091 |
| COLSUBSIDIO | Bogotá y Cundinamarca | 860007336 |
| CAFAM | Bogotá y Cundinamarca | 860013570 |
| COMPENSAR | Bogotá y Cundinamarca | 860066942 |
| COMFACUNDI | Cundinamarca | 860045904 |
| CAJAMAG | Magdalena | 891780093 |
| COFREM | Meta | 892000146 |
| COMFAORIENTE | Norte de Santander | 890500675 |
| COMFAMILIAR Putumayo | Putumayo | 891200337 |
| COMFENALCO Quindío | Quindío | 890000381 |
| COMFENALCO Santander | Santander | 890201578 |
| CAJASAN | Santander | 890200106 |
| COMFASUCRE | Sucre | 892200015 |
| CAFASUR | Tolima | 890704737 |
| COMFENALCO Tolima | Tolima | 890700148 |
| COMFATOLIMA | Tolima | 800211025 |
| COMFENALCO Valle | Valle del Cauca | 890303093 |
| COMFANDI | Valle del Cauca | 890303208 |

Con intervención administrativa (12):

| Caja | Departamento | NIT |
| --- | --- | --- |
| COMFENALCO Antioquia | Antioquia | 890900842 |
| COMFIAR | Arauca | 800219488 |
| COMFAMILIAR Cartagena y Bolívar | Bolívar | 890480110 |
| COMFACA | Caquetá | 891190047 |
| COMFACOR | Córdoba | 891080005 |
| COMCAJA | Cundinamarca | 800231969 |
| COMFAGUAJIRA | La Guajira | 892115006 |
| COMFAMILIAR Huila | Huila | 891180008 |
| COMFAMILIAR Nariño | Nariño | 891200208 |
| CAFABA | Norte de Santander | 890270275 |
| COMFANORTE | Norte de Santander | 890500516 |
| COMFAMILIAR Risaralda | Risaralda | 891480000 |

| Entidad | Campos | Reglas |
| --- | --- | --- |
| Sede | empresa, nombre, dirección, ciudad, caja de compensación (**lista fija**) | la caja se elige de `compensationFunds` (catálogo fijo en el backend, ver abajo) y un valor fuera de la lista es 422, y sólo aplica si la empresa es de Colombia (2026-11-21: «sólo empresas de Colombia»); empresa activa del tenant al crear; la empresa de una sede es fija; nombre único por empresa |
| Centro de trabajo | nombre, sede, código CIIU (texto, opcional), clase de riesgo ARL `I`–`V`, alto riesgo de pensión (sí/no, default no) | exige al menos una sede; hereda la empresa de su sede; nombre único por sede |
| Área | nombre | nombre único por tenant |
| Cargo | nombre | nombre único por tenant |

Ninguna se borra si un empleado la usa o la usó: se archiva. Una archivada no se ofrece en altas ni cambios nuevos, pero
sigue visible en los historiales. Una empresa desactivada después en Empresas no rompe nada: sólo deja de ofrecerse para
**vínculos nuevos** —crear una sede, un alta o una reincorporación—. Sus sedes existentes se editan y admiten centros, y
sus contratos siguen funcionando completos: salarios, cargo y lugar, prórrogas, conversión a indefinido, conversión de tipo
(conserva la empresa: es el mismo empleador) y terminación.

### Empleado (FI-EMP-001, 003, 017, 026, 027)

| Campo | Reglas |
| --- | --- |
| nombres, apellidos | requeridos |
| tipo de documento | `CC`, `CE`, `PEP`, `PPT`, `PA` (pasaporte), `CD` (carné diplomático), `SC` (salvoconducto) (FI-EMP-017) |
| número de documento | requerido; único por tenant entre empleados no eliminados |
| país | **nacionalidad**, código ISO 3166-1 alfa-2 del catálogo de países de Geografía; vacío ⇒ el país del tenant (Configuración › General), y `CO` si el tenant no tiene país. Cruce con el tipo de documento: `CC` exige Colombia; `CE`, `PEP`, `PPT`, `PA`, `CD` y `SC` exigen otro país |
| fecha de nacimiento, celular (E.164), correo personal, correo corporativo, dirección, ciudad | opcionales; ciudad del catálogo de Geografía (residencia en Colombia; no depende del país) |
| miembro vinculado | opcional; una membresía activa del tenant, única entre empleados |
| EPS, fondo de pensiones, fondo de cesantías, ARL | texto libre en la fase 1 |
| caja de compensación | **no se guarda en la ficha** (decisión del owner, 2026-11-21): la caja de un empleado es la de su sede vigente, que es donde vive (Aleluya hace igual: R02§11, R03§8). La ficha, la importación y el historial de actividad no la llevan. |
| subtipo de cotizante | `Standard` (estándar), `Pensioner` (pensionado), `NotRequiredByAge` (no obligado por edad), `PensionRequirementsMet` (cumplió requisitos de pensión), `SubstituteIndemnity` (recibió indemnización sustitutiva); default estándar (FI-EMP-008). La fase 1 sólo lo guarda. |
| exento de pensión por cobertura en el exterior | sí/no; sólo con país distinto de Colombia, que por el cruce de arriba equivale a documento extranjero (FI-EMP-017, Ley 100/1993 art. 15) |
| banco, tipo de cuenta (`Savings`/`Checking`), número de cuenta, medio de pago (`BankTransfer`/`Other`) | número de hasta 17 dígitos (FI-EMP-026); visibles sólo con `hr.contract.read` |
| días de descanso | días de la semana fijos; default domingo; al menos uno (FI-EMP-027) |
| estado | derivado (ver Ciclo de vida) |

### Contrato (FI-EMP-007, 009, 010)

| Campo | Reglas |
| --- | --- |
| empresa empleadora | requerida; una empresa **activa** del tenant en el alta y la reincorporación. **Fija**: no se cambia; cambiar de empresa es terminar y reincorporar con otra. La conversión de tipo la conserva, aunque la empresa esté desactivada |
| tipo de contrato | `Employee` (Empleado), `Contractor` (Contratista), `PartTime` (Tiempo parcial), `SenaApprenticeTeaching` (Aprendiz SENA lectiva), `SenaApprenticeProductive` (Aprendiz SENA productiva), `SenaApprenticeDual` (Aprendiz SENA dual), `UniversityIntern` (Practicante universitario), `StudentLaw789` (Estudiante Ley 789), `StudentDecree055` (Estudiante Decreto 055) |
| duración | `FixedTerm` (término fijo), `Indefinite` (término indefinido), `WorkOrLabor` (obra o labor) |
| fecha de inicio | requerida |
| fecha de fin | requerida en término fijo; **estimada** y requerida en obra o labor; prohibida en indefinido; ≥ inicio |
| tipo de salario | `Monthly` (mensual), `HalfTime` (medio tiempo), `Integral` (salario integral); integral sólo con tipo Empleado (llamada del spec: el salario integral es de un trabajador con contrato laboral ordinario, CST art. 132) |
| PDF firmado | opcional (sesión de subida de Storage) |
| estado | `Active` (vigente), `Terminated` (terminado), `Converted` (convertido en otro), `Closed` (cerrado por desactivación) |
| contrato anterior | enlace cuando nació de una conversión (FI-EMP-013) |
| terminación | motivo, fecha y notas (ver Terminar) |

Un empleado tiene **a lo sumo un** contrato vigente. Término fijo: la duración total desde el inicio, prórrogas incluidas,
no pasa de 4 años (Ley 2466/2025, FI-EMP-009).

### Historiales con fecha de vigencia (FI-EMP-011, 012)

Todo cambio después del alta es un registro nuevo con fecha desde la que rige; nada se sobreescribe salvo «Editar» con
confirmación. Un registro de cargo y lugar de trabajo también se edita (con confirmación) y se elimina, mientras no haya regido en
una nómina `Approved` o `Closed`: ver [`2026-11-26-hr-editar-eliminar-cargo-lugar-design.md`](2026-11-26-hr-editar-eliminar-cargo-lugar-design.md).
Cada registro puede llevar su PDF firmado (el otrosí en papel).

**Salario.** Cada registro: monto, fecha de vigencia, teletrabajador (sí/no).
- *Crear*: un registro nuevo desde una fecha posterior al último registro. Es lo que se usa para un aumento o una baja.
- *Editar*: reescribe el monto o la marca de teletrabajador de un registro existente, conservando su fecha. Exige que la
  petición confirme explícitamente la reescritura; sin esa confirmación, se rechaza con un código que la pide. Nunca se usa
  para un aumento (FI-EMP-011).
- Piso del salario integral: el monto de un salario integral es ≥ el piso vigente a la fecha del registro (13 SMMLV,
  FI-EMP-010).
- Salario bajo el SMMLV vigente a la fecha del registro: permitido sin más en Aprendiz (las tres), Estudiante (las dos),
  Contratista y Tiempo parcial (la lista exacta de FI-EMP-018); en los demás tipos exige confirmación explícita con advertencia
  (FI-EMP-018, «warn, not silently allow»).

**Cargo y lugar de trabajo.** Cada registro: cargo, área, sede, centro de trabajo y fecha de vigencia. Si la sede o el
centro de trabajo cambian respecto del registro anterior, la fecha debe ser el día 1 de un mes (restricción de PILA,
FI-EMP-012). Un cambio sólo de cargo o de área puede ir en cualquier fecha. El centro de trabajo de todo registro (alta,
conversión, reincorporación o cambio) debe pertenecer a una sede de la empresa del contrato.

**Vigente.** El backend entrega el salario, el tipo de salario, la marca de teletrabajador, el cargo, el área, la sede y el
centro de trabajo vigentes **hoy en la zona horaria del tenant**: el último registro con fecha ≤ hoy. Un registro con fecha
futura aparece como programado. Los historiales van del más reciente al más viejo.

### Prórroga y conversión a indefinido (FI-EMP-014)

- **Prorrogar** sólo aplica a término fijo: nueva fecha de fin posterior a la vigente. Se rechaza si la duración total
  desde el inicio pasa de 4 años (se ofrece convertir a indefinido), y si ya hubo tres prórrogas de menos de un año, la
  siguiente debe ser de al menos un año (Ley 2466/2025).
- **Convertir a indefinido**: cambia la duración, conserva la fecha de inicio y quita la fecha de fin.
- **Aviso de vencimiento**: un término fijo que vence en 30 días o menos, sin prórroga ni terminación registradas, se
  marca «por vencer». Lo calcula el backend al leer, en el día del tenant; la lista lo filtra.
- Cada prórroga queda en el historial del contrato, con su PDF opcional.

### Conversión de tipo de contrato (FI-EMP-013)

Cambiar el tipo (aprendiz lectiva → productiva, estudiante → empleado, contratista → laboral, etc.) **no** modifica el
contrato: el vigente termina como `Converted` el día anterior al inicio del nuevo, y se crea un contrato nuevo enlazado al
anterior, con sus propios registros iniciales de salario y de cargo y lugar de trabajo. El inicio del nuevo es posterior al
fin del anterior. El contrato nuevo conserva la empresa empleadora del anterior: convertir no cambia de empresa.

### Terminar

Motivos (modos de terminación del Código Sustantivo del Trabajo, arts. 61 y 62; Aleluya pide «justa causa sí/no»,
FI-SET-001):

| Motivo | Justa causa |
| --- | --- |
| `Resignation` (renuncia) | — |
| `DismissalWithJustCause` (despido con justa causa) | sí |
| `DismissalWithoutJustCause` (despido sin justa causa) | no |
| `TermExpiry` (vencimiento del plazo) | — |
| `WorkCompletion` (terminación de la obra o labor) | — |
| `MutualAgreement` (mutuo acuerdo) | — |
| `ProbationPeriod` (terminación en período de prueba) | — |
| `CompanyClosure` (cierre o liquidación de la empresa) | — |
| `Death` (fallecimiento) | — |

Fecha de terminación ≥ inicio del contrato; notas opcionales. El contrato pasa a `Terminated` y el empleado queda «Retirado».

### Ciclo de vida del empleado (FI-EMP-005)

Estado derivado por el backend:

| Estado | Cuándo |
| --- | --- |
| `Active` (activo) | tiene contrato vigente |
| `Retired` (retirado) | su último contrato está terminado |
| `Deactivated` (desactivado) | salió sin liquidación: traslado o datos incompletos |

- **Desactivar** (sin liquidación): cierra el contrato vigente como `Closed` en la fecha dada.
- **Reincorporar** (desde retirado o desactivado): crea un contrato nuevo con sus registros iniciales; el historial anterior
  se conserva. El contrato nuevo puede ser de otra empresa: es la forma de pasar a alguien de una empresa a otra.
- **Eliminar**: sólo si el empleado tiene un único contrato, sin prórrogas, conversiones ni registros posteriores al alta
  (creado por error). Si no, la única salida es desactivar.

En v2/docs el estado se llama «Liquidado»; en la fase 1 es «Retirado» porque todavía no hay liquidación. La fase 2 lo
convierte en «Liquidado» al liquidar.

### Alta (FI-EMP-001)

Crear un empleado exige el contrato completo: datos personales, contrato con su empresa empleadora, salario inicial, cargo
y lugar de trabajo iniciales, seguridad social y banco (opcional). Exige la configuración mínima (ver «Configuración de la
organización»).

### Valores legales (`legal-parameters`)

Tabla con fecha de vigencia: el SMMLV y el piso del salario integral, cargados de 2024 a 2026; no se escriben en el código.
Se resuelve el valor vigente a la fecha que se evalúa, nunca «el actual». Sin pantalla en la fase 1: la carga anual la hace
el owner por migración, y la pantalla de operador llega con la fase 2.

| Año (desde el 1 de enero) | SMMLV | Piso del salario integral (13 SMMLV) |
| --- | --- | --- |
| 2024 | 1.300.000 | 16.900.000 |
| 2025 | 1.423.500 | 18.505.500 |
| 2026 | 1.750.905 | 22.761.765 |

### Historial de acciones (FI-EMP-024)

Por empleado: quién, cuándo, qué acción y el antes y el después de datos personales (el país incluido, no sensible),
salario, cargo y lugar de trabajo, contrato y datos bancarios. Se ve en la ficha. Los cambios de salario y banco sólo con `hr.contract.read`.

### Importación por Excel (FI-EMP-002)

- Plantilla con listas desplegables armadas desde la configuración del tenant (empresas activas, centros de trabajo, áreas,
  cargos), con la lista de países y con las etiquetas de los enums. Bloqueada (422) mientras falte la configuración mínima.
- Columna `PAÍS` justo después de `NÚMERO DE DOCUMENTO`: código alfa-2 o nombre del país en español; vacía ⇒ país del
  tenant. Columna `EMPRESA` justo antes de `TIPO DE CONTRATO`, escrita `"<nombre> - <NIT>"`; vacía ⇒ la única empresa
  activa si hay una sola, y error si hay varias. El centro de trabajo se busca dentro de la empresa de la fila. 36 columnas.
- Vista previa sin escribir; confirmar reenvía el mismo archivo. **Todo o nada.**
- Cada fila crea empleado, contrato y registros iniciales. Si trae fecha de retiro pasada, el contrato queda terminado con
  ese motivo.
- Errores por fila con su campo; un documento repetido en el archivo es error de las dos filas; un documento ya existente
  es error, no actualización. Un salario bajo el SMMLV en un tipo que exige confirmación es un **aviso** de la fila: se ve en
  la vista previa y confirmar la importación lo acepta.
- Tope: 1000 filas.

### Contrato HTTP

Las rutas, cuerpos, respuestas y códigos de error están en el plan de backend, sección «Contrato HTTP». Convenciones: prefijo
`/api/v1/tenants/{tenantId}/hr`, una sola versión (la del empleado) en todo `If-Match`, toda escritura sobre un empleado
responde la ficha entera, sin `hr.contract.read` los campos de contrato, salario y banco llegan en `null`, y las empresas
activas llegan en la configuración de RRHH (el frontend nunca llama la API de Empresas).

## Lo que queda al implementador

Nombres de tablas, índices, handlers y DTOs internos; detalle de la sesión de subida (infraestructura de Storage); tamaño de
página por defecto; orden de las listas de configuración.

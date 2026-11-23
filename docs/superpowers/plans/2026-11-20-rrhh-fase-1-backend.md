# Recursos humanos, fase 1 — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **El `CLAUDE.md` del workspace manda sobre el proceso del skill** («Presupuesto de pruebas y ejecución de planes»): las
> tareas se ejecutan en **cuatro bloques**, un implementador (Sonnet) por bloque, una revisión por bloque y la revisión fuerte
> una sola vez al final. Cada tarea conserva su commit.

**Goal:** que un tenant con el módulo `hr` configure su organización (sedes, centros de trabajo, áreas y cargos), registre a
su gente con el contrato completo, lleve con fecha de vigencia el salario y el cargo y lugar de trabajo, prorrogue, convierta
y termine contratos con las reglas de la Ley 2466/2025, y cargue su planta desde un Excel, con permisos que separan la ficha
de la compensación. Sin calcular nómina.

**Architecture:** módulo nuevo `Hr` (esquema `hr`, cuatro capas, calcado de `Loans`). Dos familias de agregados:
la configuración (`Site`, `Workplace`, `Area`, `Position`, cada uno con su versión) y `Employee`, que contiene sus contratos
(`EmploymentContract`) y, dentro de cada contrato, los registros con fecha de vigencia (`SalaryRecord`, `AssignmentRecord`) y
las prórrogas (`ContractRenewal`), con **una sola versión**. Lo vigente, lo programado, el aviso de vencimiento y el estado
del empleado los calcula el dominio con el «hoy» del tenant (`ITenantClock`). SMMLV y piso del salario integral salen de una
tabla con fecha de vigencia (`hr.legal_parameters`, dato de plataforma sin `tenant_id`, como `catalog.tax_unit_values`).
Membresías, Geografía (ciudades y el catálogo de países que esta fase le suma a Geography), teléfonos, empresas
(`IHrCompanyLookup`, calco de `IPosCompanyLookup`, para leer y validar) y archivos entran por puertos `IHr*` con adaptadores
en `src/Bootstrapper/Hr*.cs`: Hr nunca referencia `Modules.Companies` ni `Modules.Geography`. El módulo `hr` depende de
`companies` en `TenantModuleKeys`. `hr.sites.company_id` y `hr.employment_contracts.company_id` tienen **FK real** a
`companies.companies(id)` con `ON DELETE RESTRICT`, agregada a mano en la migración como la de POS
(`20261007110447_InitialPos.cs:220-232`): una empresa con sedes o contratos de RRHH no se puede borrar (422
`companies.company.in_use`, D-11). El país por defecto del empleado sale de `ITenantDirectory.GetRegionalSettingsAsync`
(la misma lectura de `/auth/me`). Los PDFs
son archivos de dueño de sistema (`FileOwnerType.HrDocument`) sobre `IStorageSystemUploads`. El historial de acciones es una
tabla propia (`hr.employee_activity`) con antes y después; `audit.entries` sólo guarda nombres de campos. La importación calca
la de Contratos de servicio, pero es **todo o nada**.

**Tech Stack:** .NET 10, EF Core + Npgsql, minimal APIs, FluentValidation 12.1.1, ClosedXML 0.105.1, xUnit v3,
Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md` (este repo; léelo entero antes de empezar). Pantallas:
`qep-frontend/docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md`. **El contrato HTTP es la sección «Contrato HTTP» de
este archivo**: las tareas lo citan y no lo repiten.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\rrhh-fase-1`, rama
  `feature/rrhh-fase-1`. Antes de **cada** commit: `git branch --show-current` dice `feature/rrhh-fase-1`. Nunca se toca
  `qep-backend` (está en `main`). Commits: conventional commits, **sin atribución de IA**.
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`). `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- **lulo-erp no es referencia de implementación.** De ahí sólo salió la lógica de negocio, que ya está en el spec
  (`lulo-erp/v2/docs`). No se abre, no se copia ni se calca nada de su código, modelo, nombres, estructura ni patrones, ni de
  v1 ni de v2; los patrones son los de este repo (`Modules.Loans`, `Modules.ServiceContracts`).
- Mensajes al usuario (`message` de la importación, `errors` de validación) en español neutro, tuteando. Códigos, rutas,
  enums y permisos no se traducen. Comentarios en inglés que explican el porqué.
- Códigos de dominio con prefijo `hr.`, sólo los del «Contrato HTTP». Ningún status HTTP se arma en un handler: 404 es
  `ResourceNotFoundException`, 409 `ResourceConflictException`, 412 `RequestConcurrencyException("concurrency.conflict", …)`,
  428 `PreconditionRequiredException("precondition.if_match_required", …)`, 422 `HrDomainException` o `ValidationException`.
  `src/Api/ApiExceptionHandler.cs` **no cambia**: mapea por tipo de excepción, no por código.
- Las claves de `errors` van en camelCase y con punto para lo anidado (`firstName`, `contract.endDate`,
  `payment.bankAccountNumber`): todo validador de Hr hereda `HrValidator<T>` (Tarea 2).
- Doble capa de tenant (CLAUDE.md del repo): la ruta lleva `{tenantId:guid}` y la política exige el permiso; el handler
  revalida con `HrAuthorization.EnsureAsync` **antes** del repositorio y responde 403 `authorization.denied`, nunca 404. Todo
  método de repositorio recibe `tenantId`.
- **Cada `manage` exige también su `read`** (D-1): `HrAuthorization` recibe la lista completa de permisos de cada caso de uso
  (columna «Permiso» de la tabla de rutas).
- Auditoría atómica en el handler (`IHrAuditPublisher`, calco de `ILoansAuditPublisher`): `audit.entries` guarda **nombres**
  de campos, nunca valores. El antes y el después viven en `hr.employee_activity`, que se escribe en la misma transacción. Hr
  no publica eventos: no hay outbox.
- Dinero `numeric(18,2)`, monto > 0 con ≤ 2 decimales y < 10 000 000 000 000 000. Los montos son pesos colombianos y no hay
  campo de moneda (D-3).
- **Ningún valor legal se escribe en el código** (spec, «Valores legales»): SMMLV y piso integral se resuelven de
  `hr.legal_parameters` a la fecha que se evalúa. Las pruebas unitarias arman su propia tabla con los valores sembrados.
- TDD con evidencia literal de RED y GREEN. **Si el fallo esperado es de compilación, la evidencia es la salida del build y no
  se levantan contenedores.** La integración se filtra por nombre de prueba mientras se itera
  (`--filter "FullyQualifiedName~Clase.Prueba"`); la clase completa corre una vez al cierre del bloque. Toda corrida de
  `dotnet test` va por proyecto, **en primer plano**, con `-- xUnit.MaxParallelThreads=4`. Antes de una corrida de integración:
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` — si hay una ajena, se espera; no se detiene. Nunca dos
  corridas de integración a la vez. La suite completa sólo en la Tarea 11, limitada a los proyectos tocados.
- Proyectos nuevos: `dotnet restore Backend.slnx` genera sus `packages.lock.json` y actualiza los de quien los referencia
  (`Api`, `Bootstrapper`, `ArchitectureTests`, los `*.IntegrationTests` que referencian `Api.csproj`). Todos se commitean en la
  tarea que agrega el proyecto: el `Dockerfile` restaura con `--locked-mode`.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- `docs/estado.md` **no se toca en la rama** (CLAUDE.md del repo): se actualiza en `develop` al mergear.

## Review Focus

1. **El día del tenant, no el UTC**: a las 23:30 de Bogotá del 2026-11-30 (04:30Z del 1 de diciembre) un salario con
   `effectiveDate` 2026-12-01 sigue `scheduled` y no mueve `current.salary`. Prueba: Tarea 9,
   `ASalaryEffectiveTomorrowInTheTenantZoneIsStillScheduled`.
2. **Quien tiene `hr.employee.manage` sin `hr.contract.read` edita la ficha** → la respuesta del `PUT` llega enmascarada
   (`canReadContract: false`, `employee.payment: null`, `contract: null`, `previousContracts: []`), igual que el `GET`.
   Prueba: Tarea 8, `AWriteByAManagerWithoutContractReadAnswersMasked`.
3. **Regla del día 1**: cambiar de centro de trabajo con fecha 2026-12-15 → 422 `hr.assignment.site_change_not_first_of_month`;
   cambiar sólo el cargo el mismo día → 200. Prueba: Tarea 9, `AWorkplaceChangeMidMonthIs422AndAPositionChangeIsNot`.
4. **Ley 2466/2025 bloquea, no avisa**: tras tres prórrogas cortas la cuarta de seis meses → 422
   `hr.contract.renewal_minimum_one_year`; una que pase de 2030-01-14 (inicio 2026-01-15) → 422
   `hr.contract.fixed_term_cap_exceeded`. Prueba: Tarea 9, `TheFourthShortRenewalNeedsOneYearAndTheCapBlocksPastFourYears`.
5. **Alguien crea a mano un empleado con un documento del Excel entre la vista previa y la confirmación** → la confirmación
   responde 422 `hr.import.rows_invalid` y no escribe **ningún** empleado. Prueba: Tarea 10,
   `ADocumentCreatedBetweenPreviewAndConfirmWritesNothing`.
6. **La empresa del contrato es fija y manda sobre el lugar**: con dos empresas, un cambio de cargo y lugar a un centro de
   una sede de la otra empresa ⇒ 422 `errors.workplaceId` con `hr.assignment.workplace_other_company`; una conversión con
   `companyId` de la otra empresa ⇒ 422 `hr.contract.company_change_not_allowed`. Pruebas: Tarea 9,
   `AWorkplaceOfAnotherCompanyIs422` y `AConversionCannotChangeTheCompany`.

## Decisiones pendientes del owner

Lo que el spec deja abierto y el código no resuelve. El plan sigue con la opción marcada **(interina)**; cambiarla es un
cambio acotado.

- **D-1. Un `manage` sin su `read`.** Hoy ningún permiso ata a otro (backend ni editor de roles). **(interina)** el backend
  exige el `read` junto con el `manage` en cada handler (`hr.employee.read` acompaña a `hr.settings.manage`); un rol con sólo
  `manage` recibe 403. El editor de roles no cambia en este trabajo.
- **D-2. Roles por defecto.** Resuelta por el spec: los cinco permisos van al rol `admin` (el dueño del tenant es una membresía
  `admin`; no hay rol `owner`).
- **D-3. País y moneda.** La fase 1 es nómina colombiana: montos en pesos, SMMLV colombiano. **(interina)** no hay campo de
  moneda ni se bloquea el módulo por el país del tenant; un tenant de otro país que lo prenda ve las reglas colombianas. El
  país del tenant sólo es el valor por defecto de la nacionalidad del empleado (`EmployeeRequest.country`).
- **D-4. PDFs huérfanos.** Un PDF subido y nunca adjuntado queda en Storage; no hay barrido para `HrDocument`. **(interina)**
  sin barrido en la fase 1.
- **D-5. Sinónimos de ciudad en la importación.** **(interina)** la columna `CIUDAD` se compara contra `City.Name` y
  `City.DivipolaName`, no contra los sinónimos de Geografía.
- **D-6. Tipo de salario por contrato.** El spec lo pone en el contrato. **(interina)** es fijo durante el contrato; pasar de
  mensual a integral es una conversión de tipo (`…/conversion`), que crea un contrato nuevo.
- **D-7. Cargo y lugar no se editan.** El spec sólo da «Editar» al salario. **(interina)** un registro de cargo y lugar no se
  reescribe; un error del alta se corrige eliminando al empleado (si cumple la regla) o con un registro nuevo.
- **D-8. Un año sin SMMLV cargado.** **(interina)** un valor rige desde su fecha hasta el siguiente: en 2027, sin fila 2027,
  rige el de 2026. Antes de 2024 no hay valor y la escritura responde 422 `hr.legal_parameter.missing`.
- **D-9. Eliminar es borrar.** **(interina)** «Eliminar» borra el empleado, sus contratos y su historial de acciones
  (`audit.entries` conserva la entrada `hr.employee.deleted`); el documento queda libre.

## Decisiones tomadas por el owner (2026-11-20)

- **D-10. Catálogo de países en Geography.** El backend no tenía catálogo de países: `Companies` valida sólo la forma
  (`CompanyWriteRules.IsValidCountry`), igual que `Tenant` (`Tenant.cs:472`) y `Wholesale` (`DistributorWriteRules.IsCountryCode`),
  y Geography sólo tenía departamentos y ciudades de Colombia. **Decidido:** Geography suma un catálogo de países de sólo
  lectura (código ISO 3166-1 alfa-2 + nombre en español), sembrado con **exactamente** los 41 de
  `qep-frontend/src/features/customers/utils/countries.ts` (`COUNTRIES`, mismos códigos y nombres), expuesto en
  `GET /api/v1/countries` (Tarea 1). Es tabla (`geography.countries`) sembrada en cada arranque desde un JSON embebido por
  `GeographySeeder`, y no una clase estática, por consistencia con departamentos y ciudades: todo dato de referencia de
  Geography vive en tablas que el seeder reconcilia, y una tabla deja que un módulo apunte después con FK, como
  `companies → geography` con las ciudades. Hr valida `country` y lee la columna `PAÍS` por su puerto a Geography
  (`IHrGeographyLookup.ListCountriesAsync`, el mismo puerto y adaptador de las ciudades); la hoja `Valores válidos` lista
  esos nombres. **Fuera de alcance:** Companies, Tenancy y Wholesale siguen validando sólo la forma; pasarlos al catálogo es
  otro trabajo.
- **D-11. Una empresa con RRHH no se borra.** `hr.sites.company_id` y `hr.employment_contracts.company_id` llevan FK real a
  `companies.companies(id)` con `ON DELETE RESTRICT`, agregada a mano en la migración de la Tarea 5 con el patrón de POS
  (`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/20261007110447_InitialPos.cs:220-232` y el comentario de
  `PosDbContext.cs:48`: EF no modela relaciones entre `DbContext` de módulos distintos). `CompaniesUnitOfWork` ya traduce la
  violación (SQLSTATE 23001 de `RESTRICT`, o 23503) a `CompaniesDomainException("companies.company.in_use")` (422) cuando lo
  que se guardaba era el `DELETE` de una `Company` (ver el doc-comment de `Modules.Companies.Application/DeleteCompany.cs`).
  No hay fallback para una empresa borrada: no puede pasar. Companies no cambia.
- **D-12. Empresa desactivada.** Una empresa desactivada sólo deja de ofrecerse para **vínculos nuevos**: el alta y la
  reincorporación exigen empresa activa (422 `errors.<prefijo>.contract.companyId`), y crear una sede también (422
  `errors.companyId`). Todo lo de un contrato existente sigue funcionando con la empresa inactiva: salarios, cargo y lugar,
  prórrogas, conversión a indefinido, terminación **y la conversión de tipo**, que conserva la empresa y no exige que esté
  activa (es el mismo empleador). Editar una sede existente y crear un centro en ella también siguen.

## Contrato HTTP

Esta sección es **la fuente** del contrato para los dos repos: el plan de frontend
(`qep-frontend/docs/superpowers/plans/2026-11-20-rrhh-fase-1-frontend.md`) la cita y no la repite.
Lo que el spec dejaba abierto se decide acá y se marca **(llamada del plan)**.

### Convenciones

- Prefijo `/api/v1/tenants/{tenantId:guid}/hr` (como `LoansEndpoints`), header `X-Tenant-Id`.
- JSON camelCase. Enums como texto (nombre del miembro, exacto; un número no es un enum). Opcionales **siempre presentes**
  como `null`, nunca ausentes. Fechas `yyyy-MM-dd` (`DateOnly`); instantes ISO-8601 (`DateTimeOffset`). Dinero `decimal`,
  ≤ 2 decimales, pesos colombianos.
- `version` es `long`. `If-Match: "<version>"` (entre comillas, como el resto de módulos). Sin `If-Match` donde se exige ⇒ 428;
  versión vieja ⇒ 412.
- **Una sola versión por empleado: `employee.version`.** Contratos, registros y prórrogas no tienen versión propia; toda
  escritura sobre el empleado la sube. `If-Match` se exige **sólo** en las escrituras que reescriben lo que el usuario vio
  (`PUT` de ficha, de pago y de salario) **(llamada del plan)**; en las demás no se manda, y una escritura concurrente igual
  responde 412 porque `version` es token de concurrencia.
- Cada entidad de configuración tiene su propia `version`; su `PUT` exige `If-Match` con ella.
- **Toda escritura sobre un empleado responde `EmployeeDetailDto` entero**: 201 en `POST /employees`, 200 en el resto. La
  única excepción es `DELETE /employees/{id}` (204, ya no hay ficha). **(llamada del plan)**
- **Enmascarado:** sin `hr.contract.read`, `canReadContract` es `false`, `employee.payment` es `null`, `contract` es `null`,
  `previousContracts` es `[]`, y en la lista los campos de contrato van en `null`. Vale también para la respuesta de toda
  escritura: quien llama decide `canReadContract` con su propio permiso.
- «Hoy» es siempre el día del tenant (`TenantCalendar.Today`), nunca el del servidor ni el UTC.
- Validación de forma: FluentValidation ⇒ 422 con mapa `errors`. Reglas que dependen de lo cargado (configuración archivada,
  fecha posterior al último registro, ciudad inexistente): 422 con `errors` en el campo si el contrato lo dice así, o con
  `code` `hr.*` si la tabla de rutas da un código.

### Enums y etiquetas

Las etiquetas son las que muestra la UI **y** las que acepta la importación (comparación sin mayúsculas, tildes ni espacios
de más). El backend las define una vez en `HrLabels` (Application) y el frontend las copia literal.

| Enum | Valor → etiqueta |
| --- | --- |
| `HrDocumentType` | `CC` Cédula de ciudadanía · `CE` Cédula de extranjería · `PEP` Permiso especial de permanencia · `PPT` Permiso por protección temporal · `PA` Pasaporte · `CD` Carné diplomático · `SC` Salvoconducto |
| `ContributorSubtype` | `Standard` Estándar · `Pensioner` Pensionado · `NotRequiredByAge` No obligado por edad · `PensionRequirementsMet` Cumplió requisitos de pensión · `SubstituteIndemnity` Recibió indemnización sustitutiva |
| `PaymentMethod` | `BankTransfer` Transferencia bancaria · `Other` Otro |
| `BankAccountType` | `Savings` Ahorros · `Checking` Corriente |
| `RestDay` | `Monday` Lunes · `Tuesday` Martes · `Wednesday` Miércoles · `Thursday` Jueves · `Friday` Viernes · `Saturday` Sábado · `Sunday` Domingo |
| `ContractType` | `Employee` Empleado · `Contractor` Contratista · `PartTime` Tiempo parcial · `SenaApprenticeTeaching` Aprendiz SENA lectiva · `SenaApprenticeProductive` Aprendiz SENA productiva · `SenaApprenticeDual` Aprendiz SENA dual · `UniversityIntern` Practicante universitario · `StudentLaw789` Estudiante Ley 789 · `StudentDecree055` Estudiante Decreto 055 |
| `ContractDuration` | `FixedTerm` Término fijo · `Indefinite` Término indefinido · `WorkOrLabor` Obra o labor |
| `SalaryType` | `Monthly` Mensual · `HalfTime` Medio tiempo · `Integral` Salario integral |
| `ContractStatus` | `Active` Vigente · `Terminated` Terminado · `Converted` Convertido · `Closed` Cerrado |
| `TerminationReason` | `Resignation` Renuncia · `DismissalWithJustCause` Despido con justa causa · `DismissalWithoutJustCause` Despido sin justa causa · `TermExpiry` Vencimiento del plazo · `WorkCompletion` Terminación de la obra o labor · `MutualAgreement` Mutuo acuerdo · `ProbationPeriod` Terminación en período de prueba · `CompanyClosure` Cierre o liquidación de la empresa · `Death` Fallecimiento |
| `EmployeeStatus` | `Active` Activo · `Retired` Retirado · `Deactivated` Desactivado |
| `ArlRiskClass` | `I` Clase I · `II` Clase II · `III` Clase III · `IV` Clase IV · `V` Clase V |
| `HrActivityAction` | `EmployeeCreated` Alta · `EmployeeImported` Alta por importación · `PersonalDataUpdated` Datos personales · `PaymentUpdated` Datos de pago · `SalaryCreated` Salario nuevo · `SalaryRewritten` Salario reescrito · `AssignmentCreated` Cargo y lugar de trabajo · `ContractRenewed` Prórroga · `ContractConvertedToIndefinite` Conversión a término indefinido · `ContractTypeChanged` Cambio de tipo de contrato · `ContractTerminated` Terminación · `EmployeeDeactivated` Desactivación · `EmployeeReincorporated` Reincorporación · `DocumentAttached` Documento |
| `HrActivityCategory` | `Personal` · `Lifecycle` · `Payment` · `Salary` · `Assignment` · `Contract` (sin etiqueta: sólo decide el enmascarado) |

`TerminationReason` lleva `justCause`: `true` en `DismissalWithJustCause`, `false` en `DismissalWithoutJustCause`, `null`
en el resto (spec, «Terminar»). En la importación los booleanos son `Sí`/`No` (también `si`).

`RestDay` es `System.DayOfWeek` por nombre. Los tipos exentos del mínimo (spec, FI-EMP-018) son `Contractor`, `PartTime`, los
tres `SenaApprentice*`, `StudentLaw789` y `StudentDecree055`; `Employee` y `UniversityIntern` exigen confirmación.

### DTOs (respuesta)

```jsonc
// SettingRefDto — referencia a configuración; archived para pintarla «(archivada)» en un historial
{ "id": "uuid", "name": "Bodega norte", "archived": false }

// MunicipalityRefDto — misma forma que el resto de módulos
{ "id": "uuid", "name": "Medellín", "departmentId": "uuid", "departmentName": "Antioquia" }

// MemberRefDto — mismo criterio que LoanMemberDto
{ "memberId": "uuid", "displayName": "string", "isActive": true }

// FileRefDto
{ "fileId": "uuid", "fileName": "contrato.pdf" }

// CompanyRefDto — empresa del módulo Companies (razón social propia del tenant, con NIT), leída por IHrCompanyLookup
{ "id": "uuid", "name": "Comercializadora QEP S.A.S.", "taxId": "900123456", "country": "CO",   // ISO alfa-2 (2026-11-21); "CO" si la empresa no lo tiene
  "isActive": true }                              // false si se desactivó después en Empresas (una con RRHH no se borra: D-11)

// SiteDto (sede)
{ "id": "uuid", "company": CompanyRefDto,          // fija desde que se crea
  "name": "Sede principal", "address": "Cra 1 # 2-3" | null, "city": MunicipalityRefDto | null,
  "compensationFund": "<nombre de la lista fija>" | null, "archived": false,
  "inUse": true,                      // la usa un centro de trabajo o un registro de cargo y lugar: no se puede eliminar (llamada del plan)
  "version": 3 }

// WorkplaceDto (centro de trabajo) — no cambia: hereda la empresa de su sede (site.id ⇒ SiteDto.company)
{ "id": "uuid", "name": "Bodega", "site": SettingRefDto, "ciiuCode": "4711" | null, "riskClass": "III",
  "highRiskPension": false, "archived": false, "inUse": false, "version": 1 }

// AreaDto y PositionDto (área y cargo)
{ "id": "uuid", "name": "Logística", "archived": false, "inUse": false, "version": 1 }

// HrSettingsDto — GET /hr/settings, una sola llamada para las pantallas (llamada del plan)
{ "companies": [CompanyRefDto],      // empresas ACTIVAS del tenant, por nombre: el frontend las lee de acá, nunca de Companies
  "sites": [SiteDto], "workplaces": [WorkplaceDto], "areas": [AreaDto], "positions": [PositionDto],
  "setupComplete": true }            // hay al menos una empresa activa con una sede no archivada que tiene un centro no
                                     // archivado, y al menos un cargo no archivado

// PaymentDto — null sin hr.contract.read o si nunca se cargó
{ "paymentMethod": "BankTransfer", "bankName": "Bancolombia" | null, "bankAccountType": "Savings" | null,
  "bankAccountNumber": "12345678901" | null }       // con Other los tres son null

// EmployeeDto
{
  "id": "uuid",
  "firstName": "Ana", "lastName": "Pérez",
  "documentType": "CC", "documentNumber": "1020304050",
  "country": "CO",                                // nacionalidad, ISO 3166-1 alfa-2; siempre presente, nunca null
  "birthDate": "1990-04-12" | null,
  "phone": "+573001234567" | null,               // E.164
  "personalEmail": null, "workEmail": null, "address": null,
  "city": MunicipalityRefDto | null,
  "member": MemberRefDto | null,
  "healthProvider": null, "pensionFund": null, "severanceFund": null,
  "occupationalRiskProvider": null,
  "contributorSubtype": "Standard",
  "foreignPensionExempt": false,
  "restDays": ["Sunday"],                         // orden lunes→domingo
  "payment": PaymentDto | null,                   // null sin hr.contract.read
  "status": "Active",
  "version": 7,
  "createdAt": "…", "updatedAt": "…"
}

// SalaryRecordDto — historial del contrato, effectiveDate desc
{ "id": "uuid", "effectiveDate": "2026-12-01", "amount": 2800000, "teleworker": false,
  "belowMinimumAcknowledged": false,             // true si quedó bajo el SMMLV de su fecha con confirmación
  "document": FileRefDto | null,
  "scheduled": true,                              // effectiveDate > hoy del tenant
  "current": false,                               // es el registro vigente hoy
  "createdAt": "…", "updatedAt": "…" }            // updatedAt cambia con «Editar»

// AssignmentRecordDto — cargo y lugar de trabajo, effectiveDate desc
{ "id": "uuid", "effectiveDate": "2026-12-01",
  "position": SettingRefDto, "area": SettingRefDto | null, "site": SettingRefDto, "workplace": SettingRefDto,
  "document": FileRefDto | null, "scheduled": false, "current": true, "createdAt": "…" }

// RenewalDto — prórrogas, createdAt desc
{ "id": "uuid", "previousEndDate": "2026-07-14", "newEndDate": "2027-01-14",
  "short": true,                                  // newEndDate < previousEndDate + 1 año (cuenta para la regla de las tres)
  "document": FileRefDto | null, "createdAt": "…" }

// ContractCurrentDto — lo vigente hoy: el último registro con effectiveDate ≤ hoy; si el contrato aún no empieza,
// el primero (llamada del plan)
{ "salary": 2500000, "teleworker": false, "salaryType": "Monthly",
  "position": SettingRefDto, "area": SettingRefDto | null, "site": SettingRefDto, "workplace": SettingRefDto }

// TerminationDto
{ "reason": "Resignation", "justCause": null, "date": "2026-12-31", "notes": null }

// ContractDto
{
  "id": "uuid",
  "company": CompanyRefDto,                       // empresa empleadora, fija; siempre presente (isActive dice si se desactivó)
  "contractType": "Employee", "duration": "FixedTerm", "salaryType": "Monthly",
  "startDate": "2026-01-15",
  "endDate": "2027-01-14" | null,                 // fin vigente (el de la última prórroga); estimado en WorkOrLabor; null en Indefinite
  "status": "Active",
  "endedOn": null,                                // último día de un contrato Terminated, Converted o Closed
  "convertedToIndefiniteOn": null,                // día del tenant en que pasó de término fijo a indefinido
  "previousContractId": "uuid" | null,            // nació de una conversión de tipo
  "document": FileRefDto | null,
  "termination": TerminationDto | null,
  "started": true,                                // startDate ≤ hoy
  "current": ContractCurrentDto,
  "renewalNoticeDue": false,                      // ver «Aviso de vencimiento»
  "daysUntilEnd": 55 | null,                      // endDate − hoy (negativo si ya pasó); null sin endDate o si no está Active (llamada del plan)
  "salaries": [SalaryRecordDto],
  "assignments": [AssignmentRecordDto],
  "renewals": [RenewalDto]
}

// EmployeeDetailDto — respuesta de GET y de toda escritura sobre el empleado
{
  "employee": EmployeeDto,
  "canReadContract": true,
  "canDelete": false,                             // regla de eliminar del spec (llamada del plan: el backend la calcula)
  "contract": ContractDto | null,                 // el Active; null si no hay o sin hr.contract.read
  "previousContracts": [ContractDto]              // Terminated, Converted y Closed, startDate desc; [] sin hr.contract.read
}

// EmployeeListItemDto
{
  "id": "uuid", "firstName": "Ana", "lastName": "Pérez",
  "documentType": "CC", "documentNumber": "1020304050",
  "status": "Active",                             // sin country: la lista no lo lleva
  // los siete siguientes salen del contrato vigente: null sin contrato vigente o sin hr.contract.read
  "company": "Comercializadora QEP S.A.S." | null,   // nombre de la empresa empleadora
  "position": "Analista" | null, "site": "Sede principal" | null,
  "contractType": "Employee" | null, "duration": "FixedTerm" | null,
  "endDate": "2026-12-10" | null, "renewalNoticeDue": true | null
}

// EmployeePageDto
{ "items": [EmployeeListItemDto], "total": 42, "page": 1, "pageSize": 25,
  "summary": { "active": 30, "retired": 8, "deactivated": 4,
               "expiringSoon": 3 | null } }        // respetan search, ignoran status, expiringSoon y companyId; expiringSoon null sin hr.contract.read

// ActivityChangeDto
{ "field": "amount", "before": 2500000 | "texto" | true | null, "after": 2800000 | null }
// before/after son escalares: montos como número, fechas yyyy-MM-dd, enums por nombre, referencias por el NOMBRE que
// tenían al escribirse (llamada del plan)

// ActivityEntryDto
{ "id": "uuid", "occurredAt": "…",
  "actor": { "subjectId": "uuid", "displayName": "Laura Gómez" },   // nombre de la membresía al escribir; "Sin nombre" si no tenía
  "action": "SalaryCreated", "category": "Salary",
  "contractId": "uuid" | null,
  "changes": [ActivityChangeDto] }

// ActivityPageDto
{ "items": [ActivityEntryDto], "total": 12, "page": 1, "pageSize": 25 }

// SalaryRulesDto
{ "date": "2026-11-20", "smmlv": 1750905, "integralFloor": 22761765,
  "belowMinimumAllowedContractTypes": ["Contractor", "PartTime", "SenaApprenticeTeaching", "SenaApprenticeProductive",
                                       "SenaApprenticeDual", "StudentLaw789", "StudentDecree055"] }   // (llamada del plan)
```

**Aviso de vencimiento** (spec, «Prórroga»): `renewalNoticeDue` es `true` si el contrato es `FixedTerm`, está `Active` y
`endDate ≤ hoy + 30`. **(llamada del plan)** Incluye los que ya vencieron sin terminación registrada: en la fase 1 nada los
desactiva solo (FI-EMP-006 espera a la fase 2), y dejar de avisar justo cuando venció sería peor. `daysUntilEnd` dice cuál de
los dos casos es; el navegador no resta fechas.

**Historial de acciones enmascarado:** sin `hr.contract.read` la lista trae sólo las entradas de categoría `Personal` y
`Lifecycle`, y de éstas quita los cambios marcados sensibles al escribir (salario, banco, cargo, sede, contrato). Las de
`Payment`, `Salary`, `Assignment` y `Contract` no aparecen ni cuentan en `total`.

### Rutas

Permisos: la política de la ruta es el permiso **en negrita**; el handler revalida la lista completa (D-1).

| Método y ruta | Permiso | Cuerpo | Respuesta | Errores |
| --- | --- | --- | --- | --- |
| `GET /hr/settings` | **`hr.employee.read`** | — | 200 `HrSettingsDto` | — |
| `POST /hr/sites` | `hr.employee.read`, **`hr.settings.manage`** | `SiteRequest` | 201 `SiteDto` | 409 `hr.settings.name_taken` (por empresa), 422 `errors.companyId` (empresa inexistente o inactiva), 422 `errors` |
| `PUT /hr/sites/{id}` | ídem, `If-Match` | `SiteRequest` | 200 `SiteDto` | 404 `hr.site.not_found`, 409 `hr.settings.name_taken`, 422 `errors.companyId` si cambia (la empresa de una sede es fija), 412/428 |
| `POST /hr/sites/{id}/archive` | ídem | — | 200 `SiteDto` | 409 `hr.settings.already_archived`, 422 `hr.settings.site_has_active_workplaces` |
| `POST /hr/sites/{id}/unarchive` | ídem | — | 200 `SiteDto` | 409 `hr.settings.not_archived` |
| `DELETE /hr/sites/{id}` | ídem | — | 204 | 409 `hr.settings.in_use` |
| `POST /hr/workplaces` | ídem | `WorkplaceRequest` | 201 `WorkplaceDto` | 409 `hr.settings.name_taken` (por sede), 422 `hr.settings.site_archived`, 422 `errors.siteId` (no existe) |
| `PUT /hr/workplaces/{id}` | ídem, `If-Match` | `WorkplaceRequest` | 200 | 404 `hr.workplace.not_found`, 422 `errors.siteId` si cambia (llamada del plan: la sede de un centro es fija), 409, 412/428 |
| `POST /hr/workplaces/{id}/archive` · `…/unarchive` · `DELETE /hr/workplaces/{id}` | ídem | — | 200 / 200 / 204 | `hr.settings.already_archived`, `hr.settings.not_archived` (409), unarchive con sede archivada 422 `hr.settings.site_archived`, 409 `hr.settings.in_use` |
| `POST /hr/areas` · `PUT /hr/areas/{id}` · `…/archive` · `…/unarchive` · `DELETE …` | ídem | `NamedSettingRequest` | 201 / 200 / 200 / 200 / 204 `AreaDto` | 404 `hr.area.not_found`, los de sedes |
| `POST /hr/positions` · `PUT /hr/positions/{id}` · `…/archive` · `…/unarchive` · `DELETE …` | ídem | `NamedSettingRequest` | ídem `PositionDto` | 404 `hr.position.not_found`, los de sedes |
| `GET /hr/salary-rules?date=` | **`hr.contract.read`** | — | 200 `SalaryRulesDto` (`date` ausente ⇒ hoy del tenant) | 422 `hr.legal_parameter.missing` |
| `GET /hr/employees?search=&status=&expiringSoon=&companyId=&page=&pageSize=` | **`hr.employee.read`** (+ `hr.contract.read` con `expiringSoon=true` o con `companyId`, si no 403: la empresa es dato del contrato, **llamada del plan**) | — | 200 `EmployeePageDto` | 422 `errors.status`, `errors.pageSize`, `errors.page` |
| `POST /hr/employees` | los cuatro de empleados y contratos: `hr.employee.read`, `hr.employee.manage`, `hr.contract.read`, **`hr.contract.manage`** | `CreateEmployeeRequest` | 201 `EmployeeDetailDto` | 422 `hr.settings.setup_incomplete`, 409 `hr.employee.document_taken`, 409 `hr.employee.member_taken`, 422 `hr.employee.member_invalid`, 422 `errors.country` (país desconocido o que no cuadra con el documento), los de «Contrato nuevo», 422 `errors` |
| `GET /hr/employees/{id}` | **`hr.employee.read`** | — | 200 `EmployeeDetailDto` | 404 `hr.employee.not_found` |
| `PUT /hr/employees/{id}` | `hr.employee.read`, **`hr.employee.manage`**, `If-Match` | `EmployeeRequest` | 200 | 409 `document_taken`/`member_taken`, 422 `member_invalid`, 422 `errors.country`, 422 `errors`, 412/428 |
| `PUT /hr/employees/{id}/payment` | `hr.employee.read`, `hr.contract.read`, **`hr.contract.manage`**, `If-Match` | `PaymentRequest` | 200 | 422 `errors`, 412/428 |
| `POST /hr/employees/{id}/deactivate` | `hr.employee.read`, **`hr.employee.manage`** | `{ "date": "2026-11-30" }` | 200 | 422 `hr.employee.not_active`, 422 `hr.contract.records_after_end`, 422 `errors.date` (< inicio del contrato) |
| `POST /hr/employees/{id}/reincorporate` | `hr.employee.read`, `hr.contract.read`, **`hr.contract.manage`** | `NewContractRequest` | 200 | 422 `hr.employee.already_active`, 422 `hr.contract.start_not_after_previous`, los de «Contrato nuevo» |
| `DELETE /hr/employees/{id}` | `hr.employee.read`, **`hr.employee.manage`** | — | 204 | 422 `hr.employee.not_deletable` |
| `GET /hr/employees/{id}/activity?page=&pageSize=` | **`hr.employee.read`** | — | 200 `ActivityPageDto` (occurredAt desc) | 404 |
| `POST /hr/employees/{id}/contracts/{cid}/salaries` | `hr.employee.read`, `hr.contract.read`, **`hr.contract.manage`** | `SalaryRequest` | 200 | 404 `hr.contract.not_found`, 422 `hr.contract.not_active`, 422 `hr.salary.effective_date_not_after_last`, 422 `errors.effectiveDate` (después de `endDate`), 422 `hr.salary.below_integral_floor`, 422 `hr.salary.below_minimum_unacknowledged`, 422 `hr.legal_parameter.missing`, 422 `hr.file.*` |
| `PUT /hr/employees/{id}/contracts/{cid}/salaries/{sid}` | ídem, `If-Match` | `SalaryRewriteRequest` | 200 | 404 `hr.salary.not_found`, 422 `hr.salary.rewrite_unconfirmed`, 422 `hr.contract.not_active`, los dos pisos, 412/428 |
| `POST …/contracts/{cid}/assignments` | ídem | `AssignmentRequest` | 200 | 422 `hr.contract.not_active`, 422 `hr.assignment.effective_date_not_after_last`, 422 `hr.assignment.site_change_not_first_of_month`, 422 `hr.assignment.unchanged`, 422 `errors.positionId`/`areaId`/`workplaceId` (no existe o archivado), 422 `hr.assignment.workplace_other_company` con `errors.workplaceId` (centro de una sede de otra empresa), 422 `errors.effectiveDate` (después de `endDate`), 422 `hr.file.*` |
| `POST …/contracts/{cid}/renewals` | ídem | `RenewalRequest` | 200 | 422 `hr.contract.not_active`, 422 `hr.contract.not_fixed_term`, 422 `errors.newEndDate` (≤ fin vigente), 422 `hr.contract.fixed_term_cap_exceeded`, 422 `hr.contract.renewal_minimum_one_year`, 422 `hr.file.*` |
| `POST …/contracts/{cid}/convert-to-indefinite` | ídem | — | 200 | 422 `hr.contract.not_active`, 422 `hr.contract.not_fixed_term` |
| `POST …/contracts/{cid}/conversion` | ídem | `NewContractRequest` | 200 | 422 `hr.contract.not_active`, 422 `hr.contract.company_change_not_allowed` (`contract.companyId` distinto de la empresa del vigente, **llamada del plan**; la empresa del vigente vale aunque esté desactivada, D-12), 422 `hr.contract.start_not_after_previous` (inicio nuevo ≤ inicio del vigente), 422 `hr.contract.records_after_end`, los de «Contrato nuevo» |
| `POST …/contracts/{cid}/terminate` | ídem | `TerminateRequest` | 200 | 422 `hr.contract.not_active`, 422 `errors.date` (< inicio), 422 `hr.contract.records_after_end`, 422 `errors` |
| `PUT …/contracts/{cid}/document` | ídem | `{ "fileId": "uuid" \| null }` | 200 | 404, 422 `hr.file.*` |
| `PUT …/contracts/{cid}/salaries/{sid}/document` · `…/assignments/{aid}/document` · `…/renewals/{rid}/document` | ídem | `{ "fileId": "uuid" \| null }` | 200 | 404 `hr.salary.not_found` / `hr.assignment.not_found` / `hr.renewal.not_found`, 422 `hr.file.*` |
| `POST /hr/files/upload-session` | `hr.contract.read`, **`hr.contract.manage`** | `{ "fileName", "mimeType", "sizeBytes" }` | 200 `{ "fileId", "uploadUrl", "headers" }` | 422 `hr.file.invalid` (no PDF o > 10 MB) |
| `GET /hr/files/{fileId}` | **`hr.contract.read`** | — | 200 `{ "url": "…" }` (firmada, 5 min) | 404 `hr.file.not_found` |
| `GET /hr/members-available` | `hr.employee.read`, **`hr.employee.manage`** | — | 200 `[MemberRefDto]` membresías activas sin empleado vinculado, por `displayName` | — |
| `GET /hr/employees/import/template` | los cuatro de `POST /employees` | — | 200 xlsx `plantilla-empleados.xlsx` | 422 `hr.settings.setup_incomplete` |
| `POST /hr/employees/import/preview` | ídem | multipart `file` | 200 `EmployeeImportPreviewDto` | 422 `hr.settings.setup_incomplete`, 422 `hr.import.*` de archivo |
| `POST /hr/employees/import` | ídem | multipart `file` | 200 `EmployeeImportResultDto` | 422 `hr.settings.setup_incomplete`, 422 `hr.import.*` de archivo, 422 `hr.import.rows_invalid` |

Sólo el contrato `Active` del empleado admite escrituras de contrato (salarios, cargos, prórrogas, conversiones, terminar):
uno `Terminated`, `Converted` o `Closed` responde 422 `hr.contract.not_active`. Los documentos se pueden adjuntar en
cualquier estado **(llamada del plan)**: el PDF firmado puede llegar después. Las rutas `/hr/employees/import/*` se
registran **antes** que `/hr/employees/{id:guid}`.

### Requests

```jsonc
// SiteRequest
{ "companyId": "uuid",                           // requerido al crear: empresa ACTIVA del tenant ⇒ si no, 422 errors.companyId;
                                                 // en el PUT igual al guardado ⇒ si cambia, 422 errors.companyId
  "name": "string 1..100", "address": "string ≤ 200" | null, "cityId": "uuid" | null, "compensationFund": "uno de GET /hr/settings → compensationFunds" | null }   // fuera de la lista ⇒ 422 errors.compensationFund; sólo empresas de Colombia (2026-11-21): con una empresa de otro país un valor no vacío es 422 «La caja de compensación sólo aplica a empresas de Colombia.», null vale
// (llamada del plan) además de la empresa, sólo el nombre es requerido: la fase 1 no calcula con la sede.

// WorkplaceRequest
{ "name": "string 1..100", "siteId": "uuid", "ciiuCode": "^[0-9]{4}$" | null, "riskClass": "III",
  "highRiskPension": false | null }               // null ⇒ false

// NamedSettingRequest (área y cargo)
{ "name": "string 1..100" }
// Nombres únicos por tenant (sedes: por empresa; centros: por sede) comparando sin mayúsculas, tildes ni espacios de más,
// archivados incluidos: desarchivar nunca choca (llamada del plan).

// EmployeeRequest (POST y PUT; PUT reemplaza todo)
{
  "firstName": "string 1..100", "lastName": "string 1..100",
  "documentType": "CC",
  "documentNumber": "string 1..30",              // Trim; sin espacios internos; único por tenant
  "country": "CO" | null,                        // nacionalidad, ISO 3166-1 alfa-2; ver «País del empleado»
  "birthDate": null,                             // < hoy del tenant ⇒ si no, 422 errors.birthDate
  "phone": null,                                 // E.164 ^\+[1-9][0-9]{7,14}$
  "personalEmail": null, "workEmail": null,      // email ≤ 254
  "address": null,                               // ≤ 200
  "cityId": null,                                // del catálogo de Geografía; inexistente ⇒ 422 errors.cityId
  "memberId": null,                              // membresía activa del tenant
  "healthProvider": null, "pensionFund": null, "severanceFund": null,
  "occupationalRiskProvider": null,   // ≤ 120 cada uno; sin caja: es de la sede (delta 2026-11-21)
  "contributorSubtype": "Standard" | null,       // null ⇒ Standard
  "foreignPensionExempt": false | null,          // null ⇒ false; true sólo con country != "CO" ⇒ si no, 422 errors.foreignPensionExempt
  "restDays": ["Sunday"] | null                  // null ⇒ ["Sunday"]; 1..6 días distintos (llamada del plan: siete días de descanso no es un contrato)
}

// PaymentRequest — los cuatro null limpian
{ "paymentMethod": "BankTransfer" | "Other" | null,
  "bankName": "string 1..80" | null, "bankAccountType": "Savings" | null,
  "bankAccountNumber": "^[0-9]{1,17}$" | null }
// BankTransfer exige los tres; Other exige los tres en null (422 en el campo que sobre).

// ContractTermsRequest
{ "companyId": "uuid",                           // requerido. Alta y reincorporación: empresa ACTIVA del tenant ⇒ si no, 422
                                                 // errors.<prefijo>.contract.companyId (en el alta, contract.contract.companyId).
                                                 // Conversión: igual a la del vigente, activa o no (D-12)
  "contractType": "Employee", "duration": "FixedTerm", "salaryType": "Monthly",
  "startDate": "2026-01-15",
  "endDate": "2027-01-14" | null,                // requerido FixedTerm y WorkOrLabor; prohibido Indefinite; ≥ startDate
  "documentFileId": "uuid" | null }
// Integral sólo con Employee ⇒ si no, 422 errors.contract.salaryType.

// InitialSalaryRequest — rige desde startDate
{ "amount": 2500000, "teleworker": false | null, "acknowledgeBelowMinimum": false | null }

// InitialAssignmentRequest — rige desde startDate; la sede sale del centro de trabajo (llamada del plan)
{ "positionId": "uuid", "areaId": "uuid" | null, "workplaceId": "uuid" }

// NewContractRequest — alta (dentro de CreateEmployeeRequest), reincorporación y conversión de tipo
{ "contract": ContractTermsRequest, "salary": InitialSalaryRequest, "assignment": InitialAssignmentRequest }

// CreateEmployeeRequest = EmployeeRequest + { "payment": PaymentRequest | null, "contract": NewContractRequest }
// Las claves de errors del alta: firstName, payment.bankName, contract.contract.endDate, contract.salary.amount,
// contract.assignment.positionId.

// SalaryRequest
{ "effectiveDate": "2026-12-01", "amount": 2800000, "teleworker": false | null,
  "acknowledgeBelowMinimum": false | null, "documentFileId": "uuid" | null }

// SalaryRewriteRequest — conserva effectiveDate
{ "amount": 2800000, "teleworker": false | null, "acknowledgeBelowMinimum": false | null,
  "confirmRewrite": true }                       // distinto de true ⇒ 422 hr.salary.rewrite_unconfirmed

// AssignmentRequest
{ "effectiveDate": "2026-12-01", "positionId": "uuid", "areaId": "uuid" | null, "workplaceId": "uuid",
  "documentFileId": "uuid" | null }

// RenewalRequest
{ "newEndDate": "2027-07-14", "documentFileId": "uuid" | null }

// TerminateRequest
{ "reason": "Resignation", "date": "2026-12-31", "notes": "string ≤ 1000" | null }
```

**País del empleado** (`EmployeeRequest.country`, nacionalidad):

- `null` ⇒ el país del tenant (Configuración › General: `TenantRegionalSettings.Country`, el mismo valor que `/auth/me`
  expone como `country` del tenant activo); sin país del tenant, `CO`. Se recorta y se pasa a mayúsculas.
- Sin forma de código (dos letras) ⇒ 422 `errors.country` (validador); código fuera del catálogo de países de Geography
  (`GET /api/v1/countries`, D-10) ⇒ 422 `errors.country` (handler).
- Cruce con el tipo de documento ⇒ 422 `errors.country` (mensaje en español): `CC` exige `country == "CO"`; `CE`, `PEP`,
  `PPT`, `PA`, `CD` y `SC` exigen `country != "CO"`.
- `foreignPensionExempt: true` sólo con `country != "CO"` (FI-EMP-017, Ley 100/1993 art. 15) ⇒ si no, 422
  `errors.foreignPensionExempt`.
- `cityId` sigue siendo residencia en Colombia (Geografía) y no depende del país.
- En el historial de acciones, `country` es un campo más de `PersonalDataUpdated`, no sensible.

**Contrato nuevo** (alta, reincorporación, conversión). Errores comunes: 422 `errors` de forma; 422
`errors.<prefijo>.contract.companyId` si la empresa no existe en el tenant o —sólo en alta y reincorporación— está
inactiva (D-12); 422 `errors.<prefijo>.assignment.positionId`,
`…areaId`, `…workplaceId` si no existe o está archivado (también si la sede del centro está archivada); 422
`hr.assignment.workplace_other_company` con `errors.<prefijo>.assignment.workplaceId` si el centro es de una sede de otra
empresa; 422
`hr.contract.fixed_term_cap_exceeded` (término fijo con `endDate` > `startDate` + 4 años − 1 día); 422
`hr.salary.below_integral_floor`, `hr.salary.below_minimum_unacknowledged` y `hr.legal_parameter.missing` evaluados a
`startDate`; 422 `hr.file.*`. `<prefijo>` es `contract.` en el alta y vacío en la reincorporación y la conversión
(`errors.contract.companyId`, `errors.assignment.workplaceId`). En la conversión, `contract.companyId` igual a la empresa
del vigente (si no, 422 `hr.contract.company_change_not_allowed`), aunque esa empresa ya esté desactivada (D-12: es el
mismo empleador), `startDate` nuevo > `startDate` del vigente, y el vigente
se cierra `Converted` con `endedOn = startDate − 1`; en la reincorporación, `startDate` > `endedOn` del último contrato y la
empresa puede ser otra (cambiar de empresa es terminar y reincorporar). No hay ruta para cambiar la empresa de un contrato.

### Reglas del salario (spec, «Historiales»)

Con `floor = legal_parameters` vigentes a la fecha del registro (`effectiveDate`, o `startDate` en un contrato nuevo):

1. `salaryType == Integral` y `amount < floor.integralFloor` ⇒ 422 `hr.salary.below_integral_floor`.
2. `amount < floor.smmlv`, tipo no exento y `acknowledgeBelowMinimum != true` ⇒ 422 `hr.salary.below_minimum_unacknowledged`.
3. Si no, se guarda; `belowMinimumAcknowledged = amount < smmlv && tipo no exento`.
4. Sin fila vigente a esa fecha (antes del 2024-01-01) ⇒ 422 `hr.legal_parameter.missing`.

El frontend pinta el piso y el SMMLV desde `GET /hr/salary-rules?date=` (el 422 no los trae).

### Archivos (PDF)

- `upload-session` acepta sólo `application/pdf` con nombre `.pdf`, ≤ 10 MB (10 485 760 bytes), y abre la sesión con
  `IStorageSystemUploads.OpenAsync` (dueño `FileOwnerType.HrDocument`, `ownerId = tenantId`). El navegador hace el `PUT` con
  exactamente `headers`.
- Todo `documentFileId`/`fileId` de una escritura completa la subida (`CompleteAsync`) dentro del handler, **antes** de tocar
  el agregado. Inexistente, de otro tenant o de otro dueño ⇒ 422 `hr.file.not_found`; rechazado por el pipeline ⇒ 422
  `hr.file.rejected`. Nada se escribe en esos casos.
- `GET /hr/files/{fileId}` sólo responde archivos `HrDocument` del tenant.

### Importación

- Hoja `Empleados`, columnas en este orden: `NOMBRES`, `APELLIDOS`, `TIPO DE DOCUMENTO`, `NÚMERO DE DOCUMENTO`, `PAÍS`,
  `FECHA DE NACIMIENTO`, `CELULAR`, `CORREO PERSONAL`, `CORREO CORPORATIVO`, `DIRECCIÓN`, `CIUDAD`, `EPS`,
  `FONDO DE PENSIONES`, `FONDO DE CESANTÍAS`, `ARL`, `SUBTIPO DE COTIZANTE`, `EXENTO DE PENSIÓN`,
  `DÍAS DE DESCANSO`, `MEDIO DE PAGO`, `BANCO`, `TIPO DE CUENTA`, `NÚMERO DE CUENTA`, `EMPRESA`, `TIPO DE CONTRATO`,
  `DURACIÓN`, `TIPO DE SALARIO`, `FECHA DE INGRESO`, `FECHA DE FIN`, `SALARIO`, `TELETRABAJADOR`, `CARGO`, `ÁREA`,
  `CENTRO DE TRABAJO`, `FECHA DE RETIRO`, `MOTIVO DE RETIRO` (36).
- Hoja `Valores válidos`: una columna por lista (tipos de documento, países por nombre —del catálogo de Geography—, subtipos, `Sí`/`No`, medios de
  pago, tipos de cuenta, tipos de contrato, duraciones, tipos de salario, motivos de retiro, y del tenant las empresas
  **activas**, los cargos, las áreas y los centros **no archivados**), con validación de lista sobre esas columnas de
  `Empleados`. **(llamada del plan)** Un centro se escribe `"<centro> - <sede>"`: el nombre del centro sólo es único por
  sede. La sede no tiene columna: sale del centro.
- `PAÍS`: el código alfa-2 o el nombre del país en español del catálogo de países de Geography (D-10), comparando sin mayúsculas, tildes ni
  espacios de más; vacía ⇒ el país del tenant. Otro valor ⇒ `country_invalid`; el cruce con `TIPO DE DOCUMENTO` de «País
  del empleado» ⇒ `country_document_mismatch`. `foreign_pension_exempt_not_allowed` se evalúa contra el país (exento sólo
  con país distinto de Colombia).
- `EMPRESA`: `"<nombre> - <NIT>"` de una empresa activa (la lista de `Valores válidos`); vacía ⇒ la única empresa activa si
  hay una sola, y `company_required` si hay varias; un valor que no es de una empresa activa ⇒ `company_not_found`.
  `CENTRO DE TRABAJO` se resuelve **dentro** de la empresa de la fila: un centro que existe pero en una sede de otra empresa
  ⇒ `workplace_other_company`.
- `DÍAS DE DESCANSO`: etiquetas separadas por coma (`Sábado, Domingo`); vacía ⇒ `Domingo`.
- `FECHA DE RETIRO` ≤ hoy del tenant ⇒ el contrato queda `Terminated` con `MOTIVO DE RETIRO` (requerido entonces) y
  `endedOn` = esa fecha; `FECHA DE RETIRO` futura ⇒ error de fila `retirement_date_in_future` **(llamada del plan: el fin
  previsto va en `FECHA DE FIN`)**.
- Salario bajo el SMMLV de `FECHA DE INGRESO` en un tipo no exento ⇒ **aviso** de fila (`hr.import.row.below_minimum`):
  la fila es válida, y confirmar la guarda con `belowMinimumAcknowledged: true`.
- Archivo: `.xlsx`, ≤ 5 MB, ≤ 1000 filas con datos.

```jsonc
// EmployeeImportRowIssueDto — mismo shape que ImportRowErrorDto de ServiceContracts; sirve a errores y avisos
{ "rowNumber": 3, "code": "hr.import.row.document_taken", "message": "Ya hay un empleado con ese documento.", "field": "documentNumber" | null }

// EmployeeImportRowPreviewDto
{ "rowNumber": 2, "firstName": "Ana", "lastName": "Pérez", "documentType": "CC", "documentNumber": "1020",
  "company": "Comercializadora QEP S.A.S.",       // nombre de la empresa empleadora (sin el NIT)
  "contractType": "Employee", "duration": "FixedTerm", "position": "Analista", "workplace": "Bodega - Sede principal",
  "salary": 2500000, "startDate": "2025-02-01", "endDate": "2026-01-31" | null,
  "contractStatus": "Active" | "Terminated" }

// EmployeeImportPreviewDto
{ "fileName": "planta.xlsx", "totalRows": 12,
  "validRows": [EmployeeImportRowPreviewDto],
  "errors": [EmployeeImportRowIssueDto], "warnings": [EmployeeImportRowIssueDto],
  "summary": { "employeesToCreate": 11, "activeContracts": 9, "terminatedContracts": 2 } }

// EmployeeImportResultDto
{ "fileName": "planta.xlsx", "receivedAt": "…", "importedCount": 12,
  "imported": [{ "rowNumber": 2, "employeeId": "uuid" }] }
```

- `message` viene en español desde el backend y es lo que muestra el frontend.
- Errores de archivo (422, sin filas): `hr.import.file_required`, `hr.import.extension_invalid`, `hr.import.too_large`,
  `hr.import.unreadable`, `hr.import.header_mismatch`, `hr.import.empty`, `hr.import.too_many_rows`.
- Confirmar con alguna fila con **error** ⇒ 422 `hr.import.rows_invalid` y no escribe nada (todo o nada, una transacción).
  Los avisos no bloquean.
- Códigos de fila (`hr.import.row.*`, `field` entre paréntesis): `first_name_required` (firstName), `last_name_required`
  (lastName), `document_type_invalid` (documentType), `document_number_required` (documentNumber),
  `document_duplicated_in_file` (documentNumber, **en las dos filas**), `document_taken` (documentNumber),
  `country_invalid` (country), `country_document_mismatch` (country), `birth_date_invalid` (birthDate), `phone_invalid` (phone), `personal_email_invalid` (personalEmail), `work_email_invalid`
  (workEmail), `city_not_found` (city), `city_ambiguous` (city), `contributor_subtype_invalid` (contributorSubtype),
  `yes_no_invalid` (foreignPensionExempt o teleworker), `foreign_pension_exempt_not_allowed` (foreignPensionExempt),
  `rest_days_invalid` (restDays), `payment_method_invalid` (paymentMethod), `bank_incomplete` (bankName),
  `bank_not_allowed` (bankName: datos de banco con `Otro`), `bank_account_type_invalid` (bankAccountType),
  `bank_account_number_invalid` (bankAccountNumber), `company_required` (company), `company_not_found` (company),
  `contract_type_invalid` (contractType), `duration_invalid`
  (duration), `salary_type_invalid` (salaryType), `integral_not_allowed` (salaryType), `start_date_invalid` (startDate),
  `end_date_invalid` (endDate), `end_date_required` (endDate), `end_date_forbidden` (endDate), `end_date_before_start`
  (endDate), `fixed_term_cap_exceeded` (endDate), `salary_invalid` (salary), `below_integral_floor` (salary),
  `legal_parameter_missing` (startDate), `position_not_found` (position), `area_not_found` (area), `workplace_not_found`
  (workplace), `workplace_other_company` (workplace), `retirement_date_invalid` (retirementDate), `retirement_date_in_future` (retirementDate),
  `retirement_before_start` (retirementDate), `retirement_reason_required` (retirementReason),
  `retirement_reason_invalid` (retirementReason), `text_too_long` (el campo de la columna). Aviso: `below_minimum` (salary).

### Mensajes de dominio (para `ApiExceptionHandler`; el frontend tiene los suyos)

| code | HTTP |
| --- | --- |
| `hr.employee.not_found`, `hr.contract.not_found`, `hr.salary.not_found`, `hr.assignment.not_found`, `hr.renewal.not_found`, `hr.site.not_found`, `hr.workplace.not_found`, `hr.area.not_found`, `hr.position.not_found`, `hr.file.not_found` (en GET) | 404 |
| `hr.employee.document_taken`, `hr.employee.member_taken`, `hr.settings.name_taken`, `hr.settings.in_use`, `hr.settings.already_archived`, `hr.settings.not_archived` | 409 |
| `hr.employee.member_invalid`, `hr.employee.not_active`, `hr.employee.already_active`, `hr.employee.not_deletable`, `hr.contract.not_active`, `hr.contract.not_fixed_term`, `hr.contract.fixed_term_cap_exceeded`, `hr.contract.renewal_minimum_one_year`, `hr.contract.records_after_end`, `hr.contract.start_not_after_previous`, `hr.contract.company_change_not_allowed`, `hr.salary.effective_date_not_after_last`, `hr.salary.below_integral_floor`, `hr.salary.below_minimum_unacknowledged`, `hr.salary.rewrite_unconfirmed`, `hr.assignment.effective_date_not_after_last`, `hr.assignment.site_change_not_first_of_month`, `hr.assignment.unchanged`, `hr.assignment.workplace_other_company` (con `errors` en el campo del centro), `hr.settings.setup_incomplete`, `hr.settings.site_archived`, `hr.settings.site_has_active_workplaces`, `hr.legal_parameter.missing`, `hr.file.invalid`, `hr.file.not_found` (en escrituras), `hr.file.rejected`, `hr.import.*` | 422 |

`hr.contract.records_after_end`: terminar, desactivar o convertir con un registro de salario o de cargo cuya `effectiveDate`
cae después del último día del contrato **(llamada del plan)**: un registro programado para después del retiro no puede
quedar colgando.

---

## Mapa de archivos

Calco de `src/Modules/Loans/` (un tipo por archivo, como `LoanId.cs`, `LoanStatus.cs`). Lo que no está en Loans se calca del
hermano citado en cada tarea.

```
src/Modules/Hr/
  Modules.Hr.Domain/          ids (EmployeeId, EmploymentContractId, SalaryRecordId, AssignmentRecordId, ContractRenewalId,
                              SiteId, WorkplaceId, AreaId, PositionId, ActivityEntryId); enums de la tabla del contrato;
                              LegalParameterKey, LegalParameterValue, LegalParameters, SalaryFloor, SalaryFloorPolicy;
                              Site, Workplace, Area, Position, OrganizationNames; EmployeeProfile, PaymentDetails, ContractTerms,
                              SalaryEntry, AssignmentEntry, NewContract, DocumentRef, EffectiveDated, FixedTermLimits,
                              SalaryRecord, AssignmentRecord, ContractRenewal, EmploymentContract, Employee,
                              HrErrorCodes, HrDomainException
  Modules.Hr.Application/     HrPermissions, HrAuthorization, HrValidator, HrNotFound, HrConflicts, HrFieldErrors, HrEnums,
                              HrLabels, HrText, HrAuditActions, IHrAuditPublisher, IHrUnitOfWork, ILegalParameterSource,
                              SalaryRules, IHrSettingsRepository, IEmployeeRepository, IEmployeeActivityLog, ActivityChanges,
                              IHrMemberDirectory, IHrGeographyLookup (+ HrCityRef, HrCountryRef), HrCountryIndex,
                              IHrCompanyLookup (+ HrCompanyRef), HrCompanyRefs, HrSetup, EmployeeCountry, IHrDocumentStore,
                              HrDocumentAttachment,
                              HrSettingsRequests/Validators/Dtos, HrRequests, HrRequestValidators, HrDtos,
                              EmployeeDetailComposer, EmployeeRules, ContractRules, un archivo por caso de uso, y los de la
                              importación (Tarea 10)
  Modules.Hr.Infrastructure/  HrInfrastructureExtensions, HrDatabaseInitializer, Persistence/{HrDbContext, HrDbContextFactory,
                              HrUnitOfWork, UniqueViolation, LikeSearch, LegalParameterSource, HrSettingsRepository,
                              EmployeeRepository, EmployeeActivityLog, HrAuditPublisher, Migrations/},
                              Excel/{ClosedXmlEmployeeImporter, ClosedXmlEmployeeImportTemplateBuilder}
  Modules.Hr.Api/             HrEndpoints
src/Bootstrapper/             HrMemberDirectory, HrGeographyLookup, HrCompanyLookup (calco de PosCompanyLookup.cs), HrPhoneNormalizer,
                              HrDocumentStore, HrUserReferenceProbe
tests/Modules/Hr/             Modules.Hr.UnitTests/, Modules.Hr.IntegrationTests/ (HrApiHarness, FakeHrDocumentStore, *ApiTests)
tests/ArchitectureTests/ArchitectureTests/HrLayerTests.cs

src/Modules/Geography/        (Tarea 1, D-10) Domain/Country; Application/{ICountryRepository, ListCountries, CountryDto en
                              GeographyDtos}; Infrastructure/{Persistence/CountryRepository, GeographyDbContext (Countries),
                              Persistence/Migrations/<timestamp>_AddCountries, Seed/Data/countries.json, GeographySeeder
                              (SeedCountriesAsync)}; Api/GeographyEndpoints (GET /api/v1/countries, CountryResponse)
tests/Modules/Geography/      UnitTests/{CountryTests, CountriesSeedDataTests}; IntegrationTests/{GeographyApiTests,
                              GeographySeedIntegrityTests} (sumar)
```

Hr llega a Geography **sólo** por `IHrGeographyLookup` (ciudades y países), con su adaptador `src/Bootstrapper/HrGeographyLookup.cs`
(calco de `CompanyGeographyLookup.cs`, sobre `ICityRepository`, `IDepartmentRepository` y el nuevo `ICountryRepository`).

Hr llega a Companies **sólo** por `IHrCompanyLookup` (Application), calco de
`src/Modules/Pos/Modules.Pos.Application/IPosCompanyLookup.cs`, con su adaptador `src/Bootstrapper/HrCompanyLookup.cs` calco de
`src/Bootstrapper/PosCompanyLookup.cs` (sobre `ICompanyRepository.SearchAsync(…, CompanyStatusFilter.Active, …)` y
`FindAsync`), registrado junto al de POS (`QepServiceCollectionExtensions.cs:1752`). Ningún proyecto `Modules.Hr.*` referencia
`Modules.Companies.*` ni `Modules.Geography.*`; `HrLayerTests` lo verifica. El puerto queda para leer y validar; quien
impide borrar una empresa usada por RRHH es la FK de la base (D-11), no el puerto.

---

## Bloque A — Fundación (Tareas 1–3)

### Task 1: Clave de módulo `hr` en Tenancy y catálogo de países en Geography

Calco del commit `cb44db4e6` (`feat(tenancy): add the siigo module key`).

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (después de `Siigo`, línea ~111, al final de `All`
  y en el diccionario privado `Dependencies`, línea ~121, que lee `DependenciesOf` en `:147`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:151` (literal del CHECK)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddHrModuleKey.cs` (+ `.Designer.cs`, snapshot)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `OperatorReadHandlerTests.cs`;
  `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`, `OperatorConsoleApiTests.cs`, `SeedStartupTests.cs`

**Interfaces:**
- Produces: `TenantModuleKeys.Hr` (`"hr"`), **último** de `All`, fuera de `DefaultForNewTenants`, con la entrada
  `[Hr] = [Companies]` en `Dependencies` y su comentario de una línea, como las de `ServiceContracts` y `Loans`
  (`// Spec 2026-11-20 (RRHH fase 1): la empresa empleadora es una empresa de Companies.`). Doc-comment de la clave: «Spec
  2026-11-20 (RRHH fase 1). Depende de `Companies` (la empresa empleadora) y queda fuera de `DefaultForNewTenants`: se prende
  por tenant.» Con la dependencia, `TenantModuleSet` (`:39`) apaga `hr` en la práctica si `companies` está apagado, y
  `TenantModuleChangeBatch` (`:95`) rechaza prender `hr` sin `companies`, como con `loans` sin `customers`.

- [ ] **Step 1: Pruebas que fallan.** En `TenantModuleKeysTests`: el `Assert.Equal("siigo", TenantModuleKeys.All[^1].Value)` de
  `SiigoIsAKnownKeyOutsideTheDefaults` se quita y se agrega `HrIsTheLastKnownKeyOutsideTheDefaults`:
  `Assert.Same(TenantModuleKeys.Hr, TenantModuleKey.Parse("hr"))`, `Assert.Equal("hr", TenantModuleKeys.All[^1].Value)`,
  `Assert.DoesNotContain(TenantModuleKeys.Hr, TenantModuleKeys.DefaultForNewTenants)`. Y `HrDependsOnCompanies`, calco de
  `LoansDependsOnCustomersAndIsOffForNewTenants` (`TenantModuleKeysTests.cs:95`):
  `Assert.Equal([TenantModuleKeys.Companies], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Hr))`;
  `EveryKeyComesAfterAllOfItsDependencies` (`:20`) la sigue cubriendo sin cambios. En `TenantModuleChangeBatchTests`
  (`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/`), `EnablingHrWithoutCompaniesIsInconsistent`, calco de la de `:75`:
  prender `hr` con `companies` apagado ⇒ `TenantDomainException` con `Code == "tenancy.modules.inconsistent_dependencies"`
  (`TenantModuleChangeBatch.InconsistentDependenciesCode`). Sumar `"hr"` al final de las listas literales de
  `TenantModuleKeysTests:13` y `OperatorReadHandlerTests:68`, en orden alfabético en las cinco listas de `SeedStartupTests`
  (entre `"developer"` y `"loans"`), y a las listas y los `is not (… or "siigo")` de `TenantModulesApiTests:102-110` y
  `OperatorConsoleApiTests:336`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-restore`.
  Expected: `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Hr'`.
- [ ] **Step 3: Declarar la clave**, ampliar el CHECK con `,'hr'` al final y generar la migración
  (`dotnet ef migrations add AddHrModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`).
  Dejarla como `20261118190045_AddSiigoModuleKey.cs`: `Up` hace drop/add del CHECK con la lista completa; `Down` borra las
  filas `hr` (`DELETE FROM tenancy.tenant_modules WHERE module_key = 'hr';`) y repone la lista que termina en `'siigo'`.
- [ ] **Step 4: GREEN.** Run:
  `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests|FullyQualifiedName~TenantModuleChangeBatchTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests|FullyQualifiedName~OperatorConsoleApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en los dos, 0 fallas.
- [ ] **Step 5: Commit** — `feat(tenancy): add the hr module key`

**Segunda parte: catálogo de países en Geography (D-10).** Va en la Tarea 1 porque Hr lo consume desde la Tarea 6 (puerto) y
la Tarea 8 (validación). Calco de los departamentos: entidad, tabla, seed idempotente desde JSON embebido, query y endpoint
global sin tenant ni permiso.

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Domain/Country.cs`
- Create: `src/Modules/Geography/Modules.Geography.Application/ICountryRepository.cs`, `ListCountries.cs`; `CountryDto` en
  `GeographyDtos.cs`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/CountryRepository.cs`,
  `Persistence/Migrations/<timestamp>_AddCountries.cs` (+ Designer y snapshot), `Seed/Data/countries.json` (lo toma el
  `<EmbeddedResource Include="Seed\Data\*.json" />` que ya existe en el `.csproj`)
- Modify: `Persistence/GeographyDbContext.cs` (`DbSet<Country> Countries`, `ConfigureCountry` como `ConfigureDepartment`),
  `Seed/GeographySeeder.cs` (`CountriesResourceSuffix = "Seed.Data.countries.json"`, `SeedCountriesAsync` al principio de
  `SeedAsync`), `Seed/DivipolaDataParser.cs` (`ParseCountries`), `GeographyInfrastructureExtensions.cs` (registro del
  repositorio), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler de `ListCountriesQuery`, junto al de
  `ListDepartmentsQuery`), `src/Modules/Geography/Modules.Geography.Api/GeographyEndpoints.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/CountryTests.cs`, `CountriesSeedDataTests.cs`;
  `tests/Modules/Geography/Modules.Geography.IntegrationTests/GeographyApiTests.cs`, `GeographySeedIntegrityTests.cs` (sumar)

**Interfaces:**
- Produces:
  - `Country` (Domain): `string Code` (clave, ISO 3166-1 alfa-2 en mayúsculas), `string Name` (nombre en español);
    `static Country Create(string code, string name)` (código que no sea dos letras ASCII mayúsculas o nombre en blanco ⇒
    `GeographyDomainException`); `void Rename(string name)`.
  - Tabla `geography.countries`: `code varchar(2)` PK, `name varchar(80) not null`, índice único `IX_countries_name`.
  - `countries.json`: `[{ "code": "AG", "name": "Antigua y Barbuda" }, …]` con **exactamente** los 41 de
    `qep-frontend/src/features/customers/utils/countries.ts` (`COUNTRIES`), mismos códigos y nombres, copiados literal.
  - `GeographySeeder.SeedCountriesAsync`: upsert por `Code` (crea o `Rename`), como `SeedDepartmentsAsync`; no borra.
  - `ICountryRepository`: `Task<IReadOnlyList<Country>> ListAllAsync(CancellationToken)` (`AsNoTracking`).
  - `ListCountriesQuery : IQuery<IReadOnlyList<CountryDto>>`, `sealed record CountryDto(string Code, string Name)`, ordenada por
    `Name` con `StringComparer.OrdinalIgnoreCase` (el mismo criterio de `ListDepartmentsHandler`).
  - Ruta `GET /api/v1/countries` en `GeographyEndpoints`, con `RequireAuthorization()` a secas y tag `Geography`, como
    `/api/v1/departments`: responde `200 [{ "code": "CO", "name": "Colombia" }]` (`CountryResponse(string Code, string Name)`),
    ordenada por nombre. Sin `401`/`403` propios: los de autenticación de siempre.
  - **Fuera de alcance:** `Companies` (`CompanyWriteRules.IsValidCountry`), `Tenancy` (`Tenant.NormalizeCountryCode`) y
    `Wholesale` (`DistributorWriteRules.IsCountryCode`) siguen validando sólo la forma; pasarlos al catálogo es otro trabajo.

- [ ] **Step 6: Pruebas que fallan.**
  - `CountryTests`: `ACodeMustBeTwoUppercaseLetters` (`"co"`, `"COL"`, `"1A"` ⇒ `GeographyDomainException`);
    `ABlankNameIsRejected`; `RenameChangesOnlyTheName`.
  - `CountriesSeedDataTests` (lee el recurso embebido con `DivipolaDataParser.ParseCountries`): `TheCatalogHasTheFortyOneCountriesOfTheFrontend`
    (41 entradas, códigos distintos, `CO` ⇒ «Colombia», `VE` ⇒ «Venezuela», `PE` ⇒ «Perú», `US` ⇒ «Estados Unidos»);
    `EveryCodeIsTwoUppercaseLetters`.
  - `GeographyApiTests`: `ListCountriesWithoutAuthHeadersIsUnauthorized` (calco de `ListDepartmentsWithoutAuthHeadersIsUnauthorized`);
    `ListCountriesReturnsTheFortyOneByName` (41; el primero «Antigua y Barbuda»; contiene `{ "code": "CO", "name": "Colombia" }`;
    nombres en orden).
  - `GeographySeedIntegrityTests.ReseedingDoesNotChangeTheCountryCount` (dos `SeedAsync` ⇒ 41) y
    `ReseedingReconcilesACountryNameThatDrifted` (renombrar `CO` en la base y resembrar ⇒ «Colombia»).
- [ ] **Step 7: RED de compilación.** Run: `dotnet build tests/Modules/Geography/Modules.Geography.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'Country' could not be found`.
- [ ] **Step 8: Implementar** según **Interfaces** y generar la migración
  (`dotnet ef migrations add AddCountries --project src/Modules/Geography/Modules.Geography.Infrastructure --context GeographyDbContext -o Persistence/Migrations`).
- [ ] **Step 9: GREEN.** Run: `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~CountryTests|FullyQualifiedName~CountriesSeedDataTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~GeographyApiTests|FullyQualifiedName~GeographySeedIntegrityTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en los dos.
- [ ] **Step 10: Commit** — `feat(geography): country catalog and GET /api/v1/countries`

### Task 2: Esqueleto del módulo `Hr`, permisos, autorización y validador base

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.{Domain,Application,Infrastructure,Api}/Modules.Hr.*.csproj` — calco de los cuatro
  `Modules.Loans.*.csproj`. `Infrastructure` suma `<PackageReference Include="ClosedXML" />` (como
  `Modules.ServiceContracts.Infrastructure.csproj:12`) y no lleva `Microsoft.Extensions.Hosting.Abstractions` ni
  `Options.ConfigurationExtensions` (Hr no tiene worker). `InternalsVisibleTo` a `Modules.Hr.UnitTests` (Application e
  Infrastructure) y a `Modules.Hr.IntegrationTests` (Infrastructure).
- Create: `src/Modules/Hr/Modules.Hr.Domain/HrDomainException.cs`, `HrErrorCodes.cs`
- Create: `src/Modules/Hr/Modules.Hr.Application/HrPermissions.cs`, `HrAuthorization.cs`, `HrValidator.cs`, `HrAuditActions.cs`,
  `IHrAuditPublisher.cs`, `IHrUnitOfWork.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/HrInfrastructureExtensions.cs`, `HrDatabaseInitializer.cs`,
  `Persistence/HrDbContext.cs`, `Persistence/HrDbContextFactory.cs`, `Persistence/HrUnitOfWork.cs`, `Persistence/HrAuditPublisher.cs`,
  `Persistence/UniqueViolation.cs` (calcos de los de `Modules.Loans.Infrastructure`)
- Create: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` (`MapHrEndpoints` con el grupo `/api/v1/tenants/{tenantId:guid}/hr`, tag `Hr`, sin rutas)
- Create: `tests/Modules/Hr/Modules.Hr.UnitTests/Modules.Hr.UnitTests.csproj` (calco de `Modules.Loans.UnitTests.csproj`),
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/Modules.Hr.IntegrationTests.csproj` (calco de `Modules.Loans.IntegrationTests.csproj`),
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrApiHarness.cs`
- Create: `tests/ArchitectureTests/ArchitectureTests/HrLayerTests.cs` (calco de `LoansLayerTests.cs`)
- Modify: `Backend.slnx` (carpetas `/src/Modules/Hr/` y `/tests/Modules/Hr/`, como `Backend.slnx:167-175`),
  `src/Bootstrapper/Bootstrapper.csproj` y `src/Api/Api.csproj` (como las líneas de Loans),
  `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` (los cuatro proyectos, como `:49-52`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — `AddValidatorsFromAssemblyContaining<…>()` de Hr junto a la de
  Loans (`:1459`), `services.AddHrInfrastructure(configuration)` junto a `AddLoansInfrastructure` (`:1480`), los cinco
  `PermissionDefinition` después de los de Loans (`:3520-3540`), los cinco permisos en el rol `admin` (`:2450`) y las cinco
  políticas en `AddAuthorization` (`:4031`). Sin la política, `RequireAuthorization` responde 500 (CLAUDE.md del repo).
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/PermissionVocabulary.cs` (`CategoryLabels["Recursos humanos"] = "Recursos humanos"`)
- Modify: `src/Api/Program.cs` — `app.MapHrEndpoints();` después de `app.MapLoansEndpoints();` (`:415`) y
  `await services.InitializeHrDatabaseAsync(cancellationToken);` después del de Loans (`:528`), **dentro** de
  `RunUnderStartupLockAsync`, con el comentario «Hr (spec 2026-11-20): after Audit (writes audit.entries) and after
  Companies (hr.sites and hr.employment_contracts have FKs to companies.companies). Memberships, cities, countries and files
  are soft references.» (`InitializeCompaniesDatabaseAsync` corre en `:468`, antes.)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `HrLayerTests.cs`,
  `tests/Modules/Hr/Modules.Hr.UnitTests/HrValidatorTests.cs`

**Interfaces:**
- Produces:
  - `HrPermissions` (`public static class`): `EmployeeRead = "hr.employee.read"`, `EmployeeManage = "hr.employee.manage"`,
    `ContractRead = "hr.contract.read"`, `ContractManage = "hr.contract.manage"`, `SettingsManage = "hr.settings.manage"`.
  - `PermissionDefinition`s, categoría `"Recursos humanos"`, `RequiredModules: [ModuleKeys.Hr]`:

    | Permiso | DisplayName | Description | Riesgo | `Shape` |
    | --- | --- | --- | --- | --- |
    | `EmployeeRead` | Ver empleados | Permite ver la lista, la ficha, el historial de acciones y la configuración de RRHH, sin salario, cargo ni datos bancarios. | `low` | `new("hr.employee", "Empleados", "read")` |
    | `EmployeeManage` | Gestionar empleados | Permite editar los datos personales y de seguridad social, desactivar y eliminar empleados. | `medium` | `new("hr.employee", "Empleados", "manage")` |
    | `ContractRead` | Ver contratos y salarios | Permite ver contratos, salarios, cargos y sedes con su historial, prórrogas y datos bancarios. | `high` | `new("hr.contract", "Contratos laborales", "read")` |
    | `ContractManage` | Gestionar contratos | Permite crear empleados, registrar salarios y cambios de cargo, prorrogar, convertir, terminar, reincorporar, editar los datos bancarios e importar. | `high` | `new("hr.contract", "Contratos laborales", "manage")` |
    | `SettingsManage` | Configurar RRHH | Permite crear, editar, archivar y eliminar sedes, centros de trabajo, áreas y cargos. | `medium` | `new("hr.settings", "Configuración de RRHH", "manage")` |

  - `HrAuthorization.EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, CancellationToken, params string[] permissions)`
    (`internal static`): tenant distinto o **cualquier** permiso faltante ⇒ `Denied()`; después
    `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Hr, …)`. `Denied()` ⇒
    `new RequestForbiddenException("authorization.denied", "The subject cannot perform this hr operation for this tenant.")`.
    Copia de `LoansAuthorization`.
  - `HrValidator<T> : AbstractValidator<T>` (`public abstract`): sobrescribe
    `protected override void RaiseValidationException(ValidationContext<T> context, ValidationResult result)` y, antes de
    llamar a `base`, reescribe cada `failure.PropertyName` a camelCase por segmento (`"Contract.Contract.EndDate"` ⇒
    `"contract.contract.endDate"`). Si FluentValidation 12.1.1 no expone ese `virtual`, cada regla usa
    `.OverridePropertyName("…")` con la clave del contrato y `HrValidator<T>` queda como base vacía; la prueba es la misma.
  - `HrDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) : DomainException, IHasFieldErrors`
    (calco de `src/Modules/Catalog/Modules.Catalog.Domain/CatalogDomainException.cs`): un 422 con código **y** mapa `errors`
    por campo, que `ApiExceptionHandler` ya responde (`IHasFieldErrors`). Lo usa `hr.assignment.workplace_other_company`.
  - `HrErrorCodes`: una constante por código de la tabla «Mensajes de dominio» (más los de importación de la Tarea 10) y
    `public static readonly IReadOnlySet<string> Conflicts` con los seis de la fila 409.
  - `IHrUnitOfWork.SaveChangesAsync(CancellationToken)`; `IHrAuditPublisher.Publish(…)` con la firma de `ILoansAuditPublisher`
    (`Source = "hr"`).
  - `HrDbContext` (`Schema = "hr"`, `AuditEntries` con `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, historial
    `__ef_migrations_history` en `hr`), sin tablas de negocio todavía.
  - `HrApiHarness` (copia de `LoansApiHarness`): `TenantId`, `SubjectId`, `OtherTenantId`, `HrUrl(string tenantId = TenantId)` ⇒
    `/api/v1/tenants/{tenantId}/hr`, `OpenAsync(IEnumerable<TenantModuleKey>? modules = null, Action<IServiceCollection>? configureServices = null)`
    (sin `modules`, todas las claves, como `LoansApiHarness.SeedTenantAsync:75`; una lista sin `Companies` deja `hr`
    apagado en la práctica), `SeedTenantAsync` (con `string country = "CO"` además: tras `Tenant.Create`, un
    `UPDATE tenancy.tenants SET country = {country}` para las pruebas del país por defecto, como
    `CustomersApiHarness.cs:201`), `SeedMemberAsync`, `SeedCallerMembershipAsync`, `CountAuditEntriesAsync`,
    `CreateCompanyAsync(HttpClient, Guid tenantId, string name = "Comercializadora QEP S.A.S.")` (calco de
    `PosApiHarness.CreateCompanyAsync`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs:227`, por
    `POST /api/v1/tenants/{tenantId}/companies` con un cliente con `companies.company.manage`) y `DeactivateCompanyAsync`
    (`POST …/companies/{id}/deactivate`): las pruebas de Hr siembran empresas reales, así el adaptador queda cubierto,
    `CreateClient(factory, subjectId, tenantId, params string[] permissions)`, `CreateFullAccess(factory)` (los cinco permisos),
    `BodyAsync`, `QepApiFactory(string connectionString, Action<IServiceCollection>? configureServices = null)` y
    `MutableTenantClock` (copia de `LoansApiHarness.cs:308`, Bogotá).

- [ ] **Step 1: Pruebas que fallan.**
  - `HrLayerTests`: las cinco de `LoansLayerTests` con los marcadores `HrDomainException` (pasa a `Employee` en la Tarea 4),
    `HrPermissions`, `HrInfrastructureExtensions`, `HrEndpoints`; la última es
    `ApplicationOnlyReferencesTenancyAmongTheBusinessModules` (prefijos permitidos `Modules.Hr` y `Modules.Tenancy`). Más
    `NoHrLayerReferencesCompanies`: ninguno de los cuatro ensamblados `Modules.Hr.*` referencia un ensamblado que empiece por
    `Modules.Companies` (la empresa entra sólo por `IHrCompanyLookup`, adaptador en Bootstrapper); y
    `NoHrLayerReferencesGeography`, igual con `Modules.Geography` (ciudades y países entran por `IHrGeographyLookup`).
  - `CompositionRootTests`: `PermissionDiscoveryFindsTheNinetyFourConstants` ⇒ `PermissionDiscoveryFindsTheNinetyNineConstants`
    con `Assert.Equal(99, …)` y «+ the five hr.* permissions (spec 2026-11-20)» en el comentario.
    `TheHrPermissionsRequireTheHrModuleAndGoOnlyToAdmin`: para los cinco, `Category == "Recursos humanos"`,
    `RequiredModules == [ModuleKeys.Hr]`, `Shape` como la tabla, presentes en `PermissionsFor("admin")` y **ausentes** de
    `advisor`, `billing` y `cashier`.
  - `HrValidatorTests.NestedAndTopLevelFailuresUseCamelCaseKeys`: `HrValidator<Probe>` con
    `record Probe(string FirstName, ProbeContract Contract)`, `record ProbeContract(DateOnly? EndDate)` y `NotEmpty` en
    `FirstName` y `Contract.EndDate`; `ValidateAndThrowAsync(new Probe("", new(null)))` lanza `ValidationException` con
    `Errors.Select(e => e.PropertyName)` igual a `["firstName", "contract.endDate"]`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/ArchitectureTests/ArchitectureTests`.
  Expected: `error CS0246: The type or namespace name 'HrPermissions' could not be found`.
- [ ] **Step 3: Crear el esqueleto y el cableado** según **Files**; `dotnet restore Backend.slnx` y sumar al commit todos los
  `packages.lock.json` nuevos o cambiados (`git status --short -- "*packages.lock.json"`).
- [ ] **Step 4: GREEN.** Run:
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~HrLayerTests|FullyQualifiedName~CompositionRootTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~HrValidatorTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`; `EveryActionAndCategoryHasALabel` y `EveryRegisteredPermissionDeclaresItsShape` en verde.
- [ ] **Step 5: Commit** — `feat(hr): scaffold the hr module with its permissions`

### Task 3: Valores legales con fecha de vigencia y `GET /hr/salary-rules`

Patrón de `catalog.tax_unit_values` (`src/Modules/Catalog/Modules.Catalog.Domain/TaxUnitValue.cs`, `TaxUnitValues.cs`): dato de
plataforma sin `tenant_id`. Sin pantalla: la carga anual es una migración del owner.

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Domain/`: `LegalParameterKey.cs`, `LegalParameterValue.cs`, `LegalParameters.cs`,
  `SalaryFloor.cs`, `ContractType.cs`, `SalaryType.cs`, `SalaryFloorPolicy.cs`
- Create en `src/Modules/Hr/Modules.Hr.Application/`: `ILegalParameterSource.cs`, `SalaryRules.cs`, `GetSalaryRules.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/LegalParameterSource.cs`,
  `Persistence/Migrations/<timestamp>_AddHrLegalParameters.cs` (+ Designer y snapshot); modificar `HrDbContext.cs`,
  `HrInfrastructureExtensions.cs`, `HrEndpoints.cs`
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParametersTests.cs`, `SalaryFloorPolicyTests.cs`, `HrTestData.cs`;
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/SalaryRulesApiTests.cs`

**Interfaces:**
- Produces:
  - `enum LegalParameterKey { Smmlv, IntegralSalaryFloor }`.
  - `LegalParameterValue` (entidad sin tenant): `LegalParameterKey Key`, `DateOnly EffectiveFrom`, `decimal Value`. Tabla
    `hr.legal_parameters`, PK `(key, effective_from)`, `key` texto con `CHECK`, `value numeric(18,2)`.
  - `LegalParameters.Resolve(IEnumerable<LegalParameterValue> values, LegalParameterKey key, DateOnly date) : decimal?` — el
    valor de mayor `EffectiveFrom ≤ date`; ninguno ⇒ `null`.
  - `sealed record SalaryFloor(DateOnly Date, decimal Smmlv, decimal IntegralFloor)`.
  - `enum ContractType` y `enum SalaryType` en el orden de la tabla del contrato (los demás enums llegan en la Tarea 4).
  - `SalaryFloorPolicy` (`public static`):
    `IReadOnlySet<ContractType> BelowMinimumAllowed` (los siete exentos del contrato) y
    `bool Check(ContractType type, SalaryType salaryType, decimal amount, SalaryFloor floor, bool acknowledged)` — devuelve
    `belowMinimumAcknowledged`; lanza `HrDomainException(BelowIntegralFloor)` o `(BelowMinimumUnacknowledged)` según
    «Reglas del salario».
  - `ILegalParameterSource.ListAsync(CancellationToken) : Task<IReadOnlyList<LegalParameterValue>>` (la tabla es chica: se lee
    entera, `AsNoTracking`).
  - `SalaryRules` (scoped): `Task<SalaryFloor> FloorAtAsync(DateOnly date, CancellationToken)` — falta cualquiera de las dos
    claves ⇒ `HrDomainException(LegalParameterMissing, "There is no SMMLV loaded for {date}.")`.
  - `GetSalaryRulesQuery(Guid TenantId, DateOnly? Date) : IQuery<SalaryRulesDto>`; autoriza `ContractRead`; `Date` nulo ⇒
    `ITenantClock` hoy. Ruta `GET /salary-rules` (`RequireAuthorization(HrPermissions.ContractRead)`).
  - Migración `AddHrLegalParameters`: crea la tabla y siembra con `migrationBuilder.InsertData` (no `HasData`: la carga de cada
    año será otra migración igual) las seis filas del spec:

    | key | effective_from | value |
    | --- | --- | --- |
    | `Smmlv` | 2024-01-01 | 1300000 |
    | `IntegralSalaryFloor` | 2024-01-01 | 16900000 |
    | `Smmlv` | 2025-01-01 | 1423500 |
    | `IntegralSalaryFloor` | 2025-01-01 | 18505500 |
    | `Smmlv` | 2026-01-01 | 1750905 |
    | `IntegralSalaryFloor` | 2026-01-01 | 22761765 |

- [ ] **Step 1: Pruebas que fallan.** `HrTestData.LegalTable` arma las seis filas en memoria (los literales viven **sólo** en
  pruebas y migración).
  - `LegalParametersTests`: `ResolvesTheValueInForceOnTheDate` (`Smmlv` en 2025-12-31 ⇒ `1_423_500m`, en 2026-01-01 ⇒
    `1_750_905m`); `BeforeTheFirstValueThereIsNone` (2023-12-31 ⇒ `null`); `ALaterYearKeepsTheLastLoaded` (2027-06-01 ⇒
    `1_750_905m`, D-8).
  - `SalaryFloorPolicyTests` (piso 2026): `AnEmployeeBelowTheMinimumNeedsAcknowledgement` (`Employee`, `Monthly`,
    `1_750_904m`, sin confirmar ⇒ `hr.salary.below_minimum_unacknowledged`); `AcknowledgedBelowTheMinimumIsKeptAndFlagged`
    (devuelve `true`); `TheMinimumItselfNeedsNothing` (`1_750_905m` ⇒ `false`); teoría
    `ExemptTypesGoBelowTheMinimumSilently` (los siete con `1_000_000m` ⇒ `false`); `AUniversityInternIsNotExempt`
    (`1_000_000m` sin confirmar ⇒ error); `AnIntegralSalaryBelowTheFloorIsRejected` (`22_761_764m` ⇒
    `hr.salary.below_integral_floor`, aun confirmado); `TheIntegralFloorItselfIsAccepted` (`22_761_765m`);
    `TheFloorIsTheOneOfTheRecordDate` (piso 2025, `Employee`, `1_500_000m`, sin confirmar ⇒ `false`).
  - `SalaryRulesApiTests`: `AnswersTheValuesOfTheRequestedDate` (`?date=2025-06-30` ⇒ `smmlv == 1423500`,
    `integralFloor == 18505500`, siete tipos en `belowMinimumAllowedContractTypes`); `WithoutDateUsesTheTenantToday` (reloj
    2026-11-20T15:00:00Z ⇒ `date == "2026-11-20"`, `smmlv == 1750905`); `BeforeTwentyTwentyFourIs422Missing`
    (`?date=2023-12-31` ⇒ 422 `code == "hr.legal_parameter.missing"`); `WithoutContractReadIs403`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'LegalParameters' could not be found`.
- [ ] **Step 3: Implementar** y generar la migración
  (`dotnet ef migrations add AddHrLegalParameters --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`);
  agregar a mano el `InsertData` en `Up` y su `DeleteData` en `Down`.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~SalaryRulesApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): effective-dated legal parameters and salary rules`

> **Cierre del Bloque A:** `dotnet build Backend.slnx --no-restore` sin errores;
> `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~HrLayerTests|FullyQualifiedName~CompositionRootTests" -- xUnit.MaxParallelThreads=4`;
> `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4`; la clase `SalaryRulesApiTests` completa;
> las clases `GeographyApiTests` y `GeographySeedIntegrityTests` completas.
> Una revisión del bloque.

---

## Bloque B — Dominio, persistencia y configuración (Tareas 4–6)

### Task 4: Dominio — organización, empleado, contrato e historiales

Código puro, sin base: todo se prueba con unitarias.

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Domain/`: los diez ids (calco de `LoanId.cs`); los enums restantes de la tabla del
  contrato (`HrDocumentType`, `ContributorSubtype`, `PaymentMethod`, `BankAccountType`, `ContractDuration`, `ContractStatus`,
  `TerminationReason`, `EmployeeStatus`, `ArlRiskClass`, `HrActivityAction`, `HrActivityCategory`); `OrganizationNames.cs`,
  `Site.cs`, `Workplace.cs`, `Area.cs`, `Position.cs`; `EmployeeProfile.cs`, `PaymentDetails.cs`, `ContractTerms.cs`,
  `SalaryEntry.cs`, `AssignmentEntry.cs`, `NewContract.cs`, `DocumentRef.cs`, `EffectiveDated.cs`, `FixedTermLimits.cs`,
  `SalaryRecord.cs`, `AssignmentRecord.cs`, `ContractRenewal.cs`, `EmploymentContract.cs`, `Employee.cs`
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/OrganizationTests.cs`, `EffectiveDatedTests.cs`, `FixedTermLimitsTests.cs`,
  `EmployeeLifecycleTests.cs`, `ContractRecordsTests.cs`, `ContractConversionTests.cs`; ampliar `HrTestData.cs`

**Interfaces:**
- Produces:
  - Organización (cada uno agregado con `Id`, `TenantId`, `Name`, `NormalizedName`, `ArchivedAt`, `bool Archived`,
    `long Version`, `CreatedAt`, `UpdatedAt`; `Rename`/`Update` y `Archive`/`Unarchive` terminan en `Touch(now)`):
    - `Site.Create(SiteId, Guid tenantId, Guid companyId, string name, string? address, Guid? cityId, string? compensationFund, DateTimeOffset now)`;
      `Update(…)` **sin** empresa: `CompanyId` (FK a `companies.companies`, D-11) no cambia después. Que la empresa exista y
      esté activa lo decide Application (Tarea 6), no el dominio.
    - `Workplace.Create(WorkplaceId, Guid tenantId, Site site, string name, string? ciiuCode, ArlRiskClass riskClass, bool highRiskPension, DateTimeOffset now)` —
      `site.Archived` ⇒ `HrDomainException(SiteArchived)`; `SiteId` no cambia después. No guarda empresa: la hereda de su
      sede (`site.CompanyId`).
    - `Area.Create(…, string name, …)`, `Position.Create(…)`.
    - `Archive`: ya archivado ⇒ `SettingsAlreadyArchived`; `Unarchive`: no archivado ⇒ `SettingsNotArchived`.
    - `OrganizationNames.Normalize(string name) : string` — recorta, quita tildes, minúsculas invariantes y colapsa espacios
      (misma normalización que `HrText.Fold` de la Tarea 10, que la reusa).
  - `sealed record EmployeeProfile(string FirstName, string LastName, HrDocumentType DocumentType, string DocumentNumber,
    string Country, DateOnly? BirthDate, string? Phone, string? PersonalEmail, string? WorkEmail, string? Address, Guid? CityId, Guid? MemberId,
    string? HealthProvider, string? PensionFund, string? SeveranceFund, string? OccupationalRiskProvider,
    ContributorSubtype ContributorSubtype, bool ForeignPensionExempt, IReadOnlyList<DayOfWeek> RestDays)` — llega validado:
    `Country` ya resuelto (nunca nulo, alfa-2 en mayúsculas) y cruzado con el documento por `EmployeeCountry` (Tarea 8).
  - `sealed record PaymentDetails(PaymentMethod Method, string? BankName, BankAccountType? BankAccountType, string? BankAccountNumber)`.
  - `sealed record ContractTerms(Guid CompanyId, ContractType ContractType, ContractDuration Duration, SalaryType SalaryType, DateOnly StartDate, DateOnly? EndDate)`
    — `CompanyId` es la empresa empleadora (FK a `companies.companies`, D-11); Application ya comprobó que existe, que está
    activa si es un vínculo nuevo (D-12) y que el centro es de una sede suya.
  - `sealed record SalaryEntry(DateOnly EffectiveDate, decimal Amount, bool Teleworker, bool AcknowledgeBelowMinimum)`.
  - `sealed record AssignmentEntry(DateOnly EffectiveDate, PositionId PositionId, AreaId? AreaId, SiteId SiteId, WorkplaceId WorkplaceId)`
    (Application arma `SiteId` desde el centro).
  - `sealed record DocumentRef(Guid FileId, string FileName)`.
  - `sealed record NewContract(EmploymentContractId Id, ContractTerms Terms, DocumentRef? Document, SalaryRecordId SalaryId,
    decimal Amount, bool Teleworker, bool AcknowledgeBelowMinimum, AssignmentRecordId AssignmentId, PositionId PositionId,
    AreaId? AreaId, SiteId SiteId, WorkplaceId WorkplaceId)` — los dos registros iniciales rigen desde `Terms.StartDate`.
  - `EffectiveDated` (`public static`):
    `T CurrentOf<T>(IReadOnlyList<T> records, DateOnly today) where T : IEffectiveDated` y
    `bool IsScheduled(IEffectiveDated record, DateOnly today)` (= `EffectiveDate > today`), con
    `interface IEffectiveDated { DateOnly EffectiveDate { get; } }`:

    ```csharp
    // Effective dates are strictly increasing within a contract, so there are no ties.
    var inForce = records.Where(r => r.EffectiveDate <= today).MaxBy(r => r.EffectiveDate);
    return inForce ?? records.MinBy(r => r.EffectiveDate)!;   // contract not started yet: show its first record
    ```
  - `FixedTermLimits` (`public static`): `MaxYears = 4`, `ShortRenewalsBeforeMinimumYear = 3`, `NoticeDays = 30`;
    `DateOnly MaxEndDate(DateOnly startDate)` (= `startDate.AddYears(4).AddDays(-1)`);
    `bool IsShort(DateOnly previousEndDate, DateOnly newEndDate)` (= `newEndDate < previousEndDate.AddYears(1)`);
    `bool NoticeDue(EmploymentContract contract, DateOnly today)` (`FixedTerm`, `Active`, `EndDate <= today.AddDays(30)`);
    `int? DaysUntilEnd(EmploymentContract contract, DateOnly today)`.
  - `SalaryRecord` (`IEffectiveDated`): `Id`, `ContractId`, `EffectiveDate`, `Amount`, `Teleworker`, `BelowMinimumAcknowledged`,
    `DocumentFileId`, `DocumentFileName`, `CreatedAt`, `UpdatedAt`.
  - `AssignmentRecord` (`IEffectiveDated`): `Id`, `ContractId`, `EffectiveDate`, `PositionId`, `AreaId?`, `SiteId`, `WorkplaceId`,
    `DocumentFileId`, `DocumentFileName`, `CreatedAt`.
  - `ContractRenewal`: `Id`, `ContractId`, `PreviousEndDate`, `NewEndDate`, `bool IsShort`, `DocumentFileId`, `DocumentFileName`, `CreatedAt`.
  - `EmploymentContract` (entidad): `Id`, `EmployeeId`, campos de `ContractTerms` (`CompanyId` sin setter público: ningún
    método lo cambia; `EndDate` es el fin vigente), `Status`,
    `EndedOn?`, `ConvertedToIndefiniteOn?`, `PreviousContractId?`, `DocumentFileId`, `DocumentFileName`, `TerminationReason?`,
    `TerminationNotes`, `CreatedAt`, `UpdatedAt`, `Salaries`, `Assignments`, `Renewals` (sobre listas privadas).
  - `Employee` (agregado): `Id`, `TenantId`, una propiedad por campo de `EmployeeProfile`, `Payment` (`PaymentDetails?`),
    `Status`, `long Version`, `CreatedAt`, `UpdatedAt`, `Contracts`, `EmploymentContract? ActiveContract`, `bool CanDelete`.
    Todo método que recibe un id de contrato o registro ajeno lanza `HrDomainException(ContractNotFound | SalaryNotFound |
    AssignmentNotFound | RenewalNotFound)` (Application los vuelve 404). Toda mutación termina en `Touch(now)` (`Version++`,
    `UpdatedAt`) y `RefreshStatus()`. Los métodos que validan salario reciben el `SalaryFloor` ya resuelto a la fecha que toca.
    - `static Employee Hire(EmployeeId id, Guid tenantId, EmployeeProfile profile, PaymentDetails? payment, NewContract contract, SalaryFloor floorAtStart, DateTimeOffset now)` —
      `Version = 1`, `Active`.
    - `void UpdateProfile(EmployeeProfile, DateTimeOffset)`; `void SetPayment(PaymentDetails?, DateTimeOffset)`.
    - `void AddSalary(EmploymentContractId, SalaryRecordId, SalaryEntry, SalaryFloor floorAtDate, DocumentRef?, DateTimeOffset)`:
      contrato no `Active` ⇒ `ContractNotActive`; `EffectiveDate <= último.EffectiveDate` ⇒ `SalaryEffectiveDateNotAfterLast`;
      `SalaryFloorPolicy.Check` con `ContractType` y `SalaryType` del contrato.
    - `void RewriteSalary(EmploymentContractId, SalaryRecordId, decimal amount, bool teleworker, bool acknowledgeBelowMinimum, bool confirmRewrite, SalaryFloor floorAtRecordDate, DateTimeOffset)`:
      `!confirmRewrite` ⇒ `SalaryRewriteUnconfirmed` (antes que todo lo demás); conserva `EffectiveDate`; `UpdatedAt = now`.
    - `void AddAssignment(EmploymentContractId, AssignmentRecordId, AssignmentEntry, DocumentRef?, DateTimeOffset)`:

      ```csharp
      var previous = contract.Assignments.MaxBy(a => a.EffectiveDate)!;
      if (entry.EffectiveDate <= previous.EffectiveDate) throw Error(AssignmentEffectiveDateNotAfterLast);
      var placeChanges = entry.WorkplaceId != previous.WorkplaceId || entry.SiteId != previous.SiteId;
      if (!placeChanges && entry.PositionId == previous.PositionId && entry.AreaId == previous.AreaId)
          throw Error(AssignmentUnchanged);
      if (placeChanges && entry.EffectiveDate.Day != 1) throw Error(SiteChangeNotFirstOfMonth);   // PILA, FI-EMP-012
      ```
    - `ContractRenewal Renew(EmploymentContractId, ContractRenewalId, DateOnly newEndDate, DocumentRef?, DateTimeOffset)`:
      no `Active` ⇒ `ContractNotActive`; no `FixedTerm` ⇒ `ContractNotFixedTerm`; `newEndDate <= EndDate` ⇒
      `ArgumentOutOfRangeException` (Application lo frena antes como `errors.newEndDate`);
      `newEndDate > MaxEndDate(StartDate)` ⇒ `FixedTermCapExceeded`; `Renewals.Count(r => r.IsShort) >= 3 &&
      IsShort(EndDate, newEndDate)` ⇒ `RenewalMinimumOneYear`. Guarda `PreviousEndDate = EndDate` y mueve `EndDate`.
    - `void ConvertToIndefinite(EmploymentContractId, DateOnly today, DateTimeOffset)`: mismas dos primeras reglas; `Duration =
      Indefinite`, `EndDate = null`, `ConvertedToIndefiniteOn = today`; `StartDate` no cambia.
    - `void ChangeContractType(EmploymentContractId, NewContract next, SalaryFloor floorAtStart, DateTimeOffset)`:

      ```csharp
      // A type conversion keeps the employer; changing company is terminate + reincorporate.
      if (next.Terms.CompanyId != current.CompanyId) throw Error(CompanyChangeNotAllowed);
      if (next.Terms.StartDate <= current.StartDate) throw Error(ContractStartNotAfterPrevious);
      var lastDay = next.Terms.StartDate.AddDays(-1);
      current.Close(ContractStatus.Converted, lastDay);   // throws RecordsAfterEnd if a record starts after lastDay
      AddContract(next, previousContractId: current.Id, floorAtStart);
      ```
    - `void Terminate(EmploymentContractId, TerminationReason, DateOnly date, string? notes, DateTimeOffset)` — `date <
      StartDate` ⇒ `ArgumentOutOfRangeException` (Application: `errors.date`); `Close(Terminated, date)`.
    - `void Deactivate(DateOnly date, DateTimeOffset)` — sin `ActiveContract` ⇒ `EmployeeNotActive`; `Close(Closed, date)`.
    - `void Reincorporate(NewContract, SalaryFloor floorAtStart, DateTimeOffset)` — con `ActiveContract` ⇒
      `EmployeeAlreadyActive`; `next.StartDate <= último.EndedOn` ⇒ `ContractStartNotAfterPrevious`. La empresa puede ser
      otra: es la única forma de cambiar de empresa.
    - `void SetContractDocument(EmploymentContractId, DocumentRef?, DateTimeOffset)` y las tres de registros
      (`SetSalaryDocument`, `SetAssignmentDocument`, `SetRenewalDocument`), en cualquier estado.
    - `EmploymentContract.Close(ContractStatus status, DateOnly lastDay)` (`internal`): no `Active` ⇒ `ContractNotActive`;
      algún salario o cargo con `EffectiveDate > lastDay` ⇒ `RecordsAfterEnd`; `Status = status`, `EndedOn = lastDay`.
    - `RefreshStatus()`: algún contrato `Active` ⇒ `Active`; si no, el de mayor `StartDate`: `Terminated` ⇒ `Retired`,
      `Closed` ⇒ `Deactivated`. El estado se **guarda** (lo filtra la lista).
    - `CanDelete` (spec, «Ciclo de vida»): `Contracts.Count == 1` y ese contrato `Active`, sin prórrogas, con
      `ConvertedToIndefiniteOn == null`, un solo salario y un solo cargo. `EnsureDeletable()` ⇒ `EmployeeNotDeletable`.
    - Toda la creación de contratos pasa por `AddContract`, que valida: `FixedTerm` con `EndDate > MaxEndDate(StartDate)` ⇒
      `FixedTermCapExceeded`; `SalaryFloorPolicy.Check` a `StartDate`.

- [ ] **Step 1: Pruebas que fallan.** `HrTestData`: hoy 2026-11-20, piso 2026; `CompanyId` y `OtherCompanyId` (dos `Guid`
  fijos); contrato base de `CompanyId`, `Employee`, `FixedTerm`, `Monthly`, inicio 2026-01-15, fin 2026-07-14, salario
  2 500 000, cargo «Analista», centro «Bodega» de la sede «Principal» (de `CompanyId`); empleado `CC`, país `CO`;
  `HrTestData.Hire(...)` arma un empleado con ese contrato.
  - `OrganizationTests`: `AWorkplaceCannotBeCreatedOnAnArchivedSite` (`hr.settings.site_archived`); `ArchivingTwiceIsRejected`
    (`hr.settings.already_archived`); `UnarchivingAnActiveOneIsRejected` (`hr.settings.not_archived`);
    `NamesAreNormalizedForUniqueness` (`"  Bodega   NORTE "` y `"bodega norte"` ⇒ mismo `NormalizedName`; `"Logística"` ⇒ `"logistica"`);
    `UpdatingASiteKeepsItsCompany` (`Update` con otro nombre ⇒ `CompanyId` igual al de `Create`).
  - `EffectiveDatedTests`: `TheLastRecordOnOrBeforeTodayIsCurrent` (registros 2026-01-15, 2026-06-01, 2026-12-01 ⇒ vigente
    2026-06-01); `ARecordEffectiveTodayIsCurrent` (2026-11-20); `AFutureRecordIsScheduled` (2026-12-01 ⇒ `IsScheduled`);
    `BeforeTheContractStartsTheFirstRecordIsShown` (único registro 2026-12-01, hoy 2026-11-20 ⇒ ese registro).
  - `FixedTermLimitsTests`: `TheCapIsFourYearsMinusADay` (inicio 2026-01-15 ⇒ 2030-01-14; inicio 2024-02-29 ⇒ 2028-02-28);
    `ARenewalShorterThanAYearIsShort` (2026-07-14 → 2027-01-14 ⇒ `true`); `ExactlyOneYearIsNotShort` (2028-01-14 →
    2029-01-14 ⇒ `false`); `NoticeIsDueWithinThirtyDays` (fin 2026-12-20 ⇒ `true`; 2026-12-21 ⇒ `false`);
    `AnExpiredFixedTermIsStillDue` (fin 2026-11-01 ⇒ `true`, `DaysUntilEnd == -19`); `IndefiniteOrTerminatedNeverDue`.
  - `EmployeeLifecycleTests`: `HiringStartsActiveAtVersionOne`; `HiringAFixedTermPastTheCapIsRejected` (fin 2030-01-15 ⇒
    `hr.contract.fixed_term_cap_exceeded`); `HiringBelowTheMinimumWithoutAcknowledgementIsRejected` (`1_750_904m`);
    `TerminatingRetiresTheEmployee` (`Resignation`, 2026-11-30 ⇒ `Retired`, `EndedOn == 2026-11-30`);
    `DeactivatingClosesTheContract` (⇒ `Closed`, `Deactivated`); `DeactivatingWithoutAnActiveContractIsRejected`
    (`hr.employee.not_active`); `TerminatingWithAScheduledRecordAfterTheDateIsRejected` (salario 2026-12-01, terminar
    2026-11-30 ⇒ `hr.contract.records_after_end`); `ReincorporatingCreatesANewContractAndKeepsHistory` (retirado el
    2026-11-30, reincorporar desde 2026-12-15 ⇒ dos contratos, `Active`, el viejo intacto);
    `ReincorporatingOnOrBeforeThePreviousEndIsRejected` (2026-11-30 ⇒ `hr.contract.start_not_after_previous`);
    `ReincorporatingAnActiveEmployeeIsRejected` (`hr.employee.already_active`);
    `ReincorporatingMayUseAnotherCompany` (retirado de `CompanyId`, reincorporado con `OtherCompanyId` ⇒ el contrato nuevo
    es de `OtherCompanyId` y el viejo sigue de `CompanyId`); `ANewHireCanBeDeleted` (`CanDelete == true`);
    teoría `AnyLaterRecordForbidsDeleting` (salario nuevo, cargo nuevo, prórroga, convertir a indefinido, terminar ⇒
    `CanDelete == false` y `EnsureDeletable` ⇒ `hr.employee.not_deletable`); `EveryMutationBumpsTheVersion`.
  - `ContractRecordsTests`: `ASalaryMustBeAfterTheLastOne` (2026-01-15 ⇒ `hr.salary.effective_date_not_after_last`);
    `ASalaryRaiseIsANewRecord` (2026-12-01, 2 800 000 ⇒ dos registros, el viejo intacto);
    `RewritingWithoutConfirmationIsRejected` (`hr.salary.rewrite_unconfirmed`, aun con monto válido);
    `RewritingKeepsTheEffectiveDate`; `RewritingBelowTheIntegralFloorIsRejected`; `AnAssignmentMustBeAfterTheLastOne`;
    `AWorkplaceChangeOffTheFirstIsRejected` (2026-12-15 otro centro ⇒ `hr.assignment.site_change_not_first_of_month`);
    `AWorkplaceChangeOnTheFirstIsAccepted` (2026-12-01); `APositionOnlyChangeMayBeAnyDay` (2026-12-15, otro cargo);
    `ANoOpAssignmentIsRejected` (`hr.assignment.unchanged`); `RecordsOnANonActiveContractAreRejected`
    (`hr.contract.not_active`); `TheFourthShortRenewalMustLastAYear` (fin 2026-07-14 → 2027-01-14 → 2027-07-14 → 2028-01-14;
    luego 2028-07-14 ⇒ `hr.contract.renewal_minimum_one_year`; 2029-01-14 pasa); `ARenewalPastTheCapIsRejected`
    (2030-01-15 ⇒ `hr.contract.fixed_term_cap_exceeded`; 2030-01-14 pasa); `OnlyFixedTermRenews` (`Indefinite` y
    `WorkOrLabor` ⇒ `hr.contract.not_fixed_term`); `ConvertingToIndefiniteKeepsTheStartAndClearsTheEnd`.
  - `ContractConversionTests`: `AConversionClosesThePreviousTheDayBefore` (aprendiz lectiva desde 2026-01-15, productiva desde
    2026-07-01 ⇒ el viejo `Converted` con `EndedOn == 2026-06-30`, el nuevo `Active` con `PreviousContractId` del viejo y sus
    dos registros iniciales en 2026-07-01); `AConversionStartingOnOrBeforeTheCurrentStartIsRejected`
    (`hr.contract.start_not_after_previous`); `AConversionWithARecordAfterTheCloseIsRejected` (salario 2026-08-01, conversión
    2026-07-01 ⇒ `hr.contract.records_after_end`); `IntegralNeedsTheFloorAtTheNewStart`;
    `AConversionCannotChangeTheCompany` (conversión válida salvo `CompanyId = OtherCompanyId` ⇒
    `hr.contract.company_change_not_allowed`, y el vigente sigue `Active` sin cambios);
    `AConversionKeepsTheCompany` (con la misma empresa ⇒ el nuevo contrato tiene `CompanyId`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'Employee' could not be found`.
- [ ] **Step 3: Implementar** los tipos de **Interfaces**. Los textos llegan validados: el dominio no repite topes. Cambiar el
  marcador de dominio de `HrLayerTests` a `typeof(Employee)`.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4`. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): organization, employee and contract domain with effective-dated records`

### Task 5: Persistencia, migración inicial y sonda de usuarios

**Files:**
- Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/HrDbContext.cs`, `Persistence/HrUnitOfWork.cs`, `HrInfrastructureExtensions.cs`
- Create: `src/Modules/Hr/Modules.Hr.Application/IHrSettingsRepository.cs`, `IEmployeeRepository.cs`, `IEmployeeActivityLog.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/HrSettingsRepository.cs`, `EmployeeRepository.cs`,
  `EmployeeActivityLog.cs`, `LikeSearch.cs` (copia de la de Loans), `Migrations/<timestamp>_AddHrEmployees.cs` (+ Designer y snapshot)
- Create: `src/Bootstrapper/HrUserReferenceProbe.cs` (calco de `LoansUserReferenceProbe.cs`); registrarla junto a la de Loans
  (`QepServiceCollectionExtensions.cs:1923`)
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrPersistenceTests.cs`, `HrUserReferenceProbeTests.cs` (calco de
  `LoansUserReferenceProbeTests.cs`)

**Interfaces:**
- Consumes: Tareas 3–4.
- Produces:
  - Tablas: `hr.sites`, `hr.workplaces` (FK `site_id` restrict), `hr.areas`, `hr.positions`, `hr.employees`,
    `hr.employment_contracts` (FK `employee_id` cascade, FK `previous_contract_id` restrict), `hr.salary_records` y
    `hr.assignment_records` y `hr.contract_renewals` (FK `contract_id` cascade; `assignment_records` con FK restrict a
    `positions`, `areas`, `sites`, `workplaces`), `hr.employee_activity` (FK `employee_id` cascade). Columnas snake_case como
    `LoansDbContext`; enums como texto (`HasConversion<string>().HasMaxLength(32)`) con su `CHECK`; dinero
    `HasPrecision(18, 2)`; `version` bigint `IsConcurrencyToken()` en empleados y en las cuatro de configuración; navegaciones
    con `UsePropertyAccessMode(PropertyAccessMode.Field)` (calco de `AutomationsDbContext.cs:171-174`). `rest_days`
    `varchar(80)` con los nombres separados por coma (converter + `ValueComparer`). Largos: nombres 100, `normalized_name` 100,
    `document_number` 30, `phone` 32, correos 254, `address` 200, seguridad social y caja 120, `bank_name` 80,
    `bank_account_number` 17, `ciiu_code` 4, `termination_notes` 1000, `document_file_name` 255, `actor_name` 200.
  - Empresa y país: `hr.sites.company_id` y `hr.employment_contracts.company_id` (`uuid not null`) con **FK real** a
    `companies.companies(id)` `ON DELETE RESTRICT` (D-11): `FK_sites_companies_company_id` y
    `FK_employment_contracts_companies_company_id`, agregadas **a mano** al final de `Up` de `AddHrEmployees` y quitadas al
    principio de `Down`, calco de `20261007110447_InitialPos.cs:220-232` (con su comentario: borrar una empresa usada sale
    como 422 `companies.company.in_use` porque `CompaniesUnitOfWork` traduce el SQLSTATE 23001). En `HrDbContext` sólo
    `HasColumnName("company_id")` más el comentario de `PosDbContext.cs:48` («EF no modela relaciones entre DbContext de
    módulos distintos») y un índice no único por `company_id` en cada tabla. `hr.employees.country` (`varchar(2) not null`,
    sin FK: el catálogo de Geography se consulta por puerto, como `member_id` y `city_id`). `hr.workplaces` no lleva
    empresa: la hereda de su sede.
  - `hr.employee_activity`: `id`, `tenant_id`, `employee_id`, `occurred_at`, `actor_subject_id`, `actor_name`, `action`,
    `category`, `contract_id` nulo, `changes jsonb` (lista de `{ field, before, after, sensitive }`).
  - Índices con estos nombres exactos (los usa `HrUnitOfWork`):
    - `IX_sites_tenant_company_normalized_name`: único `(tenant_id, company_id, normalized_name)` (nombre de sede único por
      empresa); `IX_areas_tenant_normalized_name`, `IX_positions_tenant_normalized_name`: únicos `(tenant_id, normalized_name)`;
      `IX_workplaces_tenant_site_normalized_name`: único `(tenant_id, site_id, normalized_name)`.
    - `IX_employees_tenant_document_number`: único `(tenant_id, document_number)`.
    - `IX_employees_tenant_member_id`: único `(tenant_id, member_id)`, `HasFilter("member_id IS NOT NULL")`.
    - `IX_employment_contracts_employee_active`: único `(employee_id)`, `HasFilter("status = 'Active'")`.
    - `IX_employees_tenant_status_name` (no único) y `IX_employee_activity_employee_occurred` (no único).
  - `HrUnitOfWork.SaveChangesAsync`: `DbUpdateConcurrencyException` ⇒ `RequestConcurrencyException("concurrency.conflict", …)`;
    los cuatro `…_normalized_name` ⇒ `ResourceConflictException(HrErrorCodes.SettingsNameTaken, "Ya existe uno con ese nombre.", e)`;
    `IX_employees_tenant_document_number` ⇒ `ResourceConflictException(DocumentTaken, "Ya hay un empleado con ese documento.", e)`;
    `IX_employees_tenant_member_id` ⇒ `(MemberTaken, "Esa persona ya está vinculada a otro empleado.", e)`. Se discrimina por
    **nombre de índice** con `UniqueViolation.Is`, no sólo por `23505`.
  - `IHrSettingsRepository`: `FindSiteAsync`, `FindWorkplaceAsync`, `FindAreaAsync`, `FindPositionAsync` (todos `(Guid tenantId, <Id>, CancellationToken)`, rastreados);
    `Add(object entity)` tipado por sobrecarga; `Remove(...)`; `Task<HrSettingsSnapshot> ListAsync(Guid tenantId, CancellationToken)`
    con `sealed record HrSettingsSnapshot(IReadOnlyList<Site> Sites, IReadOnlyList<Workplace> Workplaces, IReadOnlyList<Area> Areas, IReadOnlyList<Position> Positions, IReadOnlySet<Guid> InUse)`
    (`InUse`: ids referenciados por algún `assignment_records`, más las sedes con algún centro); `Task<bool> IsInUseAsync(Guid tenantId, Guid settingId, CancellationToken)`;
    `Task<bool> SiteHasActiveWorkplacesAsync(Guid tenantId, SiteId, CancellationToken)`;
    `Task<bool> IsSetupCompleteAsync(Guid tenantId, IReadOnlyCollection<Guid> activeCompanyIds, CancellationToken)` (algún
    cargo no archivado y algún centro no archivado con sede no archivada cuya `company_id` está en `activeCompanyIds`: el
    repositorio no sabe qué empresa está activa; se lo dice `HrSetup`, Tarea 6).
  - `IEmployeeRepository`:
    - `Task<Employee?> FindAsync(Guid tenantId, EmployeeId id, CancellationToken)` (con contratos, registros y prórrogas, rastreado).
    - `void Add(Employee)`; `void AddRange(IEnumerable<Employee>)`; `void Remove(Employee)`.
    - `Task<EmployeeListPage> ListAsync(Guid tenantId, string? search, EmployeeStatus? status, bool expiringSoon, Guid? companyId, DateOnly today, int page, int pageSize, CancellationToken)`
      con `sealed record EmployeeListPage(IReadOnlyList<Employee> Items, int Total, int Active, int Retired, int Deactivated, int ExpiringSoon)`:
      los contadores respetan `search` e ignoran `status`, `expiringSoon` y `companyId`; `companyId` filtra empleados con
      contrato `Active` de esa empresa **(llamada del plan: la empresa de la lista es la del contrato vigente, así que un
      retirado no tiene empresa)**; `expiringSoon` filtra empleados con contrato
      `Active`, `FixedTerm` y `end_date <= today + 30` (en SQL); orden `last_name`, `first_name`, `id`; `search` con
      `LikeSearch.ContainsPattern` y `EF.Functions.ILike` sobre nombre completo y documento.
    - `Task<IReadOnlySet<string>> FindTakenDocumentsAsync(Guid tenantId, IReadOnlyCollection<string> documentNumbers, CancellationToken)`.
    - `Task<IReadOnlySet<Guid>> ListLinkedMemberIdsAsync(Guid tenantId, CancellationToken)`.
  - `IEmployeeActivityLog`: `void Append(EmployeeActivityEntry entry)` y
    `Task<(IReadOnlyList<EmployeeActivityEntry> Items, int Total)> ListAsync(Guid tenantId, EmployeeId id, IReadOnlySet<HrActivityCategory> categories, int page, int pageSize, CancellationToken)`,
    con `sealed record EmployeeActivityEntry(Guid Id, Guid TenantId, EmployeeId EmployeeId, DateTimeOffset OccurredAt, Guid ActorSubjectId, string ActorName, HrActivityAction Action, HrActivityCategory Category, Guid? ContractId, IReadOnlyList<ActivityChange> Changes)`
    y `sealed record ActivityChange(string Field, JsonElement? Before, JsonElement? After, bool Sensitive)` (Application).
  - `HrUserReferenceProbe` (`Source => "hr"`): retiene al usuario si alguna de sus membresías está en `hr.employees.member_id`.

- [ ] **Step 1: Pruebas que fallan** (integración; el RED es de compilación). Con la FK de D-11, `CompanyId` y
  `OtherCompanyId` ya no pueden ser `Guid` inventados en estas pruebas: el arranque de la clase siembra dos empresas reales con
  `HrApiHarness.CreateCompanyAsync` y usa sus ids.
  - `ASiteOfAnUnknownCompanyViolatesTheForeignKey` (sede con un `companyId` que no existe ⇒ `DbUpdateException` con
    `PostgresException.ConstraintName == "FK_sites_companies_company_id"`): prueba que la FK quedó en la migración.
  - `HrPersistenceTests.AnEmployeeWithItsRecordsRoundTrips` (alta + salario + cargo + prórroga; en otro scope `FindAsync`
    trae los tres historiales, `RestDays` `[Saturday, Sunday]`, `Country == "VE"`, el `CompanyId` del contrato y la misma
    `Version`).
  - `TwoSitesOfTheSameCompanyCannotShareAName` (`"Principal"` y `" PRINCIPAL "` en `CompanyId` ⇒ `hr.settings.name_taken`);
    `TwoCompaniesMayEachHaveASiteWithTheSameName` (`"Principal"` en `CompanyId` y en `OtherCompanyId` ⇒ guarda).
  - `SetupNeedsAnActiveCompanyWithASiteAndAWorkplace` (cargo + centro en una sede de `CompanyId`: con
    `activeCompanyIds = [CompanyId]` ⇒ `true`; con `[OtherCompanyId]` o `[]` ⇒ `false`).
  - `TheListFiltersByTheCompanyOfTheActiveContract` (uno activo en cada empresa y uno retirado de `CompanyId`;
    `companyId = CompanyId` ⇒ `Total == 1` y los contadores iguales a los de sin filtro).
  - `TwoEmployeesCannotShareADocument` ⇒ `ResourceConflictException` con `Code == "hr.employee.document_taken"`.
  - `TwoEmployeesCannotLinkTheSameMember` ⇒ `hr.employee.member_taken`.
  - `TwoPositionsCannotShareANormalizedName` (`"Analista"` y `" ANALISTA "` ⇒ `hr.settings.name_taken`);
    `TwoSitesMayHoldWorkplacesWithTheSameName`.
  - `AStaleVersionIsAConcurrencyConflict` (dos scopes cargan; los dos agregan salario; el segundo `SaveChangesAsync` lanza
    `RequestConcurrencyException`).
  - `ListCountsRespectSearchButNotStatus` (3 activos, 1 retirado, 1 desactivado, dos «Pérez»; `search = "pérez"`,
    `status = Retired` ⇒ `Total` = retirados «Pérez», contadores sólo de los «Pérez»).
  - `ExpiringSoonFiltersFixedTermWithinThirtyDays` (hoy 2026-11-20: fin 2026-12-20 entra, 2026-12-21 no, indefinido no;
    `ExpiringSoon == 1`).
  - `UsageMarksSettingsInUse` (un cargo usado por un registro ⇒ en `InUse`; uno sin usar ⇒ fuera; una sede con centro ⇒ en `InUse`).
  - `ActivityRoundTripsItsChangesAndFiltersByCategory`.
  - `HrUserReferenceProbeTests.AnEmployeeLinkedToTheMembershipRetainsTheUser` y `WithoutLinkTheUserIsNotRetained`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests`.
  Expected: `error CS0246: The type or namespace name 'IEmployeeRepository' could not be found`.
- [ ] **Step 3: Implementar** el mapeo, los repositorios y la sonda, y generar la migración
  (`dotnet ef migrations add AddHrEmployees --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`).
  Revisar en el SQL generado los índices con su nombre exacto y el filtro de los dos filtrados.
- [ ] **Step 4: GREEN.** Run:
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~HrPersistenceTests|FullyQualifiedName~HrUserReferenceProbeTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): persist organization, employees and their histories`

### Task 6: Configuración de la organización — rutas

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Application/`: `HrNotFound.cs` (calco de `LoansNotFound`), `HrConflicts.cs`,
  `HrFieldErrors.cs`, `HrEnums.cs`, `IHrGeographyLookup.cs`, `IHrCompanyLookup.cs`, `HrCompanyRefs.cs`, `HrSetup.cs`,
  `HrSettingsRequests.cs`, `HrSettingsValidators.cs`,
  `HrSettingsDtos.cs`, `GetHrSettings.cs`, `SaveSite.cs` (crear y editar), `SaveWorkplace.cs`, `SaveNamedSetting.cs` (área y
  cargo), `ArchiveSetting.cs` (archivar y desarchivar, los cuatro), `DeleteSetting.cs`
- Create: `src/Bootstrapper/HrGeographyLookup.cs` (calco de `CompanyGeographyLookup.cs`) y `src/Bootstrapper/HrCompanyLookup.cs`
  (calco de `src/Bootstrapper/PosCompanyLookup.cs`); registrar puertos y handlers en el bloque de Hr de
  `QepServiceCollectionExtensions.cs` (a mano y con ruta completa, como Loans en `:446-489`; el lookup junto a
  `services.AddScoped<IPosCompanyLookup, PosCompanyLookup>()`, `:1752`)
- Modify: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` (rutas de configuración y `RequireVersion`/`TryParseVersion`, copia de
  `LoansEndpoints.cs:292-318`)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/HrSettingsValidatorsTests.cs`;
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrSettingsApiTests.cs`

**Interfaces:**
- Consumes: Tareas 2, 4, 5.
- Produces:
  - `HrEnums.TryParse<TEnum>(string? text, out TEnum value)` (`internal static`): sólo el **nombre exacto** del miembro
    (`ignoreCase: false`; `"1"` no es un enum).
  - `HrConflicts.Run(Action)` y `Run<T>(Func<T>)`: convierten `HrDomainException` con `HrErrorCodes.Conflicts.Contains(code)`
    en `ResourceConflictException(code, message, e)` (409); el resto sigue 422.
  - `HrFieldErrors.Throw(string field, string message)` ⇒ `ValidationException([new ValidationFailure(field, message)])`.
  - `HrNotFound`: `Employee`, `Contract`, `Salary`, `Assignment`, `Renewal`, `Site`, `Workplace`, `Area`, `Position`, `File`
    (`Guid` ⇒ `ResourceNotFoundException` con su `hr.*.not_found`).
  - `IHrGeographyLookup`: `FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken) : Task<IReadOnlyDictionary<Guid, HrCityRef>>`,
    `ListAllCitiesAsync(CancellationToken) : Task<IReadOnlyList<HrCityRef>>` (para la Tarea 10) y
    `ListCountriesAsync(CancellationToken) : Task<IReadOnlyList<HrCountryRef>>` (el catálogo de la Tarea 1, D-10, por
    `ICountryRepository.ListAllAsync`), con `sealed record HrCountryRef(string Code, string Name)` y
    `sealed record HrCityRef(Guid CityId, string Name, string DivipolaName, Guid DepartmentId, string DepartmentName)`.
  - `IHrCompanyLookup` (Application, calco de `src/Modules/Pos/Modules.Pos.Application/IPosCompanyLookup.cs`; doc-comment
    «Puerto hacia Companies; el adaptador vive en Bootstrapper.»):
    `Task<IReadOnlyList<HrCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken)` y
    `Task<HrCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken)` (activas e inactivas; `null` si no
    existe en el tenant), con `sealed record HrCompanyRef(Guid Id, string Name, string TaxId, bool IsActive)`.
    `HrCompanyLookup` (Bootstrapper) es `PosCompanyLookup` con `ToRef` recortado a esos cuatro campos (`company.Id.Value`,
    `Name`, `TaxId`, `IsActive`).
  - `HrCompanyRefs` (scoped): `Task<IReadOnlyList<CompanyRefDto>> ListActiveAsync(Guid tenantId, CancellationToken)` (por
    `Name` `CurrentCultureIgnoreCase` y luego id) y
    `Task<IReadOnlyDictionary<Guid, CompanyRefDto>> ResolveAsync(Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken)`:
    una `ListActiveAsync` y un `FindAsync` por cada id que no esté entre las activas (una inactiva). Uno que no existe no puede pasar (FK, D-11): `InvalidOperationException`.
    `Task<HrCompanyRef> RequireActiveAsync(Guid tenantId, Guid companyId, string field, CancellationToken)`: inexistente o
    inactiva ⇒ `HrFieldErrors.Throw(field, "Elige una empresa activa.")`. Lo usan los **vínculos nuevos** (D-12): crear una
    sede (`"companyId"`), el alta y la reincorporación en `NewContractFactory` (`$"{prefix}contract.companyId"`) y la
    importación. La conversión de tipo no lo llama.
  - `HrCountryIndex(IReadOnlyList<HrCountryRef> countries)` (Application): `bool IsKnown(string code)` (código en
    mayúsculas), `HrCountryRef? FindByCodeOrName(string text)` (código, o nombre con `HrText.Fold`: sin mayúsculas, tildes ni
    espacios de más) e `IReadOnlyList<string> Names` (para `Valores válidos`). Se arma con **una** `ListCountriesAsync` por
    request.
  - `HrSetup` (scoped): `Task<bool> IsCompleteAsync(Guid tenantId, CancellationToken)` = `IHrSettingsRepository.IsSetupCompleteAsync`
    con los ids de `IHrCompanyLookup.ListActiveAsync`. Toda lectura de `setupComplete` y todo `SetupIncomplete` (alta,
    plantilla, vista previa, importación) pasa por acá.
  - Requests y validadores (`HrValidator<…>`) de «Requests» del contrato: `SiteRequest`, `WorkplaceRequest`, `NamedSettingRequest`.
  - DTOs de «DTOs» del contrato: `SettingRefDto`, `MunicipalityRefDto`, `CompanyRefDto`, `SiteDto` (con `company`),
    `WorkplaceDto`, `AreaDto`, `PositionDto`, `HrSettingsDto` (con `companies`). Listas ordenadas por `Name`
    (`CurrentCultureIgnoreCase`) y luego id **(llamada del plan)**.
  - Casos de uso: `GetHrSettingsQuery(Guid TenantId)` (autoriza `EmployeeRead`; `companies` de `HrCompanyRefs.ListActiveAsync`,
    `SiteDto.company` de `ResolveAsync` sobre las empresas de las sedes, `setupComplete` de `HrSetup`);
    `CreateSiteCommand` (`HrCompanyRefs.RequireActiveAsync(…, "companyId")` antes de crear),
    `UpdateSiteCommand(…, long ExpectedVersion)` (`companyId` distinto del guardado ⇒
    `HrFieldErrors.Throw("companyId", "La empresa de una sede no cambia.")`; si es el mismo no se revalida que esté activa:
    D-12),
    `CreateWorkplaceCommand`, `UpdateWorkplaceCommand` (`siteId` distinto del guardado ⇒ `HrFieldErrors.Throw("siteId", "La sede de un centro de trabajo no cambia.")`),
    `CreateNamedSettingCommand(Guid TenantId, NamedSettingKind Kind, NamedSettingRequest Request)` y `UpdateNamedSettingCommand`
    con `enum NamedSettingKind { Area, Position }`, `ArchiveSettingCommand(Guid TenantId, SettingKind Kind, Guid Id, bool Archive)`
    y `DeleteSettingCommand(Guid TenantId, SettingKind Kind, Guid Id)` con `enum SettingKind { Site, Workplace, Area, Position }`.
    Todas las escrituras autorizan `EmployeeRead, SettingsManage`, auditan (`hr.settings.created|updated|archived|unarchived|deleted`,
    recurso `site|workplace|area|position`) y responden el DTO (con `inUse`). Archivar una sede con un centro no archivado ⇒
    `HrDomainException(SiteHasActiveWorkplaces)`; desarchivar un centro de una sede archivada ⇒ `SiteArchived`; eliminar en
    uso ⇒ `ResourceConflictException(SettingsInUse)`; `siteId` o `cityId` inexistente ⇒ `HrFieldErrors.Throw` en el campo.
  - Rutas (`HrEndpoints`): las once filas de configuración de la tabla de rutas, `POST` con 201, `DELETE` con 204, política
    `HrPermissions.SettingsManage` en las escrituras y `EmployeeRead` en `GET /settings`.

- [ ] **Step 1: Pruebas que fallan.**
  - `HrSettingsValidatorsTests`: `ABlankNameFails` (`name`); `ASiteWithoutCompanyFails` (`companyId` nulo o `Guid.Empty` ⇒
    `companyId`); `ACiiuCodeMustBeFourDigits` (`"47A1"` ⇒ `ciiuCode`);
    `AnUnknownRiskClassFails` (`"VI"` ⇒ `riskClass`); `ARiskClassNumberIsNotAName` (`"3"`).
  - `HrSettingsApiTests` (con `CreateFullAccess`): `CreatesTheFourAndListsThemInOneCall` (sede, centro, área, cargo ⇒
    `GET /settings` con uno de cada uno y `setupComplete == true`); `WithoutPositionSetupIsIncomplete`;
    `ADuplicateNameIgnoringCaseAndAccentsIs409` (`"Logística"` y `"LOGISTICA"` ⇒ 409 `hr.settings.name_taken`);
    `TheSameWorkplaceNameInAnotherSiteIsAllowed`; `UpdateNeedsIfMatch` (428 y 412); `AWorkplaceCannotChangeSite`
    (422 `errors.siteId`); `ArchivingASiteWithAnActiveWorkplaceIs422`; `AWorkplaceOnAnArchivedSiteIs422SiteArchived`;
    `ArchivedSettingsStayInTheListMarked` (`archived == true`); `DeletingAnUnusedSettingIs204`;
    `DeletingASiteWithAWorkplaceIs409InUse`; `WithoutSettingsManageWritesAre403`; `WithOnlySettingsManageWritesAre403`
    (sin `hr.employee.read`, D-1); `EveryWriteIsAudited` (`CountAuditEntriesAsync("hr.settings.created", id) == 1`).
    Con empresas sembradas por `HrApiHarness.CreateCompanyAsync` (todas las pruebas crean la sede con `companyId`):
    `TheSettingsListOnlyActiveCompaniesByName` (dos activas y una desactivada ⇒ `companies` con las dos, por nombre, con
    `taxId` e `isActive == true`); `ASiteAnswersItsCompany` (`site.company.id`, `name`, `taxId`);
    `ASiteNeedsAnActiveCompany` (sin `companyId`, con un id de otro tenant y con una desactivada ⇒ 422
    `errors.companyId`); `ASiteCannotChangeCompany` (`PUT` con la otra empresa ⇒ 422 `errors.companyId`);
    `TheSameSiteNameInAnotherCompanyIsAllowed` y `ADuplicateSiteNameInTheSameCompanyIs409`;
    `SetupNeedsAnActiveCompanyWithASiteAndAWorkplace` (todo configurado ⇒ `setupComplete == true`; desactivar la única
    empresa ⇒ `false`); `ADeactivatedCompanyKeepsItsSites` (desactivada después ⇒ la sede sigue en `sites` con
    `company.isActive == false`, se edita y admite un centro nuevo, D-12); `DeletingACompanyWithOnlyASiteIs422InUse` (D-11: empresa con una sede y
    ningún empleado ⇒ `DELETE /api/v1/tenants/{tenantId}/companies/{id}` 422 `code == "companies.company.in_use"`; borrada
    la sede, el mismo `DELETE` ⇒ 204); `ReadingSettingsNeedsNoCompaniesPermission`
    (cliente con sólo `hr.employee.read` ⇒ 200 con `companies`).
  - `HrLayerTests.NoHrLayerReferencesCompanies` (Tarea 2) sigue verde con el puerto nuevo.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'WorkplaceRequestValidator' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**. Un handler olvidado en el registro compila y responde 500: registrar cada uno.
- [ ] **Step 4: GREEN.** Unitarias completas; la integración por nombre mientras se itera; al terminar,
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~HrSettingsApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): organization settings endpoints`

> **Cierre del Bloque B:** `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4`; las clases
> `HrPersistenceTests`, `HrUserReferenceProbeTests` y `HrSettingsApiTests` completas, en una sola corrida filtrada. Una
> revisión del bloque.

---

## Bloque C — Archivos, empleados y contrato (Tareas 7–9)

### Task 7: PDFs — dueño `HrDocument` en Storage, puerto y rutas de archivos

Calco de la imagen de plantillas: `FileOwnerType.MessageTemplate` + `IMessagingTemplateImageStore` +
`src/Bootstrapper/MessagingTemplateImageStore.cs`, y del alta de `ChatbotMedia` en Storage (commit `886e54992`).

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs` (`HrDocument = 11`, con su comentario, y en
  `SystemOwnedFiles.OwnerTypes`), `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs`
  (`[FileOwnerType.HrDocument] = TenantModuleKeys.Hr`)
- Create: `src/Modules/Hr/Modules.Hr.Application/IHrDocumentStore.cs`, `HrDocumentAttachment.cs`, `OpenHrUploadSession.cs`,
  `GetHrFileDownloadUrl.cs`
- Create: `src/Bootstrapper/HrDocumentStore.cs`; registrar puerto y handlers en el bloque de Hr
- Modify: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs`
- Create: `tests/Modules/Hr/Modules.Hr.IntegrationTests/FakeHrDocumentStore.cs` (calco de
  `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/FakeTemplateImageStore.cs`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/HrDocumentStoreTests.cs` (calco de `ChatbotMediaStoreTests.cs`, con
  `FakeStorageSystemUploads` de `StorageTestDoubles.cs`); `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrFilesApiTests.cs`;
  en Storage, `FileOwnerModulesTests.cs`, `FileOwnerModuleHandlersTests.cs` y `SystemOwnedFilesApiTests.cs` (mismos cambios que
  hizo `886e54992` para `ChatbotMedia`)

**Interfaces:**
- Produces:
  - `IHrDocumentStore` (Application; el adaptador vive en Bootstrapper porque Hr no referencia Storage):
    `OpenUploadAsync(Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken) : Task<HrUploadSession>`;
    `CompleteAsync(Guid tenantId, Guid fileId, CancellationToken) : Task<HrDocumentFile>` (idempotente);
    `CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken) : Task<Uri?>` (`null` si no es un `HrDocument`
    `Available` del tenant); `sealed record HrUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers)`;
    `enum HrDocumentState { Available, NotFound, NotUploaded, Rejected }`; `sealed record HrDocumentFile(HrDocumentState State, string? FileName)`;
    `static class HrDocumentPolicy { const long MaxSizeBytes = 10_485_760; const string MimeType = "application/pdf"; }`.
  - `HrDocumentStore` (Bootstrapper): dueño `FileOwnerType.HrDocument` con `ownerId = tenantId`. `OpenUploadAsync` traduce
    `StorageDomainException` `storage.file.type_not_allowed`, `mime_mismatch` o `size_invalid` a
    `HrDomainException(FileInvalid, …)`. `CompleteAsync`: `Available` ⇒ nombre de `FindAvailableAsync`; `ObjectMissing` ⇒
    `NotUploaded`; `Rejected` ⇒ `Rejected`; lo demás ⇒ `NotFound`. Descarga: 5 minutos.
  - `HrDocumentAttachment.ResolveAsync(IHrDocumentStore, Guid tenantId, Guid? fileId, CancellationToken) : Task<DocumentRef?>`
    (`internal static`): `null` ⇒ `null`; `Available` ⇒ `DocumentRef`; `Rejected` ⇒ `HrDomainException(FileRejected)`;
    `NotFound`/`NotUploaded` ⇒ `HrDomainException(FileNotFound)` (422). Las Tareas 8 y 9 lo llaman **antes** de tocar el agregado.
  - `OpenHrUploadSessionCommand(Guid TenantId, string FileName, string MimeType, long SizeBytes) : ICommand<HrUploadSessionDto>`
    con `HrUploadSessionRequest(string? FileName, string? MimeType, long? SizeBytes)` y
    `HrUploadSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers)`. Autoriza
    `ContractRead, ContractManage`. Nombre en blanco o sin `.pdf`, otro MIME, tamaño ≤ 0 o > 10 485 760 ⇒
    `HrDomainException(FileInvalid, "The file must be a PDF of up to 10 MB.")`, sin llamar al puerto.
  - `GetHrFileDownloadUrlQuery(Guid TenantId, Guid FileId) : IQuery<HrDownloadUrlDto>`, `HrDownloadUrlDto(string Url)`; autoriza
    `ContractRead`; `null` del puerto ⇒ `HrNotFound.File` (404).
  - Rutas: `POST /files/upload-session` (`ContractManage`) y `GET /files/{fileId:guid}` (`ContractRead`).
  - `FakeHrDocumentStore : IHrDocumentStore`: `Seed(Guid fileId, HrDocumentState state, string fileName = "contrato.pdf")`,
    `Opens`; un id no sembrado completa como `NotFound`; URL `https://files.test/{fileId}`. Se cablea con
    `HrApiHarness.OpenAsync(configureServices: services => services.AddSingleton<IHrDocumentStore>(fake))`.

- [ ] **Step 1: Pruebas que fallan.**
  - Storage: `FileOwnerModulesTests.AnHrDocumentBelongsToHr`, `AnHrDocumentIsSystemOwned`, `HrDocument` al final de
    `OwnerTypesDisabledIn(TenantModuleSet.Empty)`; `FileOwnerModuleHandlersTests.SupportAttachmentsAndTemplateImagesAreTheSystemOwnedFiles`
    con `HrDocument` al final; `SystemOwnedFilesApiTests.SystemOwners` suma `FileOwnerType.HrDocument` y sus dos
    `is MessageTemplate or ChatbotMedia` suman `or HrDocument`.
  - `HrDocumentStoreTests`: `OpenCreatesAnHrDocumentOwnedByTheTenantWithTheTenMegabyteCap`; teoría
    `AStorageDeclarationRejectionIsFileInvalid` (`"contrato.png"`/`"image/png"`; `"contrato.pdf"`/`"application/pdf"` con
    `10_485_761`) ⇒ `hr.file.invalid`; `CompleteReturnsTheStoredFileName`; teoría `CompleteMapsTheOutcome`;
    `AFileOfAnotherTenantIsNotFound`; `TheDownloadUrlIsNullForAnotherOwnersFile`.
  - `HrFilesApiTests`: `TheUploadSessionAnswersTheSignedPut`; teoría `ANonPdfOrALargerFileIsFileInvalid` (y `Opens` vacío);
    `WithoutContractManageTheSessionIs403`; `TheDownloadAnswersTheSignedUrl`; `AnUnknownFileIs404FileNotFound`;
    `WithoutContractReadTheDownloadIs403`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`.
  Expected: `error CS0117: 'FileOwnerType' does not contain a definition for 'HrDocument'`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Run, en fila:
  `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModulesTests|FullyQualifiedName~FileOwnerModuleHandlersTests" -- xUnit.MaxParallelThreads=4`;
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HrDocumentStoreTests" -- xUnit.MaxParallelThreads=4`;
  `dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --filter "FullyQualifiedName~SystemOwnedFilesApiTests" -- xUnit.MaxParallelThreads=4`;
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~HrFilesApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en los cuatro.
- [ ] **Step 5: Commit** — `feat(hr): pdf uploads through storage`

### Task 8: Empleados — lista, ficha, alta, edición, pago, desactivar, eliminar, membresías e historial de acciones

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Application/`: `HrRequests.cs`, `HrRequestValidators.cs`, `HrDtos.cs`,
  `EmployeeDetailComposer.cs`, `IHrMemberDirectory.cs`, `ActivityChanges.cs`, `ActivityWriter.cs`, `EmployeeRules.cs`,
  `HrCountryIndex.cs`, `EmployeeCountry.cs`, `NewContractFactory.cs`, `ListEmployees.cs`, `GetEmployee.cs`, `CreateEmployee.cs`, `UpdateEmployee.cs`,
  `UpdateEmployeePayment.cs`, `DeactivateEmployee.cs`, `DeleteEmployee.cs`, `ListAvailableMembers.cs`,
  `ListEmployeeActivity.cs`; completar `HrAuditActions.cs`
- Create: `src/Bootstrapper/HrMemberDirectory.cs` (calco de `LoansMemberDirectory.cs` sin `IRolePermissionChecker`, más la
  búsqueda por `Membership.UserId`); registrar puerto, composer y handlers
- Modify: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` (las de `/employees/import/*` llegan en la Tarea 10 **antes** que
  `/employees/{id:guid}`)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/HrRequestValidatorsTests.cs`, `ActivityChangesTests.cs`,
  `HrCountryIndexTests.cs`, `EmployeeCountryTests.cs`;
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrAccessApiTests.cs`, `EmployeeApiTests.cs`, `HrApiSeeds.cs` (empresa
  «Comercializadora QEP S.A.S.» por `HrApiHarness.CreateCompanyAsync`, sede «Principal» de esa empresa, centro «Bodega», área
  «Operaciones» y cargo «Analista» por API, y un alta reutilizable: esa empresa, `Employee`, `FixedTerm`, `Monthly`,
  2026-01-15 a 2027-01-14, 2 500 000, documento `CC` y país `null`, con parámetros para cambiar empresa, tipo, duración,
  fechas, documento y país; `SeedSecondCompanyAsync` agrega otra empresa con su sede «Norte» y centro «Planta»)

**Interfaces:**
- Consumes: Tareas 2–7.
- Produces:
  - Requests (`HrRequests.cs`, campos anulables para que el binding no responda 400): `EmployeeRequest`,
    `CreateEmployeeRequest : EmployeeRequest` (+ `PaymentRequest? Payment`, `NewContractRequest? Contract`), `PaymentRequest`,
    `ContractTermsRequest`, `InitialSalaryRequest`, `InitialAssignmentRequest`, `NewContractRequest`, `DeactivateRequest(DateOnly? Date)`,
    con los campos de «Requests» del contrato.
  - `EmployeeCountry` (`internal static`): `Colombia = "CO"`; `string? Normalize(string? code)` (recorta y mayúsculas, como
    `CompanyWriteRules.NormalizeCountry`); `string Effective(string? requested, string? tenantCountry)` = `Normalize(requested)`
    ?? `tenantCountry` ?? `"CO"`; `void EnsureKnown(string country, HrCountryIndex countries)` (fuera del catálogo de
    Geography ⇒ `HrFieldErrors.Throw("country", "Elige un país de la lista.")`; el país del tenant no se revalida), y `void Ensure(string country, HrDocumentType documentType, bool foreignPensionExempt)`:
    `CC` con país ≠ `CO` ⇒ `HrFieldErrors.Throw("country", "Con cédula de ciudadanía el país es Colombia.")`; `CE`, `PEP`,
    `PPT`, `PA`, `CD` o `SC` con `CO` ⇒ `("country", "Con un documento extranjero el país no puede ser Colombia.")`;
    `foreignPensionExempt` con `CO` ⇒ `("foreignPensionExempt", "Sólo un empleado de otro país puede estar exento de pensión.")`.
    Una sola tabla de la regla, que reusa la importación. El país del tenant sale de
    `ITenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), ct)` (`src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs:35`)
    → `TenantRegionalSettings.Country` (`:42`; default `"CO"` en el record), la misma lectura que `/auth/me`
    (`src/Api/SessionEstablisher.cs:95`) y que `CreateTicketHandler` de Support; `null` (tenant sin fila) ⇒ `"CO"`, como
    `DistributorDefaults.Country` de Wholesale. Hr Application ya puede referenciar `Modules.Tenancy`.
  - Validadores (`HrValidator<…>`, mensajes en español): `EmployeeRequestValidator` (topes, `documentType` con
    `HrEnums.TryParse<HrDocumentType>`, `documentNumber` sin espacios internos, `country` nulo o con la forma de `CompanyWriteRules.IsValidCountry` (dos letras ASCII) ⇒ si no,
    `country` —la pertenencia al catálogo la revisa el handler con `EmployeeCountry.EnsureKnown`, porque necesita el puerto—, `phone` `^\+[1-9][0-9]{7,14}$`, correos `EmailAddress()` ≤ 254, `contributorSubtype` válido, `restDays` 1..6
    nombres de `DayOfWeek` distintos; el cruce país–documento y el exento **no** van acá: dependen del país efectivo, que
    puede salir del tenant, y los aplica el handler con `EmployeeCountry.Ensure`); `PaymentRequestValidator` (reglas del contrato; `bankAccountNumber` `^[0-9]{1,17}$`);
    `ContractTermsRequestValidator` (`companyId` requerido y ≠ `Guid.Empty`; enums; `endDate` requerido en `FixedTerm`/`WorkOrLabor`, nulo en `Indefinite`, ≥
    `startDate`; `Integral` sólo con `Employee` ⇒ `salaryType`); `InitialSalaryRequestValidator` (`amount` > 0, ≤ 2 decimales,
    < 10^16); `InitialAssignmentRequestValidator` (`positionId`, `workplaceId` requeridos); `NewContractRequestValidator`
    (compone los tres con `SetValidator`: claves `contract.endDate`, `salary.amount`, `assignment.positionId`);
    `CreateEmployeeRequestValidator` (`Include(EmployeeRequestValidator)`, `Payment` con `When` y `Contract` **requerido**:
    claves `payment.bankName`, `contract.contract.endDate`).
  - `NewContractFactory.BuildAsync(Guid tenantId, NewContractRequest request, string errorPrefix, bool requireActiveCompany, CancellationToken) : Task<(NewContract Contract, SalaryFloor FloorAtStart)>`
    (scoped): con `requireActiveCompany` (alta, reincorporación, importación) resuelve la empresa con
    `HrCompanyRefs.RequireActiveAsync(…, $"{errorPrefix}contract.companyId")`; sin él (conversión de tipo, D-12) la empresa
    ya es la del vigente y no se pide activa. Luego resuelve el
    PDF (`HrDocumentAttachment`), carga cargo, área y centro con `IHrSettingsRepository` (inexistente,
    archivado o con sede archivada ⇒ `HrFieldErrors.Throw($"{errorPrefix}assignment.positionId", "Elige un cargo vigente.")`,
    ídem `areaId`, `workplaceId`), toma `SiteId` del centro y comprueba que la sede sea de la empresa
    (`ContractRules.EnsureWorkplaceOfCompany`, abajo), resuelve `SalaryRules.FloorAtAsync(startDate)` y arma ids nuevos.
    La reusan el alta (`"contract."`), la reincorporación y la conversión (`""`) y la importación. La conversión compara antes
    `companyId` con la empresa del vigente (dominio, `CompanyChangeNotAllowed`); el factory no lo sabe.
  - `ContractRules.EnsureWorkplaceOfCompany(Site site, Guid companyId, string field)` (`internal static`; nace acá y la Tarea 9
    la reusa): `site.CompanyId != companyId` ⇒
    `new HrDomainException(HrErrorCodes.WorkplaceOtherCompany, "El centro de trabajo es de otra empresa.", { [field] = ["Elige un centro de trabajo de la empresa del contrato."] })`
    (422 con `code` y `errors`, `IHasFieldErrors`).
  - DTOs (`HrDtos.cs`): uno por bloque de «DTOs» del contrato, mismos nombres y campos, enums como `string`. Comentario BFF en
    `EmployeeDetailDto` («every write answers the whole detail: the page repaints from the response»).
  - `EmployeeDetailComposer` (scoped, calco de `LoanDtoComposer`):
    `ComposeAsync(Employee employee, bool canReadContract, DateOnly today, CancellationToken) : Task<EmployeeDetailDto>` y
    `ComposePageAsync(EmployeeListPage page, int pageNumber, int pageSize, bool canReadContract, DateOnly today, CancellationToken) : Task<EmployeePageDto>`.
    Enmascarado de «Convenciones». `current` con `EffectiveDated.CurrentOf`, `scheduled`/`current` por registro,
    `renewalNoticeDue` y `daysUntilEnd` con `FixedTermLimits`, `justCause` de `TerminationReason`, `canDelete` de
    `Employee.CanDelete`, `employee.country` del perfil, `contract.company` y `company` de la lista con **un**
    `HrCompanyRefs.ResolveAsync` sobre las empresas de los contratos de la respuesta (o de la página). Configuración, ciudades y miembros con **una** llamada a cada puerto por respuesta; un miembro que el
    directorio no devuelve se pinta `{ memberId, displayName: "Sin nombre", isActive: false }`. Quien llama decide
    `canReadContract` con `executionContext.HasPermission(HrPermissions.ContractRead)`, también en **toda** escritura.
  - `IHrMemberDirectory`: `FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken)`,
    `ListActiveAsync(Guid tenantId, CancellationToken)` (por `DisplayName` y luego id) y
    `FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken) : Task<HrMemberRef?>` (membresía del tenant con
    `UserId == subjectId`), con `sealed record HrMemberRef(Guid MemberId, string DisplayName, bool IsActive)`.
  - `ActivityChanges` (`internal static`, puro): `Diff(EmployeeProfile before, EmployeeProfile after)`,
    `Diff(PaymentDetails? before, PaymentDetails? after)` y constructores por acción (`SalaryCreated(SalaryRecord, names)`,
    `AssignmentCreated(AssignmentRecord? previous, AssignmentRecord next, IReadOnlyDictionary<Guid, string> names)`, …) que
    devuelven `IReadOnlyList<ActivityChange>`. Campos camelCase; sólo los que cambian; referencias por nombre; `Sensitive =
    true` en todo campo de pago, salario, cargo, lugar y contrato.
  - `ActivityWriter` (scoped): `Task AppendAsync(Guid tenantId, Employee employee, HrActivityAction action, HrActivityCategory category, Guid? contractId, IReadOnlyList<ActivityChange> changes, CancellationToken)`
    — actor = `executionContext.SubjectId` y su nombre por `FindBySubjectAsync` («Sin nombre» si no hay); `OccurredAt` de `IClock`.
  - Casos de uso (handler = `I*Handler<…>`; cada uno autoriza, valida, carga, muta dentro de `HrConflicts.Run`, escribe el
    historial de acciones, audita, guarda y compone):
    - `ListEmployeesQuery(Guid TenantId, string? Search, string? Status, bool ExpiringSoon, Guid? CompanyId, int Page, int PageSize)`,
      `DefaultPageSize = 25`, `MaxPageSize = 100`; validador: `status` nulo o nombre exacto, `page` ≥ 1, `pageSize` 1..100;
      `ExpiringSoon` o `CompanyId` sin `ContractRead` ⇒ 403; `summary.expiringSoon` `null` sin `ContractRead`; un
      `companyId` desconocido da la lista vacía, no un error.
    - `GetEmployeeQuery(Guid TenantId, Guid EmployeeId)`.
    - `CreateEmployeeCommand(Guid TenantId, CreateEmployeeRequest Request)`: autoriza los cuatro; `HrSetup.IsCompleteAsync` falso
      ⇒ `HrDomainException(SetupIncomplete)`; ciudad (`EmployeeRules.EnsureCityAsync` ⇒ `errors.cityId`), miembro
      (`EnsureMemberAsync` ⇒ `hr.employee.member_invalid`), `birthDate` ≥ hoy ⇒ `errors.birthDate`, país efectivo
      (`EmployeeCountry.Effective` con el país del tenant) `EmployeeCountry.EnsureKnown` (con un `ListCountriesAsync`) y `EmployeeCountry.Ensure`; `NewContractFactory` con
      `"contract."` y `requireActiveCompany: true`; `Employee.Hire`; actividad
      `EmployeeCreated` (`Lifecycle`, con los cambios sensibles marcados); audita `hr.employee.created`. 201.
    - `UpdateEmployeeCommand(Guid TenantId, Guid EmployeeId, EmployeeRequest Request, long ExpectedVersion)` — versión distinta ⇒
      `RequestConcurrencyException`; miembro sólo se revalida si cambió; `country: null` vuelve a tomar el país del tenant
      (el `PUT` reemplaza todo) y pasa por `EmployeeCountry.EnsureKnown` (sólo si viene un país) y `EmployeeCountry.Ensure`; actividad `PersonalDataUpdated` (`Personal`) sólo si
      hubo cambios, con `country` como un campo más, no sensible.
    - `UpdateEmployeePaymentCommand(…, PaymentRequest Request, long ExpectedVersion)`: autoriza `EmployeeRead, ContractRead,
      ContractManage`; actividad `PaymentUpdated` (`Payment`).
    - `DeactivateEmployeeCommand(Guid TenantId, Guid EmployeeId, DeactivateRequest Request)`: `date < ActiveContract.StartDate`
      ⇒ `errors.date`; actividad `EmployeeDeactivated` (`Lifecycle`).
    - `DeleteEmployeeCommand(Guid TenantId, Guid EmployeeId)`: `EnsureDeletable`; `Remove`; audita `hr.employee.deleted`. 204.
    - `ListAvailableMembersQuery(Guid TenantId)`: `ListActiveAsync` menos `ListLinkedMemberIdsAsync`.
    - `ListEmployeeActivityQuery(Guid TenantId, Guid EmployeeId, int Page, int PageSize)` (autoriza `EmployeeRead`; sin
      `ContractRead` pide sólo `Personal` y `Lifecycle` y quita los cambios `Sensitive`).
  - `HrAuditActions`: `hr.employee.created|updated|payment_updated|deactivated|reincorporated|deleted|imported`,
    `hr.contract.salary_created|salary_rewritten|assignment_created|renewed|converted_to_indefinite|type_changed|terminated|document_updated`,
    `hr.settings.created|updated|archived|unarchived|deleted`; `Resources.Employee = "employee"`, `Contract = "contract"`,
    `Site`, `Workplace`, `Area`, `Position`. `changedFields` con nombres camelCase; nunca valores.
  - Rutas: `GET /employees`, `POST /employees` (201), `GET /employees/{employeeId:guid}`, `PUT /employees/{employeeId:guid}`
    (`If-Match`), `PUT …/payment` (`If-Match`, política `ContractManage`), `POST …/deactivate`, `DELETE …` (204),
    `GET …/activity`, `GET /members-available`.

- [ ] **Step 1: Pruebas que fallan.**
  - `HrRequestValidatorsTests`: `AFixedTermWithoutEndDateFailsOnTheNestedKey` (alta ⇒ `contract.contract.endDate`);
    `AnIndefiniteContractWithEndDateFails`; `AWorkOrLaborNeedsItsEstimatedEnd`; `AnEndDateBeforeTheStartFails`;
    `IntegralIsOnlyForEmployees` (`Contractor` + `Integral` ⇒ `contract.contract.salaryType`);
    `APhoneThatIsNotE164Fails` (`"3001234567"` ⇒ `phone`); `AMalformedCountryFails` (`"COL"` y `"1A"` ⇒ `country`; `"XX"` pasa el validador: lo frena el handler);
    `ALowercaseCountryIsAccepted` (`" ve "` pasa); `ANullCountryIsAccepted`; `RestDaysNeedOneToSixDistinctDays` (`[]`, siete días y `["Sunday","Sunday"]`
    fallan); `ABankTransferNeedsTheThreeFields` (`payment.bankAccountNumber`); `OtherPaymentRejectsBankFields`;
    `AnAccountNumberOfEighteenDigitsFails` (`"123456789012345678"`); `AnEnumNumberIsNotAName` (`contractType: "1"`);
    `ASalaryWithThreeDecimalsFails` (`contract.salary.amount`); `AContractWithoutCompanyFails` (alta ⇒
    `contract.contract.companyId`).
  - `HrCountryIndexTests` (índice armado con cuatro países de prueba, Colombia, Perú, Venezuela y Estados Unidos):
    `ACodeOutsideTheCatalogIsUnknown` (`"DE"` ⇒ `false`, aunque tenga forma de país); `NamesMatchWithoutAccents`
    (`"peru"` y `" PERÚ "` ⇒ `PE`; `"pe"` ⇒ `PE`); `NamesAreListedForTheTemplate`.
  - `EmployeeCountryTests.AnUnknownCountryIsRejectedOnCountry` (`EnsureKnown("DE", …)` ⇒ `country`).
  - `EmployeeCountryTests`: `NullTakesTheTenantCountry` (`null`, tenant `"MX"` ⇒ `"MX"`); `WithoutTenantCountryItIsColombia`
    (`null`, `null` ⇒ `"CO"`); `TheRequestedCountryWins` (`"ve"`, tenant `"CO"` ⇒ `"VE"`); `ACcNeedsColombia` (`CC` + `VE` ⇒
    `country`); teoría `AForeignDocumentCannotBeColombian` (`CE`, `PEP`, `PPT`, `PA`, `CD`, `SC` con `CO` ⇒ `country`; con
    `VE` pasan); `ForeignPensionExemptNeedsAForeignCountry` (`CC` + `CO` + exento ⇒ `foreignPensionExempt`; `PPT` + `VE` +
    exento pasa).
  - `ActivityChangesTests`: `OnlyChangedFieldsAreListed`; `PaymentChangesAreSensitive`; `AssignmentChangesUseNamesAtWriteTime`;
    `ACountryChangeIsPersonalAndNotSensitive` (`CO` ⇒ `VE`: un cambio `country`, `Sensitive == false`).
  - `HrAccessApiTests`: `WithoutTheModuleTheListIs403`; `AnotherTenantInTheRouteIs403AuthorizationDenied`;
    `ManageWithoutReadIs403` (sólo `hr.employee.manage` ⇒ `PUT` 403); `CreatingNeedsTheFourPermissions` (sin
    `hr.contract.manage` ⇒ 403); `AReaderWithoutContractReadGetsTheDetailMasked` (`canReadContract == false`,
    `employee.payment == null`, `contract == null`, `previousContracts` vacío; en la lista `position == null`,
    `renewalNoticeDue == null`, `company == null`, `summary.expiringSoon == null`; `employee.country` sí llega);
    `ExpiringSoonWithoutContractReadIs403`; `FilteringByCompanyWithoutContractReadIs403`; `WithoutCompaniesTheModuleIsOff`
    (`OpenAsync([Hr])` sin `Companies` ⇒ la lista 403, porque `hr` queda apagado en la práctica);
    `TheActivityWithoutContractReadHidesCompensation` (tras alta, pago y salario: sólo la entrada `EmployeeCreated`, sin
    cambios sensibles).
  - `EmployeeApiTests` (reloj 2026-11-20T15:00:00Z; configuración sembrada por `HrApiSeeds`):
    `CreatesAnEmployeeWithTheFullContract` (201; `employee.status == "Active"`, `employee.version == 1`, `restDays ==
    ["Sunday"]`, `contract.current.salary == 2500000`, `contract.current.site.name == "Principal"`, una entrada en `salaries` y
    una en `assignments`, `canDelete == true`, `employee.country == "CO"` con el país `null`, `contract.company.name ==
    "Comercializadora QEP S.A.S."` y su `taxId`); `CreatingWithoutSetupIs422SetupIncomplete` (sin cargos; y aparte, con todo
    configurado pero la única empresa desactivada); `TheCountryDefaultsToTheTenants` (tenant sembrado con país `MX`,
    documento `PPT`, país `null` ⇒ `employee.country == "MX"`);
    `ACcWithAnotherCountryIs422OnCountry`; `AForeignDocumentWithColombiaIs422OnCountry` (`CE` + `"CO"`);
    `AnUnknownCountryIs422OnCountry` (`"XX"`); `ForeignPensionExemptWithColombiaIs422` (`CC` + exento ⇒
    `errors.foreignPensionExempt`; `PPT` + `"VE"` + exento ⇒ 201); `ChangingTheCountryIsLoggedAsPersonalData` (`PUT` con
    `PPT` y `"VE"` ⇒ una entrada `PersonalDataUpdated` con el cambio `country` visible sin `hr.contract.read`);
    `AContractNeedsAnActiveCompany` (empresa desactivada y empresa de otro tenant ⇒ 422 `errors["contract.contract.companyId"]`);
    `DeletingACompanyWithAnEmployeeIs422InUse` (D-11: con un empleado en una sede de una segunda empresa, archivar y
    borrar esa sede no es posible porque el contrato la usa; `DELETE /api/v1/tenants/{tenantId}/companies/{id}` de esa empresa ⇒
    422 `code == "companies.company.in_use"` y el `GET` del empleado sigue respondiendo su `contract.company`);
    `AWorkplaceOfAnotherCompanyIs422OnTheNestedKey` (`SeedSecondCompanyAsync`; alta con la primera empresa y el centro
    «Planta» ⇒ 422 `code == "hr.assignment.workplace_other_company"` y `errors["contract.assignment.workplaceId"]`);
    `TheListShowsAndFiltersByCompany` (un empleado por empresa ⇒ `items[].company` con el nombre; `companyId=` de la segunda ⇒
    sólo el suyo; un retirado ⇒ `company == null`);
    `AnArchivedPositionIs422OnTheNestedKey` (`errors["contract.assignment.positionId"]`);
    `ABelowMinimumHireNeedsAcknowledgement` (`1750904` ⇒ 422 `hr.salary.below_minimum_unacknowledged`; con
    `acknowledgeBelowMinimum: true` ⇒ 201 y `salaries[0].belowMinimumAcknowledged == true`);
    `AnIntegralHireBelowTheFloorIs422`; `AFixedTermPastFourYearsIs422CapExceeded`; `TheListFiltersByStatusAndKeepsTheSummary`;
    `ExpiringSoonListsOnlyFixedTermWithinThirtyDays` (fin 2026-12-10 ⇒ `renewalNoticeDue == true`, `summary.expiringSoon == 1`);
    `SearchMatchesNameOrDocument`; `UpdateWithoutIfMatchIs428AndWithAStaleVersionIs412`; `ADuplicateDocumentIs409DocumentTaken`;
    `AMembershipOfAnotherTenantIs422MemberInvalid`; `AnAlreadyLinkedMemberIs409MemberTaken`; `AnUnknownCityIs422OnCityId`;
    `ThePaymentNeedsContractManageAndIfMatch`; `ClearingThePaymentNullsIt`;
    **`AWriteByAManagerWithoutContractReadAnswersMasked`** (Review Focus 2: permisos `hr.employee.read` + `hr.employee.manage`,
    `PUT` de la ficha de un empleado con pago y contrato ⇒ 200 con `canReadContract == false`, `employee.payment == null`,
    `contract == null`, `previousContracts` vacío); `DeactivatingClosesTheContractAndAnswersDeactivated`
    (`employee.status == "Deactivated"`, `previousContracts[0].status == "Closed"`, `endedOn == "2026-11-30"`);
    `DeactivatingTwiceIs422NotActive`; `ANewHireCanBeDeleted` (204 y luego `GET` 404); `AnEmployeeWithASecondSalaryIs422NotDeletable`;
    `MembersAvailableListsActiveMembershipsWithoutALinkedEmployee`; `TheActivityListsTheCreationWithTheActorName`
    (`actor.displayName` de la membresía sembrada); `EveryWriteIsAudited` (`hr.employee.created`, `updated`, `payment_updated`,
    `deactivated`, uno cada uno).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'CreateEmployeeRequestValidator' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Unitarias completas; la integración por nombre mientras se itera
  (`--filter "FullyQualifiedName~EmployeeApiTests.CreatesAnEmployeeWithTheFullContract"`); al terminar,
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~EmployeeApiTests|FullyQualifiedName~HrAccessApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): employee endpoints with masked compensation and activity log`

### Task 9: Contrato — salarios, cargo y lugar, prórrogas, conversiones, terminar, reincorporar y documentos

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Application/` (`ContractRules.cs` ya existe desde la Tarea 8 y se amplía): `AddSalary.cs`, `RewriteSalary.cs`,
  `AddAssignment.cs`, `RenewContract.cs`, `ConvertContractToIndefinite.cs`, `ChangeContractType.cs`, `TerminateContract.cs`,
  `ReincorporateEmployee.cs`, `SetHrDocument.cs` (contrato y los tres registros)
- Modify: `HrRequests.cs`, `HrRequestValidators.cs`, `ActivityChanges.cs`, `HrEndpoints.cs`, el bloque de Hr de
  `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/HrRequestValidatorsTests.cs` (sumar),
  `tests/Modules/Hr/Modules.Hr.IntegrationTests/EmployeeContractApiTests.cs`, `EmployeeRecordsApiTests.cs`

**Interfaces:**
- Consumes: Tareas 3–8.
- Produces:
  - Requests: `SalaryRequest`, `SalaryRewriteRequest`, `AssignmentRequest`, `RenewalRequest`, `TerminateRequest`,
    `DocumentRequest(Guid? FileId)`, con los campos del contrato.
  - Validadores: `SalaryRequestValidator` (`effectiveDate` requerido, `amount` como el inicial);
    `SalaryRewriteRequestValidator` (`amount`; `confirmRewrite` **no** se valida acá: su ausencia es el código de dominio
    `hr.salary.rewrite_unconfirmed`, para que el frontend abra la confirmación); `AssignmentRequestValidator`;
    `RenewalRequestValidator` (`newEndDate` requerido); `TerminateRequestValidator` (`reason` con
    `HrEnums.TryParse<TerminationReason>`, `date` requerido, `notes` ≤ 1000).
  - `ContractRules` (`internal static`): `FindContract(Employee, Guid) : EmploymentContract` (⇒ `HrNotFound.Contract`),
    `FindSalary`, `FindAssignment`, `FindRenewal` (⇒ su 404), y los chequeos de campo **antes** de mutar:
    `effectiveDate > contract.EndDate` (con `EndDate`) ⇒ `errors.effectiveDate`; `newEndDate <= contract.EndDate` ⇒
    `errors.newEndDate`; `date < contract.StartDate` ⇒ `errors.date`. Un `HrDomainException` con `*NotFound` del dominio se
    traduce a `ResourceNotFoundException`.
  - Casos de uso (autorizan `EmployeeRead, ContractRead, ContractManage`; cargan con `FindAsync` o `HrNotFound.Employee`;
    resuelven el PDF antes de mutar; resuelven `SalaryRules.FloorAtAsync` a la fecha que toca; mutan dentro de
    `HrConflicts.Run`; escriben la actividad; auditan; guardan; responden `EmployeeDetailDto` con `canReadContract: true`;
    200 todos):
    - `AddSalaryCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, SalaryRequest Request)` — piso a `effectiveDate`;
      actividad `SalaryCreated` (`Salary`, `amount` antes = vigente hoy, después = nuevo, más `effectiveDate`).
    - `RewriteSalaryCommand(…, Guid SalaryId, SalaryRewriteRequest Request, long ExpectedVersion)` — piso a la fecha del
      registro; actividad `SalaryRewritten`.
    - `AddAssignmentCommand(…, AssignmentRequest Request)` — cargo, área y centro con las mismas reglas de
      `NewContractFactory` (claves sin prefijo), más `ContractRules.EnsureWorkplaceOfCompany(site, contract.CompanyId, "workplaceId")`
      (422 `hr.assignment.workplace_other_company` con `errors.workplaceId`); no revalida que la empresa siga activa (D-12);
      actividad `AssignmentCreated` (`Assignment`, antes y después por nombre).
    - `RenewContractCommand(…, RenewalRequest Request)`; actividad `ContractRenewed` (`Contract`, `endDate`).
    - `ConvertContractToIndefiniteCommand(Guid TenantId, Guid EmployeeId, Guid ContractId)` — `today` del `ITenantClock`;
      actividad `ContractConvertedToIndefinite`.
    - `ChangeContractTypeCommand(…, NewContractRequest Request)` — **antes** del factory, `contract.companyId` distinto de
      la empresa del vigente ⇒ `HrDomainException(CompanyChangeNotAllowed, "Convertir no cambia la empresa: termina el contrato y reincorpora en la otra.")`
      (así el error dice la regla y no «elige un centro de esta empresa»; el dominio repite la guarda); luego
      `NewContractFactory` con prefijo `""` y `requireActiveCompany: false` (D-12: misma empresa, activa o no); actividad `ContractTypeChanged` con `contractId` del **nuevo**.
    - `TerminateContractCommand(…, TerminateRequest Request)`; actividad `ContractTerminated` (`Lifecycle`: `status` no
      sensible; `reason` y `date` sensibles **(llamada del plan)**).
    - `ReincorporateEmployeeCommand(Guid TenantId, Guid EmployeeId, NewContractRequest Request)` — cualquier empresa activa
      (`NewContractFactory` con prefijo `""` y `requireActiveCompany: true`)
      (es como se cambia de empresa); actividad `EmployeeReincorporated` (`Lifecycle`, con `company` por nombre entre los
      cambios sensibles).
    - `SetHrDocumentCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, HrDocumentTarget Target, Guid? RecordId, Guid? FileId)`
      con `enum HrDocumentTarget { Contract, Salary, Assignment, Renewal }`; actividad `DocumentAttached` (`Contract`).
  - Rutas: las doce filas de contrato de la tabla de rutas, más `POST /employees/{employeeId:guid}/reincorporate`, todas con
    `RequireAuthorization(HrPermissions.ContractManage)`; `If-Match` sólo en el `PUT` de salario.

- [ ] **Step 1: Pruebas que fallan.**
  - `HrRequestValidatorsTests`: `ASalaryWithoutEffectiveDateFails`; `AnAssignmentWithoutWorkplaceFails`;
    `AReincorporationWithoutCompanyFails` (`NewContractRequest` sin `contract.companyId` ⇒ `contract.companyId`);
    `ATerminationWithoutReasonFails` (`reason`); `AnUnknownReasonFails` (`"Fired"`); `ARewriteWithoutConfirmationPassesValidation`.
  - `EmployeeRecordsApiTests` (alta de `HrApiSeeds`: inicio 2026-01-15, fin 2027-01-14 salvo que la prueba diga otra;
    reloj 2026-11-20T15:00:00Z):
    `ARaiseIsANewRecordAndTheOldOneStays` (2026-12-01 a 2 800 000 ⇒ `salaries` de dos, `salaries[0].scheduled == true`,
    `current.salary == 2500000`); `ASalaryOnOrBeforeTheLastIs422`; `ASalaryAfterTheEndDateIs422OnEffectiveDate` (2027-02-01);
    **`ASalaryEffectiveTomorrowInTheTenantZoneIsStillScheduled`** (Review Focus 1: reloj 2026-12-01T04:30:00Z, salario
    2026-12-01 ⇒ `salaries[0].scheduled == true`, `current.salary == 2500000`; reloj a 2026-12-01T05:00:00Z y `GET` ⇒
    `current.salary == 2800000`, `salaries[0].current == true`, `scheduled == false`);
    `ARewriteWithoutConfirmationIs422RewriteUnconfirmed`; `ARewriteNeedsIfMatch`; `ARewriteKeepsTheDateAndChangesTheAmount`;
    `ABelowMinimumRaiseNeedsAcknowledgement`; `AnIntegralRecordBelowTheFloorIs422`;
    **`AWorkplaceChangeMidMonthIs422AndAPositionChangeIsNot`** (Review Focus 3: otro centro el 2026-12-15 ⇒ 422
    `hr.assignment.site_change_not_first_of_month`; otro cargo, mismo centro, el 2026-12-15 ⇒ 200 y `assignments[0].scheduled == true`);
    `AWorkplaceChangeOnTheFirstIsAccepted` (2026-12-01 ⇒ `assignments[0].site.name` de la sede del centro nuevo);
    `ANoOpAssignmentIs422Unchanged`; `AnArchivedPositionIs422OnPositionId`;
    **`AWorkplaceOfAnotherCompanyIs422`** (Review Focus 6: `SeedSecondCompanyAsync`; centro «Planta» el 2026-12-01 ⇒ 422
    `code == "hr.assignment.workplace_other_company"`, `errors["workplaceId"]`, la `version` no cambia);
    `ADeactivatedCompanyKeepsItsContract` (D-12: desactivar la empresa después del alta ⇒ `GET` con `contract.company.isActive ==
    false`; un salario, un cambio de cargo, una prórroga y la conversión a indefinido ⇒ 200); `ADocumentCanBeAttachedToARecordLater`
    (`salaries[0].document.fileName == "contrato.pdf"`); `ARejectedDocumentIs422AndWritesNothing` (`hr.file.rejected`, la
    `version` no cambia); `AnUnknownSalaryIs404`; `AContractOfAnotherEmployeeIs404`.
  - `EmployeeContractApiTests`:
    **`TheFourthShortRenewalNeedsOneYearAndTheCapBlocksPastFourYears`** (Review Focus 4: alta 2026-01-15 a 2026-12-14;
    prórrogas a 2027-06-14, 2027-12-14 y 2028-06-14 ⇒ tres `short: true`; a 2028-12-14 ⇒ 422
    `hr.contract.renewal_minimum_one_year`; a 2029-06-14 ⇒ 200 y `short: false`; a 2030-01-15 ⇒ 422
    `hr.contract.fixed_term_cap_exceeded` — el tope se evalúa antes que el año mínimo; el 200 justo en el tope lo cubre
    `ARenewalPastTheCapIsRejected` de la Tarea 4);
    `ARenewalNotPastTheCurrentEndIs422OnNewEndDate`; `OnlyFixedTermRenews` (indefinido ⇒ 422 `hr.contract.not_fixed_term`);
    `ConvertingToIndefiniteClearsTheEndAndKeepsTheStart` (`duration == "Indefinite"`, `endDate == null`,
    `convertedToIndefiniteOn == "2026-11-20"`, `startDate == "2026-01-15"`, `renewalNoticeDue == false`);
    `AConversionCreatesALinkedContract` (aprendiz lectiva ⇒ productiva desde 2026-12-01: `contract.previousContractId` =
    el viejo, `previousContracts[0].status == "Converted"`, `endedOn == "2026-11-30"`, `employee.status == "Active"`);
    `AConversionStartingBeforeTheCurrentStartIs422`; `AConversionWithAScheduledRecordAfterTheCloseIs422RecordsAfterEnd`;
    **`AConversionCannotChangeTheCompany`** (Review Focus 6: conversión con `companyId` de la segunda empresa y un centro suyo
    ⇒ 422 `hr.contract.company_change_not_allowed`, nada cambia); `AConversionKeepsTheCompany` (⇒ `contract.company.id` y
    `previousContracts[0].company.id` iguales); `AConversionInADeactivatedCompanyWorks` (D-12: desactivar la empresa y convertir aprendiz
    lectiva ⇒ productiva con su `companyId` ⇒ 200, el nuevo contrato con `company.isActive == false`);
    `ATerminationInADeactivatedCompanyWorks`; `ReincorporatingInADeactivatedCompanyIs422` (retirado, reincorporar con la empresa
    ya desactivada ⇒ 422 `errors["contract.companyId"]`); `ReincorporatingInAnotherCompanyChangesTheEmployer` (terminar y reincorporar con
    la segunda empresa y su centro ⇒ `contract.company` la segunda, `previousContracts[0].company` la primera);
    `ReincorporatingWithAWorkplaceOfAnotherCompanyIs422` (⇒ `errors["assignment.workplaceId"]`);
    `TerminatingRetiresTheEmployee` (`DismissalWithJustCause`, 2026-11-30 ⇒ `contract == null`,
    `previousContracts[0].termination.justCause == true`, `employee.status == "Retired"`); `ATerminationBeforeTheStartIs422OnDate`;
    `WritesOnATerminatedContractAre422NotActive`; `ReincorporatingARetiredEmployeeCreatesANewContract` (desde 2026-12-15 ⇒
    `employee.status == "Active"`, dos contratos); `ReincorporatingOnThePreviousEndIs422StartNotAfterPrevious`;
    `ReincorporatingAnActiveEmployeeIs422AlreadyActive`; `EveryContractWriteBumpsTheVersion`;
    `ContractWritesAreAuditedAndLogged` (`hr.contract.salary_created`, `assignment_created`, `renewed`, uno cada uno, y tres
    entradas nuevas en `GET …/activity`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'SalaryRequestValidator' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Unitarias completas; la integración por nombre mientras se itera; al terminar,
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~EmployeeRecordsApiTests|FullyQualifiedName~EmployeeContractApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): salary and assignment records, renewals, conversions and termination`

> **Cierre del Bloque C:** `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests` y `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests`
> completos (una corrida cada uno, `-- xUnit.MaxParallelThreads=4`), más `HrDocumentStoreTests` y las tres clases de Storage de la
> Tarea 7. Una revisión del bloque.

---

## Bloque D — Importación y cierre (Tareas 10–11)

### Task 10: Importación por Excel (plantilla, vista previa, confirmación)

Calco de la importación de Contratos de servicio (`ServiceContractsImportColumns.cs`, `IExcelServiceContractsImporter.cs`,
`ServiceContractsImportAnalyzer.cs`, `ExcelServiceContractsRowRules.cs`, `PreviewServiceContractsImport.cs`,
`ImportServiceContracts.cs`, `GetServiceContractsImportTemplate.cs`, `Infrastructure/Excel/ClosedXmlServiceContracts*.cs` y
`ServiceContractsEndpoints.cs:131-195`), con estas diferencias: **todo o nada**, sólo `.xlsx` ≤ 5 MB, ≤ 1000 filas, avisos
además de errores, y la plantilla depende de la configuración del tenant.

**Files:**
- Create en `src/Modules/Hr/Modules.Hr.Application/`: `HrImportColumns.cs`, `HrLabels.cs`, `HrText.cs`, `HrCityIndex.cs`,
  `HrSettingsIndex.cs`, `IHrPhoneNormalizer.cs`, `IExcelEmployeeImporter.cs` (fila, lector y constructor de plantilla),
  `EmployeeImportDtos.cs`, `EmployeeImportRowParser.cs`, `EmployeeImportAnalyzer.cs`, `PreviewEmployeeImport.cs`,
  `ImportEmployees.cs`, `GetEmployeeImportTemplate.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Excel/ClosedXmlEmployeeImporter.cs`,
  `Excel/ClosedXmlEmployeeImportTemplateBuilder.cs`; registrarlos en `HrInfrastructureExtensions`
- Create: `src/Bootstrapper/HrPhoneNormalizer.cs` (calco de `WholesalePhoneNormalizer.cs`); registrar puerto, analizador y
  handlers en el bloque de Hr
- Modify: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` (las tres rutas **antes** de `/employees/{employeeId:guid}`,
  `.DisableAntiforgery()` en los dos `POST`, `IFormFile?` anulable como `ServiceContractsEndpoints.cs:167`)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/HrTextTests.cs`, `HrCityIndexTests.cs`, `HrSettingsIndexTests.cs`,
  `EmployeeImportRowParserTests.cs`; `tests/Modules/Hr/Modules.Hr.IntegrationTests/EmployeeImportApiTests.cs` (arma los `.xlsx`
  con `XLWorkbook`, como `ServiceContractsImportApiTests.cs:75`)

**Interfaces:**
- Consumes: Tareas 3–9 (`Employee.Hire`, `Terminate`, `NewContract`, `SalaryFloorPolicy`, `IHrSettingsRepository`,
  `IEmployeeRepository.FindTakenDocumentsAsync`, `IHrGeographyLookup.ListAllCitiesAsync`, `ActivityWriter`,
  `IHrCompanyLookup.ListActiveAsync`, `HrSetup`, `IHrGeographyLookup.ListCountriesAsync` + `HrCountryIndex`, `EmployeeCountry`, `ITenantDirectory` para el país del tenant).
- Produces:
  - `HrImportColumns.Ordered`: los 36 encabezados del contrato, en su orden y con sus tildes. `DataSheetName = "Empleados"`,
    `ValuesSheetName = "Valores válidos"`, `TemplateFileName = "plantilla-empleados.xlsx"`, `MaxRows = 1000`,
    `MaxSizeInBytes = 5 * 1024 * 1024`.
  - `HrLabels` (`public static`): un `IReadOnlyDictionary<TEnum, string>` por enum con las etiquetas **exactas** de la tabla
    del contrato, `Yes = "Sí"`, `No = "No"`, y `TryParse<TEnum>(string? text, out TEnum value)` que compara con `HrText.Fold`.
  - `HrText.Fold(string? text) : string?` = `OrganizationNames.Normalize`; en blanco ⇒ `null`.
  - `HrCityIndex(IReadOnlyList<HrCityRef> cities)` con `HrCityMatch Match(string text)` (`Found`/`NotFound`/`Ambiguous`):

    ```text
    folded = Fold(text)
    candidates = cities with Fold(Name) == folded or Fold(DivipolaName) == folded
    if candidates is empty and text contains " - ":
        split on the LAST " - " → (cityPart, departmentPart), both folded
        candidates = cities with (Fold(Name) == cityPart or Fold(DivipolaName) == cityPart) and Fold(DepartmentName) == departmentPart
    distinct by CityId: 0 ⇒ NotFound, 1 ⇒ Found(id), more ⇒ Ambiguous
    ```
  - `HrSettingsIndex(HrSettingsSnapshot settings, IReadOnlyList<HrCompanyRef> activeCompanies)`:
    `HrCompanyRef? FindCompany(string text)` (por `Fold("<nombre> - <NIT>")` de una empresa activa),
    `HrCompanyRef? SingleCompany` (la única activa, o `null` si hay varias), `PositionId? FindPosition(string text)`,
    `AreaId? FindArea(string text)`, `HrWorkplaceMatch FindWorkplace(string text, Guid companyId)` (`Found(Workplace)`,
    `NotFound`, `OtherCompany`) — sólo **no archivados** (centro con sede no archivada); el centro se busca **entre los de
    sedes de `companyId`** sólo por `Fold("<centro> - <sede>")` (decisión del owner, 2026-11-21: el nombre solo del centro no se
    acepta, aunque sea único); si no aparece ahí pero sí en una sede de otra empresa ⇒ `OtherCompany`;
    `IReadOnlyList<string> CompanyLabels` (`"<nombre> - <NIT>"`, por nombre), `PositionLabels`, `AreaLabels`,
    `WorkplaceLabels` (para la plantilla).
  - `IHrPhoneNormalizer.ToE164(string phone) : string?` (adaptador sobre `IPhoneNumberNormalizer` de Customers, país `CO`).
  - `sealed record ExcelEmployeeRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells)` — texto crudo recortado por
    encabezado de `HrImportColumns`; fechas y números de Excel ya como texto invariante (como `ClosedXmlServiceContractsImporter.cs:112`).
  - `IExcelEmployeeImporter.ReadAsync(Stream content, CancellationToken) : Task<IReadOnlyList<ExcelEmployeeRow>>` — primera
    hoja, encabezados por nombre con `Fold`; ilegible ⇒ `HrDomainException(ImportUnreadable)`; falta un encabezado ⇒
    `ImportHeaderMismatch`; salta filas en blanco.
  - `IEmployeeImportTemplateBuilder.Build(HrSettingsIndex settings) : byte[]` — hoja `Empleados` con los 36 encabezados; hoja
    `Valores válidos` con una columna por lista del contrato (países por nombre de `HrCountryIndex.Names`, empresas de
    `CompanyLabels`); validación de lista sobre esas columnas de `Empleados`, como
    `ClosedXmlServiceContractsImportTemplateBuilder.cs:63-80`.
  - DTOs (`EmployeeImportDtos.cs`): `EmployeeImportRowIssueDto`, `EmployeeImportRowPreviewDto`, `EmployeeImportSummaryDto`,
    `EmployeeImportPreviewDto`, `EmployeeImportedRowDto`, `EmployeeImportResultDto`, `EmployeeImportTemplateFile(byte[] Content, string FileName)`.
  - `sealed record EmployeeImportContext(DateOnly Today, string TenantCountry, HrCountryIndex Countries, HrCityIndex Cities, HrSettingsIndex Settings, IHrPhoneNormalizer Phones, IReadOnlyList<LegalParameterValue> LegalTable)`
    (`TenantCountry` ya resuelto con `EmployeeCountry.Effective(null, …)`)
    y `sealed record ParsedEmployeeRow(int RowNumber, EmployeeProfile Profile, PaymentDetails? Payment, ContractTerms Terms, string CompanyName, decimal Salary, bool Teleworker, bool BelowMinimum, PositionId PositionId, AreaId? AreaId, Workplace Workplace, TerminationReason? RetirementReason, DateOnly? RetirementDate)`
    (`Profile.Country` y `Terms.CompanyId` resueltos; `CompanyName` alimenta `EmployeeImportRowPreviewDto.company`).
  - `EmployeeImportRowParser.Parse(ExcelEmployeeRow row, EmployeeImportContext context) : EmployeeImportRowResult` (`public static`,
    puro) con `sealed record EmployeeImportRowResult(ParsedEmployeeRow? Row, IReadOnlyList<EmployeeImportRowIssueDto> Errors, IReadOnlyList<EmployeeImportRowIssueDto> Warnings)`:
    junta **todos** los errores de la fila; sólo arma `Row` sin errores. `message` en español, una oración que diga qué
    corregir (p. ej. `salary_invalid` ⇒ «El salario debe ser un número mayor que cero, con máximo dos decimales.»). Las reglas
    son las de `EmployeeRequestValidator`, `PaymentRequestValidator`, `ContractTermsRequestValidator` y del dominio, con el
    código de fila de la lista del contrato; las no obvias:
    - Fechas: `yyyy-MM-dd`, `dd/MM/yyyy`, `d/M/yyyy` (los formatos de `ExcelServiceContractsRowRules`).
    - `CIUDAD`: `context.Cities.Match` ⇒ `city_not_found` / `city_ambiguous`.
    - `PAÍS`: vacía ⇒ `context.TenantCountry`; si no, `context.Countries.FindByCodeOrName` ⇒ `null` es `country_invalid`. Con país
      y tipo de documento válidos, la tabla de `EmployeeCountry` ⇒ `country_document_mismatch` (country); `EXENTO DE PENSIÓN`
      `Sí` con país `CO` ⇒ `foreign_pension_exempt_not_allowed`.
    - `EMPRESA`: vacía ⇒ `context.Settings.SingleCompany`, y `company_required` si es `null`; si no, `FindCompany` ⇒ `null`
      es `company_not_found`. `CENTRO DE TRABAJO` con `FindWorkplace(text, company.Id)`: `NotFound` ⇒ `workplace_not_found`,
      `OtherCompany` ⇒ `workplace_other_company`. Sin empresa resuelta el centro no se evalúa (sólo se reporta la empresa).
    - `EXENTO DE PENSIÓN` y `TELETRABAJADOR`: vacía ⇒ `No`; otra cosa que `Sí`/`No` ⇒ `yes_no_invalid`.
    - `MEDIO DE PAGO` vacío con banco vacío ⇒ sin pago; vacío con banco ⇒ `BankTransfer`.
    - `SALARIO`: piso con `LegalParameters.Resolve(context.LegalTable, …, startDate)`; sin valor ⇒ `legal_parameter_missing`;
      integral bajo el piso ⇒ `below_integral_floor`; bajo el SMMLV en tipo no exento ⇒ **aviso** `below_minimum` y
      `BelowMinimum = true`.
    - `FECHA DE FIN` con `FixedTerm` > `MaxEndDate(ingreso)` ⇒ `fixed_term_cap_exceeded`.
    - `FECHA DE RETIRO`: ilegible ⇒ `retirement_date_invalid`; > hoy ⇒ `retirement_date_in_future`; < ingreso ⇒
      `retirement_before_start`; válida sin `MOTIVO DE RETIRO` ⇒ `retirement_reason_required`; motivo sin etiqueta ⇒
      `retirement_reason_invalid`.
  - `EmployeeImportAnalyzer` (scoped): `Task<EmployeeImportAnalysis> AnalyzeAsync(Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken)`,
    `sealed record EmployeeImportAnalysis(string FileName, int TotalRows, IReadOnlyList<ParsedEmployeeRow> ValidRows, IReadOnlyList<EmployeeImportRowIssueDto> Errors, IReadOnlyList<EmployeeImportRowIssueDto> Warnings)`.
    Orden: `HrSetup.IsCompleteAsync` falso ⇒ `SetupIncomplete` ⇒ archivo (`file_required` sin nombre o tamaño 0;
    `extension_invalid` si no termina en `.xlsx`; `too_large`) ⇒ lector ⇒ 0 filas `empty`, > 1000 `too_many_rows` ⇒ contexto
    (hoy de `ITenantClock`, país del tenant, catálogo de países de Geography, ciudades, configuración con las empresas activas, tabla legal) ⇒ `Parse` por fila ⇒ documentos repetidos: entre **todas** las
    filas con número no vacío, agrupar por `Fold(número)`; cada fila de un grupo de dos o más suma
    `document_duplicated_in_file` y sale de las válidas ⇒ `FindTakenDocumentsAsync` sobre las válidas ⇒ `document_taken` ⇒
    errores y avisos ordenados por `RowNumber` y luego por `Field`. Los avisos de una fila con error no se listan.
  - `PreviewEmployeeImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<EmployeeImportPreviewDto>`;
    `ImportEmployeesCommand(…) : ICommand<EmployeeImportResultDto>`; `GetEmployeeImportTemplateQuery(Guid TenantId) : IQuery<EmployeeImportTemplateFile>`.
    Los tres autorizan `EmployeeRead, EmployeeManage, ContractRead, ContractManage`; la plantilla también responde
    `SetupIncomplete` sin configuración.
  - `ImportEmployeesHandler`: corre el mismo análisis; con algún **error** ⇒
    `HrDomainException(ImportRowsInvalid, "The file has rows with errors.")` sin escribir. Si no, por fila `Employee.Hire`
    (con `acknowledgeBelowMinimum = BelowMinimum`) y, con retiro, `Terminate(reason, date, null)`; `AddRange`; una actividad
    `EmployeeImported` y un `hr.employee.imported` por empleado; **un solo** `SaveChangesAsync` (todo o nada; una violación
    única en la carrera sale 409 `hr.employee.document_taken` desde `HrUnitOfWork`). `receivedAt` = `IClock.UtcNow`.

- [ ] **Step 1: Pruebas que fallan.**
  - `HrTextTests.FoldRemovesAccentsCaseAndExtraSpaces` (`"  Término   FIJO "` ⇒ `"termino fijo"`; `"   "` ⇒ `null`).
  - `HrCityIndexTests` (Medellín/Antioquia; Barbosa en Antioquia y en Santander; Cali con `DivipolaName` «Santiago de Cali»):
    `MatchesWithoutAccentsOrCase`; `TheDivipolaNameAlsoMatches`; `ARepeatedNameIsAmbiguous`;
    `TheDepartmentResolvesTheAmbiguity` (`"Barbosa - Santander"`); `AnUnknownCityIsNotFound`.
  - `HrSettingsIndexTests` (dos empresas activas, «Comercializadora QEP S.A.S.» `900123456` con la sede «Principal» y otra
    con la sede «Norte»): `AWorkplaceMatchesWithItsSite` (`"bodega - principal"`); `ABareWorkplaceNameDoesNotMatchEvenWhenUnique`;
    `ARepeatedWorkplaceNameNeedsTheSite` (centro «Bodega» en dos sedes de la misma empresa: `"Bodega"` ⇒ `NotFound`);
    `ArchivedSettingsAreNotOffered`; `ACompanyMatchesByNameAndTaxId` (`"comercializadora qep s.a.s. - 900123456"`);
    `WithSeveralCompaniesThereIsNoSingle` (`SingleCompany == null`; con una sola, esa);
    `AWorkplaceOfAnotherCompanyIsOtherCompany` (`"Planta - Norte"` buscado en la primera ⇒ `OtherCompany`);
    `ASameNamedWorkplaceResolvesInsideTheRowCompany` (centro «Bodega» en una sede de cada empresa: `"Bodega - Principal"` y
    `"Bodega - Norte"` con su empresa ⇒ el suyo; `"Bodega"` ⇒ `NotFound`).
  - `EmployeeImportRowParserTests` (hoy 2026-11-20, tabla legal sembrada, normalizador que acepta `3001234567` ⇒
    `+573001234567`): teoría `EachRuleReportsItsCodeAndField` con una fila por código de la lista del contrato;
    `ARowCanCarrySeveralErrors`; `LabelsAreComparedWithoutAccents` (`"termino fijo"`, `"aprendiz sena lectiva"`, `"AHORROS"`,
    `"cedula de ciudadania"`); `RestDaysAreACommaList` (`"sábado, domingo"` ⇒ `[Saturday, Sunday]`; vacía ⇒ `[Sunday]`);
    `ABelowMinimumEmployeeIsAWarningNotAnError` (`Empleado`, `1500000`, ingreso 2026-02-01 ⇒ `Row` no nulo,
    `BelowMinimum == true`, un aviso `hr.import.row.below_minimum`); `AContractorBelowTheMinimumHasNoWarning`;
    `TheFloorIsTheOneOfTheStartDate` (`Empleado`, `1500000`, ingreso 2025-03-01 ⇒ sin aviso); `APastRetirementTerminates`
    (2026-10-31, `Renuncia` ⇒ `RetirementDate` y `RetirementReason == Resignation`); `ARetirementWithoutReasonIsAnError`;
    `AFutureRetirementIsAnError`; `ThePhoneIsStoredAsE164`; `AnEmptyCountryTakesTheTenants` (contexto con `TenantCountry =
    "MX"`, `PPT` ⇒ `Profile.Country == "MX"`); `ACountryMatchesByCodeOrSpanishName` (`"ve"`, `"Venezuela"`, `"PERÚ"`);
    `AnUnknownCountryIsCountryInvalid` (`"Alemania"` y `"XX"`); `ACcFromAnotherCountryIsAMismatch` (`CC` + `"Venezuela"` ⇒
    `country_document_mismatch`); `PensionExemptionIsCheckedAgainstTheCountry` (`PPT` + `Venezuela` + `Sí` pasa; `CC` + vacío
    (tenant `CO`) + `Sí` ⇒ `foreign_pension_exempt_not_allowed`); `AnEmptyCompanyTakesTheOnlyOne` (una activa ⇒
    `Terms.CompanyId` de ésa y `CompanyName`); `AnEmptyCompanyWithSeveralIsCompanyRequired`; `AnUnknownCompanyIsCompanyNotFound`;
    `AWorkplaceOfAnotherCompanyIsAnError` (`workplace_other_company`, field `workplace`).
  - `EmployeeImportApiTests`: `TheTemplateNeedsSetup` (sin configuración ⇒ 422 `hr.settings.setup_incomplete`);
    `TheTemplateHasTheHeadersAndTheTenantLists` (hojas `Empleados` y `Valores válidos`, los 36 encabezados con `PAÍS` en la
    columna 5 y `EMPRESA` en la 24, la columna de centros con `"Bodega - Principal"` y sin los archivados, la de empresas
    con `"<nombre> - <NIT>"` sólo de las activas, la de países con «Colombia», `Content-Disposition` con
    `plantilla-empleados.xlsx`); `TheTemplateNeedsAnActiveCompany` (única empresa desactivada ⇒ 422
    `hr.settings.setup_incomplete`); `PreviewShowsTheCompanyOfEachRow` (`validRows[0].company == "Comercializadora QEP S.A.S."`);
    `ImportedEmployeesKeepCountryAndCompany` (fila con `PPT`, `Venezuela` y `EMPRESA` vacía con una sola empresa ⇒
    `employee.country == "VE"`, `contract.company.id` la única);
    `PreviewReportsValidRowsErrorsWarningsAndSummaryWithoutWriting` (una vigente, una con retiro pasado, una con
    `SALARIO = "abc"`, una bajo el mínimo ⇒ `validRows` 3, un error `hr.import.row.salary_invalid` con `field == "salary"`,
    un aviso `below_minimum`, `summary` `{3, 2, 1}`, y `SELECT count(*) FROM hr.employees` = 0);
    `ARepeatedDocumentMarksBothRows`; `AnExistingDocumentIsDocumentTaken`; `ImportCreatesEmployeesContractsAndRecords`
    (`importedCount == 2`; el retirado queda `Retired` con `previousContracts[0].termination.reason == "Resignation"`; el de
    aviso con `salaries[0].belowMinimumAcknowledged == true`; dos `hr.employee.imported` en auditoría);
    `ConfirmingWithErrorsIs422RowsInvalidAndWritesNothing`; `ConfirmingWithOnlyWarningsImports`;
    **`ADocumentCreatedBetweenPreviewAndConfirmWritesNothing`** (Review Focus 5: vista previa limpia de dos filas, `POST /employees`
    a mano con el documento de la segunda, confirmación ⇒ 422 `hr.import.rows_invalid` y en la base sólo el creado a mano);
    teoría `FileErrors` (sin archivo ⇒ `hr.import.file_required`; `planta.xls` ⇒ `extension_invalid`; 5 242 881 bytes ⇒
    `too_large`; bytes basura ⇒ `unreadable`; otros encabezados ⇒ `header_mismatch`; sólo encabezados ⇒ `empty`; 1001 filas ⇒
    `too_many_rows`); `WithoutContractManageTheImportIs403`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'EmployeeImportRowParser' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4`; la integración por
  nombre mientras se itera; al terminar,
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~EmployeeImportApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): import employees and contracts from excel`

### Task 11: Verificación final

**Files:** ninguno nuevo; sólo lo que la verificación obligue a corregir.

- [ ] **Step 1: Formato y build.** Run: `dotnet restore Backend.slnx --locked-mode`; `dotnet format Backend.slnx --verify-no-changes`;
  `dotnet build Backend.slnx --no-restore`. Expected: sin errores (con `--locked-mode` en verde, los `packages.lock.json`
  quedaron commiteados).
- [ ] **Step 2: Suite de los módulos tocados**, una sola vez y en fila (mirar antes
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`), cada una con `-- xUnit.MaxParallelThreads=4`:
  `tests/Modules/Hr/Modules.Hr.UnitTests`, `tests/Modules/Hr/Modules.Hr.IntegrationTests`, `tests/ArchitectureTests/ArchitectureTests`,
  `tests/Bootstrapper/Bootstrapper.UnitTests`, `tests/Modules/Storage/Modules.Storage.UnitTests`,
  `tests/Modules/Storage/Modules.Storage.IntegrationTests`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests`,
  `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`, `tests/Modules/Geography/Modules.Geography.UnitTests`,
  `tests/Modules/Geography/Modules.Geography.IntegrationTests`. Validar cada ruta con `Test-Path` antes de correrla (una ruta vacía
  corre la solución entera). Expected: `Passed!` en todos; si algo falla, se reporta con su salida literal.
- [ ] **Step 3: Contrato contra código.** Recorrer la tabla «Rutas» y comprobar en `HrEndpoints.cs` método, ruta, política y
  status de cada fila; que cada código de «Mensajes de dominio» y de la lista de códigos de fila exista en `HrErrorCodes`; y que
  `HrLabels` coincida letra por letra con la tabla de enums. Una diferencia se arregla en el código, no en el contrato.
- [ ] **Step 4: Commit** (sólo si hubo correcciones) — `fix(hr): <lo corregido>`

> **Cierre:** revisión fuerte de toda la rama y entrega para merge a `develop` cuando el owner lo pida. **`docs/estado.md` no
> se toca en la rama:** al mergear, en `develop`, se agrega la fila de RRHH fase 1 y su entrada del Registro (CLAUDE.md del
> repo). Despliegue: backend antes que el frontend; el módulo nace apagado (fuera de `DefaultForNewTenants`).

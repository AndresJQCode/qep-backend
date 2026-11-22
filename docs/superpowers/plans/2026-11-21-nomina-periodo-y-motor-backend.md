# Nómina, proyecto 1 — período quincenal, motor de liquidación y Excel — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **El `CLAUDE.md` del workspace manda sobre el proceso del skill** («Presupuesto de pruebas y ejecución de planes»): las
> tareas se ejecutan en **cinco bloques** (A–E) más un bloque diferido (F), un implementador (Sonnet) por bloque, una revisión
> por bloque y la revisión fuerte una sola vez al final. Cada tarea conserva su commit.

**Goal:** que un tenant con el módulo `payroll` abra cada quincena de cada empresa empleadora, capture lo variable (horas
extras, ingresos, deducciones, conceptos recurrentes; a mano o por Excel), la liquide con el cálculo legal colombiano y un
rastro por línea, la revise, apruebe, cierre y reabra, y exporte el «Resumen de nómina» a Excel. Sin ausencias, retención en
la fuente ni préstamos (proyectos 2 y 3).

**Architecture:** módulo nuevo `Payroll` (esquema `payroll`, cuatro capas, calcado de `Hr`), clave de módulo `payroll` que
depende de `hr`. El **motor es una función pura** en `Modules.Payroll.Domain/Engine` (sin base, sin reloj, sin
`IServiceProvider`): recibe un `EngineInput` completo por empleado y devuelve un `EmployeeLiquidation` con sus líneas y el
rastro de cada una; cada regla del spec es un archivo con un método estático. Payroll **no referencia** `Modules.Hr.*` ni
`Modules.Companies.*`: lee empleados, contratos, salarios, cargo y centro de trabajo por `IPayrollHrDirectory`, los valores
legales por `IPayrollLegalValues` y el nombre del actor por `IPayrollMemberDirectory`, con adaptadores en
`src/Bootstrapper/Payroll*.cs`. Los valores legales siguen siendo de Hr: se amplía `hr.legal_parameters` (más claves y
`numeric(18,6)`) y se suma `hr.legal_parameter_brackets` (tramos del FSP). El agregado `PayrollPeriod` (una versión) lleva el
estado, lo capturado por empleado y el historial de transiciones; la liquidación se guarda como **foto fija** en
`payroll.employee_results` (líneas, rastro, acumulados del mes, huella de entradas) junto con la foto de valores legales del
período. El Excel sale de un generador ClosedXML que recibe filas ya calculadas y un **diseño de columnas**.

**Tech Stack:** .NET 10, EF Core + Npgsql, minimal APIs, FluentValidation 12.1.1, ClosedXML 0.105.1, System.Text.Json,
xUnit v3, Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md` (este repo; léelo entero antes de empezar).
Pantallas: `qep-frontend/docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md`. **El contrato HTTP es la sección
«Contrato HTTP» de este archivo** y **las fórmulas son la sección «Motor: definiciones exactas»**: las tareas las citan y no
las repiten. Patrones: `docs/superpowers/plans/2026-11-20-rrhh-fase-1-backend.md` y el módulo `src/Modules/Hr/` ya mergeado.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\nomina-periodo-motor`,
  rama `feature/nomina-periodo-motor`. Antes de **cada** commit: `git branch --show-current` dice
  `feature/nomina-periodo-motor`. Commits: conventional commits, **sin atribución de IA** (ni `Co-Authored-By`).
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`). `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- **lulo-erp no es referencia.** Las reglas de negocio ya están en el spec y en «Motor: definiciones exactas»; no se abre ni
  se calca nada de ese repo (código, modelo, nombres, estructura ni patrones).
- Mensajes al usuario (`message` de la importación, `errors`, etiquetas de líneas y de motivos) en español neutro, tuteando.
  Códigos, rutas, enums y permisos no se traducen. Comentarios en inglés que explican el porqué.
- Códigos de dominio con prefijo `payroll.`, sólo los del «Contrato HTTP». Ningún status HTTP se arma en un handler: 404
  `ResourceNotFoundException`, 409 `ResourceConflictException`, 412 `RequestConcurrencyException("concurrency.conflict", …)`,
  428 `PreconditionRequiredException("precondition.if_match_required", …)`, 422 `PayrollDomainException` o
  `ValidationException`. `src/Api/ApiExceptionHandler.cs` **no cambia**.
- Claves de `errors` en camelCase con punto e índice para lo anidado (`overtime[0].hours`, `incomes[1].amount`): todo
  validador de Payroll hereda `PayrollValidator<T>` (calco de `HrValidator<T>`).
- Doble capa de tenant: la ruta lleva `{tenantId:guid}` y la política exige el permiso **en negrita** de la tabla de rutas;
  el handler revalida con `PayrollAuthorization.EnsureAsync` la lista completa **antes** del repositorio y responde 403
  `authorization.denied`, nunca 404. Todo método de repositorio recibe `tenantId`, así que el id de un período, una empresa o
  un concepto de otro tenant pedido con la ruta del propio responde el mismo 404 `payroll.*.not_found` que un id inventado:
  no confirma que exista en otro lado (mismo criterio que Hr).
- **Cada escritura exige también `payroll.period.read`** (mismo criterio D-1 de RRHH), **salvo** las de configuración y
  conceptos de la empresa y las de conceptos recurrentes de cada empleado (crear, editar, activar, desactivar), que exigen
  sólo `payroll.settings.manage` (decisión del owner, 2026-11-21: un rol con sólo `payroll.settings.manage` usa las
  pantallas de configuración y de recurrentes, como dice la descripción del permiso). Por lo mismo, `GET /payroll/companies`,
  `GET {c}/settings` (que trae el catálogo de conceptos) y `GET {c}/employees/{employeeId}/recurring-concepts` aceptan
  **cualquiera** de `payroll.period.read` o `payroll.settings.manage`: política
  `PayrollPolicies.SettingsView` (calco de `SupportPolicies.SettingsView`, `AddAnyPermissionRequirement`) y en el handler
  `PayrollAuthorization.EnsureAnyAsync`. Ese rol no ve períodos, liquidaciones, rastro ni Excel (403).
- Auditoría atómica en el handler (`IPayrollAuditPublisher`, calco de `IHrAuditPublisher`, `Source = "payroll"`):
  `audit.entries` guarda **nombres** de campos, nunca valores. Las transiciones del período viven además en
  `payroll.period_events` (quién, cuándo, desde, hacia, motivo, confirmación de ausencias). Payroll no publica eventos: no hay
  outbox.
- Dinero `numeric(18,2)`, pesos colombianos, sin campo de moneda. Montos capturados > 0, ≤ 2 decimales,
  < 10 000 000 000 000 000. Horas > 0, ≤ 2 decimales, ≤ 360 por línea. Tasas y multiplicadores `numeric(18,6)`.
- **Ningún valor legal se escribe en el código** (spec, «Valores legales»): todo sale de `hr.legal_parameters` y
  `hr.legal_parameter_brackets`, resuelto al **primer día del período**. Las pruebas del motor arman su `LegalValueSet` en
  memoria con los valores sembrados (literales sólo en pruebas, archivos de oro y migración).
- Todo `decimal`, nunca `double`. Redondeos sólo por `PayrollRounding` (Tarea 4).
- TDD con evidencia literal de RED y GREEN. **Si el fallo esperado es de compilación, la evidencia es la salida del build y no
  se levantan contenedores.** La integración se filtra por nombre de prueba mientras se itera
  (`--filter "FullyQualifiedName~Clase.Prueba"`); la clase completa corre una vez al cierre del bloque. Toda corrida de
  `dotnet test` va por proyecto, **en primer plano**, con `-- xUnit.MaxParallelThreads=4`. Antes de una corrida de integración:
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` — si hay una ajena, se espera; no se detiene. Nunca dos corridas
  de integración a la vez. La suite completa sólo en la Tarea 16, limitada a los proyectos tocados.
- Proyectos nuevos: `dotnet restore Backend.slnx` genera sus `packages.lock.json` y actualiza los de quien los referencia
  (`Api`, `Bootstrapper`, `ArchitectureTests`, `Bootstrapper.UnitTests`, los `*.IntegrationTests` que referencian
  `Api.csproj`). Todos se commitean en la tarea que agrega el proyecto: el `Dockerfile` restaura con `--locked-mode`.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- `docs/estado.md` **no se toca en la rama** (CLAUDE.md del repo): se actualiza en `develop` al mergear.

## Review Focus

1. **El día del tenant, no el UTC**: a las 23:30 de Bogotá del 2026-11-15 (04:30Z del 16) `ensure-current` de una empresa
   con la primera quincena de noviembre abierta **no** crea la segunda. Prueba: Tarea 9,
   `TheCurrentPeriodUsesTheTenantDayNotUtc`.
2. **La quincena de un mes de 31 días y la de febrero valen 15 días, y el mes cuadra al peso**: un salario de 1 750 905 con
   las dos quincenas completas de noviembre, de diciembre y de febrero de 2026 paga `salary` 875 453 + 875 452; el mes suma
   exactamente el salario (el redondeo es del acumulado del mes, R-6). Pruebas: Tarea 4,
   `TheSecondHalfOfAThirtyOneDayMonthIsFifteenDays` y `TheMonthAddsUpToTheMonthlySalary`; Tarea 6, casos
   `minimum-wage-first-half` y `minimum-wage-second-half`.
3. **Un valor legal nuevo no cambia un período aprobado ni uno reabierto y vuelto a liquidar**: insertar en
   `hr.legal_parameters` un `TransportAllowance` con `effective_from` 2026-11-01 después de cerrar ⇒ el detalle, el Excel y la
   reliquidación tras reabrir dan los mismos montos y la misma foto `legalValues`. Prueba: Tarea 13 (la que habilita el
   Excel, M-15), `ANewLegalValueChangesNeitherAClosedNorAReliquidatedPeriod`.
4. **Doble control**: con `dualControl: true`, quien liquidó por última vez no aprueba aunque haya reabierto otro; con
   `false`, sí. Prueba: Tarea 11, `WithDualControlTheLastLiquidatorCannotApprove`.
5. **Dos escrituras sobre el mismo período**: dos capturas con el mismo `If-Match` ⇒ la segunda 412 y sus líneas no
   quedan; una liquidación con `If-Match` viejo después de una captura ⇒ 412 y no hay resultados. Pruebas: Tarea 10,
   `AStaleIfMatchIs412AndWritesNothing`; Tarea 11, `ALiquidationWithAStaleIfMatchIs412`.
6. **Otro tenant**: con la sesión del tenant A, la ruta del tenant B ⇒ 403 `authorization.denied` en el detalle, el rastro,
   el Excel, las capturas y cada transición; y la ruta de A con el `periodId` de B ⇒ 404 `payroll.period.not_found`, igual que
   un id inventado. Nunca datos de B. Prueba: Tarea 13 (cuando ya existen todas las rutas `{p}…`, M-15),
   `AnotherTenantsPeriodIsForbiddenEverywhere`.
7. **Un empleado que el motor no sabe calcular no se liquida**: integral, aprendiz, pensionado, exento de pensión, salario bajo
   el SMMLV y dos contratos de la empresa en la quincena aparecen en `excluded` con su motivo y sin líneas, y la liquidación
   del resto sigue. Prueba: Tarea 11, `UnsupportedEmployeesAreExcludedWithTheirReason`.

## Rulings

Lo que el spec deja al implementador o no resuelve del todo. Cada una es acotada de cambiar; las marcadas **(a confirmar con
el caso real)** las revisa la Tarea 15.

- **R-1. Forma de los valores legales** (spec, «Llamadas» 7). `hr.legal_parameters` sigue siendo escalar por clave: se suman
  claves al enum `LegalParameterKey` y la columna `value` pasa de `numeric(18,2)` a `numeric(18,6)`. Los porcentajes se
  guardan como **fracción** (4 % ⇒ `0.04`, 0,522 % ⇒ `0.00522`), los topes en múltiplos de SMMLV (`2`, `10`, `25`), la
  jornada en horas. Las clases ARL son cinco claves. Los tramos del FSP van en una tabla nueva
  `hr.legal_parameter_brackets` (clave, vigencia, límite inferior y superior en múltiplos de SMMLV, tasa). La fecha desde la
  que el teletrabajador recibe conectividad (Llamada 2) es la clave `ConnectivityAllowanceSince` con valor `1` y vigencia
  2025-06-25: se lee su `EffectiveFrom`, no su valor.
- **R-2. Factor de cada tipo de hora** (spec, «Horas extras»: «cada tipo multiplica el valor hora por su porcentaje de la
  tabla»). Una hora **extra** es una hora adicional y se paga `1 + recargo`; un **recargo** cae sobre una hora ya pagada en el
  salario y se paga sólo el recargo; los tres compuestos **se derivan en el motor** del recargo dominical vigente a la fecha
  del período (spec corregido, commit `67dfce91d`) y no tienen clave propia:

  | Concepto (`SystemConceptKey`) | Factor | Con 80 % (desde 2025-07-01) | Con 90 % (desde 2026-07-01) |
  | --- | --- | --- | --- |
  | `ExtraDay` Hora extra diurna | `1 + ExtraDayRate` | 1,25 | 1,25 |
  | `ExtraNight` Hora extra nocturna | `1 + ExtraNightRate` | 1,75 | 1,75 |
  | `NightSurcharge` Recargo nocturno | `NightSurchargeRate` | 0,35 | 0,35 |
  | `SundaySurcharge` Recargo dominical y festivo | `SundaySurchargeRate` | 0,80 | 0,90 |
  | `ExtraSundayDay` Hora extra dominical diurna | `1 + ExtraDayRate + SundaySurchargeRate` | 2,05 | 2,15 |
  | `ExtraSundayNight` Hora extra dominical nocturna | `1 + ExtraNightRate + SundaySurchargeRate` | 2,55 | 2,65 |
  | `NightSundaySurcharge` Recargo nocturno dominical | `NightSurchargeRate + SundaySurchargeRate` | 1,15 | 1,25 |
  | Concepto propio de la empresa | su `multiplier` | — | — |

  La tabla vive una sola vez en `OvertimeFactors` (Domain, `Engine/`) y el rastro de cada línea lista las claves que sumó.
- **R-3. Pensión de alto riesgo.** La tabla dice «Pensión alto riesgo (total) 26 %». El empleado sigue con su 4 %; el
  empleador paga `HighRiskPensionTotalRate − PensionEmployeeRate` (22 %). Confirmado en el spec corregido (O-2 resuelta).
- **R-4. Bases de los aportes del empleador** (pre-flight I-7). Salud y pensión van sobre el **IBC** del mes (con la regla del
  40 %, piso y tope). ARL, caja, ICBF y SENA van sobre el **devengo salarial del mes** (`SalDev_m`: salario + horas extras y
  recargos + ingresos salariales), sin auxilio, sin no salariales y sin la regla del 40 %, como dice el spec («ARL … no
  incluye auxilio ni no salariales»). **(a confirmar con la quincena real)**: la ley puede pedir el IBC con el exceso del 40 %
  para la ARL; si es así, la ARL difiere en los meses con no salariales por encima del 40 %.
- **R-5. Todo lo de seguridad social se acumula en el mes** (spec, «Deducciones del empleado», lo extiende a los aportes del
  empleador y a los parafiscales): cada aporte o deducción de la segunda quincena es `ceil100(acumulado del mes) − lo de la
  primera`. Salario, auxilio y conectividad de la segunda también son `round(acumulado del mes) − lo de la primera`, para que
  el mes cuadre al peso con el salario mensual. Las provisiones son del período, sin acumular. **(a confirmar con el caso
  real)**
- **R-6. Redondeo.** `IBC`: hacia arriba al peso. Cada aporte o deducción de seguridad social (salud, pensión, FSP del
  empleado; salud, pensión, ARL, caja, ICBF, SENA del empleador): hacia arriba al múltiplo de 100, **sobre el acumulado del
  mes**. Todo lo demás (salario, auxilio, horas, ingresos, recurrentes, provisiones): al peso, mitad hacia arriba
  (`MidpointRounding.AwayFromZero`). Los montos capturados ya vienen con ≤ 2 decimales y se pagan redondeados al peso. Los
  valores intermedios (valor hora, `SalaryRaw`, bases) **no** se redondean: sólo la línea (pre-flight M-5).
- **R-7. Días** (Llamada 1). Un contrato cubre el día `d` si `StartDate ≤ d` y (`EndedOn` nulo o `d ≤ EndedOn`). Si cubre
  todos los días de la quincena, son **15**. Si no, son los días reales cubiertos, máximo 15. Con un cambio de salario
  dentro de la quincena, cada tramo cuenta sus días reales y **el último tramo absorbe la diferencia** para que la suma sea la
  de la quincena (16–19 de febrero = 4 días; 20–28 = 15 − 4 = 11). **(a confirmar con el caso real)**
- **R-8. «Salario vigente»** para el ingreso del mes, el auxilio y el valor hora es el del **último día cubierto** de la
  quincena (el último del período, o el de retiro si el contrato termina adentro).
- **R-9. FSP.** Los tramos son de límite inferior **inclusivo** (un IBC de exactamente 4 SMMLV paga 1 %); el último incluye
  su tope. Primera quincena: tramo por `min(S, L.IbcCapSmmlv × L.Smmlv) ÷ L.Smmlv` (pre-flight B-1: un salario de 50 000 000
  cae en el tramo superior, no fuera de tabla), aplicado al IBC de la quincena; si el salario no llega a 4 SMMLV, cero. Segunda: tramo por `IBC_m ÷ SMMLV`, `ceil100(IBC_m × tasa) − FSP de la primera` (puede ser negativo: es una
  devolución y se muestra como deducción negativa). Confirmado en el spec corregido: desde IBC ≥ 4 SMMLV (O-3 resuelta).
- **R-10. Dos o más contratos de la empresa en la quincena** (texto del spec corregido; O-4 resuelta): si cubren algún día
  de la quincena ⇒ no liquidable con motivo `MultipleContractsInPeriod`. Hr no permite contratos superpuestos, así que en la
  práctica son una terminación y una reincorporación, o una conversión de tipo, dentro de la misma quincena.
- **R-11. Conceptos.** Los conceptos del sistema (las siete horas, cuatro ingresos y una deducción, tabla en el contrato) no son
  filas: tienen **UUID fijos** en `SystemConcepts` (Domain), iguales en todos los tenants. Los propios de la empresa son filas
  de `payroll.concepts` (`Overtime` con `multiplier`; `Income` con `defaultSalaryConstitutive`; `Deduction`). Una línea
  capturada guarda `conceptId` y, al liquidar, el nombre del concepto se copia en la foto.
- **R-12. Tope de un concepto recurrente.** `accumulated` es la suma de lo aplicado por ese concepto en la **última foto** de
  cada período anterior (por `startDate`) de la empresa que no esté en `Draft`. El concepto aplica `min(valor, tope −
  accumulated)` y queda `Exhausted` (estado calculado, no guardado) cuando `accumulated ≥ tope`. Reabrir y reliquidar un
  período viejo recalcula solo; no hay contador que desincronizar.
- **R-13. Creación del período** (spec, «Período»). `POST …/periods/ensure-current` es idempotente, exige
  `payroll.period.read` y `payroll.period.manage` (pre-flight I-6: abrir es de `manage`, spec «Módulo y permisos»; quien
  sólo lee usa `GET …/periods`, que no crea nada) y crea **todas** las quincenas que falten desde la última hasta la
  actual del día del tenant, sin huecos. Sin ninguna, la primera es la actual **sólo si es una primera quincena**; si hoy es
  segunda quincena no crea nada y responde `firstPeriodStartsOn` = día 1 del mes siguiente. Una empresa inactiva no abre
  períodos (422 `payroll.company.inactive`).
- **R-14. Dependencia de la segunda quincena.** Liquidar la segunda exige la primera `Liquidated`, `Approved` o `Closed` y no
  desactualizada (422 `payroll.period.first_half_not_liquidated`). Aprobar la segunda exige la primera `Approved` o `Closed`
  (422 `payroll.period.first_half_not_approved`). Reabrir o volver a liquidar la primera marca la segunda `outdated` si la
  segunda no está en `Draft`.
- **R-15. Bloqueos.** Un empleado liquidable con datos faltantes **bloquea la liquidación** del período (422
  `payroll.liquidation.blocked`; la lista está en el detalle). Un neto negativo **no** bloquea la liquidación: bloquea la
  aprobación (422 `payroll.period.negative_net`). Una quincena sin nadie liquidable no se liquida (422
  `payroll.liquidation.nobody_to_liquidate`). Las exclusiones (tipo, subtipo, exento, `SalaryBelowMinimum`, varios contratos)
  se evalúan **antes** que los bloqueos: un excluido nunca bloquea (pre-flight I-3). Los bloqueados **sí** están en
  `employees` (se les captura desde su fila); `blocked` repite sólo `employeeId` y `reasons` (I-9).
- **R-16. Cambios en Hr después de liquidar.** Hr no publica eventos. Cada resultado guarda la **huella** (`SHA-256` del JSON
  canónico de su `EngineInput` sin la foto legal) y el período la huella del conjunto. Al leer un período `Liquidated`, el
  backend recalcula la huella con lo que hay hoy en Hr, las capturas y la configuración; si difiere, `sourceChanged: true` y la
  aprobación responde 422 `payroll.period.outdated`, igual que con `outdated`. En `Approved` y `Closed` no se recalcula: son
  foto.
- **R-17. Versiones.** Una versión por período; **todas** las escrituras sobre un período exigen `If-Match` (capturas,
  importación, liquidar, aprobar, cerrar, reabrir). La configuración de la empresa tiene su versión; mientras nunca se guardó,
  `GET` responde los valores por defecto con `version: 0` y el primer `PUT` va con `If-Match: "0"`. Crear y editar conceptos y
  recurrentes llevan `If-Match` en la edición; archivar, desarchivar, activar y desactivar van **sin** `If-Match` (cambios
  de estado idempotentes, pre-flight I-8).
- **R-16b. Huella sin datos futuros** (pre-flight I-10). `EngineInputFactory` sólo pasa los registros de salario con
  `EffectiveDate ≤ period.End` y `ContractEndedOn` sólo si cae dentro del período: un salario programado o una terminación
  futura en Hr no levantan un `sourceChanged` falso.
- **R-18. Respuestas de las escrituras.** Las transiciones (liquidar, aprobar, cerrar, reabrir) responden el
  `PayrollPeriodDetailDto` entero. Las capturas de un empleado responden `PayrollEmployeeDetailDto`, que trae el encabezado
  del período (`period`, con su nueva `version` y `outdated`). La importación confirmada responde el detalle entero.
- **R-19. FK a la empresa.** `payroll.periods.company_id` y `payroll.company_settings.company_id` llevan FK real a
  `companies.companies(id)` `ON DELETE RESTRICT`, a mano en la migración, con el patrón de Hr (D-11 de la fase 1).
- **R-20. Líneas exoneradas.** Con exoneración, salud del empleador, ICBF y SENA salen como líneas en **0** con la nota
  «Exonerado (ET art. 114-1)»: las columnas del Excel y el rastro no cambian de forma según el empleado.
- **R-21. Bases de las provisiones.** Cesantías y prima: salario + horas + ingresos salariales + auxilio o conectividad del
  período. Intereses: la misma base que cesantías × 1 %. Vacaciones: salario + ingresos salariales del período, sin horas
  extras, recargos ni auxilio. **(a confirmar con el caso real)**
- **R-22. Base del tope del auxilio, tercera opción.** «Salario más ausencias pagas sin comisiones»: en este proyecto no hay
  ausencias, así que hoy vale el salario vigente; el proyecto 2 suma las ausencias pagas en `MonthIncomeRule`.
- **R-23. Auxilio con frecuencia mensual.** La primera quincena paga 0 (línea en 0 con nota «Se paga en la segunda quincena»);
  la segunda paga el mes completo por los días cubiertos del mes (primera + segunda).
- **R-24. Importación de novedades.** Confirmar **reemplaza** lo capturado de cada empleado que aparece en el archivo; los que
  no aparecen no se tocan. Todo o nada.
- **R-25. Sugerencia de clasificación de ingresos del sistema.** `Commissions` y `PrestationalBonus` sugieren constitutivo
  (`true`); `SalaryAdvance` y `OtherIncome` no sugieren (`null`): quien captura elige. La línea siempre guarda lo elegido.
- **R-26. Versión del motor.** `PayrollEngine.Version = "2026.11.1"`; se guarda en cada resultado. Cualquier cambio de regla la
  sube (Tarea 15).
- **R-27. Ausencias.** Liquidar exige `confirmNoAbsences: true` en el cuerpo (422 `payroll.liquidation.absences_unconfirmed`);
  la confirmación queda en `payroll.period_events` y en `audit.entries` (campo `absencesConfirmed`).

## Decisiones pendientes del owner (contradicciones o huecos del spec)

El plan sigue con la opción marcada; cambiarla es una migración de datos o una regla aislada, más su caso de oro.

- **O-1. Multiplicadores dominicales — resuelta** (spec corregido, `67dfce91d`): los tres compuestos se derivan del recargo
  dominical vigente (R-2); no hay claves para ellos. Con 80 % dan 2,05 / 2,55 / 1,15; con 90 %, 2,15 / 2,65 / 1,25.
- **O-2. Alto riesgo — resuelta:** 22 % del empleador (R-3).
- **O-3. FSP desde 4 SMMLV — resuelta:** desde IBC ≥ 4 SMMLV, límite inferior incluido (R-9).
- **O-4. Dos contratos — resuelta:** «dos o más contratos de la empresa en la quincena» (R-10).
- **O-5. Recargo dominical antes de 2025-07-01.** El spec no da el valor (75 %); no se siembra. Una quincena anterior responde
  422 `payroll.legal_parameter.missing`, que no puede pasar porque no hay períodos históricos.
- **O-6. Caja de compensación.** El código de Hr guarda caja en la sede (`Site.CompensationFund`) y todavía en el empleado
  (`Employee.CompensationFund`). Payroll toma la de la sede sólo para mostrarla; no cambia el cálculo.

## Contrato HTTP

Esta sección es **la fuente** del contrato para los dos repos: el plan de frontend
(`qep-frontend/docs/superpowers/plans/2026-11-21-nomina-periodo-y-motor-frontend.md`) la cita y no la repite. Lo que el spec
dejaba abierto se decide acá y se marca **(llamada del plan)**.

### Convenciones

- Prefijo `/api/v1/tenants/{tenantId:guid}/payroll`, header `X-Tenant-Id`.
- JSON camelCase. Enums como texto (nombre exacto del miembro). Opcionales **siempre presentes** como `null`. Fechas
  `yyyy-MM-dd` (`DateOnly`); instantes ISO-8601 (`DateTimeOffset`). Dinero `decimal` en pesos colombianos; los montos
  calculados vienen **sin decimales** (enteros en pesos); horas, tasas y multiplicadores con sus decimales.
- `version` es `long`. `If-Match: "<version>"` (entre comillas). Sin `If-Match` donde se exige ⇒ 428; versión vieja ⇒ 412.
  `If-Match` se lee **después** de autorizar (calco de `HrIfMatch`).
- **Todas las escrituras sobre un período exigen `If-Match` con `period.version`** (R-17) y toda escritura la sube.
- **Respuestas** (R-18): liquidar, aprobar, cerrar, reabrir e importar responden `PayrollPeriodDetailDto` entero; las capturas
  de un empleado responden `PayrollEmployeeDetailDto` (con el encabezado del período).
- «Hoy» es el día del tenant (`TenantCalendar.Today`). Las quincenas van del 1 al 15 y del 16 al último día del mes.
- **Nada se calcula en el navegador**: días, auxilio, aportes, redondeos, neto, totales, estado de los botones (`can`) y
  motivos de bloqueo los manda el backend.

### Enums y etiquetas

Las etiquetas son las de la UI **y** las que acepta la importación de novedades (comparación sin mayúsculas, tildes ni
espacios de más). Viven una vez en `PayrollLabels` (Application) y el frontend las copia literal.

| Enum | Valor → etiqueta |
| --- | --- |
| `PayrollPeriodStatus` | `Draft` Borrador · `Liquidated` Liquidado · `Approved` Aprobado · `Closed` Cerrado · `Reopened` Reabierto |
| `PayrollHalf` | `First` Primera quincena · `Second` Segunda quincena |
| `TransportFrequency` | `Biweekly` Quincenal · `Monthly` Mensual |
| `TransportCapBase` | `SalaryOnly` Sólo salario · `SalaryAndSalaryIncome` Salario más ingresos salariales sin extras · `SalaryAndPaidAbsences` Salario más ausencias pagas sin comisiones |
| `OvertimeBase` | `SalaryOnly` Sólo salario · `SalaryAndMonthSalaryIncome` Salario más ingresos salariales del mes |
| `ConceptKind` | `Overtime` Horas extras y recargos · `Income` Ingreso · `Deduction` Deducción |
| `RecurringMode` | `Fixed` Fijo · `Proportional` Proporcional a los días |
| `RecurringStatus` | `Active` Activo · `Inactive` Inactivo · `Exhausted` Tope alcanzado |
| `PayrollLineGroup` | `Earning` Devengado · `Deduction` Deducción · `EmployerContribution` Aporte del empleador · `Provision` Provisión · `Info` Informativo |
| `ExclusionReason` | `ContractTypeNotSupported` El tipo de contrato no se liquida todavía · `SalaryTypeNotSupported` El tipo de salario no se liquida todavía · `ContributorSubtypeNotSupported` El subtipo de cotizante no se liquida todavía · `ForeignPensionExempt` Los exentos de pensión no se liquidan todavía · `SalaryBelowMinimum` El salario es menor que el mínimo · `MultipleContractsInPeriod` Tiene más de un contrato en la quincena |
| `BlockerReason` | `MissingSalary` No tiene salario vigente en la quincena · `MissingWorkplace` No tiene centro de trabajo en la quincena · `MissingRiskClass` Su centro de trabajo no tiene clase de riesgo |
| `PeriodAction` (historial) | `Created` Abierta · `InputsImported` Novedades por Excel · `Liquidated` Liquidada · `Approved` Aprobada · `Closed` Cerrada · `Reopened` Reabierta · `MarkedOutdated` Desactualizada |

**(llamada del plan)** Una captura manual no deja evento en el historial (quedaría uno por cada guardado); queda en
`audit.entries` como `payroll.period.inputs_updated`. Sí deja `MarkedOutdated` la primera vez que desactualiza un período
liquidado.

Conceptos del sistema (R-11), con su UUID fijo en `SystemConcepts`:

| `systemKey` | Id | Tipo | Nombre | `defaultSalaryConstitutive` |
| --- | --- | --- | --- | --- |
| `ExtraDay` | `00000000-0000-0000-0001-000000000001` | `Overtime` | Hora extra diurna | — |
| `ExtraNight` | `00000000-0000-0000-0001-000000000002` | `Overtime` | Hora extra nocturna | — |
| `NightSurcharge` | `00000000-0000-0000-0001-000000000003` | `Overtime` | Recargo nocturno | — |
| `SundaySurcharge` | `00000000-0000-0000-0001-000000000004` | `Overtime` | Recargo dominical y festivo | — |
| `ExtraSundayDay` | `00000000-0000-0000-0001-000000000005` | `Overtime` | Hora extra dominical diurna | — |
| `ExtraSundayNight` | `00000000-0000-0000-0001-000000000006` | `Overtime` | Hora extra dominical nocturna | — |
| `NightSundaySurcharge` | `00000000-0000-0000-0001-000000000007` | `Overtime` | Recargo nocturno dominical | — |
| `Commissions` | `00000000-0000-0000-0002-000000000001` | `Income` | Comisiones | `true` |
| `PrestationalBonus` | `00000000-0000-0000-0002-000000000002` | `Income` | Bonificación prestacional | `true` |
| `SalaryAdvance` | `00000000-0000-0000-0002-000000000003` | `Income` | Anticipo de salario | `null` |
| `OtherIncome` | `00000000-0000-0000-0002-000000000004` | `Income` | Otros ingresos | `null` |
| `OtherDeduction` | `00000000-0000-0000-0003-000000000001` | `Deduction` | Otra deducción | — |

Códigos de línea (`PayrollLineDto.code`), estables para el frontend y el Excel:

| Código | Grupo | Etiqueta |
| --- | --- | --- |
| `salary` | `Earning` | Salario |
| `transport_allowance` | `Earning` | Auxilio de transporte |
| `connectivity_allowance` | `Earning` | Auxilio de conectividad |
| `overtime:<conceptId>` | `Earning` | nombre del concepto |
| `income:<conceptId>:<n>` | `Earning` | nombre del concepto (`n` = posición en la captura, desde 0) |
| `recurring:<recurringId>` | `Earning` o `Deduction` | nombre del concepto |
| `ibc` | `Info` | Ingreso base de cotización del mes |
| `health_employee` | `Deduction` | Salud |
| `pension_employee` | `Deduction` | Pensión |
| `solidarity_fund` | `Deduction` | Fondo de solidaridad pensional |
| `deduction:<conceptId>:<n>` | `Deduction` | nombre del concepto |
| `health_employer` | `EmployerContribution` | Salud (empleador) |
| `pension_employer` | `EmployerContribution` | Pensión (empleador) |
| `arl` | `EmployerContribution` | ARL |
| `compensation_fund` | `EmployerContribution` | Caja de compensación |
| `icbf` | `EmployerContribution` | ICBF |
| `sena` | `EmployerContribution` | SENA |
| `severance` | `Provision` | Cesantías |
| `severance_interest` | `Provision` | Intereses a las cesantías |
| `premium` | `Provision` | Prima de servicios |
| `vacation` | `Provision` | Vacaciones |

### DTOs (respuesta)

```jsonc
// PayrollCompanyDto
{ "id": "uuid", "name": "Comercializadora QEP S.A.S.", "taxId": "900123456", "isActive": true }

// ActorDto — nombre de la membresía al escribir; "Sin nombre" si no tenía
{ "subjectId": "uuid", "displayName": "Laura Gómez" }

// PayrollConceptDto
{ "id": "uuid", "kind": "Overtime", "name": "Hora extra diurna",
  "system": true, "systemKey": "ExtraDay" | null,
  "defaultSalaryConstitutive": true | false | null,     // sólo Income; null en los demás
  "multiplier": 1.4 | null,                             // sólo Overtime propio; los del sistema salen de la tabla legal (null)
  "archived": false,
  "version": 3 | null }                                 // null en los del sistema: no se editan

// PayrollSettingsDto — GET y PUT de la configuración de una empresa
{ "company": PayrollCompanyDto,
  "transportFrequency": "Biweekly", "transportCapBase": "SalaryOnly", "overtimeBase": "SalaryOnly",
  "exonerated": true, "dualControl": false,
  "version": 0,                                         // 0 = nunca guardada: son los valores por defecto (R-17)
  "concepts": [PayrollConceptDto] }                     // sistema primero en el orden de la tabla, luego propios por kind y nombre; archivados incluidos

// PayrollTotalsDto — suma de las filas liquidadas; la calcula el backend
{ "salary": 0, "transportAllowance": 0,               // transporte + conectividad
  "overtime": 0, "otherSalaryIncome": 0, "nonSalaryIncome": 0, "totalEarned": 0,
  "healthEmployee": 0, "pensionEmployee": 0, "solidarityFund": 0, "otherDeductions": 0, "totalDeductions": 0,
  "net": 0,
  "healthEmployer": 0, "pensionEmployer": 0, "arl": 0, "compensationFund": 0, "icbf": 0, "sena": 0,
  "severance": 0, "severanceInterest": 0, "premium": 0, "vacation": 0,
  "employerCost": 0 }

// PayrollEmployeeSummaryDto — misma forma que PayrollTotalsDto, para una fila

// PayrollPeriodCanDto — qué ofrecer; ya cruza estado, permisos de quien llama y doble control (llamada del plan)
{ "capture": true, "liquidate": true, "approve": false, "close": false, "reopen": false, "export": false }

// PayrollPeriodDto — encabezado
{ "id": "uuid", "company": PayrollCompanyDto,
  "startDate": "2026-11-01", "endDate": "2026-11-15", "half": "First",
  "status": "Liquidated",
  "outdated": false,                                    // guardado: una captura o la primera quincena cambió después de liquidar
  "sourceChanged": false,                               // calculado al leer un Liquidated: Hr o la configuración cambiaron (R-16)
  "version": 4,
  "legalValuesDate": "2026-11-01" | null,              // fecha a la que se resolvió la foto legal; null hasta la primera liquidación
  "engineVersion": "2026.11.1" | null,
  "lastLiquidatedAt": "…" | null, "lastLiquidatedBy": ActorDto | null,
  "approvedAt": null, "approvedBy": null, "closedAt": null, "closedBy": null,
  "employeeCount": 12,                                  // liquidables (con o sin bloqueo); no cuenta los excluidos
  "totals": PayrollTotalsDto | null,                    // null hasta liquidar
  "can": PayrollPeriodCanDto,
  "approveBlockedBy": ["outdated"]                      // subconjunto ordenado de: "status", "outdated", "source_changed",
                                                        // "negative_net", "first_half_not_approved", "dual_control"; [] si se puede
}

// PayrollEmployeeRowDto — una fila del período
{ "employeeId": "uuid", "documentType": "CC", "documentNumber": "1020304050", "fullName": "Ana Pérez",
  "position": "Analista" | null, "workplace": "Bodega - Principal" | null,
  "workedDays": 15 | null,                              // null hasta liquidar
  "hasInputs": true,                                    // tiene horas, ingresos o deducciones capturados en la quincena
  "finalSettlementPending": false,                      // el contrato termina dentro de la quincena
  "negativeNet": false,
  "summary": PayrollEmployeeSummaryDto | null }         // null si no está liquidado (o entró a la quincena después)

// ExcludedEmployeeDto
{ "employeeId": "uuid", "documentType": "CC", "documentNumber": "…", "fullName": "…", "reason": "SalaryTypeNotSupported" }

// BlockedEmployeeDto — el empleado ya está en employees (I-9); acá sólo el motivo
{ "employeeId": "uuid", "reasons": ["MissingWorkplace"] }

// PeriodEventDto — historial, occurredAt desc
{ "id": "uuid", "occurredAt": "…", "actor": ActorDto, "action": "Reopened",
  "fromStatus": "Approved" | null, "toStatus": "Reopened" | null,
  "reason": "Faltó una comisión" | null,               // obligatorio en Reopened
  "absencesConfirmed": true | null }                    // sólo en Liquidated

// PayrollPeriodDetailDto
{ "period": PayrollPeriodDto,
  "employees": [PayrollEmployeeRowDto],                 // liquidables Y bloqueados (I-9), por fullName; en Approved y Closed, los de la foto
  "excluded": [ExcludedEmployeeDto],                    // por fullName; en Approved y Closed, los de la foto
  "blocked": [BlockedEmployeeDto],                      // [] en Approved y Closed
  "history": [PeriodEventDto] }

// PayrollPeriodListDto — respuesta de ensure-current y de la lista
{ "company": PayrollCompanyDto,
  "items": [PayrollPeriodDto],                          // startDate desc; sin paginar (24 por año)
  "firstPeriodStartsOn": "2026-12-01" | null }          // sólo sin períodos y con hoy en una segunda quincena (R-13)

// TraceInputDto
{ "name": "salario mensual", "value": 1750905 | "Mensual" }

// LegalValueUseDto
{ "key": "HealthEmployeeRate", "label": "Salud del empleado", "value": 0.04, "effectiveFrom": "2025-01-01",
  "basis": "Ley 100/1993 art. 204" }

// PayrollLineDto
{ "code": "health_employee", "group": "Deduction", "label": "Salud", "amount": 35000,
  "trace": { "formula": "techo100(IBC del mes × 4 %) − retenido en la primera quincena",
             "inputs": [TraceInputDto], "legalValues": [LegalValueUseDto],
             "note": "Exonerado (ET art. 114-1)" | null } }

// InputLineDto
{ "conceptId": "uuid", "conceptName": "Comisiones", "kind": "Income",
  "hours": 4.5 | null,                                  // sólo Overtime
  "amount": 500000 | null,                              // Income y Deduction
  "salaryConstitutive": true | null }                   // sólo Income

// RecurringConceptDto
{ "id": "uuid", "employeeId": "uuid",
  "concept": { "id": "uuid", "name": "Auxilio de alimentación", "kind": "Income" },
  "mode": "Fixed", "amount": 100000, "salaryConstitutive": false | null,   // null en Deduction
  "cap": 1200000 | null, "accumulated": 300000, "remaining": 900000 | null,
  "status": "Active", "version": 2 }

// PayrollEmployeeDetailDto — GET de un empleado en la quincena y respuesta de sus capturas
{ "period": PayrollPeriodDto,
  "employee": PayrollEmployeeRowDto,
  "inputs": { "overtime": [InputLineDto], "incomes": [InputLineDto], "deductions": [InputLineDto] },
  "recurring": [RecurringConceptDto],                   // los del empleado en esa empresa, con su estado de hoy
  "result": { "workedDays": 15, "engineVersion": "2026.11.1",
              "lines": [PayrollLineDto],                // orden: Earning, Info, Deduction, EmployerContribution, Provision; dentro, el de la tabla
              "summary": PayrollEmployeeSummaryDto,
              "legalValues": [LegalValueUseDto] }       // la foto legal completa del período
            | null }                                    // null hasta liquidar

// InputsImportRowIssueDto — mismo shape que EmployeeImportRowIssueDto de Hr
{ "rowNumber": 3, "code": "payroll.import.row.concept_not_found", "message": "…", "field": "concept" | null }

// InputsImportRowPreviewDto
{ "rowNumber": 2, "documentNumber": "1020", "fullName": "Ana Pérez", "concept": "Comisiones", "kind": "Income",
  "hours": null, "amount": 500000, "salaryConstitutive": true }

// InputsImportPreviewDto
{ "fileName": "novedades.xlsx", "totalRows": 12, "validRows": [InputsImportRowPreviewDto],
  "errors": [InputsImportRowIssueDto],
  "summary": { "employees": 5, "overtimeLines": 4, "incomeLines": 6, "deductionLines": 2 } }
```

`PayrollPeriodCanDto` (llamada del plan): `capture` = estado `Draft`, `Liquidated` o `Reopened` y quien llama tiene
`payroll.period.manage`; `liquidate` = lo mismo y, en una segunda quincena, la primera cumple R-14; `approve` =
`approveBlockedBy` vacío y quien llama tiene `payroll.period.approve`; `close` = `Approved`, no desactualizado y `manage`;
`reopen` = `Approved` o `Closed` y `manage`; `export` = estado distinto de `Draft` y `payroll.period.read`.
`approveBlockedBy` lleva `status` si no está `Liquidated`; `dual_control` sólo con `dualControl: true` y quien llama es
`lastLiquidatedBy`.

### Rutas

Permisos: la política de la ruta es el permiso **en negrita**; el handler revalida la lista completa. «A o B» es
**cualquiera** de los dos (decisión del owner, 2026-11-21). `{p}` es
`/payroll/periods/{periodId:guid}`; `{c}` es `/payroll/companies/{companyId:guid}`.

| Método y ruta | Permiso | Cuerpo | Respuesta | Errores |
| --- | --- | --- | --- | --- |
| `GET /payroll/companies` | **`payroll.period.read` o `payroll.settings.manage`** (política `SettingsView`) | — | 200 `[PayrollCompanyDto]`: activas más las inactivas con algún período **o** con configuración guardada (M-1), por nombre | — |
| `GET {c}/settings` | **`payroll.period.read` o `payroll.settings.manage`** (política `SettingsView`) | — | 200 `PayrollSettingsDto` (con el catálogo de conceptos) | 404 `payroll.company.not_found` |
| `PUT {c}/settings` | **`payroll.settings.manage`**, `If-Match` | `PayrollSettingsRequest` | 200 `PayrollSettingsDto` | 404, 422 `errors`, 412/428 |
| `POST {c}/concepts` | **`payroll.settings.manage`** | `ConceptRequest` | 201 `PayrollConceptDto` | 404, 409 `payroll.concept.name_taken` (por empresa y tipo), 422 `errors` |
| `PUT {c}/concepts/{conceptId}` | **`payroll.settings.manage`**, `If-Match` | `ConceptRequest` (`kind` igual al guardado ⇒ si no, 422 `errors.kind`) | 200 `PayrollConceptDto` | 404 `payroll.concept.not_found`, 422 `payroll.concept.system_readonly`, 409, 412/428 |
| `POST {c}/concepts/{conceptId}/archive` · `…/unarchive` | **`payroll.settings.manage`**, **sin** `If-Match` (I-8) | — | 200 `PayrollConceptDto` | 404, 422 `payroll.concept.system_readonly`, 409 `payroll.concept.already_archived` / `payroll.concept.not_archived` |
| `GET {c}/employees/{employeeId}/recurring-concepts` | **`payroll.period.read` o `payroll.settings.manage`** (política `SettingsView`) | — | 200 `[RecurringConceptDto]` (activos primero, luego por nombre) | 404 `payroll.employee.not_found` (sin contrato con la empresa) |
| `POST {c}/employees/{employeeId}/recurring-concepts` | **`payroll.settings.manage`** | `RecurringConceptRequest` | 201 `[RecurringConceptDto]` (la lista entera) | 404, 422 `payroll.concept.archived`, 422 `payroll.concept.kind_mismatch` (un `Overtime`), 422 `errors` |
| `PUT {c}/employees/{employeeId}/recurring-concepts/{id}` | **`payroll.settings.manage`**, `If-Match` | `RecurringConceptRequest` | 200 `[RecurringConceptDto]` | 404 `payroll.recurring.not_found`, los del POST, 412/428 |
| `POST …/recurring-concepts/{id}/activate` · `…/deactivate` | **`payroll.settings.manage`**, **sin** `If-Match` (I-8) | — | 200 `[RecurringConceptDto]` | 404, 409 `payroll.recurring.already_active` / `payroll.recurring.already_inactive` |
| `POST {c}/periods/ensure-current` | `payroll.period.read`, **`payroll.period.manage`** (I-6) | — | 200 `PayrollPeriodListDto` | 404, 422 `payroll.company.inactive` |
| `GET {c}/periods` | **`payroll.period.read`** | — | 200 `PayrollPeriodListDto` (no crea nada; `firstPeriodStartsOn` igual que arriba) | 404 |
| `GET {p}` | **`payroll.period.read`** | — | 200 `PayrollPeriodDetailDto` | 404 `payroll.period.not_found` |
| `GET {p}/employees/{employeeId}` | **`payroll.period.read`** | — | 200 `PayrollEmployeeDetailDto`; un **excluido** responde 200 con `result: null` y las listas vacías en `Draft`, `Liquidated` y `Reopened` (I-13); en `Approved` y `Closed`, 200 si está en la foto (resultado o excluido) | 404 `payroll.employee.not_in_period` sólo si no tiene contrato de la empresa en la quincena (o, en `Approved`/`Closed`, no está en la foto) |
| `PUT {p}/employees/{employeeId}/inputs` | `payroll.period.read`, **`payroll.period.manage`**, `If-Match` | `EmployeeInputsRequest` | 200 `PayrollEmployeeDetailDto` | 404 `payroll.employee.not_in_period`, 422 `payroll.employee.not_liquidable`, 422 `payroll.period.not_editable`, 422 `errors` (`overtime[i].conceptId` desconocido, archivado o de otro tipo; `overtime[i].conceptId` repetido), 412/428 |
| `POST {p}/liquidate` | ídem, `If-Match` | `{ "confirmNoAbsences": true }` | 200 `PayrollPeriodDetailDto` | 422 `payroll.liquidation.absences_unconfirmed`, 422 `payroll.period.invalid_transition`, 422 `payroll.period.first_half_not_liquidated`, 422 `payroll.liquidation.blocked`, 422 `payroll.liquidation.nobody_to_liquidate`, 422 `payroll.legal_parameter.missing`, 412/428 |
| `POST {p}/approve` | `payroll.period.read`, **`payroll.period.approve`**, `If-Match` | — | 200 `PayrollPeriodDetailDto` | 422 `payroll.period.invalid_transition`, 422 `payroll.period.outdated` (también con `sourceChanged`), 422 `payroll.period.negative_net`, 422 `payroll.period.first_half_not_approved`, 422 `payroll.period.same_person_cannot_approve`, 412/428 |
| `POST {p}/close` | `payroll.period.read`, **`payroll.period.manage`**, `If-Match` | — | 200 `PayrollPeriodDetailDto` | 422 `payroll.period.invalid_transition`, 422 `payroll.period.outdated`, 412/428 |
| `POST {p}/reopen` | ídem | `{ "reason": "string 1..500" }` | 200 `PayrollPeriodDetailDto` | 422 `payroll.period.invalid_transition`, 422 `errors.reason`, 412/428 |
| `GET {p}/export` | **`payroll.period.read`** | — | 200 xlsx `resumen-nomina-<startDate>-a-<endDate>.xlsx` | 422 `payroll.period.not_exportable` (en `Draft`) |
| `GET {p}/inputs/template` | `payroll.period.read`, **`payroll.period.manage`** | — | 200 xlsx `plantilla-novedades.xlsx` | 422 `payroll.period.not_editable` |
| `POST {p}/inputs/import/preview` | ídem | multipart `file` | 200 `InputsImportPreviewDto` | 422 `payroll.period.not_editable`, 422 `payroll.import.*` de archivo |
| `POST {p}/inputs/import` | ídem, `If-Match` | multipart `file` | 200 `PayrollPeriodDetailDto` | los de la vista previa, 422 `payroll.import.rows_invalid`, 412/428 |

Transiciones válidas (cualquier otra ⇒ 422 `payroll.period.invalid_transition`): `Draft|Reopened → Liquidated` (liquidar),
`Liquidated → Liquidated` (volver a liquidar), `Liquidated → Approved`, `Approved → Closed`, `Approved|Closed → Reopened`.
Capturar o importar sólo en `Draft`, `Liquidated` y `Reopened`; en `Liquidated` deja `outdated: true` (y el evento
`MarkedOutdated` la primera vez). En `Approved` y `Closed` ⇒ 422 `payroll.period.not_editable`. Liquidar pone
`outdated: false`. Las rutas `{p}/inputs/*` se registran antes que `{p}/employees/{employeeId:guid}`.

### Requests

```jsonc
// PayrollSettingsRequest — reemplaza todo
{ "transportFrequency": "Biweekly", "transportCapBase": "SalaryOnly", "overtimeBase": "SalaryOnly",
  "exonerated": true, "dualControl": false }

// ConceptRequest
{ "kind": "Income", "name": "string 1..80",
  "defaultSalaryConstitutive": true | false | null,     // sólo Income; en otro kind debe ser null ⇒ si no, 422 errors.defaultSalaryConstitutive
  "multiplier": 1.4 | null }                            // requerido en Overtime, 0 < m ≤ 10, ≤ 4 decimales; null en los demás
// Nombre único por empresa y kind, comparando sin mayúsculas, tildes ni espacios de más, archivados incluidos, y distinto de
// los nombres del sistema de su kind ⇒ 409 payroll.concept.name_taken.

// RecurringConceptRequest
{ "conceptId": "uuid",                                  // Income o Deduction (del sistema o propio, no archivado)
  "mode": "Fixed", "amount": 100000,
  "salaryConstitutive": false | null,                   // requerido con Income; null con Deduction
  "cap": 1200000 | null }                               // ≥ amount si viene

// EmployeeInputsRequest — reemplaza las tres listas del empleado en la quincena
{ "overtime":   [{ "conceptId": "uuid", "hours": 4.5 }],                       // ≤ 20; un concepto una vez
  "incomes":    [{ "conceptId": "uuid", "amount": 500000, "salaryConstitutive": true }],   // ≤ 50
  "deductions": [{ "conceptId": "uuid", "amount": 20000 }] }                   // ≤ 50
// Cada conceptId debe ser del kind de su lista, del sistema o propio de la empresa del período, y no archivado.
```

### Importación de novedades

- Hoja `Novedades`, columnas en este orden: `NÚMERO DE DOCUMENTO`, `EMPLEADO`, `CONCEPTO`, `HORAS`, `VALOR`,
  `CONSTITUTIVO DE SALARIO` (6). `EMPLEADO` es informativa y no se lee.
- Hoja `Empleados` (referencia): documento y nombre de los liquidables de la quincena. Hoja `Valores válidos`: los nombres de
  los conceptos no archivados de la empresa (sistema y propios) y `Sí`/`No`, con validación de lista en `CONCEPTO` y
  `CONSTITUTIVO DE SALARIO`.
- Una fila es una línea. `CONCEPTO` se compara sin mayúsculas, tildes ni espacios con los nombres no archivados de la empresa.
  `Overtime`: `HORAS` requerida y `VALOR` vacío. `Income`: `VALOR` requerido, `HORAS` vacía, `CONSTITUTIVO DE SALARIO` vacío ⇒
  la sugerencia del concepto; sin sugerencia ⇒ error. `Deduction`: `VALOR` requerido, `HORAS` y `CONSTITUTIVO` vacíos.
- Un mismo empleado con el mismo concepto `Overtime` en dos filas ⇒ error en las dos (`overtime_duplicated_in_file`).
- Archivo: `.xlsx`, ≤ 5 MB, ≤ 5000 filas con datos. Errores de archivo (422, sin filas): `payroll.import.file_required`,
  `payroll.import.extension_invalid`, `payroll.import.too_large`, `payroll.import.unreadable`, `payroll.import.header_mismatch`,
  `payroll.import.empty`, `payroll.import.too_many_rows`.
- Códigos de fila (`payroll.import.row.*`, `field` entre paréntesis): `document_required` (documentNumber),
  `employee_not_in_period` (documentNumber), `employee_not_liquidable` (documentNumber), `concept_required` (concept),
  `concept_not_found` (concept), `hours_required` (hours), `hours_invalid` (hours), `hours_not_allowed` (hours),
  `amount_required` (amount), `amount_invalid` (amount), `amount_not_allowed` (amount), `yes_no_invalid`
  (salaryConstitutive), `salary_constitutive_required` (salaryConstitutive), `salary_constitutive_not_allowed`
  (salaryConstitutive), `overtime_duplicated_in_file` (concept), `too_many_lines` (concept: las filas de un empleado que
  pasan de 20 horas, 50 ingresos o 50 deducciones, los mismos topes de `EmployeeInputsRequest`; se marcan todas las filas
  sobrantes, pre-flight M-13).
- Confirmar con alguna fila con error ⇒ 422 `payroll.import.rows_invalid` y no escribe nada. Si no, reemplaza las capturas de
  cada empleado del archivo (R-24) en una transacción, sube la versión una vez y deja un evento `InputsImported`.

### Excel «Resumen de nómina»

- Hoja `Resumen`: filas 1–5 de encabezado (`Empresa` nombre y NIT, `Período` `2026-11-01 a 2026-11-15`, `Quincena`
  etiqueta, `Estado` etiqueta, `Generado` fecha y hora del tenant `yyyy-MM-dd HH:mm`), fila 7 de títulos, una fila por
  empleado liquidado (por nombre) y una fila final `Totales`. Columnas en este orden: Tipo de documento, Número de
  documento, Nombre, Cargo, Centro de trabajo, Días, Salario, Auxilio, Horas extras y recargos, Otros ingresos salariales,
  Ingresos no salariales, Total devengado, Salud, Pensión, FSP, Otras deducciones, Total deducciones, Neto, Salud empleador,
  Pensión empleador, ARL, Caja, ICBF, SENA, Cesantías, Intereses a las cesantías, Prima, Vacaciones, Costo empresa.
  **(llamada del plan)** el orden de las provisiones es el del spec (cesantías, intereses, prima, vacaciones).
- Hoja `No liquidados`: tipo y número de documento, nombre, motivo (etiqueta de `ExclusionReason`).
- Los totales son valores que manda el backend, no fórmulas.

### Mensajes de dominio

| code | HTTP |
| --- | --- |
| `payroll.company.not_found`, `payroll.period.not_found`, `payroll.employee.not_found`, `payroll.employee.not_in_period`, `payroll.concept.not_found`, `payroll.recurring.not_found` | 404 |
| `payroll.concept.name_taken`, `payroll.concept.already_archived`, `payroll.concept.not_archived`, `payroll.recurring.already_active`, `payroll.recurring.already_inactive` | 409 |
| `payroll.company.inactive`, `payroll.employee.not_liquidable`, `payroll.concept.system_readonly`, `payroll.concept.archived`, `payroll.concept.kind_mismatch`, `payroll.period.not_editable`, `payroll.period.invalid_transition`, `payroll.period.outdated`, `payroll.period.negative_net`, `payroll.period.first_half_not_liquidated`, `payroll.period.first_half_not_approved`, `payroll.period.same_person_cannot_approve`, `payroll.period.not_exportable`, `payroll.liquidation.absences_unconfirmed`, `payroll.liquidation.blocked`, `payroll.liquidation.nobody_to_liquidate`, `payroll.legal_parameter.missing` (el `message` nombra las claves que faltan), `payroll.import.*` | 422 |

## Valores legales: claves y siembra

Claves nuevas de `LegalParameterKey` (Hr), en este orden después de `IntegralSalaryFloor`. Vigencia 2025-01-01 y 2026-01-01
con el mismo valor salvo lo indicado («cada año lleva un conjunto completo»). `label` y `basis` viven en `LegalBasis`
(Payroll, Domain) y salen en el rastro.

| Clave | Valor | Vigencias distintas | label · basis |
| --- | --- | --- | --- |
| `TransportAllowance` | 200000 (2025) · 249095 (2026) | — | Auxilio de transporte · Ley 15/1959 |
| `ConnectivityAllowanceSince` | 1 | sólo 2025-06-25 | Auxilio de conectividad para teletrabajo · Ley 2121/2021 |
| `HealthEmployeeRate` / `HealthEmployerRate` | 0.04 / 0.085 | — | Salud del empleado / del empleador · Ley 100/1993 art. 204 |
| `PensionEmployeeRate` / `PensionEmployerRate` | 0.04 / 0.12 | — | Pensión del empleado / del empleador · Ley 100/1993 art. 204 |
| `HighRiskPensionTotalRate` | 0.26 | — | Pensión de alto riesgo (total) · Decreto 2090/2003 |
| `ArlClassIRate` … `ArlClassVRate` | 0.00522 · 0.01044 · 0.02436 · 0.0435 · 0.0696 | — | ARL clase I…V · Decretos 1772/1994 y 1607/2002 |
| `CompensationFundRate` / `IcbfRate` / `SenaRate` | 0.04 / 0.03 / 0.02 | — | Caja de compensación · Ley 21/1982; ICBF · Ley 89/1988; SENA · Ley 119/1994 |
| `SeveranceProvisionRate` / `PremiumProvisionRate` / `VacationProvisionRate` / `SeveranceInterestProvisionRate` | 0.0833 / 0.0833 / 0.0417 / 0.01 | — | Cesantías · Ley 50/1990 art. 99; Prima · CST art. 306; Vacaciones · CST arts. 186 a 192; Intereses a las cesantías · Ley 52/1975 |
| `TransportAllowanceCapSmmlv` | 2 | — | Tope del auxilio de transporte · Ley 15/1959 |
| `NonSalaryCapRate` | 0.40 | — | Tope de pagos no salariales · Ley 1393/2010 art. 30 |
| `ExonerationThresholdSmmlv` | 10 | — | Umbral de exoneración · ET art. 114-1 |
| `IbcCapSmmlv` | 25 | — | Tope del IBC · Ley 797/2003 art. 5 |
| `SundaySurchargeRate` | — | 0.80 desde 2025-07-01 · 0.90 desde 2026-07-01 · 1.00 desde 2027-07-01 | Recargo dominical y festivo · Ley 2466/2025 |
| `WeeklyHours` | — | 47 desde 2023-07-15 · 46 desde 2024-07-15 · 44 desde 2025-07-15 · 42 desde 2026-07-15 | Jornada máxima semanal · Ley 2101/2021 art. 3 |
| `ExtraDayRate` / `ExtraNightRate` / `NightSurchargeRate` | 0.25 / 0.75 / 0.35 | — | Hora extra diurna / nocturna / Recargo nocturno · CST, Ley 2466/2025 |

`Smmlv` ya existe (fase 1; label «Salario mínimo mensual legal vigente», basis «Decreto anual del salario mínimo»).
Tramos del FSP (`hr.legal_parameter_brackets`, clave `SolidarityFund`, vigencias 2025-01-01 y 2026-01-01, `basis` «Ley 100/1993
art. 27»): `[4,16)` 0.01 · `[16,17)` 0.012 · `[17,18)` 0.014 · `[18,19)` 0.016 · `[19,20)` 0.018 · `[20,25]` 0.02 (el último con
`upper_bound` 25 e inclusivo).

## Motor: definiciones exactas

Notación: período `P = [ps, pe]` de la mitad `h`; `L` es el `LegalValueSet` resuelto a `ps`; `prior` son los
`MonthAccumulators` de la primera quincena del mismo mes y empresa (en `h = First`, todo cero; en `h = Second` sin resultado del
empleado en la primera, todo cero). `round` = al peso, mitad hacia arriba; `ceil100` = hacia arriba al múltiplo de 100;
`ceilPeso` = hacia arriba al peso (R-6). Los montos de las líneas son los redondeados; lo que se acumula para el mes son los
montos de las líneas salvo `SalaryRaw` y `TransportRaw`.

1. **Días** (`WorkedDaysRule`, R-7). `covered = [max(ps, StartDate), min(pe, EndedOn ?? pe)]`. `workedDays = 15` si
   `covered = P`; si no, `min(15, días de covered)`. `finalSettlementPending = EndedOn ∈ [ps, pe]`.
2. **Salario** (`SalaryRule`). Tramos: cada registro de salario rige desde su `EffectiveDate` hasta el día antes del siguiente;
   se corta a `covered`; días de cada tramo por R-7. `SalaryRaw = Σ monto_i ÷ 30 × días_i`. `salary = round(SalaryRaw)` en la
   primera; `round(prior.SalaryRaw + SalaryRaw) − prior.Salary` en la segunda. `S` = monto del tramo del último día cubierto;
   `teleworker` y `salaryRecordDate` también de ese tramo (R-8).
3. **Ingresos** (`IncomeRule`, `RecurringConceptRule`). Cada ingreso capturado ⇒ `income:<conceptId>:<n>` = `round(amount)`.
   Cada recurrente activo y no agotado ⇒ `Fixed`: `amount`; `Proportional`: `amount × workedDays ÷ 15`; con tope,
   `min(eso, cap − accumulatedBefore)`; `round`; si da 0 no hay línea. `SI` = ingresos salariales (capturados y recurrentes);
   `NSI` = no salariales.
4. **Mes** (`MonthIncomeRule`). `SI_m = prior.SalaryIncome + SI`; `OT_m = prior.Overtime + OT`; `NSI_m = prior.NonSalaryIncome +
   NSI`. `monthSalaryIncome = S + SI_m + OT_m` (exoneración). `transportCapIncome` = `S` (`SalaryOnly` y `SalaryAndPaidAbsences`,
   R-22) o `S + SI_m` (`SalaryAndSalaryIncome`).
5. **Horas** (`OvertimeRule`). `base = S` (`SalaryOnly`) o `S + SI_m` (`SalaryAndMonthSalaryIncome`); `hourValue = base ÷ 30 ÷
   (L.WeeklyHours ÷ 6)` **sin redondear** (M-5); cada captura ⇒ `overtime:<conceptId>` = `round(hours × hourValue × factor)`, con
   `factor = OvertimeFactors.For(input, L)` (R-2): el motor lo deriva de `L` (los compuestos dominicales suman
   `L.SundaySurchargeRate` vigente a `ps`); un concepto propio trae su `multiplier`.
   `OT` = suma. (Se calcula antes del paso 4, que usa `OT`; `SI_m` no depende de `OT`.)
6. **Auxilio** (`TransportAllowanceRule`). `eligible = transportCapIncome ≤ L.TransportAllowanceCapSmmlv × L.Smmlv`. Código
   `connectivity_allowance` si `teleworker` y `L.ConnectivityAllowanceSince ≠ null` y `salaryRecordDate ≥
   ConnectivityAllowanceSince`; si no, `transport_allowance`. No elegible ⇒ 0, `TransportRaw = 0`, nota «El ingreso del mes
   supera 2 SMMLV». `Biweekly`: `raw = L.TransportAllowance ÷ 30 × workedDays`; primera `round(raw)`; segunda
   `round(prior.TransportRaw + raw) − prior.Transport`. `Monthly` (R-23): primera 0 con nota; segunda
   `round(L.TransportAllowance ÷ 30 × (prior.WorkedDays + workedDays))`.
7. **IBC** (`IbcRule`). `SalDev_m = prior.Salary + salary + OT_m + SI_m`; `total = SalDev_m + NSI_m`; `raw = total × (1 −
   L.NonSalaryCapRate)` si `NSI_m > L.NonSalaryCapRate × total`, si no `SalDev_m`. `days_m = prior.WorkedDays + workedDays`;
   `IBC_m = ceilPeso(min(max(raw, L.Smmlv ÷ 30 × days_m), L.IbcCapSmmlv × L.Smmlv))`. Línea `ibc` (Info) = `IBC_m`.
8. **Deducciones de seguridad social** (`EmployeeDeductionsRule`). `health_m = ceil100(IBC_m × L.HealthEmployeeRate)`; línea =
   `health_m − prior.HealthEmployee`. Igual pensión con `PensionEmployeeRate`.
9. **FSP** (`SolidarityFundRule`, R-9). Primera: `rate = tramo(min(S, L.IbcCapSmmlv × L.Smmlv) ÷ L.Smmlv)` (0 bajo 4;
   B-1); línea `ceil100(IBC_m × rate)`.
   Segunda: `rate = tramo(IBC_m ÷ L.Smmlv)`; línea `ceil100(IBC_m × rate) − prior.SolidarityFund` (negativa = devolución, nota
   «Devolución de lo retenido en la primera quincena»). Sin tramo y sin retención previa no hay línea.
10. **Otras deducciones**. Cada capturada ⇒ `deduction:<conceptId>:<n>` = `round(amount)`; recurrentes de deducción como en 3.
11. **Aportes del empleador** (`EmployerContributionsRule`, R-3, R-4, R-5, R-20). `exempt = settings.Exonerated &&
    monthSalaryIncome < L.ExonerationThresholdSmmlv × L.Smmlv`. Sobre `IBC_m`: `health_employer` (0 si `exempt`),
    `pension_employer` (tasa `L.HighRiskPensionTotalRate − L.PensionEmployeeRate` si el centro es de alto riesgo, si no
    `L.PensionEmployerRate`). Sobre `SalDev_m` (R-4, I-7): `arl` (tasa de la clase del centro), `compensation_fund`, `icbf`
    (0 si `exempt`), `sena` (0 si `exempt`). Cada una `ceil100(base × tasa) − prior.<la misma>`.
12. **Provisiones** (`ProvisionsRule`, R-21). `base = salary + auxilio + OT + SI` del período; `severance = round(base ×
    L.SeveranceProvisionRate)`, `premium = round(base × L.PremiumProvisionRate)`, `severance_interest = round(base ×
    L.SeveranceInterestProvisionRate)`, `vacation = round((salary + SI) × L.VacationProvisionRate)`.
13. **Resultado** (`PayrollEngine.Liquidate`). `totalEarned = salary + auxilio + OT + SI + NSI`; `totalDeductions` = salud +
    pensión + FSP + otras (capturadas y recurrentes); `net = totalEarned − totalDeductions`; `negativeNet = net < 0`;
    `employerCost = totalEarned + Σ aportes del empleador + Σ provisiones`. `MonthAccumulators` de salida = `prior +` lo del
    período, con `SalaryRaw` y `TransportRaw` sin redondear (no guarda el IBC: la segunda lo recalcula del mes, M-7).
    Orden de las líneas, fijo (I-11): `Earning`, `Info`, `Deduction`, `EmployerContribution`, `Provision`; dentro de cada grupo,
    el de la tabla de códigos del contrato (capturas y recurrentes en su orden de captura). Los casos de oro comparan en ese
    orden.

Rastro: cada línea lleva `formula` (texto fijo por regla, en español, con los símbolos `×`, `÷`, `techo100`, `techo al
peso`), `inputs` (los nombres y valores que usó, en el orden de la fórmula), los `legalValues` que leyó (clave, label, valor,
vigencia, basis) y `note`. Cada regla devuelve sus líneas ya con el rastro; el orquestador no arma rastros.

Anclas calculadas a mano (2026; quincena completa; centro clase I; empresa exonerada; las usa la Tarea 6):

| Caso | Datos | Esperado |
| --- | --- | --- |
| `minimum-wage-first-half` | 2026-11-01..15, salario 1 750 905, `Biweekly` | salary 875 453 · transport_allowance 124 548 · ibc 875 453 · health_employee 35 100 · pension_employee 35 100 · health_employer 0 · pension_employer 105 100 · arl 4 600 · compensation_fund 35 100 · icbf 0 · sena 0 · severance 83 300 · premium 83 300 · severance_interest 10 000 · vacation 36 506 · net 929 801 · employerCost 1 357 907 |
| `minimum-wage-second-half` | 2026-11-16..30, mismo empleado, `prior` del caso anterior | salary 875 452 · transport_allowance 124 547 · ibc 1 750 905 · health_employee 35 000 · pension_employee 35 000 · pension_employer 105 100 · arl 4 600 · compensation_fund 35 000 · severance 83 300 · premium 83 300 · severance_interest 10 000 · vacation 36 506 |
| `overtime-after-42-hours` | 2026-08-01..15, salario 2 100 000, 10 h `ExtraDay`, 8 h `SundaySurcharge` | valor hora 10 000 · overtime ExtraDay 125 000 · overtime SundaySurcharge 72 000 |
| `non-salary-over-forty-percent` | 2026-11-01..15, salario 2 000 000, `OtherIncome` 1 000 000 no constitutivo | salary 1 000 000 · ibc 1 200 000 · health_employee 48 000 · pension_employee 48 000 · arl 5 300 (sobre 1 000 000, I-7) · compensation_fund 40 000 |
| `solidarity-fund-refund` | 2026-11, salario 7 500 000; primera completa; contrato termina 2026-11-20 | primera: ibc 3 750 000 · solidarity_fund 37 500. Segunda: workedDays 5 · salary 1 250 000 · ibc 5 000 000 · solidarity_fund −37 500 · health_employee 50 000 · transport_allowance 0 · `finalSettlementPending` |
| `ibc-cap-and-top-bracket` | 2026-11, salario 50 000 000 | primera: ibc 25 000 000 · solidarity_fund 500 000 · health_employee 1 000 000. Segunda: ibc 43 772 625 · health_employee 751 000 · pension_employee 751 000 · solidarity_fund 375 500 · health_employer 1 595 700 |
| `month-income-keeps-transport` | 2026-11-01..15, salario 3 000 000, `Commissions` 500 000 constitutivo, `transportCapBase: SalaryAndSalaryIncome` | transport_allowance 124 548 (3 500 000 ≤ 3 501 810) |
| `month-income-loses-transport` | igual con `Commissions` 501 811 | transport_allowance 0 con nota |
| `sunday-overtime-before-july-2026` | 2026-06-01..15 (recargo dominical 0,80; jornada 44), salario 2 200 000, 10 h de cada uno: `SundaySurcharge`, `ExtraSundayDay`, `ExtraSundayNight`, `NightSundaySurcharge` | valor hora 10 000 (2 200 000 ÷ 30 ÷ (44 ÷ 6)) · factores 0,80 / 2,05 / 2,55 / 1,15 ⇒ 80 000 · 205 000 · 255 000 · 115 000 |
| `sunday-overtime-after-july-2026` | 2026-08-01..15 (recargo dominical 0,90; jornada 42), salario 2 100 000, las mismas 10 h | valor hora 10 000 · factores 0,90 / 2,15 / 2,65 / 1,25 ⇒ 90 000 · 215 000 · 265 000 · 125 000 |

## Mapa de archivos

Calco de `src/Modules/Hr/` (un tipo por archivo; `HrDbContext`, `HrUnitOfWork`, `HrAuditPublisher`, `HrAuthorization`,
`HrValidator`, `HrIfMatch`, `HrApiHarness`). Lo que no está en Hr se calca del hermano citado en cada tarea.

```
src/Modules/Payroll/
  Modules.Payroll.Domain/       PayrollDomainException, PayrollErrorCodes; ids (PayrollPeriodId, PayrollConceptId,
                                RecurringConceptId, PeriodEventId); enums de la tabla del contrato + ArlClass, SystemConceptKey;
                                SystemConcepts, QuincenaCalendar, PayrollText (Fold), CompanyPayrollSettings, PayrollConcept,
                                RecurringConcept, PayrollPeriod, PeriodInput, PeriodEvent
    Engine/                     LegalValue, LegalValueSet, SolidarityBracket, LegalBasis, PayrollRounding, EngineInput (+ sus
                                registros), MonthAccumulators, PayrollLine, LineTrace, EmployeeLiquidation, PayrollSummary,
                                WorkedDaysRule, SalaryRule, IncomeRule, RecurringConceptRule, OvertimeRule, MonthIncomeRule,
                                TransportAllowanceRule, IbcRule, EmployeeDeductionsRule, SolidarityFundRule,
                                EmployerContributionsRule, ProvisionsRule, PayrollEngine
  Modules.Payroll.Application/  PayrollPermissions, PayrollAuthorization, PayrollValidator, PayrollIfMatch, PayrollNotFound,
                                PayrollAuditActions, IPayrollAuditPublisher, IPayrollUnitOfWork, PayrollLabels,
                                IPayrollHrDirectory (+ Hr*Source), IPayrollLegalValues, IPayrollMemberDirectory, repositorios
                                (ICompanyPayrollSettingsRepository, IPayrollConceptRepository, IRecurringConceptRepository,
                                IPayrollPeriodRepository, IPayrollResultStore), PayrollEligibility, EngineInputFactory,
                                InputFingerprint, ConceptCatalog, RecurringAccumulation, PeriodComposer, PayrollDtos,
                                PayrollRequests, PayrollRequestValidators, un archivo por caso de uso, Excel/ (PayrollColumn,
                                PayrollColumnLayout, PayrollColumnLayouts, PayrollWorkbook, IPayrollWorkbookBuilder),
                                Import/ (InputsImportColumns, IExcelInputsImporter, IInputsImportTemplateBuilder,
                                InputsImportRowParser, InputsImportAnalyzer, InputsImportDtos)
  Modules.Payroll.Infrastructure/ PayrollInfrastructureExtensions, PayrollDatabaseInitializer, Persistence/{PayrollDbContext,
                                PayrollDbContextFactory, PayrollUnitOfWork, UniqueViolation, PayrollAuditPublisher, repositorios,
                                PayrollResultStore, Migrations/}, Excel/{ClosedXmlPayrollWorkbookBuilder,
                                ClosedXmlInputsImporter, ClosedXmlInputsImportTemplateBuilder}
  Modules.Payroll.Api/          PayrollEndpoints
src/Bootstrapper/               PayrollHrDirectory, PayrollLegalValues, PayrollMemberDirectory
src/Modules/Hr/                 (Tareas 3 y 7) LegalParameterKey (+claves), LegalBracketKey, LegalParameterBracket,
                                LegalParameters.ResolveBrackets, ILegalParameterSource.ListBracketsAsync, LegalParameterSource,
                                HrDbContext, migración AddPayrollLegalParameters; IEmployeeRepository.ListForPayrollAsync
tests/Modules/Payroll/          Modules.Payroll.UnitTests/ (Engine/*, Golden/*.json, GoldenCaseTests, …),
                                Modules.Payroll.IntegrationTests/ (PayrollApiHarness, PayrollApiSeeds, *ApiTests)
tests/ArchitectureTests/ArchitectureTests/PayrollLayerTests.cs
```

Payroll llega a Hr **sólo** por `IPayrollHrDirectory` e `IPayrollLegalValues`, y a las membresías por
`IPayrollMemberDirectory`; ningún proyecto `Modules.Payroll.*` referencia `Modules.Hr.*`, `Modules.Companies.*` ni
`Modules.Identity.*` (`PayrollLayerTests`). Los adaptadores leen por las interfaces de `Modules.Hr.Application`
(`IEmployeeRepository`, `IHrSettingsRepository`, `IHrCompanyLookup`, `ILegalParameterSource`, `IHrMemberDirectory`), nunca SQL
crudo entre módulos.

## Bloques

| Bloque | Tareas | Qué deja | Cierre |
| --- | --- | --- | --- |
| A — Fundación | 1 clave `payroll` · 2 esqueleto y permisos · 3 valores legales | módulo vacío cableado, tabla legal ampliada y su puerto | build, ArchitectureTests, unitarias de Hr y Payroll, `HrLegalParametersApiTests` |
| B — Motor | 4 reglas de devengo · 5 seguridad social, provisiones y orquestador · 6 casos de oro sintéticos | motor puro probado con anclas | `Modules.Payroll.UnitTests` completo (sin contenedores) |
| C — Hr, configuración y período | 7 puerto a Hr y validador previo · 8 persistencia, configuración, conceptos y recurrentes · 9 período y transiciones | quincenas abiertas, aprobadas, cerradas y reabiertas sin cálculo | unitarias + clases de integración del bloque |
| D — Capturas, liquidación y Excel | 10 capturas · 11 liquidación y foto · 12 importación de novedades · 13 Excel, aislamiento entre tenants y valores legales congelados (Review Focus 3 y 6, M-15) | la nómina completa de punta a punta | unitarias + clases de integración del bloque |
| E — Cierre | 14 matriz de permisos y camino feliz · 16 verificación final | la rama lista para revisión fuerte | suite de los proyectos tocados |
| F — Diferido | 15 caso de oro real de Siigo | el criterio de aceptación del spec | cuando el owner entregue la quincena |

Tareas encadenadas sobre los mismos archivos van en el mismo bloque (4–6 comparten `Engine/`; 9–11 comparten
`PayrollPeriod` y `PayrollEndpoints`). Un implementador por bloque, una revisión por bloque.

---

## Bloque A — Fundación (Tareas 1–3)

### Task 1: Clave de módulo `payroll` en Tenancy

Calco del commit de `hr` (`20261121015802_AddHrModuleKey.cs` y sus pruebas).

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (después de `Hr`, línea ~115, al final de `All` y en
  `Dependencies`, línea ~150)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:151` (literal del CHECK)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddPayrollModuleKey.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `OperatorReadHandlerTests.cs`,
  `TenantModuleChangeBatchTests.cs`; `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`,
  `OperatorConsoleApiTests.cs`, `SeedStartupTests.cs`

**Interfaces:**
- Produces: `TenantModuleKeys.Payroll` (`"payroll"`), **último** de `All`, fuera de `DefaultForNewTenants`, con
  `[Payroll] = [Hr]` en `Dependencies` y el comentario `// Spec 2026-11-21 (nómina, proyecto 1): liquida a los empleados de
  Hr.` Doc-comment: «Spec 2026-11-21 (nómina, proyecto 1). Depende de `Hr` (empleados y contratos), que depende de
  `Companies`; queda fuera de `DefaultForNewTenants`: se prende y se vende por tenant.»

- [ ] **Step 1: Pruebas que fallan.** En `TenantModuleKeysTests`: la aserción `All[^1] == "hr"` de la prueba de `hr` se quita
  y se agrega `PayrollIsTheLastKnownKeyOutsideTheDefaults` (`Assert.Same(TenantModuleKeys.Payroll,
  TenantModuleKey.Parse("payroll"))`, `Assert.Equal("payroll", TenantModuleKeys.All[^1].Value)`, fuera de
  `DefaultForNewTenants`) y `PayrollDependsOnHr` (`Assert.Equal([TenantModuleKeys.Hr],
  TenantModuleKeys.DependenciesOf(TenantModuleKeys.Payroll))`). En `TenantModuleChangeBatchTests`,
  `EnablingPayrollWithoutHrIsInconsistent` (código `tenancy.modules.inconsistent_dependencies`). Sumar `"payroll"` en las
  listas literales donde hoy aparece `"hr"` (`grep -rn '"hr"' tests/Modules/Tenancy`), en orden alfabético donde las listas lo
  están (`SeedStartupTests`: entre `"orders"` y `"pipeline"`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-restore`.
  Expected: `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Payroll'`.
- [ ] **Step 3: Declarar la clave**, ampliar el CHECK con `,'payroll'` al final y generar la migración
  (`dotnet ef migrations add AddPayrollModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`).
  `Up` hace drop/add del CHECK con la lista completa; `Down` borra las filas `payroll` y repone la lista que termina en `'hr'`.
- [ ] **Step 4: GREEN.** Run:
  `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests|FullyQualifiedName~TenantModuleChangeBatchTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests|FullyQualifiedName~OperatorConsoleApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en los dos.
- [ ] **Step 5: Commit** — `feat(tenancy): add the payroll module key`

### Task 2: Esqueleto del módulo `Payroll`, permisos, autorización y validador base

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.{Domain,Application,Infrastructure,Api}/*.csproj` — calco de los cuatro
  `Modules.Hr.*.csproj` (Infrastructure con `ClosedXML`; `InternalsVisibleTo` a `Modules.Payroll.UnitTests` en Application e
  Infrastructure y a `Modules.Payroll.IntegrationTests` en Infrastructure).
- Create Domain: `PayrollDomainException.cs` (calco de `HrDomainException`, con `IHasFieldErrors`), `PayrollErrorCodes.cs`
  (una constante por código de «Mensajes de dominio» y de la importación; `Conflicts` con los de 409).
- Create Application: `PayrollPermissions.cs`, `PayrollPolicies.cs`, `PayrollAuthorization.cs`, `PayrollValidator.cs`, `PayrollIfMatch.cs`,
  `PayrollNotFound.cs`, `PayrollAuditActions.cs`, `IPayrollAuditPublisher.cs`, `IPayrollUnitOfWork.cs`
- Create Infrastructure: `PayrollInfrastructureExtensions.cs`, `PayrollDatabaseInitializer.cs`, `Persistence/PayrollDbContext.cs`
  (esquema `payroll`, `AuditEntries` con `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, historial
  `__ef_migrations_history` en `payroll`, sin tablas todavía), `PayrollDbContextFactory.cs`, `PayrollUnitOfWork.cs`,
  `PayrollAuditPublisher.cs`, `UniqueViolation.cs`
- Create Api: `PayrollEndpoints.cs` (`MapPayrollEndpoints`, grupo `/api/v1/tenants/{tenantId:guid}/payroll`, tag `Payroll`)
- Create: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Modules.Payroll.UnitTests.csproj`,
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/Modules.Payroll.IntegrationTests.csproj` (calcos de los de Hr; el de
  unitarias con `<None Include="Golden\**\*.json" CopyToOutputDirectory="PreserveNewest" />`),
  `Modules.Payroll.IntegrationTests/PayrollApiHarness.cs`, `tests/ArchitectureTests/ArchitectureTests/PayrollLayerTests.cs`
- Modify: `Backend.slnx` (carpetas `/src/Modules/Payroll/` y `/tests/Modules/Payroll/`, como las de Hr en `:177-185`),
  `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Api.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — `AddValidatorsFromAssembly(typeof(PayrollPermissions).Assembly)`
  junto a la de Hr (`:1589`), `AddPayrollInfrastructure(configuration)` junto a `AddHrInfrastructure` (`:1611`), los cuatro
  `PermissionDefinition` después de los de Hr (`:3767`), los cuatro en el rol `admin` (junto a `:2616`) y las cuatro políticas
  en `AddAuthorization` (después de `:4282`).
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/PermissionVocabulary.cs` — `ActionLabels["approve"] =
  "Aprobar"` y `CategoryLabels["Nómina"] = "Nómina"`.
- Modify: `src/Api/Program.cs` — `app.MapPayrollEndpoints();` después de `app.MapHrEndpoints();` (`:419`) y
  `await services.InitializePayrollDatabaseAsync(cancellationToken);` después del de Hr (`:537`), dentro de
  `RunUnderStartupLockAsync`, con el comentario «Payroll (spec 2026-11-21): after Audit (writes audit.entries) and after
  Companies (payroll.periods and payroll.company_settings have FKs to companies.companies). Hr is read through ports only.»
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `PayrollLayerTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollValidatorTests.cs`

**Interfaces:**
- Produces:
  - `PayrollPermissions`: `PeriodRead = "payroll.period.read"`, `PeriodManage = "payroll.period.manage"`,
    `PeriodApprove = "payroll.period.approve"`, `SettingsManage = "payroll.settings.manage"`.
  - `PermissionDefinition`s, categoría `"Nómina"`, `RequiredModules: [ModuleKeys.Payroll]`, todas riesgo `high` (ven plata):

    | Permiso | DisplayName | Description | `Shape` |
    | --- | --- | --- | --- |
    | `PeriodRead` | Ver nómina | Permite ver los períodos de nómina, la liquidación de cada empleado con su rastro de cálculo y exportar el resumen a Excel. | `new("payroll.period", "Períodos de nómina", "read")` |
    | `PeriodManage` | Liquidar nómina | Permite abrir quincenas, registrar horas extras, ingresos y deducciones, liquidar, cerrar y reabrir. | `new("payroll.period", "Períodos de nómina", "manage")` |
    | `PeriodApprove` | Aprobar nómina | Permite aprobar una quincena liquidada. | `new("payroll.period", "Períodos de nómina", "approve")` |
    | `SettingsManage` | Configurar nómina | Permite cambiar la configuración de nómina de cada empresa, sus conceptos y los conceptos recurrentes de cada empleado. | `new("payroll.settings", "Configuración de nómina", "manage")` |

  - `PayrollAuthorization.EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, CancellationToken, params string[]
    permissions)` — copia de `HrAuthorization` con `TenantModuleKeys.Payroll`; `Denied()` con «…this payroll operation…».
  - `PayrollValidator<T>` (copia de `HrValidator<T>`; además convierte `Overtime[0].Hours` ⇒ `overtime[0].hours`).
  - `PayrollAuthorization.EnsureAnyAsync(IExecutionContext, ITenantModules, Guid tenantId, CancellationToken, params string[]
    anyOf)`: igual que `EnsureAsync` pero basta **uno** de los permisos. `PayrollPolicies` (Application, `public static`):
    `SettingsView = "payroll.policies.settings_view"`, registrada en `AddAuthorization` con
    `AddAnyPermissionRequirement(policy, PayrollPermissions.PeriodRead, PayrollPermissions.SettingsManage)`, junto a las de
    `SupportPolicies` (`QepServiceCollectionExtensions.cs:4297`). No es un permiso: no se asigna a roles ni cuenta en la
    prueba de los 104.
  - `PayrollIfMatch.Require(string? ifMatch)` (copia de `HrIfMatch`) y `PayrollIfMatch.RequireAllowingZero(string?)` (acepta
    `"0"` para la primera escritura de la configuración, R-17).
  - `IPayrollUnitOfWork.SaveChangesAsync`; `IPayrollAuditPublisher.Publish(…)` con la firma de `IHrAuditPublisher`.
  - `PayrollApiHarness` (copia de `HrApiHarness`): `PayrollUrl(string tenantId = TenantId)` ⇒
    `/api/v1/tenants/{tenantId}/payroll`, `HrUrl(...)`, `OpenAsync(modules, configureServices)`, `SeedTenantAsync`,
    `SeedCallerMembershipAsync`, `CountAuditEntriesAsync`, `CreateCompanyAsync`, `DeactivateCompanyAsync`,
    `CreateClient(factory, subjectId, tenantId, params string[] permissions)`, `CreateFullAccess(factory)` (los cuatro de
    nómina **y** los cinco de Hr: las pruebas siembran empleados por la API de Hr), `MutableTenantClock` (Bogotá).

- [ ] **Step 1: Pruebas que fallan.**
  - `PayrollLayerTests`: las de `HrLayerTests` con marcadores `PayrollDomainException`, `PayrollPermissions`,
    `PayrollInfrastructureExtensions`, `PayrollEndpoints`; `ApplicationOnlyReferencesTenancyAmongTheBusinessModules`
    (prefijos permitidos `Modules.Payroll` y `Modules.Tenancy`); `NoPayrollLayerReferencesHr`,
    `NoPayrollLayerReferencesCompanies`, `NoPayrollLayerReferencesIdentity` (ningún ensamblado `Modules.Payroll.*` referencia
    uno que empiece por ese prefijo). `DomainDoesNotReferenceOuterLayers` también cubre `Engine/`.
  - `CompositionRootTests`: `PermissionDiscoveryFindsTheHundredConstants` ⇒ `PermissionDiscoveryFindsTheHundredAndFourConstants`
    con `Assert.Equal(104, …)` y «+ the four payroll.* permissions (spec 2026-11-21).» en el comentario y el `<summary>`.
    `ThePayrollPermissionsRequireThePayrollModuleAndGoOnlyToAdmin`: los cuatro con `Category == "Nómina"`,
    `RequiredModules == [ModuleKeys.Payroll]`, `Shape` como la tabla, en `PermissionsFor("admin")` y **ausentes** de
    `advisor`, `billing` y `cashier`. `EveryActionAndCategoryHasALabel` queda en verde con `approve` y `Nómina`.
  - `PayrollValidatorTests.NestedListFailuresUseCamelCaseIndexedKeys`: `PayrollValidator<Probe>` con
    `record Probe(IReadOnlyList<ProbeLine> Overtime)`, `record ProbeLine(decimal Hours)` y `RuleForEach(…).ChildRules(… GreaterThan(0))`;
    `new Probe([new(0)])` ⇒ `PropertyName == "overtime[0].hours"`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/ArchitectureTests/ArchitectureTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollPermissions' could not be found`.
- [ ] **Step 3: Crear el esqueleto y el cableado** según **Files**; `dotnet restore Backend.slnx` y sumar al commit todos los
  `packages.lock.json` nuevos o cambiados (`git status --short -- "*packages.lock.json"`).
- [ ] **Step 4: GREEN.** Run:
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PayrollLayerTests|FullyQualifiedName~CompositionRootTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~PayrollValidatorTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): scaffold the payroll module with its permissions`

### Task 3: Valores legales de nómina en Hr y su puerto

Amplía la tabla de la fase 1 (R-1). Los valores son los de «Valores legales: claves y siembra».

**Files:**
- Modify Hr Domain: `LegalParameterKey.cs` (las claves nuevas, en el orden de la tabla); `LegalParameters.cs`
  (`ResolveEntry` y `ResolveBrackets`). Create: `LegalBracketKey.cs`, `LegalParameterBracket.cs`, `LegalParameterEntry.cs`
- Modify Hr: `Modules.Hr.Application/ILegalParameterSource.cs` (`ListBracketsAsync`),
  `Modules.Hr.Infrastructure/Persistence/LegalParameterSource.cs`, `Persistence/HrDbContext.cs` (`value` con
  `HasPrecision(18, 6)`, CHECK con todas las claves, `LegalParameterBrackets`)
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/Migrations/<timestamp>_AddPayrollLegalParameters.cs` (+ Designer, snapshot)
- Create Payroll Domain `Engine/`: `LegalValue.cs`, `SolidarityBracket.cs`, `LegalValueSet.cs`, `LegalBasis.cs`; Domain
  `ArlClass.cs` (`I`…`V`; pre-flight I-1, ya implementado: la Tarea 4 lo reusa)
- Create Payroll Application: `IPayrollLegalValues.cs`
- Create: `src/Bootstrapper/PayrollLegalValues.cs`; registrarlo junto a los adaptadores de Hr (`QepServiceCollectionExtensions.cs:1898`)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParametersTests.cs` (sumar), `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrLegalParametersApiTests.cs`
  (nueva; arranca la API y lee la tabla sembrada), `tests/Bootstrapper/Bootstrapper.UnitTests/PayrollLegalValuesTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.UnitTests/LegalBasisTests.cs`

**Interfaces:**
- Produces:
  - Hr: `sealed record LegalParameterEntry(LegalParameterKey Key, DateOnly EffectiveFrom, decimal Value)`;
    `LegalParameters.ResolveEntry(IEnumerable<LegalParameterValue>, LegalParameterKey, DateOnly) : LegalParameterEntry?` (la
    fila vigente, con su `EffectiveFrom`; `Resolve` pasa a usarla). `enum LegalBracketKey { SolidarityFund }`;
    `LegalParameterBracket(LegalBracketKey Key, DateOnly EffectiveFrom, decimal LowerBound, decimal? UpperBound, decimal Rate)`;
    tabla `hr.legal_parameter_brackets`, PK `(key, effective_from, lower_bound)`, `lower_bound`/`upper_bound`
    `numeric(9,4)`, `rate numeric(18,6)`, CHECK `key IN ('SolidarityFund')`;
    `LegalParameters.ResolveBrackets(IEnumerable<LegalParameterBracket>, LegalBracketKey, DateOnly) :
    IReadOnlyList<LegalParameterBracket>` (todos los de la mayor `EffectiveFrom ≤ date`, por `LowerBound`).
    `ILegalParameterSource.ListBracketsAsync(CancellationToken)`.
  - Migración `AddPayrollLegalParameters`: `AlterColumn` de `value` a `numeric(18,6)`, drop/add de `CK_legal_parameters_key`
    con las 30 claves (las 2 de la fase 1 y las 28 nuevas; los compuestos dominicales no tienen clave, R-2), `CreateTable` de los tramos e `InsertData` de todas las filas de la tabla de siembra; `Down` las borra,
    repone el CHECK de dos claves y la precisión `(18,2)`.
  - Payroll Domain: `sealed record LegalValue(string Key, decimal Value, DateOnly EffectiveFrom)`;
    `sealed record SolidarityBracket(decimal LowerSmmlv, decimal? UpperSmmlv, decimal Rate, DateOnly EffectiveFrom)`;
    `sealed record LegalValueSet(DateOnly ResolvedAt, LegalValue Smmlv, LegalValue TransportAllowance, …una propiedad
    `LegalValue` por clave de la tabla…, DateOnly? ConnectivityAllowanceSince, IReadOnlyList<SolidarityBracket>
    SolidarityBrackets)` con `LegalValue ArlRate(ArlClass)` y `decimal? SolidarityRate(decimal smmlvMultiple)` (inferior
    inclusivo; el último incluye su tope; fuera de todo tramo ⇒ `null`). Serializable con `System.Text.Json` sin
    configuración (es la foto del período).
  - `LegalBasis` (`public static`): `string LabelOf(string key)` y `string BasisOf(string key)` con los textos de la tabla de
    siembra; una clave sin entrada lanza `KeyNotFoundException` (lo cubre `LegalBasisTests`).
  - `IPayrollLegalValues.ResolveAsync(DateOnly date, CancellationToken) : Task<LegalValueSet>` — falta cualquier clave o no
    hay tramos ⇒ `PayrollDomainException(PayrollErrorCodes.LegalParameterMissing, "Missing legal values on {date}: {keys}.")`.
  - `PayrollLegalValues` (Bootstrapper, `internal sealed`, sobre `ILegalParameterSource`): resuelve cada clave con
    `ResolveEntry` a `date`; `ConnectivityAllowanceSince` = la `EffectiveFrom` de la fila de valor 1 vigente a `date`, o
    `null` si no hay.

- [ ] **Step 1: Pruebas que fallan.**
  - `LegalParametersTests` (Hr): `ResolveEntryKeepsTheEffectiveDate` (`WeeklyHours` en 2026-07-14 ⇒ 44 desde 2025-07-15; en
    2026-07-15 ⇒ 42); `BracketsOfTheYearInForce` (2026-03-01 ⇒ seis tramos de 2026 por `LowerBound`).
  - `HrLegalParametersApiTests` (integración; lee con `ILegalParameterSource` desde el `IServiceProvider` del factory):
    `TheMigrationSeedsTheTwentyTwentySixSet` (a 2026-11-01: `TransportAllowance` 249095, `ArlClassVRate` 0.0696,
    `SundaySurchargeRate` 0.90, `WeeklyHours` 42, `IbcCapSmmlv` 25); `ValuesKeepSixDecimals` (`ArlClassIRate` == 0.00522m);
    `TheSolidarityBracketsAreSeeded` (seis en 2026; `[20,25]` 0.02); `SalaryRulesStillAnswer` (el `GET /hr/salary-rules`
    de la fase 1 sigue dando `smmlv` 1750905).
  - `PayrollLegalValuesTests` (Bootstrapper, con un `ILegalParameterSource` falso armado con las filas de la siembra):
    `ResolvesEveryKeyAtTheDate` (2026-08-01: `SundaySurchargeRate.Value == 0.90m` con `EffectiveFrom` 2026-07-01,
    `WeeklyHours.Value == 42m`); `TheFirstHalfOfJulyUsesThePreviousWeek` (2026-07-01 ⇒ 44); `ConnectivityHasItsStartDate`
    (`ConnectivityAllowanceSince == 2025-06-25`); `AMissingKeyIsLegalParameterMissing` (sin `SundaySurchargeRate` a
    2025-03-01 ⇒ `payroll.legal_parameter.missing` y el mensaje nombra `SundaySurchargeRate`); `SolidarityRateIsLowerInclusive`
    (4 ⇒ 0.01, 15.99 ⇒ 0.01, 16 ⇒ 0.012, 25 ⇒ 0.02, 3.99 ⇒ `null`).
  - `LegalBasisTests.EveryKeyHasALabelAndABasis` (recorre las propiedades `LegalValue` de `LegalValueSet` por reflexión).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'LegalValueSet' could not be found`.
- [ ] **Step 3: Implementar** y generar la migración
  (`dotnet ef migrations add AddPayrollLegalParameters --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`);
  agregar a mano los `InsertData`/`DeleteData` y revisar que el `AlterColumn` no pierda datos.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~LegalParametersTests" -- xUnit.MaxParallelThreads=4`,
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PayrollLegalValuesTests" -- xUnit.MaxParallelThreads=4`,
  `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~LegalBasisTests" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~HrLegalParametersApiTests|FullyQualifiedName~SalaryRulesApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(hr): payroll legal values with brackets and six-decimal rates`

> **Cierre del Bloque A:** `dotnet build Backend.slnx --no-restore` sin errores;
> `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PayrollLayerTests|FullyQualifiedName~HrLayerTests|FullyQualifiedName~CompositionRootTests" -- xUnit.MaxParallelThreads=4`;
> `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests -- xUnit.MaxParallelThreads=4`; la clase `HrLegalParametersApiTests`
> completa. Una revisión del bloque.

---

## Bloque B — Motor (Tareas 4–6)

Código puro: sólo unitarias, ningún contenedor. Todo vive en `src/Modules/Payroll/Modules.Payroll.Domain/Engine/` y se prueba
en `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/`. `EngineTestData.ForDate(DateOnly date) : LegalValueSet` arma
el conjunto legal vigente a **cualquier** fecha desde las filas de la siembra (pre-flight I-5; las pruebas usan 2026-06-01,
2026-07-01, 2026-07-16, 2026-08-01 y 2026-11-01), y `EngineTestData` da un `EngineInput` base (salario 1 750 905, empresa con los valores por defecto,
centro clase I sin alto riesgo, contrato `Employee`/`Monthly` desde 2025-01-01, sin capturas).

### Task 4: Entradas del motor, redondeo y reglas de devengo

**Files:**
- Create en `Engine/`: `PayrollRounding.cs`, `EngineInput.cs` (los registros de **Interfaces**), `MonthAccumulators.cs`,
  `PayrollLine.cs`, `LineTrace.cs`, `WorkedDaysRule.cs`, `SalaryRule.cs`, `IncomeRule.cs`, `RecurringConceptRule.cs`,
  `OvertimeFactors.cs`, `OvertimeRule.cs`, `MonthIncomeRule.cs`, `TransportAllowanceRule.cs`
- Create en Domain: los enums `PayrollHalf`, `TransportFrequency`, `TransportCapBase`, `OvertimeBase`, `ConceptKind`,
  `RecurringMode`, `PayrollLineGroup`, `SystemConceptKey`; `SystemConcepts.cs`, `QuincenaCalendar.cs` (`ArlClass` ya existe:
  lo creó la Tarea 3, I-1)
- Test: `Engine/EngineTestData.cs`, `PayrollRoundingTests.cs`, `QuincenaCalendarTests.cs`, `SystemConceptsTests.cs`,
  `WorkedDaysRuleTests.cs`, `SalaryRuleTests.cs`, `IncomeAndRecurringRuleTests.cs`, `OvertimeFactorsTests.cs`, `OvertimeRuleTests.cs`,
  `TransportAllowanceRuleTests.cs`

**Interfaces:**
- Consumes: `LegalValueSet` (Tarea 3).
- Produces:
  - `PayrollRounding`: `decimal ToPeso(decimal)` (`AwayFromZero`), `decimal CeilingToPeso(decimal)`,
    `decimal CeilingToHundred(decimal)` (`Math.Ceiling(x / 100) * 100`; para negativos no se usa: el acumulado del mes nunca lo
    es).
  - `QuincenaCalendar`: `static (DateOnly Start, DateOnly End, PayrollHalf Half) Containing(DateOnly day)`,
    `static (DateOnly Start, DateOnly End, PayrollHalf Half) After(DateOnly start)`.
  - `SystemConcepts`: `IReadOnlyList<SystemConcept> All` (la tabla del contrato, en su orden), `SystemConcept? Find(Guid id)`,
    `sealed record SystemConcept(Guid Id, SystemConceptKey Key, ConceptKind Kind, string Name, bool? DefaultSalaryConstitutive)`.
  - Entradas (`EngineInput.cs`):
    - `sealed record PeriodInfo(DateOnly Start, DateOnly End, PayrollHalf Half)`.
    - `sealed record EngineSettings(TransportFrequency TransportFrequency, TransportCapBase TransportCapBase, OvertimeBase OvertimeBase, bool Exonerated)`.
    - `sealed record SalarySegmentSource(DateOnly EffectiveDate, decimal MonthlyAmount, bool Teleworker)` (todos los registros
      del contrato, en orden).
    - `sealed record WorkplaceInfo(string Name, ArlClass RiskClass, bool HighRiskPension)`.
    - `sealed record OvertimeInput(Guid ConceptId, string ConceptName, decimal Hours, SystemConceptKey? SystemKey,
      decimal? CustomMultiplier)` — exactamente uno de los dos viene; el factor **no** viaja en la entrada: lo deriva el motor.
  - `OvertimeFactors` (`public static`, Engine): `(decimal Factor, IReadOnlyList<LegalValue> Used) For(OvertimeInput input,
    LegalValueSet legal)` con la tabla de R-2 (`ExtraSundayDay` = `1 + ExtraDayRate + SundaySurchargeRate`, `ExtraSundayNight`
    = `1 + ExtraNightRate + SundaySurchargeRate`, `NightSundaySurcharge` = `NightSurchargeRate + SundaySurchargeRate`); un
    concepto propio devuelve su `CustomMultiplier` y `Used` vacío.
    - `sealed record IncomeInput(Guid ConceptId, string ConceptName, decimal Amount, bool SalaryConstitutive)`;
      `sealed record DeductionInput(Guid ConceptId, string ConceptName, decimal Amount)`.
    - `sealed record RecurringInput(Guid RecurringId, string ConceptName, ConceptKind Kind, RecurringMode Mode, decimal Amount, bool SalaryConstitutive, decimal? RemainingCap)`.
    - `sealed record EngineInput(Guid EmployeeId, PeriodInfo Period, EngineSettings Settings, LegalValueSet Legal,
      DateOnly ContractStart, DateOnly? ContractEndedOn, IReadOnlyList<SalarySegmentSource> Salaries, WorkplaceInfo Workplace,
      IReadOnlyList<OvertimeInput> Overtime, IReadOnlyList<IncomeInput> Incomes, IReadOnlyList<DeductionInput> Deductions,
      IReadOnlyList<RecurringInput> Recurring, MonthAccumulators Prior)`.
  - `sealed record MonthAccumulators(int WorkedDays, decimal SalaryRaw, decimal Salary, decimal TransportRaw, decimal Transport,
    decimal Overtime, decimal SalaryIncome, decimal NonSalaryIncome, decimal HealthEmployee, decimal PensionEmployee,
    decimal SolidarityFund, decimal HealthEmployer, decimal PensionEmployer, decimal Arl, decimal CompensationFund, decimal Icbf,
    decimal Sena)` con `static MonthAccumulators Empty` (17 campos; sin IBC, M-7).
  - `sealed record TraceInput(string Name, object Value)`; `sealed record LineTrace(string Formula, IReadOnlyList<TraceInput>
    Inputs, IReadOnlyList<LegalValue> LegalValues, string? Note)`; `sealed record PayrollLine(string Code, PayrollLineGroup Group,
    string Label, decimal Amount, bool? SalaryConstitutive, LineTrace Trace)` (pre-flight I-2: `SalaryConstitutive` sólo en
    ingresos capturados y recurrentes, `null` en las demás; el resumen sale de las líneas).
  - Reglas (`public static`, puras), con los pasos 1–6 de «Motor: definiciones exactas»:
    - `WorkedDaysRule.Compute(PeriodInfo, DateOnly contractStart, DateOnly? endedOn) : WorkedDays` con
      `sealed record WorkedDays(DateOnly CoveredStart, DateOnly CoveredEnd, int Days, bool FinalSettlementPending)`.
    - `SalaryRule.Compute(EngineInput, WorkedDays) : SalaryOutcome` con `sealed record SalaryOutcome(PayrollLine Line,
      decimal Raw, decimal SalaryInForce, bool Teleworker, DateOnly SalaryRecordDate, IReadOnlyList<SalarySegment> Segments)` y
      `sealed record SalarySegment(DateOnly From, DateOnly To, int Days, decimal MonthlyAmount)`.
    - `IncomeRule.Compute(EngineInput) : IncomeOutcome` (líneas, `SalaryIncome`, `NonSalaryIncome`).
    - `RecurringConceptRule.Compute(EngineInput, WorkedDays) : RecurringOutcome` (líneas de ingreso y de deducción y sus sumas
      salarial / no salarial / deducción).
    - `OvertimeRule.Compute(EngineInput, decimal salaryInForce, decimal salaryIncomeMonth) : OvertimeOutcome` (líneas, total,
      `HourValue`; `salaryIncomeMonth` es `SI_m`, sólo se usa con `SalaryAndMonthSalaryIncome`).
    - `MonthIncomeRule.Compute(EngineInput, decimal salaryInForce, decimal salaryIncome, decimal overtime, decimal nonSalaryIncome) : MonthIncome`
      con `sealed record MonthIncome(decimal SalaryIncomeMonth, decimal OvertimeMonth, decimal NonSalaryIncomeMonth,
      decimal MonthSalaryIncome, decimal TransportCapIncome)`.
    - `TransportAllowanceRule.Compute(EngineInput, WorkedDays, SalaryOutcome, MonthIncome) : TransportOutcome` (línea, `Raw`).

- [ ] **Step 1: Pruebas que fallan.**
  - `PayrollRoundingTests`: `ToPesoRoundsHalfUp` (875 452.5 ⇒ 875 453); `CeilingToHundredNeverRoundsDown` (35 018.12 ⇒ 35 100;
    35 000 ⇒ 35 000; 35 000.01 ⇒ 35 100); `CeilingToPeso` (875 452.01 ⇒ 875 453).
  - `QuincenaCalendarTests`: `TheFifteenthIsInTheFirstHalf`; `TheSixteenthStartsTheSecondUntilTheLastDay` (2026-02-16 ⇒ fin
    2026-02-28; 2026-12-16 ⇒ 2026-12-31); `AfterTheSecondComesTheFirstOfNextMonth` (2026-12-16 ⇒ 2027-01-01).
  - `SystemConceptsTests`: `TheTwelveSystemConceptsHaveTheContractIds` (ids y `Key` de la tabla); `IdsAreUnique`.
  - `WorkedDaysRuleTests`: `AFullFirstHalfIsFifteen`; `TheSecondHalfOfAThirtyOneDayMonthIsFifteenDays` (2026-12-16..31);
    `TheSecondHalfOfFebruaryIsFifteen` (2026-02-16..28); `AHireMidPeriodCountsRealDays` (inicio 2026-11-10 ⇒ 6);
    `AHireOnTheThirtyFirstIsOneDay` (inicio 2026-12-31 ⇒ 1); `AnEndInsideThePeriodIsPendingFinalSettlement` (fin 2026-11-20 en
    la segunda ⇒ 5 y `FinalSettlementPending`); `AnEndOnTheLastDayIsStillFifteen` (fin 2026-11-30 ⇒ 15 y pendiente).
  - `SalaryRuleTests`: `HalfOfTheMinimumWage` (875 453); `TheMonthAddsUpToTheMonthlySalary` (segunda con `prior` de la primera
    ⇒ 875 452; suma 1 750 905); `ARaiseMidPeriodSplitsIntoSegments` (1 750 905 hasta 2026-11-09, 2 000 000 desde 2026-11-10 ⇒
    tramos de 9 y 6 días; `SalaryRaw` = 1 750 905 ÷ 30 × 9 + 2 000 000 ÷ 30 × 6; `SalaryInForce` 2 000 000);
    `TheLastSegmentAbsorbsTheDifference` (febrero, cambio el 20 ⇒ 4 y 11 días); `TheTraceListsTheSegments`.
  - `IncomeAndRecurringRuleTests`: `SalaryAndNonSalaryIncomesAreSeparated`; `AProportionalRecurringFollowsTheDays` (100 000
    proporcional con 6 días ⇒ 40 000); `ACapLimitsTheLastInstallment` (300 000 fijo, `RemainingCap` 120 000 ⇒ 120 000);
    `AnExhaustedRecurringHasNoLine` (`RemainingCap` 0 ⇒ sin línea).
  - `OvertimeFactorsTests`: teoría `SystemFactorsWithNinetyPercent` (legal 2026-08-01 ⇒ 1.25, 1.75, 0.35, 0.90, 2.15, 2.65,
    1.25); teoría `SystemFactorsWithEightyPercent` (legal 2026-06-01 ⇒ 1.25, 1.75, 0.35, 0.80, 2.05, 2.55, 1.15);
    `TheJulySecondHalfAlreadyUsesNinety` (legal 2026-07-16 ⇒ `ExtraSundayDay` 2.15); `CompositesListTheKeysTheyAdded`
    (`ExtraSundayNight` ⇒ `Used` con `ExtraNightRate` y `SundaySurchargeRate`); `ACustomConceptUsesItsMultiplier` (1.4,
    `Used` vacío).
  - `OvertimeRuleTests` (legal 2026-08-01): `TheHourValueIsNotRounded` (M-5: 1 750 905 ⇒ valor hora 8 337,642857…; 10 h
    `ExtraDay` ⇒ `round(104 220,54)` = 104 221, no 104 225); `HourValueUsesTheWeeklyHoursOfThePeriodStart` (2 100 000 ⇒ 10 000);
    `ExtraDayPaysOnePointTwentyFive` (10 h ⇒ 125 000); `TheSundaySurchargeIsOnlyTheSurcharge` (8 h ⇒ 72 000);
    `TheJulyFirstHalfStillUsesFortyFourHours` (legal 2026-07-01: `HourValue` = 2 100 000 ÷ 30 ÷ (44 ÷ 6));
    `TheMonthSalaryIncomeBaseAddsSalaryIncomes` (`SalaryAndMonthSalaryIncome`, comisión 300 000 ⇒ base 2 400 000);
    `ACustomConceptUsesItsMultiplier` (`CustomMultiplier` 1.4); `ExtraSundayDayFollowsTheRamp` (10 h con valor hora
    10 000 ⇒ 205 000 con legal 2026-06-01 y 215 000 con legal 2026-08-01).
  - `TransportAllowanceRuleTests`: `BiweeklyPaysTheDays` (124 548); `TheSecondHalfCompletesTheMonth` (124 547);
    `UpToTwiceTheMinimumKeepsIt` (`SalaryAndSalaryIncome`, 3 000 000 + 500 000 ⇒ 124 548);
    `OverTwiceTheMinimumLosesIt` (3 000 000 + 501 811 ⇒ 0 con nota); `ByDefaultOnlyTheSalaryCounts` (`SalaryOnly`, 3 000 000 +
    501 811 ⇒ 124 548); `MonthlyPaysNothingInTheFirstHalf`; `MonthlyPaysTheWholeMonthInTheSecond` (249 095 con 15 + 15 días);
    `ATeleworkerGetsConnectivityInstead` (salario de teletrabajador con `EffectiveDate` 2025-07-01 ⇒ código
    `connectivity_allowance`, mismo monto, sin línea de transporte); `ATeleworkerRecordBeforeTheLawGetsTransport`
    (`EffectiveDate` 2025-06-24 ⇒ `transport_allowance`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollRounding' could not be found`.
- [ ] **Step 3: Implementar** los tipos de **Interfaces** con las fórmulas 1–6. Cada regla arma el rastro de su línea
  (`formula` fija, `inputs`, `legalValues` leídos, `note`).
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): engine inputs, rounding and earning rules`

### Task 5: Seguridad social, provisiones y orquestador del motor

**Files:**
- Create en `Engine/`: `IbcRule.cs`, `EmployeeDeductionsRule.cs`, `SolidarityFundRule.cs`, `EmployerContributionsRule.cs`,
  `ProvisionsRule.cs`, `PayrollSummary.cs`, `EmployeeLiquidation.cs`, `PayrollEngine.cs`
- Test: `Engine/IbcRuleTests.cs`, `EmployeeDeductionsRuleTests.cs`, `SolidarityFundRuleTests.cs`,
  `EmployerContributionsRuleTests.cs`, `ProvisionsRuleTests.cs`, `PayrollEngineTests.cs`

**Interfaces:**
- Consumes: Tarea 4.
- Produces:
  - `IbcRule.Compute(EngineInput, WorkedDays, decimal salaryLine, MonthIncome) : IbcOutcome` (`IbcMonth`, `SalaryEarningsMonth`
    = `SalDev_m`, línea `ibc`).
  - `EmployeeDeductionsRule.Compute(EngineInput, IbcOutcome) : IReadOnlyList<PayrollLine>` (salud y pensión).
  - `SolidarityFundRule.Compute(EngineInput, decimal salaryInForce, IbcOutcome) : PayrollLine?`.
  - `EmployerContributionsRule.Compute(EngineInput, IbcOutcome, MonthIncome) : IReadOnlyList<PayrollLine>` (siempre las seis,
    R-20).
  - `ProvisionsRule.Compute(EngineInput, decimal salary, decimal transport, decimal overtime, decimal salaryIncome) :
    IReadOnlyList<PayrollLine>` (siempre las cuatro).
  - `sealed record PayrollSummary(decimal Salary, decimal TransportAllowance, decimal Overtime, decimal OtherSalaryIncome,
    decimal NonSalaryIncome, decimal TotalEarned, decimal HealthEmployee, decimal PensionEmployee, decimal SolidarityFund,
    decimal OtherDeductions, decimal TotalDeductions, decimal Net, decimal HealthEmployer, decimal PensionEmployer, decimal Arl,
    decimal CompensationFund, decimal Icbf, decimal Sena, decimal Severance, decimal SeveranceInterest, decimal Premium,
    decimal Vacation, decimal EmployerCost)` con `static PayrollSummary From(IReadOnlyList<PayrollLine>)` (por código y grupo)
    y `static PayrollSummary Sum(IEnumerable<PayrollSummary>)`: es la misma forma que `PayrollTotalsDto`.
  - `sealed record EmployeeLiquidation(Guid EmployeeId, int WorkedDays, bool FinalSettlementPending, bool NegativeNet,
    IReadOnlyList<PayrollLine> Lines, PayrollSummary Summary, MonthAccumulators Month, string EngineVersion)`.
  - `PayrollEngine`: `public const string Version = "2026.11.1"`;
    `static EmployeeLiquidation Liquidate(EngineInput input)` — llama las reglas en el orden de «Motor: definiciones exactas»
    y ordena las líneas en el orden fijo de I-11 (`Earning`, `Info`, `Deduction`, `EmployerContribution`, `Provision`; dentro
    de cada grupo, el de la tabla de códigos del contrato).
  - `SolidarityFundRule` consulta el tramo de la primera quincena con `min(salaryInForce, L.IbcCapSmmlv × L.Smmlv) ÷ L.Smmlv`
    (B-1), así `SolidarityRate` de la Tarea 3 no cambia.

- [ ] **Step 1: Pruebas que fallan.**
  - `IbcRuleTests`: `WithoutNonSalaryTheIbcIsTheSalaryEarnings`; `NonSalaryOverFortyPercentAddsTheExcess` (ancla
    `non-salary-over-forty-percent` ⇒ 1 200 000); `ExactlyFortyPercentAddsNothing` (salario 1 200 000 de quincena y no salarial
    800 000 ⇒ IBC 1 200 000); `TheFloorLiftsAnIbcBelowTheMinimumByDays` (M-6; entrada armada a mano con salario 1 200 000, que
    la elegibilidad excluiría: el piso es defensivo; primera completa ⇒ salary 600 000, IBC 875 453 = techo de
    1 750 905 ÷ 30 × 15);
    `TheCapIsTwentyFiveMinimums` (segunda de 50 000 000 ⇒ 43 772 625); `TheIbcRoundsUpToThePeso`;
    `TransportIsOutsideTheIbc`.
  - `EmployeeDeductionsRuleTests`: `TheFirstHalfWithholdsOnItsOwnIbc` (35 100); `TheSecondHalfSettlesTheMonth` (35 000);
    `TheSettlementRoundsTheMonthNotTheHalf` (IBC del mes 1 750 905 ⇒ 70 100 en el mes).
  - `SolidarityFundRuleTests`: `BelowFourMinimumsThereIsNone`; `TheFirstHalfDecidesBySalaryAlone` (salario 7 500 000 ⇒ 1 %);
    `TheSecondHalfRefundsWhenTheMonthFallsBelow` (ancla `solidarity-fund-refund` ⇒ −37 500 con nota);
    `TheFirstHalfOfATopEarnerUsesTheTopBracket` (B-1: salario 50 000 000 = 28,56 SMMLV ⇒ tramo de 25 ⇒ 2 % ⇒ 500 000);
    `TheTopBracketAppliesToTheCappedIbc` (ancla `ibc-cap-and-top-bracket` ⇒ 375 500); `ExactlyFourMinimumsPaysOnePercent`
    (R-9).
  - `EmployerContributionsRuleTests`: `AnExoneratedCompanyBelowTenMinimumsPaysNoHealthIcbfOrSena` (las tres en 0 con nota
    «Exonerado (ET art. 114-1)»; caja 35 100); `TenMinimumsOrMoreAreNotExonerated` (salario 17 509 050 ⇒ salud empleador > 0);
    `ANonExoneratedCompanyAlwaysPays`; `HighRiskPensionIsTheTotalMinusTheEmployeeShare` (tasa 0.22 en el rastro);
    `ArlFollowsTheWorkplaceClass` (clase V ⇒ 0.0696); `ArlAndParafiscalsIgnoreTheFortyPercentRule` (I-7, ancla de 40 %: ARL y
    caja sobre 1 000 000, no sobre el IBC de 1 200 000 ⇒ arl 5 300, caja 40 000; salud empleador exonerada); `TheSecondHalfSettlesEachContribution`.
  - `ProvisionsRuleTests`: `SeveranceAndPremiumIncludeTransport` (83 300); `InterestIsOnePercentOfTheSeveranceBase` (10 000);
    `VacationExcludesTransportAndOvertime` (36 506 con y sin horas extras).
  - `PayrollEngineTests`: `TheMinimumWageFirstHalfMatchesTheAnchor` (todas las líneas del ancla `minimum-wage-first-half`,
    `Net` 929 801, `EmployerCost` 1 357 907); `ANegativeNetIsFlagged` (deducción 2 000 000 ⇒ `NegativeNet`);
    `LinesComeInContractOrder`; `TheSameInputGivesTheSameOutput` (dos corridas ⇒ JSON idéntico);
    `TheMonthAccumulatorsFeedTheSecondHalf` (la salida `Month` de la primera como `Prior` reproduce el ancla de la segunda);
    `FiveHundredEmployeesUnderTenSeconds` (500 entradas variadas, `Stopwatch` < 10 s; spec, «Cómo se prueba»).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollEngine' could not be found`.
- [ ] **Step 3: Implementar** con las fórmulas 7–13.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): social security, provisions and the pure liquidation engine`

### Task 6: Casos de oro sintéticos

Un archivo JSON versionado por caso, con la entrada completa y el resultado esperado línea por línea (spec, «Cómo se
prueba»). Los esperados se calculan **a mano** y cada archivo lleva en `notes` la aritmética de cada línea: un esperado
copiado de la salida del motor no prueba nada (la revisión del bloque lo verifica muestreando tres archivos).

**Files:**
- Create: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/GoldenCase.cs` (modelo), `GoldenCaseLoader.cs`,
  `GoldenCaseTests.cs`, `Golden/legal/2026-06-01.json` (I-5), `Golden/legal/2026-07-01.json`, `Golden/legal/2026-08-01.json`,
  `Golden/legal/2026-11-01.json`, `Golden/synthetic/*.json`

**Interfaces:**
- Consumes: `PayrollEngine.Liquidate`, `EngineInput`, `LegalValueSet` (Tareas 3–5).
- Produces: el formato de caso que también usa la Tarea 15.

  ```jsonc
  {
    "name": "minimum-wage-first-half",
    "source": "synthetic",            // "synthetic" | "siigo"
    "description": "…",
    "legal": "legal/2026-11-01.json", // ruta relativa a Golden/; el archivo es un LegalValueSet serializado
    "input": { /* EngineInput serializado, sin Legal */ },
    "expected": {
      "workedDays": 15, "finalSettlementPending": false, "negativeNet": false,
      "lines": [{ "code": "salary", "amount": 875453 }, …],   // TODAS las líneas, en orden
      "net": 929801, "employerCost": 1357907
    },
    "notes": ["salary = round(1750905 ÷ 30 × 15) = round(875452.5) = 875453", …]
  }
  ```
  `GoldenCaseTests.EveryCaseMatches` es una teoría sobre `GoldenCaseLoader.All()` (todos los `*.json` bajo `Golden/synthetic`
  y `Golden/siigo`, por nombre): compara `workedDays`, banderas, la lista de `(code, amount)` completa y en el orden fijo de
  I-11, `net` y
  `employerCost`; un fallo imprime las diferencias línea por línea. `GoldenCaseTests.EveryRuleHasACase` falla si alguna de las
  reglas de **Files** de las Tareas 4–5 no aparece en el campo `"covers": ["SalaryRule", …]` de algún caso.

- [ ] **Step 1: Casos (la prueba es el archivo).** Las diez anclas de «Motor: definiciones exactas» en **12 archivos** (M-3:
  un archivo es una quincena, así que `solidarity-fund-refund` e `ibc-cap-and-top-bracket` van en dos cada una, `-first` y
  `-second`, con el `prior` de la primera escrito a mano en la segunda; incluidas `sunday-overtime-before-july-2026` y
  `sunday-overtime-after-july-2026`, que prueban la derivación de R-2 a cada lado de 2026-07-01) más 14: `hire-mid-first-half` (inicio 2026-11-10), `raise-mid-second-half-february` (R-7),
  `overtime-all-seven-types` (2026-08, las siete horas, factores derivados con 0,90), `overtime-month-income-base`, `exonerated-off` (`exonerated: false`), `ten-minimums-not-exonerated`,
  `high-risk-pension`, `arl-class-v`, `teleworker-connectivity`, `monthly-transport-first-half`, `monthly-transport-second-half`,
  `recurring-with-cap`, `negative-net`, `custom-overtime-multiplier`. Cada uno con su `covers`.
- [ ] **Step 2: RED.** Antes de `GoldenCaseLoader` la prueba no compila. Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'GoldenCaseLoader' could not be found`.
- [ ] **Step 3: Implementar el cargador y la comparación.** Si un caso falla con el cargador ya en verde, se revisa primero la
  aritmética de `notes`; si el caso está bien, se corrige la regla (y su prueba de la Tarea 4–5), nunca el esperado.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~GoldenCaseTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` con 26 casos.
- [ ] **Step 5: Commit** — `test(payroll): synthetic golden cases for every engine rule`

> **Cierre del Bloque B:** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` completo.
> Una revisión del bloque, con el encargo explícito de rehacer a mano tres casos de oro elegidos por el revisor.

---

## Bloque C — Hr, configuración y período (Tareas 7–9)

### Task 7: Puerto hacia Hr y validador previo

**Files:**
- Modify Hr: `Modules.Hr.Application/IEmployeeRepository.cs` y `Modules.Hr.Infrastructure/Persistence/EmployeeRepository.cs`
  (`ListForPayrollAsync`)
- Create Payroll Application: `IPayrollHrDirectory.cs` (con sus registros), `IPayrollMemberDirectory.cs`, `PayrollEligibility.cs`;
  Domain: `ExclusionReason.cs`, `BlockerReason.cs`
- Create: `src/Bootstrapper/PayrollHrDirectory.cs`, `src/Bootstrapper/PayrollMemberDirectory.cs` (calco de
  `HrMemberDirectory.cs`); registrarlos junto a `PayrollLegalValues`
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrPersistenceTests.cs` (sumar),
  `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollEligibilityTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollApiSeeds.cs` (helpers para sembrar empresa, sede, centro,
  cargo y empleados **por la API de Hr**, calco de `HrApiSeeds`), `PayrollHrDirectoryTests.cs`

**Interfaces:**
- Produces:
  - Hr: `Task<IReadOnlyList<Employee>> ListForPayrollAsync(Guid tenantId, Guid companyId, DateOnly from, DateOnly to,
    CancellationToken)` — `AsNoTracking`, con contratos, salarios y cargos; empleados con **algún** contrato de `companyId` con
    `start_date ≤ to` y (`ended_on` nulo o `ended_on ≥ from`); orden `last_name`, `first_name`, `id`.
  - Payroll (`IPayrollHrDirectory.cs`):

    ```csharp
    public sealed record PayrollCompanyRef(Guid Id, string Name, string TaxId, bool IsActive);
    public sealed record HrSalarySource(DateOnly EffectiveDate, decimal Amount, bool Teleworker);
    public sealed record HrAssignmentSource(DateOnly EffectiveDate, string PositionName, string SiteName, string WorkplaceName,
        ArlClass? RiskClass, bool HighRiskPension, string? CompensationFund);
    public sealed record HrContractSource(Guid ContractId, string ContractType, string SalaryType, DateOnly StartDate,
        DateOnly? EndedOn, IReadOnlyList<HrSalarySource> Salaries, IReadOnlyList<HrAssignmentSource> Assignments);
    public sealed record HrEmployeeSource(Guid EmployeeId, string FirstName, string LastName, string DocumentType,
        string DocumentNumber, string ContributorSubtype, bool ForeignPensionExempt, IReadOnlyList<HrContractSource> Contracts);

    public interface IPayrollHrDirectory
    {
        Task<IReadOnlyList<PayrollCompanyRef>> ListActiveCompaniesAsync(Guid tenantId, CancellationToken cancellationToken);
        Task<PayrollCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
        // Only the contracts of companyId that cover a day of [from, to]; employees without one are left out.
        Task<IReadOnlyList<HrEmployeeSource>> ListEmployeesAsync(Guid tenantId, Guid companyId, DateOnly from, DateOnly to,
            CancellationToken cancellationToken);
    }
    ```
    `ContractType`, `SalaryType`, `ContributorSubtype` y `DocumentType` llevan el **nombre** del enum de Hr (`"Employee"`,
    `"Monthly"`, `"Standard"`, `"CC"`): Payroll no conoce los enums de Hr. `RiskClass` es `ArlClass` de Payroll (mismos
    nombres `I`…`V`; el adaptador hace `Enum.Parse`). `CompensationFund` es la de la sede (O-6).
  - `IPayrollMemberDirectory.FindDisplayNameAsync(Guid tenantId, Guid subjectId, CancellationToken) : Task<string?>`.
  - `PayrollEligibility` (`public static`, pura):
    `EligibilityResult Evaluate(HrEmployeeSource employee, DateOnly periodStart, DateOnly periodEnd, decimal smmlv)` con
    `sealed record EligibilityResult(EligibilityStatus Status, ExclusionReason? Exclusion, IReadOnlyList<BlockerReason>
    Blockers, HrContractSource? Contract, HrSalarySource? SalaryAtEnd, HrAssignmentSource? AssignmentAtEnd, DateOnly?
    LastCoveredDay)` y `enum EligibilityStatus { Liquidable, Excluded, Blocked }`. Orden (gana la primera); **todas las
    exclusiones van antes que los bloqueos: un excluido nunca bloquea** (pre-flight I-3, spec «A quién se liquida»):
    1. más de un contrato ⇒ `MultipleContractsInPeriod` (R-10);
    2. `ContractType != "Employee"` ⇒ `ContractTypeNotSupported`; `SalaryType != "Monthly"` ⇒ `SalaryTypeNotSupported`;
       `ContributorSubtype != "Standard"` ⇒ `ContributorSubtypeNotSupported`; `ForeignPensionExempt` ⇒ `ForeignPensionExempt`;
    3. algún salario que rija dentro de lo cubierto con monto `< smmlv` ⇒ `SalaryBelowMinimum`;
    4. bloqueos (se juntan todos): sin salario que cubra el último día cubierto ⇒ `MissingSalary`; sin cargo y lugar que lo
       cubra ⇒ `MissingWorkplace`; centro sin `RiskClass` ⇒ `MissingRiskClass` (defensivo: Hr exige la clase al crear el
       centro, I-12); con alguno ⇒ `Blocked`;
    5. si no, `Liquidable`.

- [ ] **Step 1: Pruebas que fallan.**
  - `HrPersistenceTests.ListForPayrollReturnsOnlyTheCompanyContractsThatCoverTheRange` (cuatro empleados: activo en la
    empresa; retirado el 2026-10-31; contratado desde 2026-11-20; activo en otra empresa ⇒ para 2026-11-01..15 sólo el
    primero; para 2026-11-16..30, el primero y el tercero).
  - `PayrollEligibilityTests`: teoría `UnsupportedEmployeesAreExcludedWithTheirReason` (`Contractor`, `Integral`,
    `HalfTime`, `Pensioner`, exento ⇒ su motivo); `ASalaryBelowTheMinimumIsExcluded` (1 500 000 con SMMLV 1 750 905);
    `TwoContractsInThePeriodAreExcluded` (terminado el 2026-11-05 y reincorporado el 2026-11-10);
    `ATypeExclusionWinsOverBlockers`; `ABelowMinimumExclusionWinsOverBlockers` (I-3: salario 1 500 000 y sin cargos ⇒
    `Excluded`, sin bloqueos); `AMissingWorkplaceBlocks` (sin cargos);
    `AWorkplaceWithoutRiskClassBlocks` (I-12: sigue siendo unitaria, con un `HrAssignmentSource` armado a mano con
    `RiskClass: null`; por la API de Hr no se puede producir); `BlockersAccumulate`; `AStandardMonthlyEmployeeIsLiquidable` (y trae
    `SalaryAtEnd`, `AssignmentAtEnd`); `TheLastCoveredDayIsTheEndDateWhenTheContractEndsInside`.
  - `PayrollHrDirectoryTests` (integración; resuelve `IPayrollHrDirectory` de un scope del factory):
    `MapsAnEmployeeHiredThroughHr` (alta por `POST /hr/employees` ⇒ nombres, `"CC"`, `"Employee"`, `"Monthly"`,
    `"Standard"`, un salario, un cargo con `WorkplaceName`, `SiteName`, `RiskClass` y `HighRiskPension` del centro);
    `OnlyTheRequestedCompanyContractsCome` (dos empresas); `AnotherTenantSeesNothing` (mismo `companyId` con otro `tenantId`
    ⇒ lista vacía); `ListsOnlyActiveCompanies`.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollEligibility' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~PayrollEligibilityTests" -- xUnit.MaxParallelThreads=4`,
  `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~HrPersistenceTests.ListForPayroll" -- xUnit.MaxParallelThreads=4`
  y `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollHrDirectoryTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): read employees and contracts from hr through a port`

### Task 8: Persistencia, configuración por empresa y conceptos

**Files:**
- Create Domain: los enums `PayrollPeriodStatus`, `PeriodAction` y `RecurringStatus` (pre-flight I-4: nacen acá porque los
  necesita `PayrollLabelsTests`; las Tareas 9 y 10 los reusan), `PayrollText.cs` (`Fold`, igual que `HrText.Fold`),
  `CompanyPayrollSettings.cs`, `PayrollConcept.cs`,
  `PayrollConceptId.cs`
- Create Application: `ICompanyPayrollSettingsRepository.cs`, `IPayrollConceptRepository.cs`, `ConceptCatalog.cs`,
  `PayrollLabels.cs`, `PayrollDtos.cs` (los DTO de compañía, configuración y conceptos), `PayrollRequests.cs`,
  `PayrollRequestValidators.cs`, `ListPayrollCompanies.cs`, `GetPayrollSettings.cs`, `UpdatePayrollSettings.cs`,
  `SaveConcept.cs` (crear y editar), `ArchiveConcept.cs`
- Create Infrastructure: `Persistence/CompanyPayrollSettingsRepository.cs`, `Persistence/PayrollConceptRepository.cs`,
  `Persistence/Migrations/<timestamp>_InitialPayroll.cs` (+ Designer, snapshot); modificar `PayrollDbContext.cs`,
  `PayrollUnitOfWork.cs`, `PayrollInfrastructureExtensions.cs`
- Modify: `PayrollEndpoints.cs`; `QepServiceCollectionExtensions.cs` (handlers a mano, como el bloque de Hr en `:492`)
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/ConceptCatalogTests.cs`, `PayrollLabelsTests.cs`,
  `PayrollRequestValidatorsTests.cs`; `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollSettingsApiTests.cs`

**Interfaces:**
- Consumes: `IPayrollHrDirectory` (Tarea 7), `SystemConcepts` (Tarea 4).
- Produces:
  - `CompanyPayrollSettings` (agregado): `Id`, `TenantId`, `CompanyId`, `TransportFrequency`, `TransportCapBase`,
    `OvertimeBase`, `Exonerated`, `DualControl`, `Version`, `CreatedAt`, `UpdatedAt`; `static Create(...)`,
    `Update(...)` (sube versión); `static EngineSettings DefaultsFor()` ⇒ `Biweekly`, `SalaryOnly`, `SalaryOnly`, exonerada, sin
    doble control (spec, «Configuración de nómina por empresa»); `EngineSettings ToEngine()`.
  - `PayrollConcept` (agregado): `Id`, `TenantId`, `CompanyId`, `Kind`, `Name`, `NormalizedName`,
    `DefaultSalaryConstitutive`, `Multiplier`, `ArchivedAt`, `Version`; `Create`, `Update(name, defaultSalaryConstitutive,
    multiplier)`, `Archive`/`Unarchive` (`payroll.concept.already_archived` / `not_archived`).
  - Tablas: `payroll.company_settings` (único `(tenant_id, company_id)`, `IX_company_settings_tenant_company`; FK
    `FK_company_settings_companies_company_id` a mano, R-19) y `payroll.concepts` (único
    `IX_concepts_tenant_company_kind_normalized_name`; `multiplier numeric(18,6)`); enums como texto con `CHECK`, `version`
    token de concurrencia.
  - `PayrollUnitOfWork`: `DbUpdateConcurrencyException` ⇒ 412; `IX_concepts_tenant_company_kind_normalized_name` ⇒
    `ResourceConflictException(PayrollErrorCodes.ConceptNameTaken, "Ya existe un concepto con ese nombre.")`;
    `IX_company_settings_tenant_company` ⇒ `RequestConcurrencyException` (dos primeras escrituras a la vez, R-17).
  - `ConceptCatalog` (scoped): `Task<ConceptCatalogSnapshot> LoadAsync(Guid tenantId, Guid companyId, CancellationToken)` con
    `ConceptCatalogSnapshot.Find(Guid conceptId) : ResolvedConcept?` (del sistema o propio de esa empresa) y
    `IReadOnlyList<PayrollConceptDto> ToDtos()`; `sealed record ResolvedConcept(Guid Id, ConceptKind Kind, string Name, bool
    System, SystemConceptKey? SystemKey, bool? DefaultSalaryConstitutive, decimal? Multiplier, bool Archived)`. Un nombre propio
    igual (con `Fold`) a uno del sistema del mismo kind ⇒ 409 `payroll.concept.name_taken` (lo decide el handler antes de
    guardar).
  - `PayrollLabels` (`public static`): un diccionario por enum con las etiquetas exactas de la tabla del contrato,
    `TryParse<TEnum>` con `PayrollText.Fold`.
  - Casos de uso y rutas: `GET /payroll/companies`, `GET|PUT {c}/settings`, `POST|PUT {c}/concepts…`, `…/archive`,
    `…/unarchive`, con los permisos, cuerpos y errores del contrato: los dos `GET` con `RequireAuthorization(PayrollPolicies.SettingsView)`
    y `PayrollAuthorization.EnsureAnyAsync(…, PeriodRead, SettingsManage)`; las escrituras con
    `RequireAuthorization(PayrollPermissions.SettingsManage)` y `EnsureAsync(…, SettingsManage)` (sin `PeriodRead`). Auditoría: `payroll.settings.updated`,
    `payroll.concept.created|updated|archived|unarchived`.
  - `GET /payroll/companies`: activas del puerto más las inactivas que tienen configuración guardada o algún período
    (`FindCompanyAsync` por cada `company_id` distinto de `payroll.company_settings`; desde la Tarea 9 también de
    `payroll.periods`).

- [ ] **Step 1: Pruebas que fallan.**
  - `ConceptCatalogTests`: `SystemConceptsAreAlwaysThere`; `ACustomConceptOfAnotherCompanyIsNotFound`;
    `TheSystemConceptsComeFirstInTheContractOrder`.
  - `PayrollLabelsTests.EveryEnumMemberHasItsContractLabel` (teoría por enum; comparar con la tabla del contrato literal).
  - `PayrollRequestValidatorsTests`: `AnOvertimeConceptNeedsAMultiplier` (`errors.multiplier`);
    `AMultiplierOnlyForOvertime`; `ADefaultClassificationOnlyForIncome`; `ANameIsOneToEightyCharacters`.
  - `PayrollSettingsApiTests`: `UnsavedSettingsAreTheDefaultsAtVersionZero` (`version == 0`, `Biweekly`, `SalaryOnly`,
    `SalaryOnly`, `exonerated == true`, `dualControl == false`, doce conceptos del sistema con sus ids);
    `TheFirstSaveUsesIfMatchZero` (PUT con `"0"` ⇒ 200 y `version == 1`; otro PUT con `"0"` ⇒ 412);
    `WithoutIfMatchIs428`; `ACustomOvertimeConceptIsListedAfterTheSystemOnes`;
    `ACustomNameEqualToASystemOneIsTaken` (`"hora extra diurna "` ⇒ 409 `payroll.concept.name_taken`);
    `SystemConceptsCannotBeEditedOrArchived` (422 `payroll.concept.system_readonly`); `ArchivingTwiceIs409`;
    `ArchivingNeedsNoIfMatch` (I-8: archivar y desarchivar sin encabezado ⇒ 200; editar sin `If-Match` ⇒ 428);
    `AnInactiveCompanyWithSavedSettingsIsListed` (M-1: guardar configuración y desactivar la empresa ⇒ sigue en
    `GET /payroll/companies` con `isActive: false`);
    `ChangingTheKindIs422`; `AnUnknownCompanyIs404`; `CompaniesListsActiveOnesByName`; `WritesAreAudited`
    (decisión del owner 2026-11-21, se escriben **primero**, antes que las demás de la clase):
    `ASettingsOnlyRoleReadsCompaniesAndSettings` (sólo `payroll.settings.manage` ⇒ 200 en `GET /payroll/companies` y en
    `GET {c}/settings`, con el catálogo de conceptos); `ASettingsOnlyRoleWritesSettingsAndConcepts` (sólo
    `payroll.settings.manage` ⇒ 200 en `PUT {c}/settings` y 201 en `POST {c}/concepts`);
    `AReadOnlyRoleReadsSettingsButCannotWriteThem` (sólo `payroll.period.read` ⇒ 200 en los dos `GET`, 403
    `authorization.denied` en `PUT {c}/settings`, `POST {c}/concepts` y `…/archive`);
    `WithoutEitherPermissionTheSettingsAre403` (sólo `payroll.period.manage` ⇒ 403 en los dos `GET`);
    (`payroll.settings.updated` y `payroll.concept.created` en `audit.entries`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'ConceptCatalog' could not be found`.
- [ ] **Step 3: Implementar** y generar la migración
  (`dotnet ef migrations add InitialPayroll --project src/Modules/Payroll/Modules.Payroll.Infrastructure --context PayrollDbContext -o Persistence/Migrations`);
  agregar a mano la FK a `companies.companies` al final de `Up` y quitarla al principio de `Down` (calco de
  `20261007110447_InitialPos.cs:220-232`).
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollSettingsApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): company payroll settings and concepts`

### Task 9: Período quincenal, estados y detalle sin cálculo

**Files:**
- Create Domain (`PayrollPeriodStatus` y `PeriodAction` ya existen desde la Tarea 8, I-4): `PayrollPeriodId.cs`,
  `PeriodEventId.cs`, `PayrollActor.cs`,
  `PeriodInput.cs`, `PeriodEvent.cs`, `PayrollPeriod.cs`
- Create Application: `IPayrollPeriodRepository.cs`, `IPayrollResultStore.cs` (+ `EmployeeResult`), `PeriodComposer.cs`,
  `PeriodActors.cs` (resuelve `PayrollActor` con `IPayrollMemberDirectory`; «Sin nombre» si no hay),
  `EnsureCurrentPeriods.cs`, `ListPeriods.cs`, `GetPeriod.cs`; en `PayrollDtos.cs`, los DTO del período (M-14):
  `ActorDto`, `PayrollTotalsDto`, `PayrollEmployeeSummaryDto`, `PayrollPeriodCanDto`, `PayrollPeriodDto`,
  `PayrollEmployeeRowDto`, `ExcludedEmployeeDto`, `BlockedEmployeeDto`, `PeriodEventDto`, `PayrollPeriodDetailDto`,
  `PayrollPeriodListDto`
- Create Infrastructure: `Persistence/PayrollPeriodRepository.cs`, `Persistence/PayrollResultStore.cs`,
  `Persistence/Migrations/<timestamp>_AddPayrollPeriods.cs` (+ Designer, snapshot); modificar `PayrollDbContext.cs`,
  `PayrollUnitOfWork.cs`
- Modify: `PayrollEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `ListPayrollCompanies.cs` (suma las empresas con períodos)
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollPeriodTests.cs`;
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollPeriodApiTests.cs`, `PayrollPersistenceTests.cs`

**Interfaces:**
- Consumes: Tareas 4, 7, 8.
- Produces:
  - `sealed record PayrollActor(Guid SubjectId, string DisplayName)`.
  - `PeriodInput` (entidad hija): `Id`, `PeriodId`, `EmployeeId`, `ConceptId`, `Kind`, `Hours?`, `Amount?`,
    `SalaryConstitutive?`, `Position` (orden dentro de su lista).
  - `PeriodEvent` (entidad hija): `Id`, `PeriodId`, `OccurredAt`, `ActorSubjectId`, `ActorName`, `Action`, `FromStatus?`,
    `ToStatus?`, `Reason?`, `AbsencesConfirmed?`.
  - `PayrollPeriod` (agregado): `Id`, `TenantId`, `CompanyId`, `StartDate`, `EndDate`, `Half`, `Status`, `Outdated`,
    `Version`, `LegalValuesJson?`, `LegalValuesDate?`, `EngineVersion?`, `Fingerprint?`, `ExcludedJson?`,
    `LastLiquidatedBy?`/`LastLiquidatedByName?`/`LastLiquidatedAt?`, `ApprovedBy?`…, `ClosedBy?`…, `CreatedAt`, `UpdatedAt`,
    `Inputs`, `Events`. Toda mutación sube `Version` (`Touch`). Métodos:
    - `static PayrollPeriod Open(PayrollPeriodId, Guid tenantId, Guid companyId, (DateOnly Start, DateOnly End, PayrollHalf Half) quincena, PayrollActor, DateTimeOffset now)` ⇒ `Draft`, `Version = 1`, evento `Created`.
    - `void ReplaceInputs(Guid employeeId, IReadOnlyList<PeriodInputDraft> lines, PayrollActor, DateTimeOffset)` y
      `void ReplaceInputsBulk(IReadOnlyDictionary<Guid, IReadOnlyList<PeriodInputDraft>>, PayrollActor, DateTimeOffset)`
      (evento `InputsImported`); en `Approved`/`Closed` ⇒ `payroll.period.not_editable`; en `Liquidated` ⇒ `MarkOutdated`.
      `sealed record PeriodInputDraft(Guid ConceptId, ConceptKind Kind, decimal? Hours, decimal? Amount, bool? SalaryConstitutive)`.
    - `void MarkOutdated(PayrollActor, DateTimeOffset)` — sólo si no está `Draft` y no estaba ya desactualizado; evento
      `MarkedOutdated`.
    - `void RecordLiquidation(PayrollActor, bool absencesConfirmed, string legalValuesJson, DateOnly legalValuesDate,
      string engineVersion, string fingerprint, string excludedJson, DateTimeOffset)` — desde `Draft`, `Reopened` o
      `Liquidated` (si no, `invalid_transition`); `!absencesConfirmed` ⇒ `payroll.liquidation.absences_unconfirmed`; fija
      `LegalValuesJson`/`LegalValuesDate` **sólo si eran nulos**; `Outdated = false`; evento `Liquidated` con
      `AbsencesConfirmed = true`.
    - `IReadOnlyList<string> ApproveBlockers(ApprovalContext context)` con `sealed record ApprovalContext(PayrollActor Caller,
      bool DualControl, bool SourceChanged, bool AnyNegativeNet, bool FirstHalfApproved)` — los códigos de
      `approveBlockedBy`, en ese orden: `status`, `outdated`, `source_changed`, `negative_net`, `first_half_not_approved`,
      `dual_control`.
    - `void Approve(ApprovalContext, DateTimeOffset)` — el primer bloqueo se lanza con su código
      (`status` ⇒ `payroll.period.invalid_transition`; `outdated` y `source_changed` ⇒ `payroll.period.outdated`;
      `negative_net` ⇒ `payroll.period.negative_net`; `first_half_not_approved` ⇒ `payroll.period.first_half_not_approved`;
      `dual_control` ⇒ `payroll.period.same_person_cannot_approve`).
    - `void Close(PayrollActor, DateTimeOffset)` — sólo `Approved` y no desactualizado.
    - `void Reopen(PayrollActor, string reason, DateTimeOffset)` — sólo `Approved` o `Closed`; evento con `Reason`.
  - Tablas (`AddPayrollPeriods`): `payroll.periods` (único `IX_periods_tenant_company_start`, FK
    `FK_periods_companies_company_id` a mano, R-19; `legal_values jsonb`, `excluded jsonb`), `payroll.period_inputs` (FK
    `period_id` cascade; `hours numeric(6,2)`, `amount numeric(18,2)`), `payroll.period_events` (FK cascade),
    `payroll.employee_results` (`period_id` FK cascade, único `(period_id, employee_id)`; columnas `employee_id`,
    `document_type`, `document_number`, `full_name`, `position`, `workplace`, `worked_days`, `negative_net`,
    `final_settlement_pending`, `engine_version`, `fingerprint`, `lines jsonb`, `summary jsonb`, `month jsonb`,
    `recurring_applied jsonb`). `IX_periods_tenant_company_start` ⇒ `PayrollUnitOfWork` lo traduce a
    `ResourceConflictException(PayrollErrorCodes.PeriodAlreadyExists, …)` (código interno, no sale por HTTP: lo atrapa
    `EnsureCurrentPeriods`).
  - `IPayrollPeriodRepository`: `FindAsync(Guid tenantId, PayrollPeriodId, CancellationToken)` (con capturas y eventos,
    rastreado); `ListAsync(Guid tenantId, Guid companyId, CancellationToken)` (sin hijos, `start_date` desc);
    `FindByStartAsync(Guid tenantId, Guid companyId, DateOnly start, CancellationToken)`; `Add`;
    `ListCompanyIdsAsync(Guid tenantId, CancellationToken)`.
  - `IPayrollResultStore`: `ListAsync(Guid tenantId, PayrollPeriodId, CancellationToken)`,
    `FindAsync(Guid tenantId, PayrollPeriodId, Guid employeeId, CancellationToken)`,
    `ReplaceAll(Guid tenantId, PayrollPeriodId, IReadOnlyList<EmployeeResult>)` (borra y escribe en la misma unidad de
    trabajo), `SumRecurringAppliedAsync(Guid tenantId, Guid companyId, Guid employeeId, DateOnly? before, CancellationToken)
    : Task<IReadOnlyDictionary<Guid, decimal>>` (por `recurringId`, sobre los resultados de los períodos no `Draft` de la
    empresa con `start_date < before`, o todos si `before` es nulo); `sealed record EmployeeResult(Guid EmployeeId, string
    DocumentType, string DocumentNumber, string FullName, string? Position, string? Workplace, EmployeeLiquidation
    Liquidation, string Fingerprint, IReadOnlyDictionary<Guid, decimal> RecurringApplied)`. Se llenan en la Tarea 11.
  - `EnsureCurrentPeriodsCommand(Guid TenantId, Guid CompanyId) : ICommand<PayrollPeriodListDto>` — R-13 con
    `QuincenaCalendar` y el día del tenant; atrapa la violación del índice único, relee y responde la lista (idempotente).
    Autoriza `PeriodRead` **y** `PeriodManage` (I-6). Auditoría `payroll.period.created` por cada período nuevo.
  - `ListPeriodsQuery`, `GetPeriodQuery` y `PeriodComposer.ComposeAsync(PayrollPeriod, PayrollActor caller,
    IReadOnlySet<string> callerPermissions, CancellationToken) : Task<PayrollPeriodDetailDto>`: en `Draft`, `Liquidated` y
    `Reopened` arma `employees`, `excluded` y `blocked` con `IPayrollHrDirectory.ListEmployeesAsync` +
    `PayrollEligibility` (SMMLV de `IPayrollLegalValues` a `StartDate`) y suma los resultados guardados por `employeeId`
    (`employees` lleva liquidables **y** bloqueados; `blocked` sólo `employeeId` y `reasons`, I-9); en
    `Approved` y `Closed` usa sólo los resultados y `ExcludedJson`. `can` y `approveBlockedBy` según el contrato. En esta
    tarea `sourceChanged` es siempre `false` (lo calcula la Tarea 11).

- [ ] **Step 1: Pruebas que fallan.**
  - `PayrollPeriodTests` (unitarias): `OpeningStartsInDraftAtVersionOne`; `InputsAreNotEditableOnceApproved`;
    `CapturingOnALiquidatedPeriodMarksItOutdatedOnce` (dos capturas ⇒ un solo `MarkedOutdated`);
    `LiquidatingNeedsTheAbsenceConfirmation`; `TheLegalSnapshotIsFixedOnTheFirstLiquidation` (segunda `RecordLiquidation`
    con otro JSON ⇒ conserva el primero); `LiquidatingClearsOutdated`; teoría `InvalidTransitionsAreRejected` (aprobar en
    `Draft`, cerrar en `Liquidated`, reabrir en `Draft` y `Liquidated` ⇒ `payroll.period.invalid_transition`);
    `ApproveBlockersComeInContractOrder`; `WithDualControlTheLastLiquidatorCannotApprove` (y sin doble control sí);
    `AReopenedPeriodRemembersTheReason`; `ClosingAnOutdatedApprovedIsRejected`; `EveryMutationBumpsTheVersion`.
  - `PayrollPeriodApiTests` (reloj del tenant fijo): `TheFirstPeriodOfACompanyIsTheCurrentFirstHalf` (hoy 2026-11-10 ⇒ un
    período 2026-11-01..15 `Draft`); `InASecondHalfNothingOpensUntilNextMonth` (hoy 2026-11-20, sin períodos ⇒ `items` vacío y
    `firstPeriodStartsOn == "2026-12-01"`); **`TheCurrentPeriodUsesTheTenantDayNotUtc`** (Review Focus 1: con la primera
    abierta, reloj en 2026-11-16T04:30:00Z ⇒ sigue un solo período); `EnsuringFillsTheGapsWithoutHoles` (primera de noviembre
    abierta, reloj en 2026-12-20 ⇒ cuatro períodos, `startDate` desc); `EnsuringTwiceIsIdempotent`;
    `AnInactiveCompanyDoesNotOpenPeriods` (422 `payroll.company.inactive`); `TheDetailListsLiquidableExcludedAndBlocked`
    (un empleado estándar, uno con salario integral ⇒ `employees` 1, `excluded[0].reason == "SalaryTypeNotSupported"`,
    `blocked` vacío, `totals == null`, `can.liquidate == true`, `history[0].action == "Created"`);
    `CanFollowsTheCallerPermissions` (sólo `payroll.period.read` ⇒ los seis de `can` en `false`, porque en `Draft` tampoco
    se exporta; con `manage` ⇒ `capture` y `liquidate` en `true`); `AnUnknownPeriodIs404`; `EnsuringIsAudited`;
    `ASettingsOnlyRoleCannotReadPeriods` (sólo `payroll.settings.manage` ⇒ 403 `authorization.denied` en `ensure-current`,
    la lista y el detalle; decisión del owner 2026-11-21); `EnsuringNeedsPeriodManage` (I-6: sólo `payroll.period.read` ⇒
    403 en `ensure-current` y 200 en `GET {c}/periods`, que no crea nada); `ABlockedEmployeeIsAlsoAnEmployeeRow` (I-9:
    decorador de `IPayrollHrDirectory` que quita los cargos de uno ⇒ está en `employees` y `blocked[0]` sólo trae
    `employeeId` y `reasons == ["MissingWorkplace"]`). Las pruebas que abren períodos usan un cliente con
    `payroll.period.manage`.
  - `PayrollPersistenceTests`: `APeriodWithInputsAndEventsRoundTrips`; `TwoPeriodsCannotShareAStart`;
    `AStaleVersionIsAConcurrencyConflict`; `APeriodOfAnUnknownCompanyViolatesTheForeignKey`
    (`FK_periods_companies_company_id`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollPeriod' could not be found`.
- [ ] **Step 3: Implementar** y generar la migración
  (`dotnet ef migrations add AddPayrollPeriods --project src/Modules/Payroll/Modules.Payroll.Infrastructure --context PayrollDbContext -o Persistence/Migrations`)
  con la FK a mano.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollPeriodApiTests|FullyQualifiedName~PayrollPersistenceTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): quincenal periods with their state machine`

> **Cierre del Bloque C:** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`; las
> clases `PayrollHrDirectoryTests`, `PayrollSettingsApiTests`, `PayrollPeriodApiTests` y `PayrollPersistenceTests`
> completas, una por vez; `HrPersistenceTests` completa. Una revisión del bloque.

---

## Bloque D — Capturas, liquidación y Excel (Tareas 10–13)

### Task 10: Capturas por empleado y conceptos recurrentes

**Files:**
- Create Domain (`RecurringStatus` ya existe desde la Tarea 8, I-4): `RecurringConceptId.cs`, `RecurringConcept.cs`
- Create Application: `IRecurringConceptRepository.cs`, `RecurringAccumulation.cs`, `UpdateEmployeeInputs.cs`,
  `GetPeriodEmployee.cs`, `ListRecurringConcepts.cs`, `SaveRecurringConcept.cs` (crear y editar), `ToggleRecurringConcept.cs`;
  sumar `EmployeeInputsRequest`, `RecurringConceptRequest` y sus validadores; en `PayrollDtos.cs`, los DTO de capturas,
  recurrentes y rastro (M-14): `InputLineDto`, `RecurringConceptDto`, `TraceInputDto`, `LegalValueUseDto`, `PayrollLineDto`,
  `PayrollEmployeeDetailDto` (con `result` anulable; la Tarea 11 lo llena)
- Create Infrastructure: `Persistence/RecurringConceptRepository.cs`, `Persistence/Migrations/<timestamp>_AddRecurringConcepts.cs`
- Modify: `PayrollDbContext.cs`, `PayrollEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/RecurringConceptTests.cs`, `EmployeeInputsRequestValidatorTests.cs`;
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollInputsApiTests.cs`, `RecurringConceptsApiTests.cs`

**Interfaces:**
- Consumes: Tareas 7–9 (`PayrollPeriod.ReplaceInputs`, `ConceptCatalog`, `PayrollEligibility`, `IPayrollResultStore`).
- Produces:
  - `RecurringConcept` (agregado): `Id`, `TenantId`, `CompanyId`, `EmployeeId`, `ConceptId`, `Mode`, `Amount`,
    `SalaryConstitutive?`, `Cap?`, `Active`, `Version`, `CreatedAt`, `UpdatedAt`; `Create`, `Update`, `Activate`
    (`payroll.recurring.already_active`), `Deactivate` (`already_inactive`); `RecurringStatus StatusFor(decimal accumulated)`
    (`Inactive` si no está activo; `Exhausted` si `Cap` y `accumulated ≥ Cap`; si no `Active`). Tabla
    `payroll.recurring_concepts` (índice `(tenant_id, company_id, employee_id)`).
  - `IRecurringConceptRepository` (M-14): `Task<RecurringConcept?> FindAsync(Guid tenantId, RecurringConceptId id,
    CancellationToken)` (rastreado); `Task<IReadOnlyList<RecurringConcept>> ListByEmployeeAsync(Guid tenantId, Guid companyId,
    Guid employeeId, CancellationToken)`; `Task<IReadOnlyList<RecurringConcept>> ListByCompanyAsync(Guid tenantId, Guid
    companyId, CancellationToken)` (lectura masiva, `AsNoTracking`, para el planificador de la Tarea 11); `void
    Add(RecurringConcept)`.
  - `RecurringAccumulation` (scoped): `Task<IReadOnlyDictionary<Guid, decimal>> AccumulatedAsync(Guid tenantId, Guid companyId,
    Guid employeeId, DateOnly? before, CancellationToken)` sobre `IPayrollResultStore.SumRecurringAppliedAsync` (R-12).
  - `UpdateEmployeeInputsCommand(Guid TenantId, Guid PeriodId, Guid EmployeeId, EmployeeInputsRequest Request, string? IfMatch)
    : ICommand<PayrollEmployeeDetailDto>` — autoriza; `If-Match`; período del tenant; el empleado debe estar en la quincena
    (`IPayrollHrDirectory` + `PayrollEligibility`: no aparece ⇒ 404 `payroll.employee.not_in_period`; `Excluded` ⇒ 422
    `payroll.employee.not_liquidable`; `Blocked` sí se captura); cada `conceptId` se valida contra `ConceptCatalog` (no
    existe, archivado o de otro kind ⇒ 422 `errors.<lista>[i].conceptId`); `ReplaceInputs`; auditoría
    `payroll.period.inputs_updated`.
  - `GetPeriodEmployeeQuery(Guid TenantId, Guid PeriodId, Guid EmployeeId) : IQuery<PayrollEmployeeDetailDto>` — en esta
    tarea `result` es `null`; la Tarea 11 lo llena. Un **excluido** responde 200 con `result: null`, `inputs` y `recurring`
    vacíos en `Draft`, `Liquidated` y `Reopened` (I-13); 404 `payroll.employee.not_in_period` sólo si no tiene contrato de la
    empresa en la quincena. En `Approved`/`Closed` responde lo de la foto (resultado o excluido) y 404 si no está en ella.
  - Rutas de recurrentes y de capturas del contrato. Autorización de recurrentes (decisión del owner): el `GET` con
    `RequireAuthorization(PayrollPolicies.SettingsView)` y `PayrollAuthorization.EnsureAnyAsync(…, PeriodRead,
    SettingsManage)`; crear, editar, activar y desactivar con `RequireAuthorization(PayrollPermissions.SettingsManage)` y
    `EnsureAsync(…, SettingsManage)`, sin `PeriodRead`. Las capturas siguen con `PeriodRead` + `PeriodManage`. Un empleado «tiene contrato con la empresa» si aparece en
    `IPayrollHrDirectory.ListEmployeesAsync(tenantId, companyId, DateOnly.MinValue, DateOnly.MaxValue)`; si no, 404
    `payroll.employee.not_found`.

- [ ] **Step 1: Pruebas que fallan.**
  - `RecurringConceptTests`: `ACapReachedIsExhausted`; `AnInactiveOneIsInactiveEvenUnderTheCap`; `ActivatingTwiceIsRejected`.
  - `EmployeeInputsRequestValidatorTests`: `HoursArePositiveWithTwoDecimals` (`overtime[0].hours`); `HoursAtMostThreeHundredSixty`;
    `AmountsArePositiveWithTwoDecimals` (`incomes[0].amount`); `AnOvertimeConceptOnlyOnce` (`overtime[1].conceptId`);
    `IncomesNeedTheirClassification` (`incomes[0].salaryConstitutive`); `ListsHaveTheirLimits`.
  - `PayrollInputsApiTests`: `CapturingReplacesTheThreeLists` (dos PUT; el segundo con una sola deducción ⇒ el detalle trae
    sólo esa; `employee.hasInputs`); `TheAnswerCarriesTheNewVersion` (`period.version` sube);
    **`AStaleIfMatchIs412AndWritesNothing`** (Review Focus 5); `AnExcludedEmployeeIs422NotLiquidable`;
    `AnEmployeeOutsideThePeriodIs404`; `AnExcludedEmployeeDetailIsEmptyNot404` (I-13: integral ⇒ `GET {p}/employees/{id}`
    200, `result == null`, listas vacías); `AConceptOfAnotherKindIs422OnTheField` (`errors["overtime[0].conceptId"]`);
    `AnArchivedConceptIs422`; `ACustomConceptOfAnotherCompanyIs422`; `CapturesNeedPeriodManage` (403);
    `CapturingIsAudited` (`payroll.period.inputs_updated`, sin valores).
  - `RecurringConceptsApiTests`: `CreatingAnswersTheWholeList`; `AnOvertimeConceptIsKindMismatch`; `ACapBelowTheAmountIs422`;
    `DeactivatingAndActivatingNeedNoIfMatch` (I-8); `EditingNeedsIfMatch`; `AnEmployeeWithoutAContractInTheCompanyIs404`;
    `AccumulatedStartsAtZero`; y, escritas **primero** (decisión del owner): `ASettingsOnlyRoleReadsAndWritesRecurringConcepts`
    (sólo `payroll.settings.manage` ⇒ 200 en el `GET`, 201 al crear, 200 al editar, activar y desactivar);
    `ASettingsOnlyRoleStillCannotReadOrLiquidatePeriods` (el mismo rol ⇒ 403 `authorization.denied` en `GET {p}`,
    `GET {p}/employees/{id}`, `PUT {p}/employees/{id}/inputs` y `POST {p}/liquidate`);
    `AReadOnlyRoleReadsRecurringConceptsButCannotWriteThem` (sólo `payroll.period.read` ⇒ 200 en el `GET`, 403 al crear,
    editar, activar y desactivar); `WithoutEitherPermissionRecurringConceptsAre403` (sólo `payroll.period.manage` ⇒ 403 en
    el `GET`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'RecurringConcept' could not be found`.
- [ ] **Step 3: Implementar** y generar `AddRecurringConcepts`.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollInputsApiTests|FullyQualifiedName~RecurringConceptsApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): capture overtime, incomes, deductions and recurring concepts`

### Task 11: Liquidación, foto fija, transiciones y rastro

**Files:**
- Create Application: `EngineInputFactory.cs`, `InputFingerprint.cs`, `LiquidationPlanner.cs`,
  `LiquidatePeriod.cs`, `ApprovePeriod.cs`, `ClosePeriod.cs`, `ReopenPeriod.cs`; modificar `PeriodComposer.cs`
  (`sourceChanged`, resultados, totales) y `GetPeriodEmployee.cs` (`result`)
- Modify: `PayrollEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/EngineInputFactoryTests.cs`,
  `InputFingerprintTests.cs`; `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollLiquidationApiTests.cs`

**Interfaces:**
- Consumes: Tareas 3–10.
- Produces:
  - `EngineInputFactory.Build(HrEmployeeSource employee, EligibilityResult eligibility, PeriodInfo period, EngineSettings
    settings, LegalValueSet legal, ConceptCatalogSnapshot concepts, IReadOnlyList<PeriodInput> inputs,
    IReadOnlyList<RecurringConcept> recurring, IReadOnlyDictionary<Guid, decimal> appliedBefore, MonthAccumulators prior) :
    EngineInput` (`public static`, puro). Sólo recurrentes `Active` y no agotados; `RemainingCap = Cap − appliedBefore`. Cada
    hora capturada pasa con `SystemKey` (concepto del sistema) o `CustomMultiplier` (propio); el factor lo deriva el motor
    (`OvertimeFactors`, Tarea 4), nunca la aplicación. Sólo pasa los salarios con `EffectiveDate ≤ period.End` y
    `ContractEndedOn` sólo si cae dentro del período (R-16b, I-10): lo programado en Hr para después no cambia la huella.
  - `InputFingerprint`: `static string Of(EngineInput input)` (SHA-256 en hexadecimal minúscula del JSON de `input` con
    `Legal` fuera) y `static string OfPeriod(IEnumerable<(Guid EmployeeId, string Fingerprint)> liquidable,
    IEnumerable<(Guid EmployeeId, string Reason)> excludedOrBlocked)` (ordenados por id).
  - `LiquidationPlanner` (scoped): `Task<LiquidationPlan> PlanAsync(PayrollPeriod period, CancellationToken)` — configuración
    (guardada o por defecto), catálogo, empleados del puerto, `PayrollEligibility`, valores legales (la foto del período si ya
    existe; si no, `IPayrollLegalValues.ResolveAsync(StartDate)`), `prior` (en una segunda, el `Month` del resultado del
    empleado en la primera; si no hay, `Empty`), recurrentes con `RecurringAccumulation.AccumulatedAsync(…, before:
    StartDate)`. `sealed record LiquidationPlan(LegalValueSet Legal, IReadOnlyList<PlannedEmployee> Liquidable,
    IReadOnlyList<ExcludedEmployeeDto> Excluded, IReadOnlyList<BlockedEmployeeDto> Blocked, string Fingerprint)`;
    `sealed record PlannedEmployee(HrEmployeeSource Source, EligibilityResult Eligibility, EngineInput Input, string Fingerprint)`.
  - `LiquidatePeriodCommand(Guid TenantId, Guid PeriodId, LiquidateRequest Request, string? IfMatch) :
    ICommand<PayrollPeriodDetailDto>`. Orden: autorizar ⇒ `If-Match` ⇒ cargar ⇒ estado ⇒ `confirmNoAbsences` ⇒ en una segunda,
    la primera según R-14 ⇒ `PlanAsync` ⇒ `Blocked` no vacío ⇒ `payroll.liquidation.blocked` ⇒ `Liquidable` vacío ⇒
    `nobody_to_liquidate` ⇒ `PayrollEngine.Liquidate` por empleado ⇒ `IPayrollResultStore.ReplaceAll` ⇒
    `period.RecordLiquidation(…)` ⇒ en una primera, si la segunda existe y no está en `Draft`, `MarkOutdated` ⇒ auditoría
    `payroll.period.liquidated` (campos `absencesConfirmed`, `employees`) ⇒ **un** `SaveChangesAsync`.
  - `ApprovePeriodCommand`, `ClosePeriodCommand`, `ReopenPeriodCommand(…, ReopenRequest Request, …)` — `ApprovalContext` con la
    configuración de la empresa, `sourceChanged` del planificador, `AnyNegativeNet` de los resultados y el estado de la primera;
    reabrir una primera marca la segunda (R-14). Auditoría `payroll.period.approved|closed|reopened`.
  - `PeriodComposer`: en `Liquidated` calcula `sourceChanged = plan.Fingerprint != period.Fingerprint`; `totals` =
    `PayrollSummary.Sum` de los resultados. `GetPeriodEmployee`: `result` con las líneas guardadas, su `summary` y la foto
    legal completa como `LegalValueUseDto` (label y basis de `LegalBasis`).

- [ ] **Step 1: Pruebas que fallan.**
  - `EngineInputFactoryTests`: `CapturesBecomeEngineLinesInOrder`; `SystemOvertimeCarriesItsKeyAndCustomItsMultiplier`; `ExhaustedAndInactiveRecurringAreLeftOut`;
    `TheRemainingCapSubtractsWhatWasApplied`; `HrNamesBecomeTheWorkplaceInfo`; `FutureSalariesAndEndsAreLeftOut` (I-10:
    salario con fecha posterior al fin del período y terminación futura ⇒ no entran y la huella no cambia).
  - `InputFingerprintTests`: `TheSameInputHasTheSameFingerprint`; `TheLegalSnapshotDoesNotChangeIt`; `ASalaryChangeChangesIt`;
    `ThePeriodFingerprintIgnoresOrder`.
  - `PayrollLiquidationApiTests` (empresa exonerada, centro clase I; empleado contratado por Hr con 1 750 905 desde
    2025-01-01; reloj 2026-11-10 y luego 2026-11-25):
    `LiquidatingTheFirstHalfMatchesTheAnchor` (detalle `Liquidated`, `totals.net == 929801`, `totals.employerCost ==
    1357907`; el empleado trae las líneas de `minimum-wage-first-half` y `trace.legalValues` de `health_employee` con `basis`
    «Ley 100/1993 art. 204»; `legalValuesDate == "2026-11-01"`; `engineVersion == PayrollEngine.Version`, nunca el literal, M-12);
    `TheSecondHalfSettlesTheMonth` (ancla `minimum-wage-second-half`);
    **`UnsupportedEmployeesAreExcludedWithTheirReason`** (Review Focus 7: uno estándar y uno integral ⇒ se liquida uno; el otro
    en `excluded` sin resultado); `LiquidatingNeedsTheAbsenceConfirmation`;
    `ABlockedEmployeeStopsTheLiquidation` (decorador de `IPayrollHrDirectory` en `configureServices` que quita los cargos de
    un empleado ⇒ 422 `payroll.liquidation.blocked`, `blocked[0].reasons == ["MissingWorkplace"]`);
    `NobodyToLiquidateIs422`; `TheSecondHalfNeedsTheFirstLiquidated`; `ReliquidatingTheFirstHalfOutdatesTheSecond`;
    `ReopeningTheFirstHalfOutdatesTheSecond`; `ApprovingTheSecondNeedsTheFirstApproved`;
    **`WithDualControlTheLastLiquidatorCannotApprove`** (Review Focus 4, con dos sujetos y `dualControl: true`: A liquida, A no
    aprueba ⇒ 422 `payroll.period.same_person_cannot_approve`; B aprueba; A reabre, B reliquida, B no aprueba, A sí);
    `WithoutDualControlTheLiquidatorApproves`; `ANegativeNetBlocksApproval`;
    `AChangeInHrAfterLiquidatingMarksSourceChanged` (salario nuevo por `POST /hr/…/salaries` con fecha 2026-11-05 ⇒
    `sourceChanged == true`, `approveBlockedBy` contiene `source_changed`, aprobar ⇒ 422 `payroll.period.outdated`; reliquidar lo
    limpia); `CapturingAfterLiquidatingOutdatesAndBlocksApproval`; `CloseAndReopenKeepTheHistory` (eventos `Created`,
    `Liquidated` con `absencesConfirmed`, `Approved`, `Closed`, `Reopened` con motivo); `ReopenNeedsAReason`;
    **`ALiquidationWithAStaleIfMatchIs412`**; `RecurringCapAccumulatesAcrossPeriods` (fijo 100 000 con tope 150 000 ⇒ primera
    100 000, segunda 50 000, luego `status == "Exhausted"` y `accumulated == 150000`); `ApprovedPeriodsAreReadFromTheSnapshot`
    (después de aprobar, terminar el contrato en Hr no cambia `employees` ni `totals`); `LiquidationIsAudited`;
    `ApprovingNeedsPeriodApprove` (403 con sólo `manage`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'EngineInputFactory' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`; la
  integración por nombre mientras se itera; al terminar,
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollLiquidationApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): liquidate, approve, close and reopen with an immutable snapshot`

### Task 12: Importación masiva de novedades

Calco de la importación de empleados de Hr (`HrImportColumns`, `IExcelEmployeeImporter`, `EmployeeImportRowParser`,
`EmployeeImportAnalyzer`, `ClosedXmlEmployeeImporter`, `ClosedXmlEmployeeImportTemplateBuilder`, rutas en `HrEndpoints.cs`
`:217-240`), con las reglas de «Importación de novedades».

**Files:**
- Create Application `Import/`: `InputsImportColumns.cs`, `IExcelInputsImporter.cs` (fila, lector y constructor de plantilla),
  `InputsImportDtos.cs`, `InputsImportRowParser.cs`, `InputsImportAnalyzer.cs`, `GetInputsImportTemplate.cs`,
  `PreviewInputsImport.cs`, `ImportInputs.cs`
- Create Infrastructure: `Excel/ClosedXmlInputsImporter.cs`, `Excel/ClosedXmlInputsImportTemplateBuilder.cs`
- Modify: `PayrollEndpoints.cs` (las tres rutas antes de `{p}/employees/{employeeId:guid}`; `.DisableAntiforgery()` en los
  `POST`; `IFormFile?`), `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/InputsImportRowParserTests.cs`;
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollInputsImportApiTests.cs`

**Interfaces:**
- Consumes: `ConceptCatalog`, `PayrollEligibility`, `PayrollPeriod.ReplaceInputsBulk`, `PayrollText.Fold`, `PayrollLabels`.
- Produces:
  - `InputsImportColumns.Ordered` (los seis encabezados), `DataSheetName = "Novedades"`, `EmployeesSheetName = "Empleados"`,
    `ValuesSheetName = "Valores válidos"`, `TemplateFileName = "plantilla-novedades.xlsx"`, `MaxRows = 5000`,
    `MaxSizeInBytes = 5 * 1024 * 1024`.
  - `sealed record InputsImportContext(IReadOnlyDictionary<string, EligibilityResult> EmployeesByFoldedDocument,
    IReadOnlyDictionary<string, HrEmployeeSource> SourcesByFoldedDocument, ConceptCatalogSnapshot Concepts)`.
  - `InputsImportRowParser.Parse(ExcelInputsRow row, InputsImportContext context) : InputsImportRowResult` (puro; junta todos
    los errores de la fila) con `sealed record ParsedInputRow(int RowNumber, Guid EmployeeId, PeriodInputDraft Draft, string
    FullName, string ConceptName)`.
  - `InputsImportAnalyzer.AnalyzeAsync(PayrollPeriod period, string? fileName, long size, Stream content, CancellationToken)`
    : archivo ⇒ lector ⇒ contexto ⇒ filas ⇒ `overtime_duplicated_in_file` ⇒ topes por empleado (M-13: más de 20 horas, 50
    ingresos o 50 deducciones de un mismo empleado ⇒ `too_many_lines` en cada fila sobrante, en orden de fila) ⇒ errores por
    `RowNumber` y `Field`.
  - `ImportInputsCommand(…, string? IfMatch) : ICommand<PayrollPeriodDetailDto>` — algún error ⇒
    `PayrollDomainException(ImportRowsInvalid)` sin escribir; si no, `ReplaceInputsBulk` agrupado por empleado en el orden de
    las filas; auditoría `payroll.inputs.imported`; un `SaveChangesAsync`.

- [ ] **Step 1: Pruebas que fallan.**
  - `InputsImportRowParserTests`: teoría `EachRuleReportsItsCodeAndField` (una fila por código de fila del contrato);
    `ConceptsMatchWithoutAccents` (`"hora extra diurna"`, `"COMISIONES"`); `AnEmptyClassificationTakesTheSuggestion`
    (`Comisiones` ⇒ `true`); `WithoutSuggestionTheClassificationIsRequired` (`Otros ingresos` vacío ⇒
    `salary_constitutive_required`); `AnExcludedEmployeeIsNotLiquidable`; `ARowCanCarrySeveralErrors`.
  - `PayrollInputsImportApiTests`: `TheTemplateHasTheThreeSheetsAndTheConcepts` (encabezados en orden, `Empleados` con los
    liquidables, `Valores válidos` con los doce del sistema y los propios no archivados, `Content-Disposition` con
    `plantilla-novedades.xlsx`); `PreviewWritesNothing`; `ImportReplacesOnlyTheEmployeesInTheFile` (A y B con capturas; archivo
    sólo con A ⇒ A reemplazado, B intacto); `ImportingOnALiquidatedPeriodOutdatesIt`; `ConfirmingWithErrorsIs422AndWritesNothing`;
    `ARepeatedOvertimeMarksBothRows`; `TheImportAppliesTheCaptureLimits` (M-13: 51 filas de ingreso de un empleado ⇒ la 51
    con `payroll.import.row.too_many_lines` y nada se escribe al confirmar); `ImportNeedsIfMatch`; teoría `FileErrors` (los siete códigos de archivo);
    `ApprovedPeriodsDoNotImport` (422 `payroll.period.not_editable`); `ImportIsAuditedAndLeavesAnEvent` (`InputsImported`).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'InputsImportRowParser' could not be found`.
- [ ] **Step 3: Implementar** según **Interfaces**.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollInputsImportApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): bulk import of period inputs from excel`

### Task 13: Excel «Resumen de nómina» con diseño de columnas

El generador no calcula: recibe filas ya liquidadas y un diseño (spec, «Excel»). El formato que pase el owner entra como otro
`PayrollColumnLayout`, sin tocar el motor ni el generador.

**Files:**
- Create Application `Excel/`: `PayrollColumn.cs`, `PayrollColumnLayout.cs`, `PayrollColumnLayouts.cs`, `PayrollWorkbook.cs`,
  `IPayrollWorkbookBuilder.cs`; `ExportPeriod.cs`
- Create Infrastructure: `Excel/ClosedXmlPayrollWorkbookBuilder.cs`
- Modify: `PayrollEndpoints.cs` (`Results.File` con el content type de `HrEndpoints.cs:465`), `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollColumnLayoutTests.cs`;
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollExportApiTests.cs`, `PayrollIsolationApiTests.cs` (M-15:
  las pruebas de Review Focus 3 y 6 viven en la tarea que las habilita —la última ruta `{p}…` es el Excel— para que tengan
  RED)

**Interfaces:**
- Consumes: `IPayrollResultStore`, `PayrollSummary`, `PayrollLabels`, `ITenantClock`.
- Produces:
  - `enum PayrollColumnFormat { Text, Integer, Money }`; `sealed record PayrollColumn(string Header, PayrollColumnFormat Format,
    Func<PayrollWorkbookRow, object?> Value, Func<PayrollSummary, decimal?>? Total)`;
    `sealed record PayrollColumnLayout(string Key, string SheetName, IReadOnlyList<PayrollColumn> Columns)`.
  - `PayrollColumnLayouts.Summary` (`Key = "summary"`, hoja `Resumen`): las 29 columnas del contrato, en su orden; las de
    dinero con su `Total`.
  - `sealed record PayrollWorkbookRow(string DocumentType, string DocumentNumber, string FullName, string? Position, string?
    Workplace, int WorkedDays, PayrollSummary Summary)`; `sealed record PayrollWorkbookExcluded(string DocumentType, string
    DocumentNumber, string FullName, string Reason)`; `sealed record PayrollWorkbook(string CompanyName, string CompanyTaxId,
    DateOnly Start, DateOnly End, string HalfLabel, string StatusLabel, DateTimeOffset GeneratedAtLocal,
    IReadOnlyList<PayrollWorkbookRow> Rows, PayrollSummary Totals, IReadOnlyList<PayrollWorkbookExcluded> Excluded)`.
  - `IPayrollWorkbookBuilder.Build(PayrollWorkbook workbook, PayrollColumnLayout layout) : byte[]` — encabezado, títulos,
    filas, `Totales` (de `workbook.Totals`, nunca fórmulas), hoja `No liquidados`; dinero con formato `#,##0`.
  - `ExportPeriodQuery(Guid TenantId, Guid PeriodId) : IQuery<PayrollExportFile>`, `sealed record PayrollExportFile(byte[]
    Content, string FileName)`; `Draft` ⇒ 422 `payroll.period.not_exportable`.

- [ ] **Step 1: Pruebas que fallan.**
  - `PayrollColumnLayoutTests`: `TheSummaryHasTheTwentyNineColumnsInOrder` (los títulos literales del contrato);
    `EveryMoneyColumnHasATotal`; `ValuesComeFromTheSummary` (una fila de prueba ⇒ `Neto` = `Summary.Net`).
  - `PayrollExportApiTests` (un estándar liquidado y uno integral excluido): `ADraftPeriodIsNotExportable`;
    `TheFileHasTheHeaderTheRowsAndTheTotals` (`Empresa` con nombre y NIT, `Estado` «Liquidado», fila del empleado con
    `Neto` 929 801, fila `Totales` igual a `totals` del detalle); `TheExcludedGoToTheirSheet` (motivo «El tipo de salario no se
    liquida todavía»); `TheFileNameCarriesTheDates` (`resumen-nomina-2026-11-01-a-2026-11-15.xlsx`);
    `AnApprovedPeriodSaysSo` («Aprobado»); `ExportNeedsPeriodRead`;
    **`ANewLegalValueChangesNeitherAClosedNorAReliquidatedPeriod`** (Review Focus 3, movida desde la Tarea 14 por M-15: tras
    cerrar la primera, `INSERT INTO hr.legal_parameters (key, effective_from, value) VALUES ('TransportAllowance',
    '2026-11-01', 300000)` ⇒ detalle, Excel y `legalValues` iguales; reabrir y reliquidar ⇒ `transport_allowance` sigue en
    124 548).
  - `PayrollIsolationApiTests`: **`AnotherTenantsPeriodIsForbiddenEverywhere`** (Review Focus 6, movida desde la Tarea 14 por
    M-15: con dos tenants y un período liquidado en B, la sesión de A contra cada ruta `{p}…` de la tabla —detalle, empleado,
    capturas, liquidar, aprobar, cerrar, reabrir, Excel, plantilla, vista previa e importación— con la ruta de B ⇒ 403
    `authorization.denied`; con la ruta de A y el id de B ⇒ 404 `payroll.period.not_found`; nunca un cuerpo con datos de B).
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'PayrollColumnLayouts' could not be found`. Las dos pruebas de
  integración nuevas fallan contra la ruta `{p}/export` todavía inexistente (404 de ruta, no `authorization.denied`): se
  corren por nombre y se copia la salida como RED antes de implementar.
- [ ] **Step 3: Implementar** según **Interfaces** (lectura en pruebas con `XLWorkbook`, como `EmployeeImportApiTests`).
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollExportApiTests|FullyQualifiedName~PayrollIsolationApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): export the payroll summary to excel through a column layout`

> **Cierre del Bloque D:** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`; las
> clases `PayrollInputsApiTests`, `RecurringConceptsApiTests`, `PayrollLiquidationApiTests`, `PayrollInputsImportApiTests`,
> `PayrollExportApiTests` y `PayrollIsolationApiTests` completas, una por vez. Una revisión del bloque.

---

## Bloque E — Cierre (Tareas 14 y 16)

### Task 14: Matriz de permisos y camino feliz de punta a punta

**Excepción a RED antes que GREEN (M-15, anotada):** esta tarea es de regresión sobre código ya hecho en las Tareas 8–13; sus
pruebas pasan o señalan un defecto. Por eso las de Review Focus 3 y 6 se movieron a la Tarea 13, donde sí tienen RED.

**Files:**
- Create: `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollAccessApiTests.cs`, `PayrollEndToEndApiTests.cs`
- Modify: sólo lo que estas pruebas obliguen a corregir.

- [ ] **Step 1: Pruebas.**
  - `PayrollAccessApiTests`: teoría
    `EachRouteRequiresItsPermissions` (una fila por ruta del contrato: sin el permiso en negrita ⇒ 403; con la lista completa ⇒
    distinto de 403; en las filas «A o B», con sólo A y con sólo B ⇒ distinto de 403); teoría
    `ASettingsOnlyRoleSeesOnlyTheSettingsScreens` (sólo `payroll.settings.manage`: 200 en `GET /payroll/companies` y
    `GET {c}/settings` y `GET {c}/employees/{id}/recurring-concepts`, éxito en las escrituras de configuración, conceptos y
    recurrentes, 403 en cada ruta de períodos, empleados del período, capturas, liquidación, importación y Excel); teoría
    `AReadOnlyRoleCannotWriteSettings` (sólo `payroll.period.read`: 200 en los tres `GET`, 403 en `PUT {c}/settings`,
    `POST|PUT {c}/concepts…`, `…/archive`, `…/unarchive` y en crear, editar, activar y desactivar recurrentes);
    `WithThePayrollModuleOffEverythingIs403`; `WithHrOffPayrollIsOffToo` (dependencia de la Tarea 1);
    `ACompanyOfAnotherTenantIs404`.
  - `PayrollEndToEndApiTests`: `AWholeMonthFromOpeningToExport` (dos empleados, uno con comisión y horas en la primera
    quincena: ensure ⇒ capturas ⇒ liquidar ⇒ aprobar ⇒ cerrar; segunda: ensure ⇒ liquidar ⇒ aprobar ⇒ cerrar ⇒ exportar;
    el mes de cada empleado suma su salario al peso y `health_employee` de las dos quincenas suma `ceil100(IBC_m × 4 %)`).
    El cliente que abre las quincenas tiene `payroll.period.manage` (I-6).
- [ ] **Step 2: Correr.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollAccessApiTests|FullyQualifiedName~PayrollEndToEndApiTests" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`. Si alguna falla, es un defecto de las Tareas 8–13: se reproduce con la prueba que falla (ya es el RED,
  con su salida literal), se corrige en el archivo dueño y se vuelve a correr.
- [ ] **Step 3: Commit** — `test(payroll): permission matrix and a whole month end to end` (más `fix(payroll): …`
  aparte por cada corrección).

### Task 16: Verificación final

**Files:** ninguno nuevo; sólo lo que la verificación obligue a corregir.

- [ ] **Step 1: Formato y build.** Run: `dotnet restore Backend.slnx --locked-mode`; `dotnet format Backend.slnx --verify-no-changes`;
  `dotnet build Backend.slnx --no-restore`. Expected: sin errores.
- [ ] **Step 2: Suite de los módulos tocados**, una sola vez y en fila (mirar antes
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`), cada una con `-- xUnit.MaxParallelThreads=4`, validando cada
  ruta con `Test-Path` antes (una ruta vacía corre la solución entera):
  `tests/Modules/Payroll/Modules.Payroll.UnitTests`, `tests/Modules/Payroll/Modules.Payroll.IntegrationTests`,
  `tests/Modules/Hr/Modules.Hr.UnitTests`, `tests/Modules/Hr/Modules.Hr.IntegrationTests`, `tests/ArchitectureTests/ArchitectureTests`,
  `tests/Bootstrapper/Bootstrapper.UnitTests`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests`,
  `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`. Expected: `Passed!` en todos; si algo falla, se reporta con su
  salida literal. **Esta corrida es la de antes de mergear a `develop`**: no se repite en `develop`. **Decisión del owner
  (M-16):** aunque el cambio toca Bootstrapper, `Program.cs` y migraciones de tres módulos (lo que el `CLAUDE.md` del
  workspace llama transversal), la suite de integración de **toda** la solución sólo corre si el owner la pide; por defecto
  es esta lista de proyectos tocados.
- [ ] **Step 3: Contrato contra código.** Recorrer la tabla «Rutas» y comprobar en `PayrollEndpoints.cs` método, ruta,
  política y status; que cada código de «Mensajes de dominio» y de la importación exista en `PayrollErrorCodes`; que
  `PayrollLabels` coincida letra por letra con la tabla de enums; y que `SystemConcepts` tenga los ids de la tabla. Una
  diferencia se arregla en el código, no en el contrato.
- [ ] **Step 4: Commit** (sólo si hubo correcciones) — `fix(payroll): <lo corregido>`

> **Cierre:** revisión fuerte de toda la rama (modelo más capaz) y entrega para merge a `develop` cuando el owner lo pida.
> **`docs/estado.md` no se toca en la rama:** al mergear, en `develop`, se agrega la fila de nómina proyecto 1 y su entrada del
> Registro. Despliegue: backend antes que el frontend; el módulo nace apagado (fuera de `DefaultForNewTenants`). La regla del
> módulo para `.claude/rules/` (gotchas de nómina) se escribe al mergear, con lo que haya salido de la revisión.

---

## Bloque F — Diferido

### Task 15: Caso de oro real de Siigo

**Bloqueada hasta que el owner entregue una quincena ya liquidada en Siigo** (entradas por empleado y resultado línea por
línea). Es el criterio de aceptación del spec («Cómo se prueba») y la prueba de las llamadas marcadas **(a confirmar con el
caso real)**: R-3, R-4, R-5, R-7, R-21 y las reglas del mes acumulado (spec, «Llamadas» 1, 2 y 6).

**Files:**
- Create: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/siigo/<yyyy-mm>-<1|2>-<nn>.json` (uno por empleado, con
  `"source": "siigo"`; documentos y nombres **anonimizados**: el repo no guarda datos personales reales),
  `Golden/legal/<fecha>.json` si hace falta.
- Modify: sólo las reglas que el caso contradiga, con su prueba de la Tarea 4–5.

- [ ] **Step 1: Transcribir** cada empleado al formato de la Tarea 6, con `notes` que citen la línea de Siigo de cada monto.
- [ ] **Step 2: Correr.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~GoldenCaseTests" -- xUnit.MaxParallelThreads=4`.
  Las diferencias son el RED: se copian literal a una tabla (empleado, línea, esperado Siigo, QEP, regla).
- [ ] **Step 3: Decidir con el owner** cada diferencia: o la regla de QEP cambia (sube `PayrollEngine.Version`, se corrige la
  regla y su prueba unitaria, y se rehacen a mano los casos sintéticos que dependan de ella), o Siigo está mal y el caso
  documenta la diferencia en `notes` con el valor legal correcto. Nunca se ajusta un esperado para que pase.
- [ ] **Step 4: GREEN.** El mismo comando. Expected: `Passed!` con los casos de Siigo incluidos.
- [ ] **Step 5: Commit** — `test(payroll): real siigo quincena as a golden case` (y `fix(payroll): …` por cada regla
  corregida, aparte).

## Fuera de este plan

Deliberadamente no se planea (proyectos 2 y 3 del spec, o fuera de alcance):

- **Proyecto 2:** incapacidades, licencias y vacaciones como novedades; ausencias pagas en la base del auxilio (R-22) y en el
  IBC; vacaciones fuera del IBC. Hasta entonces, la confirmación de «sin ausencias» al liquidar.
- **Proyecto 3:** retención en la fuente y préstamos como deducciones.
- Salario integral, medio tiempo, tiempo parcial, aprendices, estudiantes, contratistas, pensionados, exentos de pensión,
  subtipos distintos de estándar y salarios bajo el mínimo: se excluyen con motivo, no se calculan.
- Periodicidad mensual, períodos históricos y arrancar a mitad de mes cargando lo retenido antes (Llamada 5).
- Liquidación final del contrato (sólo la marca `finalSettlementPending`), prima y cesantías como **pago** (sólo provisión),
  PILA, nómina electrónica, archivos de banco, interfaz contable, colillas en PDF, autoservicio del empleado.
- Pantalla de operador para valores legales: la carga es por migración.
- Validar los topes legales de horas extras (2 diarias, 12 semanales): se paga lo capturado.
- El formato definitivo del Excel del owner: entra como otro `PayrollColumnLayout` cuando llegue.
- Avisar a Payroll desde Hr (Hr no publica eventos): los cambios se detectan con la huella al leer (R-16).
- Impedir en Hr borrar un empleado o una empresa que ya aparece en una nómina: la foto guarda nombre y documento, y la FK de
  R-19 ya protege a la empresa; el empleado borrable de Hr es sólo el recién contratado sin historial.
- Permisos por empresa: QEP no los tiene; los de nómina valen para todas las empresas del tenant.
- `docs/estado.md` y la regla `.claude/rules/` del módulo: al mergear, en `develop`.
- El plan de frontend (otro repo): cita la sección «Contrato HTTP» de este archivo.

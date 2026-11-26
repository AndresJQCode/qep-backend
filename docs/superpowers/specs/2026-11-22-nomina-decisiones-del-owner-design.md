# Nómina, proyecto 1: decisiones del owner (2026-11-22)

Fecha: 2026-11-22. Estado: borrador para aprobación del owner; sin código.
Repo: `qep-backend`, rama `feature/nomina-decisiones` (desde `develop`). Plan:
`docs/superpowers/plans/2026-11-22-nomina-decisiones-del-owner.md`.

Parte de `docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md` (el «spec base») y de su plan
`docs/superpowers/plans/2026-11-21-nomina-periodo-y-motor-backend.md` (el «plan base»; sus reglas se citan como R-n, I-n, M-n).
Coordina con el borrador `docs/superpowers/specs/2026-11-22-parametros-legales-consola-operador-design.md` (no aprobado): ver
«Coordinación con el spec de parámetros legales».

## Contexto

La nómina sale en diciembre de 2026. **No hay nada en producción ni ningún resultado de nómina guardado**, así que ningún
cambio de este documento necesita migrar datos, recalcular períodos viejos ni convivir con resultados de una versión anterior
del motor. El owner respondió once decisiones abiertas. Este documento dice, para cada una, qué hay hoy en el código, qué cambia
y lo que el código obligó a decidir («Decisión del implementador»). Lo que sólo el owner puede decidir va al final, en
«Pendiente del owner».

No se agregan campos persistidos, estados, rutas, permisos ni códigos de error: todo lo que hace falta ya existe
(`payroll.company.inactive`, `payroll.legal_parameter.missing`, el 422 por campo de la captura y la nota del rastro).

## Resumen

| # | Decisión | Cambia | Dónde |
| - | -------- | ------ | ----- |
| 1 | ARL sobre el IBC de seguridad social; parafiscales sobre el devengo salarial | Sí (motor) | `IbcRule`, `EmployerContributionsRule` |
| 2 | Auxilio y exoneración: liquidación mensual definitiva en la segunda quincena | Sí (motor) | `TransportAllowanceRule`, `EmployerContributionsRule` |
| 3 | Auxilio mensual con contrato que termina en la primera quincena | Sí (motor) | `TransportAllowanceRule` |
| 4 | La huella no lleva nombres de conceptos ni de centros de trabajo | Sí | `InputFingerprint` |
| 5 | Concepto archivado: lo ya capturado se vuelve a guardar; el recurrente deja de aplicar desde la quincena siguiente | Sí | `UpdateEmployeeInputs`, `ConceptCatalog`, `EngineInputFactory`, `LiquidationPlanner` |
| 6 | Tramos del FSP con el mismo control de año que los valores escalares | Sí | `src/Bootstrapper/PayrollLegalValues.cs` |
| 7 | No sembrar `SundaySurchargeRate` de enero a junio de 2025 | No | — |
| 8 | No detectar primeras quincenas guardadas con el motor 2026.11.1 | No | — |
| 9 | Cita del piso del salario integral | Sí (texto) | `LegalBasis` |
| 10 | Configuración de una empresa inactiva: se lee, no se edita | Sí | `UpdatePayrollSettings`, `PayrollCompanies` |
| 11 | N+1 en las listas | No | — |

Las decisiones 1 a 3 cambian resultados: el motor pasa de `2026.11.2` a **`2026.11.3`** (ver «Versión del motor»).

## 1. Base de la ARL y de los parafiscales

**Hoy.** `IbcRule` devuelve `IbcOutcome(IbcMonth, SalaryEarningsMonth, Line)`. `EmployerContributionsRule.cs:31-34` calcula ARL,
caja, ICBF y SENA sobre `ibc.SalaryEarningsMonth` (`SalDev_m` = salario del mes + horas + ingresos salariales, sin regla del
40 %, sin piso ni tope; R-4, I-7 del plan base). Salud, pensión y FSP van sobre `IbcMonth`.

**Cambia.** Dos bases explícitas en el resultado del IBC:

- `SocialSecurityIbc`: el IBC de seguridad social de hoy (devengo salarial más el exceso no salarial sobre el 40 %, piso
  `SMMLV ÷ 30 × días del mes`, tope 25 SMMLV, techo al peso). Base de salud, pensión, FSP **y ARL**.
- `ParafiscalBase`: `SalDev_m`, igual que hoy, sin regla del 40 %, sin piso ni tope. Base de caja, ICBF y SENA.

`IbcOutcome(decimal SocialSecurityIbc, decimal ParafiscalBase, PayrollLine Line)` reemplaza los dos nombres viejos (se renombran
en `EmployeeDeductionsRule`, `SolidarityFundRule` y `EmployerContributionsRule`). El rastro de la línea `arl` dice «IBC del mes»;
el de caja, ICBF y SENA sigue diciendo «Devengo salarial del mes». La línea `ibc` (Info) no cambia.

Los descansos remunerados que la decisión suma a la base parafiscal y la ARL en 0 durante vacaciones e incapacidades son del
proyecto 2 (ausencias): fuera de este cambio.

**Efecto, calculado corriendo los 28 casos de oro con la ARL sobre el IBC** (sólo dos cambian):

| Caso | Línea | Antes | Después | Costo empresa |
| ---- | ----- | ----- | ------- | ------------- |
| `non-salary-over-forty-percent` | `arl` | 5 300 = techo100(1 000 000 × 0,00522) | **6 300** = techo100(1 200 000 × 0,00522 = 6 264) | 2 554 143 → 2 555 143 |
| `ibc-cap-and-top-bracket-second` | `arl` | 130 500 = techo100(50 000 000 × 0,00522) − 130 500 | **98 000** = techo100(43 772 625 × 0,00522 = 228 493,10) − 130 500 | 36 686 500 → 36 654 000 |

En el segundo, la ARL baja porque el tope de 25 SMMLV ahora también la limita. Los otros 26 casos dan lo mismo: su IBC coincide
con el devengo salarial salvo por el techo al peso, que no cruza un múltiplo de 100.

## 2. Auxilio de transporte y exoneración: liquidación mensual definitiva

La primera quincena es **provisional**; la segunda **consolida el mes** y recalcula el derecho mensual al auxilio y la
exoneración del art. 114-1 ET (salud del empleador 8,5 %, SENA e ICBF para quien devenga menos de 10 SMMLV; la caja nunca se
exonera; DIAN, Concepto 007196/2025).

### 2a. Auxilio de transporte

**Hoy.** `TransportAllowanceRule.cs:32-35`: si `month.TransportCapIncome` supera `2 × SMMLV` la línea es 0 **y `Raw` también es
0**. En la segunda quincena `amount = round(prior.TransportRaw + raw) − prior.Transport`. Consecuencias:

- Primera pagada y mes por encima del tope (comisión en la segunda): la segunda da 0 y no descuenta. Ya cumple el art. 149 del
  CST, pero no avisa (`transport-flips-in-second-half-second`).
- Primera por encima del tope y mes por debajo (el salario baja el 16, o la empresa cambia la base del tope entre quincenas): la
  primera guardó `TransportRaw = 0`, así que la segunda paga sólo sus días y pierde los de la primera.

**Cambia.**

1. `Raw` se acumula **siempre** por días, cumpla o no el tope: es lo causado por días, no lo pagado. El derecho se decide al
   pagar. Así la segunda quincena, si el mes queda por debajo del tope, paga el mes completo menos lo pagado
   (`round(prior.TransportRaw + raw) − prior.Transport`).
2. Si el mes supera el tope, la segunda paga **0, nunca un valor negativo** (CST art. 149). Si la primera había pagado algo, la
   nota de la línea lo dice: «El ingreso del mes supera 2 SMMLV: lo pagado en la primera quincena no se descuenta (CST art.
   149)», y el rastro suma la entrada «Pagado en la primera quincena» con el monto. El monto que resulta de cualquier camino se
   acota en 0 (`Math.Max(0, …)`).

**Excepción legal: horas extras y recargos no hacen perder el auxilio.** Verificado: no hace falta código.
`MonthIncomeRule.cs:26-28` arma `TransportCapIncome` como `S` (`SalaryOnly`, `SalaryAndPaidAbsences`) o `S + SI_m`
(`SalaryAndSalaryIncome`). Las siete horas del sistema (incluidos `SundaySurcharge`, `NightSurcharge` y los dominicales
compuestos) y las horas propias son `OvertimeInput`, que van a `OT_m` y **nunca** entran en esa base. Una comisión o cualquier
ingreso salarial capturado o recurrente (`SI_m`) sí cuenta, con la base `SalaryAndSalaryIncome`. Se agrega una prueba que lo fija.

**Decisión del owner (2026-11-22, opción A).** La base del tope sigue siendo la de la configuración de la empresa, pero el valor
por defecto (`CompanyPayrollSettings.DefaultsFor`) pasa de `SalaryOnly` a `SalaryAndSalaryIncome`: así una comisión cuenta sin
que nadie toque la configuración. `SalaryOnly` se conserva como opción: pagar el auxilio en más casos favorece al trabajador y no
viola la ley. No hay empresas en producción, así que nada guardado cambia.

**Decisión del implementador: el aviso va en la nota de la línea, no en un indicador nuevo.** El motor ya modela avisos de dos
formas: indicadores del resultado (`EmployeeLiquidation.FinalSettlementPending`, `NegativeNet`, que son columnas de
`payroll.employee_results` y viajan en la fila del empleado) y la nota del rastro de cada línea (`LineTrace.Note`: «Exonerado
(ET art. 114-1)», «El ingreso del mes supera 2 SMMLV», «Se paga en la segunda quincena»). El de la decisión 2 es un hecho de
una línea, explicado por su rastro, y no bloquea nada: va en la nota. Un indicador por empleado exigiría columna, migración y
campo nuevo en el DTO; si el owner quiere verlo en la lista del período, se agrega después sin tocar el cálculo.

### 2b. Exoneración

**Hoy.** `EmployerContributionsRule.cs:16-17`: `exempt = Exonerated && monthSalaryIncome < 10 × SMMLV`, con
`monthSalaryIncome = S + SI_m + OT_m` (en la primera sólo lo de la primera). Exonerada, la línea es **0 fijo**, sin restar lo
aportado en la primera (`:48`).

- Primera exonerada y mes no exonerado (comisión grande en la segunda): la segunda cobra el mes entero (`techo100(base × tasa)
  − 0`). Ya consolida; se agrega una prueba que lo fija.
- Primera no exonerada y mes exonerado (el salario baja, o la empresa activa la exoneración entre quincenas): la segunda da 0 y
  **lo aportado en la primera queda cobrado**: el mes no consolida.

**Cambia.** Cada línea exonerable es `(exempt ? 0 : techo100(base × tasa)) − prior`. Con el mes exonerado y algo aportado en la
primera, la línea de la segunda es **negativa** (la corrección va del lado del empleador, en la segunda quincena, como pide la
decisión) con la nota «Exonerado en el mes (ET art. 114-1): se reversa lo aportado en la primera quincena». Sin nada aportado
antes, sigue «Exonerado (ET art. 114-1)». El trabajador no se toca: sus deducciones no dependen de la exoneración.

**Decisión del implementador.** Una línea negativa de aporte del empleador baja el costo empresa del período: es lo que el mes
debía. No se agrega indicador; `NegativeNet` mide el neto del trabajador y no cambia.

## 3. Auxilio mensual cuando el contrato termina en la primera quincena

**Hoy.** `TransportAllowanceRule.cs:42-45`: con frecuencia `Monthly` la primera quincena paga 0 («Se paga en la segunda
quincena»). Si el contrato termina en la primera, no hay segunda para ese empleado y el auxilio no se paga nunca.

**Cambia.** Con `Monthly`, la quincena que liquida es la segunda **o** la última trabajada: si `worked.FinalSettlementPending`
(el contrato termina dentro de la quincena, `WorkedDaysRule.cs:14`) en la primera, se paga lo causado
(`round(prior.TransportRaw + raw) − prior.Transport`, con `prior` en cero), con la nota «El contrato termina en esta quincena: se
paga lo causado del mes». Si el ingreso del mes supera el tope, manda el tope (0).

Caso de oro nuevo `monthly-transport-ends-in-first-half` (2026-11-01..15, salario 1 750 905, `Monthly`, contrato termina el
2026-11-10; calculado a mano, el implementador lo confirma):

| Línea | Monto | Cómo |
| ----- | ----- | ---- |
| `salary` | 583 635 | 1 750 905 ÷ 30 × 10 |
| `transport_allowance` | 83 032 | round(249 095 ÷ 30 × 10 = 83 031,67) |
| `ibc` | 583 635 | piso 1 750 905 ÷ 30 × 10 = 583 635 |
| `health_employee`, `pension_employee` | 23 400 c/u | techo100(23 345,4) |
| `health_employer`, `icbf`, `sena` | 0 | exonerado |
| `pension_employer` | 70 100 | techo100(70 036,2) |
| `arl` | 3 100 | techo100(3 046,57) |
| `compensation_fund` | 23 400 | techo100(23 345,4) |
| `severance`, `premium` | 55 533 c/u | round(666 667 × 0,0833) |
| `severance_interest` | 6 667 | round(6 666,67) |
| `vacation` | 24 338 | round(583 635 × 0,0417) |

`workedDays` 10, `finalSettlementPending` true, neto 619 867, costo empresa 905 338.

## 4. La huella no lleva nombres

**Hoy.** `InputFingerprint.Of` (`InputFingerprint.cs:17-24`) serializa el `EngineInput` entero menos `legal`. Lleva
`conceptName` en cada `OvertimeInput`, `IncomeInput`, `DeductionInput` y `RecurringInput`, y `name` en `WorkplaceInfo`
(`EngineInputFactory.cs:54-55, 73, 84, 90, 115`). Renombrar un concepto o un centro de trabajo marca `sourceChanged` en todo
período Liquidado que lo use.

**Cambia.** Antes de hashear se quitan `conceptName` de los elementos de `overtime`, `incomes`, `deductions` y `recurring`, y
`name` de `workplace`. Quedan los ids, horas, montos, topes y la naturaleza del concepto: tipo (`kind`), clave del sistema,
multiplicador propio, modo y si es constitutivo de salario. El `EngineInput` y el motor no cambian: los nombres siguen siendo
las etiquetas de las líneas.

**Decisión del implementador.** Un período Liquidado guarda sus líneas con el nombre de cuando se liquidó; renombrar ya no lo
marca como cambiado, así que la etiqueta vieja queda hasta la próxima liquidación. Es lo que pide la decisión: el nombre es una
etiqueta, no una entrada del cálculo.

## 5. Concepto archivado

### 5a. Capturas

**Hoy.** `UpdateEmployeeInputs.cs:117-119` rechaza **cualquier** línea con un concepto archivado (422 `validation.failed`, campo
`<lista>[i].conceptId`, «El concepto está archivado.»). Como la captura reemplaza las tres listas del empleado, guardar de nuevo
la quincena de alguien que ya tenía una línea de un concepto archivado después falla.

**Cambia.** Una línea con concepto archivado se acepta **si el empleado ya tenía guardada en esa quincena una línea de ese
concepto y tipo**; sólo una línea **nueva** con concepto archivado es 422, con el mismo campo y el mismo texto de hoy.

**Decisión del implementador: se cuenta por concepto y tipo.** La solicitud no trae el id de cada línea (reemplaza la lista
entera), así que «línea existente» no se puede identificar una a una. Regla: por empleado, concepto y tipo, se aceptan hasta
tantas líneas archivadas como había guardadas (`period.Inputs`, que `PayrollPeriodRepository.FindAsync` ya carga); la siguiente
es nueva y se rechaza. El valor (horas o monto) se puede cambiar. Vive en una clase pura `ArchivedLineQuota` en Application, con
prueba unitaria.

La importación por Excel no cambia: busca conceptos por nombre sólo entre los no archivados (`ConceptCatalog.cs:53`,
`InputsImportHandlers.cs:54`), así que una fila con un concepto archivado sigue siendo `payroll.import.row.concept_not_found`.
Una fila del archivo no es una línea guardada: es una línea nueva.

### 5b. Recurrentes

**Hoy (verificado).** `EngineInputFactory.RecurringOf` (`EngineInputFactory.cs:94-124`) sólo mira `item.StatusFor(applied)`
(activo, inactivo, agotado): **un recurrente de un concepto archivado sigue aplicando** en toda liquidación. `SaveRecurringConcept`
ya impide crear o editar uno con un concepto archivado (`SaveRecurringConcept.cs:32-35`, `payroll.concept.archived`).

**Cambia.** Un recurrente cuyo concepto se archivó **antes del primer día de la quincena** no entra en el `EngineInput`. La
quincena en la que se archivó lo conserva. «Antes» se mide en el **día del tenant** (`TenantCalendar.ToLocal`), como abrir
quincenas (`EnsureCurrentPeriods`): archivar a las 22:00 del 15 en Bogotá (03:00 UTC del 16) lo saca desde la quincena del 16.

Así se cumple que los períodos ya calculados no cambien: un período Liquidado empieza en o antes del día del archivo, su
`EngineInput` sigue llevando el recurrente y su huella no cambia (no aparece `sourceChanged`). Los Aprobados y Cerrados no se
recalculan nunca (`sourceChanged` sólo se calcula en un Liquidado, `PeriodApprovals.cs:36`). Desarchivar lo vuelve a aplicar.

Implementación: `ConceptCatalogSnapshot.ArchivedBefore(DateOnly periodStart, Func<DateTimeOffset, DateOnly> tenantDay)` devuelve
los ids de los conceptos propios archivados antes de ese día (los del sistema no se archivan); `LiquidationPlanner` lo calcula
con el calendario del tenant (`ITenantClock`, ya usado por los handlers de Payroll) y se lo pasa a `EngineInputFactory.Build`,
que sigue siendo pura.

## 6. Tramos del FSP: control de año

**Hoy.** `PayrollLegalValues.cs:29` marca faltante una clave anual cuya fila vigente es de un año anterior al del período, pero
los tramos (`:38-42`) sólo faltan si no hay ningún conjunto vigente: el de 2026 sirve para 2027.

**Cambia.** El conjunto de `SolidarityFund` vigente a la fecha también tiene que ser del año del período: si
`solidarity[0].EffectiveFrom.Year < asOf.Year`, `SolidarityFund` va a la lista de faltantes y la liquidación es
`payroll.legal_parameter.missing` con el mismo mensaje de hoy. La siembra (`AddPayrollLegalParameters`) tiene conjuntos de 2025
y 2026, así que diciembre de 2026 liquida; enero de 2027 necesita el conjunto de 2027, igual que el SMMLV.

## 7. Recargo dominical de enero a junio de 2025

Sin cambio. La siembra empieza el 2025-07-01 (`AddPayrollLegalParameters`, línea del `SundaySurchargeRate`) y `IsYearly` lo
excluye del control de año (`PayrollLegalValues.cs:14-15`). Una quincena de enero a junio de 2025 daría
`payroll.legal_parameter.missing`, pero la nómina arranca en diciembre de 2026 y no hay períodos históricos (no objetivo del spec
base). No hace falta nada.

## 8. Primeras quincenas guardadas con el motor 2026.11.1

Sin cambio. La versión se guarda en el período y en cada resultado (`PayrollPeriod.EngineVersion`,
`EmployeeResultRow.EngineVersion`) y viaja en los DTO, pero nada la compara: no entra en la huella ni en «desactualizado». No hay
resultados en producción, así que no hay nada que detectar.

## 9. Cita del piso del salario integral

`LegalBasis.cs:14`: `IntegralSalaryFloor` pasa de «CST art. 132» a **«CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)»**. Sólo
texto; la etiqueta no cambia.

## 10. Configuración de una empresa inactiva

**Hoy.** `EnsureCurrentPeriods.cs:67-70` rechaza abrir quincenas de una empresa inactiva con `payroll.company.inactive` (422).
`UpdatePayrollSettings` no mira `IsActive`: guarda.

**Cambia.** `PUT …/companies/{companyId}/settings` de una empresa inactiva es **422 `payroll.company.inactive`** (código existente;
`PayrollDomainException` ya mapea a 422 en `ApiExceptionHandler`, sin cambio ahí). El `GET` sigue respondiendo la configuración, y
el DTO ya trae `company.isActive`. El chequeo va justo después de resolver la empresa (`PayrollCompanies.RequireAsync`), antes de
la versión: a una empresa inactiva se le dice eso, no un 412. Se extrae `PayrollCompanies.EnsureActive(company, message)`, que
también usa `EnsureCurrentPeriods` con su mensaje de hoy.

**Decisión del implementador.** Sólo la configuración (`UpdatePayrollSettings`), como dice la decisión. Crear, editar y archivar
conceptos y recurrentes de una empresa inactiva no se bloquea en este cambio.

## 11. N+1 en las listas

Sin cambio, por decisión del owner.

## Versión del motor

`PayrollEngine.Version` pasa de `2026.11.2` a **`2026.11.3`** (R-26: cualquier cambio de regla la sube). Se guarda en el período y
en cada resultado y sale en los DTO; no forma parte de la huella ni del cálculo de «desactualizado» (ver decisión 8). Como no hay
resultados guardados en producción, subirla no deja nada inconsistente. Las decisiones 4 a 6 y 10 no cambian el cálculo del
motor: no la suben por sí solas.

## Casos de oro

- `non-salary-over-forty-percent` y `ibc-cap-and-top-bracket-second`: `arl` y `employerCost` como en la tabla de la decisión 1;
  sus `notes` se corrigen (en el primero, «aportes 189 300» pasa a 190 300; en el segundo, 6 229 000 pasa a 6 196 500). La
  descripción del primero deja de decir que el exceso no va a la ARL.
- `monthly-transport-ends-in-first-half`: nuevo (decisión 3). El conteo de `ThereAreTwentyEightSyntheticFilesWithUniqueNames`
  pasa a 29 y la prueba se renombra `ThereAreTwentyNineSyntheticFilesWithUniqueNames`.
- Los demás no cambian de montos. Las notas nuevas del auxilio y de la exoneración no se comparan en los casos de oro (comparan
  código y monto): se prueban en `TransportAllowanceRuleTests` y `EmployerContributionsRuleTests`.

El spec base se corrige donde el código cambia: «Aportes del empleador» (ARL sobre el IBC; corrección de la exoneración en la
segunda) y «Auxilio de transporte» (consolidación en la segunda, sin descuento, y mensual al terminar en la primera). El plan
base es el registro de lo que se ejecutó y no se toca.

## Coordinación con el spec de parámetros legales

El borrador `2026-11-22-parametros-legales-consola-operador-design.md` dice que los tramos `SolidarityFund` «no son anuales para
nómina» (sección de restricciones, `PayrollLegalValues.cs:38-42`) y, por decisión del owner de ese mismo día, que no llevan aviso
de `nextYearMissing`/`currentYearMissing`. **Con la decisión 6 los tramos pasan a ser anuales para nómina**: un enero sin conjunto
del año no liquida, igual que una clave escalar. Este cambio sólo corrige la frase de hecho del borrador (los tramos ahora sí se
exigen cada año) y no reescribe su diseño; si el aviso debe cubrir los tramos lo decide el owner (ver «Pendiente del owner»). Si
lo cubre, el `nextYearMissing` del conjunto se prende con la misma regla que las claves: no hay conjunto con vigencia
exactamente el 1 de enero del año siguiente.

## Frontend

Revisado `qep-frontend/src/features/payroll` (el checkout está en `main`; `develop` tiene el mismo último commit de la feature,
`a00767fb`). Hace falta:

1. `src/features/payroll/utils/payroll-failures.ts`, `FAILURE_MESSAGES['payroll.company.inactive']`: hoy dice «Esta empresa está
   inactiva: no abre quincenas nuevas.»; pasa a «Esta empresa está inactiva: no abre quincenas ni cambia su configuración.»
   (decisión 10).
2. `src/features/payroll/pages/payroll-settings-page.tsx`: con `settings.company.isActive === false`, la tarjeta se dibuja como
   sin permiso (`PayrollSettingsCard` recibe `canManage && settings.company.isActive`: controles deshabilitados y sin «Guardar»)
   y un aviso arriba: «Esta empresa está inactiva: puedes ver su configuración, pero no cambiarla.» (decisión 10).

No hace falta:

- **Aviso del auxilio (decisión 2):** va en `trace.note` de la línea, que `components/trace-sheet.tsx:96-97` ya pinta.
- **Concepto archivado (decisión 5):** `components/capture-sheet.tsx:152-154` y `:183-185` ya dejan elegible el concepto
  archivado de una línea guardada y no lo ofrecen para líneas nuevas; el 422 por campo ya se pinta.
- **ARL, exoneración negativa, versión del motor:** son montos, rastros y textos del backend.

El frontend tendrá su propio plan.

## Lo que no cambia

Rutas, permisos, DTOs, códigos de error, `ApiExceptionHandler`, esquema y migraciones de Payroll y de Hr, la siembra de valores
legales, la importación por Excel, el Excel «Resumen de nómina» y el rendimiento de las listas.

## Resuelto con el owner (2026-11-22)

1. **Base del tope del auxilio por defecto:** opción A, `SalaryAndSalaryIncome` por defecto y `SalaryOnly` como opción (Tarea B5b).
2. **Aviso de año faltante para los tramos del FSP:** reabierto en el spec de parámetros legales. El owner había decidido «tramos
   sin aviso» cuando no eran anuales; con la decisión 6 lo son. No se implementa en este cambio.

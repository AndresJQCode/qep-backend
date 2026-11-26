# Nómina: vacaciones disfrutadas (pago anticipado, salario fijo)

Fecha: 2026-11-23. Estado: borrador para aprobación del owner; sin código.
Repos: `qep-backend` (este documento, rama `feature/nomina-vacaciones`) y `qep-frontend`
(`docs/superpowers/specs/2026-11-23-nomina-vacaciones-disfrutadas-design.md`, pantallas). El contrato HTTP exacto vivirá en el
plan de backend, sección «Contrato HTTP», como en el proyecto 1.

Parte de `2026-11-21-nomina-periodo-y-motor-design.md` (el «spec base»; sus reglas R-n, I-n y M-n son las del plan
`2026-11-21-nomina-periodo-y-motor-backend.md`) y de `2026-11-22-nomina-decisiones-del-owner-design.md` (las «decisiones del
owner», D-n). Adelanta del proyecto 2 (novedades de ausencia) **sólo** las vacaciones disfrutadas.

## Fuente

Lógica de negocio: `lulo-erp/v2/docs/research/02-aleluya-payroll-engine-pila-dian.md` §4 (vacaciones, modos de pago anticipado
«Solo vacaciones» y «Nómina + Vacaciones», días hábiles por días de descanso, IBC del mes anterior, ARL en 0) y el openspec
`lulo-nomina-platform` (`specs/payroll-novedades/spec.md`, requisitos de vacaciones; `design.md` §4.5, matriz de efectos). **De
lulo-erp no se toma código, modelo, nombres ni patrones** (decisión del owner, 2026-11-20).

Verificado contra la ley, con diferencias frente a la referencia:

- **IBC durante vacaciones:** Decreto 806/1998 art. 70 (lo citan Aleluya y el openspec). El Decreto 1406/1999 art. 70 que a
  veces se cita no trata vacaciones; su regla de novedades (art. 40) es de incapacidades y licencias. El art. 70 dice que la
  cotización se causa **completa** y se liquida sobre el **último IBC reportado antes de que empiece el disfrute**. Por eso todos
  los días de vacaciones, también los de enero, usan el IBC del mes anterior al **inicio**, no el del mes anterior a cada mes.
- **Valor de las vacaciones:** CST art. 192: el salario ordinario que el trabajador devengue **el día en que empieza** a
  disfrutarlas. Un aumento de enero no cambia lo ya pagado en diciembre.
- **Auxilio de transporte:** Ley 15/1959 y Decreto 1258/1959: se paga sólo por los días trabajados. Ningún día de vacaciones lo
  causa.
- **Parafiscales:** Ley 21/1982 art. 17: la nómina mensual de salarios incluye los pagos «por descansos remunerados de ley». Las
  vacaciones disfrutadas entran en la base de caja, ICBF y SENA. Es lo que D-1 dejó para el proyecto 2.
- **Derecho:** 15 días hábiles consecutivos por año de servicio (CST art. 186). El tiempo de vacaciones cuenta como tiempo de
  servicio.

## Intención

La nómina sale en diciembre de 2026 (decisión del owner). Una persona con salario fijo sale de vacaciones en diciembre y vuelve en
enero. Hoy la liquidación exige confirmar que nadie tuvo ausencias (spec base, «Ausencias»; `LiquidateRequest.ConfirmNoAbsences`).
Con esa persona de vacaciones la confirmación sería falsa, y sin ella la quincena no se liquida. Este cambio deja registrarlas y
liquidarlas bien.

Modo de pago elegido por el owner, el **A**: todo se paga por adelantado en la quincena en que empiezan las vacaciones, también
los días de enero. Es el «Nómina + Vacaciones» de Aleluya. Las quincenas siguientes que caen dentro de las vacaciones pagan
salario 0 por esos días, sin pago doble. Los aportes se reparten por mes, como espera la PILA con la novedad VAC: diciembre cotiza
los días de diciembre y enero los de enero.

## Objetivos, no objetivos, antiobjetivos

**Objetivos**
- Registrar, editar y borrar vacaciones de un empleado (inicio y fin) fuera de una quincena puntual, con sus días hábiles
  calculados por el backend.
- Pagarlas completas en la quincena que contiene el inicio, con su rastro.
- Que cada quincena que tocan pague salario y auxilio sólo por los días trabajados.
- Cotizar cada mes sus días de vacaciones: seguridad social sobre el IBC anterior al inicio, ARL en 0 y parafiscales con el valor de
  los días.
- Que el Excel y la fila del empleado muestren días y valor de vacaciones.

**No objetivos (este cambio)**
- Salario variable (promedio de 12 meses, CST art. 192 par.), vacaciones compensadas en dinero, vacaciones anticipadas sin causar
  como modo distinto, el modo «Solo vacaciones» (archivo aparte), incapacidades y licencias (proyecto 2).
- **Saldo de días causados.** Ver llamada 1.
- PILA, nómina electrónica, colillas y archivos de banco (no objetivos del spec base).
- Solicitudes de tiempo libre del empleado, aprobaciones y calendario de ausencias.
- **Integración con Siigo, en cualquier sentido** (decisión del owner, 2026-11-23). Siigo es la nómina oficial y QEP la replica
  para control. QEP no importa nada a Siigo y Siigo no alimenta a QEP. Lo que Siigo ya pagó entra a mano (origen `External`).

**Antiobjetivos** (fallan aunque «funcionen»)
- Que el Excel se trate como un layout de importación a Siigo: es para control y conciliación contra Siigo.
- Que las vacaciones de QEP den otros números que las de Siigo sin que nadie lo vea: la liquidación de vacaciones se verifica
  contra la exportación real de Siigo (Tarea 15 del plan base), y una diferencia es un RED, no un redondeo aceptado.
- Pagar dos veces un día: como vacaciones en la quincena del inicio y como salario en la siguiente.
- Que un día de vacaciones de enero cotice en diciembre, o al revés.
- Que la nómina de una quincena aprobada o cerrada cambie porque alguien editó unas vacaciones.
- Que el navegador cuente días hábiles, días de nómina, quincenas o montos.
- Que una quincena cubierta por vacaciones deje un neto negativo que no se puede aprobar.

## Restricciones

- El motor sigue siendo **puro** (sin base de datos, reloj ni calendario). Los días hábiles se calculan en Application, al guardar.
  El motor sólo cuenta días de nómina a partir de fechas.
- Payroll no referencia Messaging, donde vive `ColombianHolidays`. El calendario llega por un puerto (llamada 4).
- Payroll lee Hr sólo por `IPayrollHrDirectory`. Los días de descanso (`Employee.RestDays`) hoy no viajan por ese puerto:
  `HrEmployeeSource` gana `RestDays` (adaptador `Bootstrapper/PayrollHrDirectory.cs`). En Hr son obligatorios, al menos uno, con el
  domingo por defecto (spec `2026-11-20-rrhh-fase-1-design.md`, «días de descanso»). Por eso el sábado es hábil salvo que el
  empleado lo tenga configurado como descanso.
- Un período Aprobado o Cerrado es una foto fija (R-16): nada de este cambio lo recalcula.
- Sólo empleados liquidables (spec base, «A quién se liquida»): salario `Mensual`, que es el fijo que este cambio cubre.

## Enfoques

- **A. Un registro de vacaciones propio, pagado completo en la quincena del inicio (elegido por el owner).** Un agregado
  `Vacation` por empleado y empresa, fuera de cualquier quincena. La quincena que contiene el inicio lo paga entero. Las siguientes
  lo leen para descontar días y cotizar su mes.
  - A favor: cumple el pago antes del disfrute y es lo que espera el owner. El trabajador recibe todo junto.
  - En contra: una quincena depende de otra de **otro mes**. El proyecto 1 sólo encadenaba primera y segunda del mismo mes (R-14).
    Además hay que retener por adelantado la seguridad social del trabajador de los días siguientes (ver «Deducciones del
    trabajador»), y la foto de la quincena que paga pasa a ser una entrada de las siguientes.
- B. Cada quincena paga sus propios días de vacaciones, como una captura más.
  - A favor: sin dependencias entre meses, sin anticipo de deducciones. Cada mes cotiza lo que paga.
  - En contra: no es el modo del owner. El trabajador cobra enero en enero, después de haber salido.
- C. Capturar las vacaciones en cada quincena (lo variable de la quincena, `PeriodInput`): descartado. Unas vacaciones cruzan
  quincenas y meses, y repetirlas a mano en cada una es la fuente del pago doble.

## Modelo de datos

### `Vacation` (esquema `payroll`, tabla `vacations`)

| Campo | Tipo | Qué es |
| --- | --- | --- |
| `Id` | `VacationId` | |
| `TenantId`, `CompanyId`, `EmployeeId` | `Guid` | La empresa empleadora es la del período que la paga. |
| `StartDate` | `DateOnly` | Primer día de descanso. Debe ser un día hábil del empleado. |
| `EndDate` | `DateOnly` | Último día de descanso, inclusive. Incluye los descansos y festivos del final si quien registra los marca (ver «Días»). |
| `Origin` | `VacationOrigin` | `Qep` (las paga una quincena de QEP) o `External` (las pagó Siigo). Viaja con su nombre. |
| `BusinessDays` | `int` | `Qep`: días hábiles entre inicio y fin, calculados al guardar. `External`: tecleados, los de Siigo. Sólo se muestran. |
| `ReturnDate` | `DateOnly` | El primer día hábil después de `EndDate`, calculado al guardar. Sólo se muestra. |
| `PreviousMonthIbc` | `decimal?` | IBC del mes anterior al inicio, tecleado. `Qep`: opcional, sólo se usa si QEP no lo tiene (ver «IBC»). `External`: **obligatorio**. |
| `ExternalMonths` | lista, sólo `External` | Por mes que tocan (`yyyy-MM`): `vacationPay?` (lo que Siigo pagó por los días de ese mes) y lo que Siigo ya retuvo por adelantado de salud, pensión y FSP del trabajador. Cada valor ausente vale 0. Tabla hija o JSON, al implementador. |
| `Version` | `long` | Concurrencia optimista, `If-Match`. |
| `CreatedAt`, `UpdatedAt` | `DateTimeOffset` | |

**Origen externo (decisión de la sesión).** Siigo es la nómina oficial y QEP la replica sólo para control, sin importar nada
entre los dos (decisión del owner, 2026-11-23). Por eso unas vacaciones que empezaron en noviembre, pagadas en Siigo, y siguen en
diciembre existen al arrancar y tienen que registrarse. Unas `External` **no tienen quincena que paga**: no generan `vacation_pay`
ni anticipos en QEP. Las quincenas de QEP que tocan las tratan igual que a unas `Qep` pagadas en otra quincena: salario y auxilio 0
por esos días, aportes repartidos por mes, IBC anterior, ARL 0. Lo que en el caso `Qep` sale de la foto de la quincena que paga
(salario del inicio, IBC anterior, anticipo por mes), en el `External` sale de los valores tecleados:

- IBC anterior: `PreviousMonthIbc`, sin buscar en QEP (no hay noviembre).
- Anticipo del mes, término (b) de «Álgebra del anticipo»: lo retenido por Siigo que se tecleó para ese mes; 0 si no se tecleó.
- Valor de un día de vacaciones (base parafiscal y provisiones): `vacationPay` del mes ÷ días de nómina del mes si se tecleó; si no,
  el salario vigente el día del inicio ÷ 30 (Hr guarda el historial de salario).
- Días hábiles: los tecleados. No se exige que el inicio sea día hábil ni que los hábiles calculados coincidan: el dato es de Siigo.

Las reglas 4 y 5 del ciclo de vida (`paid_period_locked`, `not_paid_yet`) no aplican a unas `External`: nunca se busca una quincena
que paga. El bloqueo es sólo la regla 1, por las quincenas que tocan, Aprobadas o Cerradas. El inicio de unas `External` puede
caer antes de la primera quincena de la empresa en QEP; su fin no puede caer antes de esa quincena (si no, no tocan nada de QEP y
no hay qué registrar: `payroll.vacation.before_first_period`).

La quincena que paga unas `Qep` **no se guarda**: es la que contiene `StartDate` (`QuincenaCalendar`), exista o no todavía. No hay columna de
estado: lo que la pantalla necesita se deriva al leer (ver `VacationDto`). Índice `(tenant_id, company_id, employee_id,
start_date)`. El solapamiento se valida en el handler. Si el implementador quiere la garantía en la base, la restricción `EXCLUDE`
por rango de fechas pide la extensión `btree_gist`, que se decide en el plan.

`BusinessDays` y `ReturnDate` se guardan, no se recalculan: si después cambian los días de descanso del empleado en Hr, las
vacaciones ya registradas no cambian. Para recalcularlas se editan.

### Días

- **Día hábil:** ni día de descanso del empleado (`RestDays`, siempre al menos uno) ni festivo nacional de Colombia. Con el
  default de Hr (sólo domingo), el sábado cuenta como hábil.
- **Un solo contrato** (decisión de la sesión): las vacaciones caben en el contrato del empleado con la empresa **vigente el día
  del inicio**, y ese contrato debe cubrir también el día del fin. Si no, 422 `payroll.vacation.outside_contract`. Un cambio de
  contrato o un contrato nuevo en medio de las vacaciones no se soporta: se editan las vacaciones. Un cambio de salario (aumento)
  después del inicio no cambia el valor de las vacaciones (CST art. 192), y los aportes siguen el Decreto 806/1998 art. 70 igual.
- **Días de nómina por quincena** (los que el motor paga y cotiza): se cuentan con la misma regla de 30 días de `WorkedDaysRule`
  (una quincena completa son 15). Si las vacaciones cubren el último día de la quincena, sus días son `15 − (días de la quincena
  antes del inicio)`. Si no, son los días reales. Así el 31 de diciembre no cuenta, y en febrero el final absorbe la diferencia.
  Siempre se cumple `días trabajados + días de vacaciones = días cubiertos por el contrato`.
- **Días trabajados** de la quincena: los de hoy (`WorkedDaysRule`) menos sus días de vacaciones. Pueden ser 0.

### Quién puede qué

- Registrar, editar y borrar: `payroll.period.manage` (y `payroll.period.read`, D-1 del plan base). Es una novedad de nómina, como
  las capturas, no la ficha del empleado (`settings.manage`, recurrentes).
- Leer: `payroll.period.read`.
- Empresa inactiva: 422 `payroll.company.inactive`, como la configuración (D-10).

### Ciclo de vida y bloqueos

Unas vacaciones **tocan** la quincena que las paga y cada quincena con días dentro del rango. Las reglas:

1. **Bloqueo por foto fija (decisión de la sesión).** Una escritura (crear, editar, borrar) se rechaza si **cambiaría la huella**
   (`InputFingerprint.Of`) de algún empleado en una quincena Aprobada o Cerrada. Las vacaciones son parte del `EngineInput` y, por
   tanto, de la huella (ver «Entrada del motor y huella»). Prueba del bloqueo: para cada quincena Aprobada o Cerrada que el rango
   viejo o el nuevo toca, se arma con el planificador la entrada del empleado con las vacaciones como quedarían. Si su huella
   difiere de la guardada en el resultado, la respuesta es 422 `payroll.period.not_editable` (el código de hoy para un período que
   no recibe capturas), con el mensaje «Reabre la quincena para cambiar estas vacaciones». Las quincenas que todavía no existen no
   cuentan. `can.update` y `can.delete` del DTO dicen si alguna quincena Aprobada o Cerrada toca las vacaciones.
2. **Desactualizar, como el código de hoy (decisión de la sesión).** Crear, editar o borrar llama `PayrollPeriod.MarkOutdated` en
   cada quincena que tocan. Hoy ese método marca desactualizado (y sube la versión) a todo período que **no** está en Borrador y no
   lo estaba ya: Liquidado y Reabierto. En Borrador, o si ya estaba desactualizado, no hace nada ni sube la versión. Aprobados y
   Cerrados no llegan acá por la regla 1. La huella cambia igual, así que en un Liquidado también llega `sourceChanged`.
3. **Reabrir la quincena que paga** (la ruta de hoy) vuelve a dejar editables las vacaciones **sólo** si ninguna otra quincena que
   tocan está Aprobada o Cerrada con una huella que cambiaría. Si enero ya se aprobó, hay que reabrir enero también.
4. **Volver a liquidar la quincena que paga** cambia la foto que leen las siguientes (ver «Foto de las vacaciones»), y la foto es
   parte de su entrada. Si con la foto nueva cambiaría la huella de una quincena siguiente Aprobada o Cerrada, la liquidación falla
   con 422 `payroll.vacation.paid_period_locked` y nombra esa quincena. Si no cambia, pasa. Las siguientes Liquidadas o Reabiertas
   se marcan con `MarkOutdated`, como R-14 hace con la segunda del mes.
5. **«Pagada» (decisión de la sesión)** quiere decir: la quincena que paga está Liquidada, Aprobada o Cerrada, **no** está
   desactualizada (`Outdated`) y **no** tiene `SourceChanged`. Un Borrador que contiene el inicio no la paga todavía. Una quincena
   con días de vacaciones pagadas en otra no se liquida mientras no estén pagadas: 422 `payroll.vacation.not_paid_yet`. Es la R-14
   extendida entre meses.

## Cálculo

### Valor y línea `vacation_pay`

Sólo en la quincena que paga. Es una línea `Earning`, código `vacation_pay`, etiqueta «Vacaciones disfrutadas», no constitutiva de
salario para el auxilio, la regla del 40 % ni la exoneración:

`vacation_pay = redondeo al peso(salario mensual vigente el día del inicio ÷ 30 × días de nómina de vacaciones de todas las quincenas)`

El rastro lleva inicio, fin, días hábiles, días de nómina por quincena (con su fecha de inicio), el salario del inicio con su fecha
de registro, y la nota «CST arts. 186 y 192; pago anticipado de todo el disfrute». Unas vacaciones que empiezan en esta quincena
aportan una línea cada una, con código `vacation_pay:{vacationId}`. El resumen las suma en `VacationPay`.

### Días pagados: calendario, no sólo hábiles (llamada 2, a verificar en la Tarea 15)

Se pagan como vacaciones **todos los días de nómina** del rango (hábiles, descansos y festivos dentro de él), no sólo los hábiles.
Con salario fijo el trabajador recibe lo mismo de las dos formas. Si se pagaran sólo los hábiles como vacaciones, los descansos y
festivos dentro del rango se pagarían como salario de cada quincena. El total por día es el mismo, salario ÷ 30, y la suma da el
salario completo del mes. Lo que cambia es **en qué línea y en qué quincena** cae. Elijo el calendario porque:

- el owner pidió que las quincenas siguientes paguen 0 por los días dentro de las vacaciones, y con sólo hábiles el domingo de enero
  saldría como salario en enero;
- la PILA reporta días de VAC por mes en días calendario, y el IBC, la ARL y los parafiscales se reparten con esos mismos días.

Si la exportación real de Siigo (Tarea 15 del plan base) separa «vacaciones» por días hábiles y deja los descansos como salario, se
cambia la regla de la línea, no el modelo.

### Salario, auxilio y acumulados del mes

- `SalaryRule` paga sólo los días trabajados (sin los de vacaciones). Con 0 días trabajados la línea `salary` es 0, en vez de la
  excepción de hoy («at least one covered day»). Los tramos de salario se cortan sólo sobre días trabajados.
- Los conceptos recurrentes **proporcionales** no pagan los días de vacaciones (se prorratean por los días realmente trabajados, los mismos que usan el salario y el auxilio); los **fijos** siguen pagando: decisión del owner (2026-11-23).
- `TransportAllowanceRule` causa por días trabajados: ningún día de vacaciones da auxilio, se pague o no por adelantado. El tope y
  la consolidación del mes (D-2, D-3) no cambian.
- `MonthAccumulators.WorkedDays` sigue contando sólo días trabajados. Los de vacaciones del mes se calculan de las fechas de las
  vacaciones, no se acumulan: el motor los conoce por la entrada.

### IBC: tres bases por mes

Las vacaciones del mes son los días de nómina de vacaciones **de las quincenas del mes hasta ésta inclusive**, se hayan pagado
donde se hayan pagado. `IbcOutcome` pasa a llevar tres bases:

| Base | Fórmula | La usan |
| --- | --- | --- |
| `SocialSecurityIbc` | `techo al peso(mín(máx(base de días trabajados + IBC anterior ÷ 30 × días de vacaciones del mes; SMMLV ÷ 30 × (días trabajados + días de vacaciones del mes)); 25 SMMLV))` | salud, pensión, FSP |
| `ArlBase` | la misma, **sin** la parte de vacaciones y con piso sólo por días trabajados | ARL (0 por días de vacaciones) |
| `ParafiscalBase` | `SalDev_m` de hoy + valor de los días de vacaciones del mes (`salario del inicio ÷ 30 × días`) | caja, ICBF, SENA (Ley 21/1982 art. 17) |

La «base de días trabajados» es la de hoy: devengo salarial con la regla del 40 %. El pago de vacaciones no entra en esa regla. En
la quincena que paga, los días de enero **no** entran en el IBC de diciembre. Enero los cotiza con el mismo IBC anterior, que viaja
en la foto. La línea `ibc` (Info) suma al rastro «IBC anterior a las vacaciones», «Días de vacaciones del mes» y «Parte de
vacaciones».

**IBC anterior al inicio** (decisión de la sesión): el del **mes anterior al mes del inicio**, resuelto al liquidar la quincena
que paga, en este orden:
1. De QEP, si **las dos quincenas** de ese mes tienen resultado del empleado (Liquidada o más): el `ibc` de la segunda, que es el
   IBC del mes.
2. Si no, `Vacation.PreviousMonthIbc`, si se tecleó.
3. Si no, el salario mensual vigente el día anterior al inicio, con la nota «Sin IBC del mes anterior en QEP: se usa el salario».

En diciembre de 2026 no hay noviembre en QEP: rige el 2 o el 3. Con salario fijo y sin horas ni comisiones en noviembre, los dos
dan lo mismo.

### Aportes del empleador

Salud y pensión sobre `SocialSecurityIbc`, ARL sobre `ArlBase`, caja, ICBF y SENA sobre `ParafiscalBase`. Siguen siendo «lo del mes
menos lo aportado antes en el mes» (D-2). La exoneración no cambia. El empleador cotiza los días de enero en enero, no en
diciembre: eso **es** el reparto por mes que pide el owner.

### Deducciones del trabajador: anticipo en la quincena que paga (llamada 3)

Sin anticipo, una quincena de enero cubierta por vacaciones tendría salario 0 y aun así retendría salud y pensión sobre su IBC de
vacaciones: neto negativo, que hoy bloquea la aprobación (`payroll.period.negative_net`). Por eso la quincena que paga **retiene por
adelantado** la seguridad social del trabajador de los días de vacaciones de las **otras** quincenas:

**Álgebra del anticipo (decisión de la sesión).** El anticipo se guarda **por mes**, no por quincena:

- **Qué se anticipa.** Los días de vacaciones fuera de la quincena que paga, agrupados por mes `m`: los de la otra quincena del mes
  que paga (si los hay) y los de cada mes siguiente. Por cada mes: `parte_m = techo al peso(IBC anterior ÷ 30 × d_m)`;
  `anticipo de salud_m = redondeo al peso(4 % × parte_m)`, igual para pensión, y FSP con la tasa del tramo del IBC anterior (si
  llega a 4 SMMLV).
- **Dónde queda.** Líneas `Deduction` en la quincena que paga, una por concepto: `health_employee_vacation_advance`,
  `pension_employee_vacation_advance`, `solidarity_fund_vacation_advance`; cada rastro desglosa por mes. El monto **efectivamente
  retenido** por mes queda congelado en la foto (ver «Foto de las vacaciones»). Lo de los días de vacaciones de la propia quincena
  que paga va en `health_employee` y `pension_employee`, como hoy.
- **Acumulados.** Las líneas de anticipo **no** entran en `MonthAccumulators.HealthEmployee`, `PensionEmployee` ni `SolidarityFund`
  de la quincena que paga: son de días de otra quincena o de otro mes.
- **En una quincena posterior del mes `m`** (cualquiera de las dos), cada línea del trabajador es:
  `línea = (tasa × IBC del mes) − (a) − (b)`, donde **(a)** es lo retenido como líneas reales por las quincenas anteriores del mes
  (`Prior.HealthEmployee`, como hoy) y **(b)** es el anticipo guardado para el mes `m`. Son dos términos separados: (a) es un
  acumulado del mes y (b) llega en la entrada, de la foto. El anticipo de un mes se descuenta **completo** en la primera quincena
  del mes que liquida con vacaciones de ese mes; la segunda lo recibe otra vez en (b), y su (a) ya incluye la línea de la primera,
  que a su vez descontó el anticipo. Así la suma del mes cierra: `(a) + (b) + línea = tasa × IBC del mes`.
- **Anticipo acotado.** Si en la quincena que paga el neto no alcanza para todo el anticipo, se retiene hasta dejar el neto en 0, y
  la foto guarda **lo retenido**, no lo calculado. La quincena posterior resta sólo lo guardado en (b), así que el resto se retiene
  allí normalmente.
- **Tope por neto en la posterior.** En una quincena con 0 días trabajados y sin otro devengo, las líneas de salud, pensión y FSP del
  trabajador no bajan el neto de 0. Si queda una diferencia (redondeos, o el piso del SMMLV de un año nuevo sobre un IBC anterior
  de salario mínimo), en la primera quincena del mes la retiene la segunda. Si la que queda sin devengo es la segunda, **los pocos
  pesos no se retienen** y la nota de la línea lo dice (decisión del owner, antes pregunta 1).

**Ejemplo: segunda de enero con 0 días trabajados y anticipo.** Salario fijo 3 000 000, descanso domingo y sábado, empresa
exonerada, ARL clase I (0,522 %). Diciembre de 2026 liquidado en QEP con IBC 3 000 000. Vacaciones del martes 2027-01-12 (el lunes
11 es festivo de Reyes) al lunes 2027-02-01: 15 días hábiles. Los valores legales de 2027 que el ejemplo usa se suponen iguales a
los de 2026 (salvo el SMMLV, que no toca: el piso queda bajo el IBC).

| | 2027-01-01..15 (paga) | 2027-01-16..31 | 2027-02-01..15 |
| --- | --- | --- | --- |
| Días de vacaciones / trabajados | 4 / 11 | 15 / 0 | 1 / 14 |
| `salary` | 1 100 000 | 0 | 1 400 000 |
| `vacation_pay` | 2 000 000 (20 días × 100 000) | — | — |
| Auxilio (valor de 2026 supuesto) | redondeo(249 095 ÷ 30 × 11) = 91 335 | 0 (ningún día trabajado) | — |
| IBC del mes hasta la quincena | 1 100 000 + 400 000 = 1 500 000 | 1 100 000 + 1 900 000 = 3 000 000 | — |
| `health_employee` | 4 % × 1 500 000 = 60 000 | 120 000 − (a) 60 000 − (b) 60 000 = **0** | — |
| `pension_employee` | 60 000 | **0**, igual | — |
| Anticipo de salud (enero / febrero) | 60 000 (4 % × 1 500 000) / 4 000 (4 % × 100 000) | — | — |
| Anticipo de pensión (enero / febrero) | 60 000 / 4 000 | — | — |
| `pension_employer` | techo100(12 % × 1 500 000) = 180 000 | techo100(12 % × 3 000 000) − 180 000 = 180 000 | — |
| `health_employer`, `icbf`, `sena` | 0 (exonerada) | 0 | — |
| `arl` (base sin vacaciones) | techo100(1 100 000 × 0,00522 = 5 742) = 5 800 | 5 800 − 5 800 = **0** | — |
| `compensation_fund` | techo100(4 % × 1 500 000) = 60 000 | techo100(4 % × 3 000 000) − 60 000 = 60 000 | — |
| Neto de la segunda de enero | | 0 − 0 = **0** | |

La segunda de enero no paga nada al trabajador y tampoco le retiene: su (a) es la línea real de la primera (60 000) y su (b) el
anticipo de enero (60 000). Febrero resta su (b) de 4 000 en la primera de febrero. Sus provisiones salen de los 15 días de
vacaciones como tiempo de servicio (1 500 000): cesantías y prima 124 950 cada una, intereses 15 000, vacaciones 62 550.

El resumen (`PayrollSummary`) suma las líneas de anticipo en `HealthEmployee`, `PensionEmployee` y `SolidarityFund`: es lo que la
colilla de esa quincena le retiene al trabajador. `IsOther` no las toma, porque no empiezan por `deduction:` ni `recurring:`.

### Provisiones

La línea `vacation_pay` **no** entra en ninguna base de provisión: en diciembre llevaría enero. Los días de vacaciones de **esta**
quincena cuentan como tiempo de servicio (CST art. 186) por `salario del inicio ÷ 30 × días`, en las bases de cesantías, intereses,
prima y vacaciones. Con salario fijo, las provisiones de cada quincena quedan iguales a las de una quincena trabajada, salvo el
auxilio, que no se causa en vacaciones y baja cesantías, intereses y prima. Es mi lectura de «excluida de la base de la provisión
de vacaciones»: se excluye el dinero, no los días (llamada 5).

### Foto de las vacaciones

Al liquidar la quincena que paga, el resultado de cada empleado guarda junto a sus líneas una foto por vacaciones (como guarda lo
aplicado de cada recurrente): `vacationId`, salario del inicio, IBC anterior y su fuente, los días de nómina por quincena y, **por
mes** tocado fuera de la quincena que paga, `{ mes, parte de IBC, anticipo retenido de salud, de pensión y de FSP }` (lo retenido,
no lo calculado). Las quincenas siguientes la leen de la última foto de la quincena que paga, no la recalculan. Así enero no cambia
si después cambian los valores legales o el salario de diciembre.

**Lectura del planificador (decisión de la sesión).** `LiquidationPlanner` gana una lectura de repositorio nueva: las vacaciones de
la empresa que se solapan con el **mes** del período y, para cada una pagada en otra quincena, la foto guardada de esa quincena (la
del empleado en `payroll.employee_results`). La forma de la consulta (una o dos, índice, unión con resultados) la decide el
implementador. Con eso arma `VacationInput` y resuelve «pagada» (regla 5 del ciclo de vida).

### Entrada del motor y huella

`EngineInput` gana `IReadOnlyList<VacationInput> Vacations`: las vacaciones que tocan el **mes** de la quincena (para contar los
días del mes de la primera quincena en la segunda). Cada una con `VacationId`, `StartDate`, `EndDate`, `BusinessDays`, si esta
quincena la paga, y, si la paga otra, la foto (`IBC anterior`, `salario del inicio`, el anticipo guardado para el mes de esta quincena). La quincena
que paga recibe el IBC anterior ya resuelto (orden de arriba).

La huella (`InputFingerprint.Of`) la incluye sin cambios de código: serializa el `EngineInput` entero. Editar unas vacaciones o
volver a liquidar la quincena que paga cambia la huella de las quincenas Liquidadas que las leen.

`EmployeeLiquidation` gana `VacationDays` (días de nómina de vacaciones de esta quincena). `WorkedDays` queda sin ellos.

`PayrollEngine.Version` pasa de `2026.11.4` a **`2026.11.5`** (R-26).

### Ejemplo (a confirmar a mano por el implementador)

Salario fijo 3 000 000, descanso sábado y domingo, inicio lunes 2026-12-14, fin martes 2027-01-05 (festivos 25 de diciembre y 1 de
enero): **15 días hábiles**, regresa el miércoles 2027-01-06.

| Quincena | Días de vacaciones | Días trabajados | Salario | `vacation_pay` | Parte de IBC de vacaciones |
| --- | --- | --- | --- | --- | --- |
| 2026-12-01..15 (paga) | 2 | 13 | 1 300 000 | 2 200 000 (22 días) | 200 000 |
| 2026-12-16..31 | 15 | 0 | 0 | — | 1 500 000 |
| 2027-01-01..15 | 5 | 10 | 10 días al salario de enero | — | 500 000 |

IBC de diciembre = 1 300 000 + 1 700 000 = 3 000 000. ARL de diciembre sobre 1 300 000. Parafiscales de diciembre sobre 3 000 000.
Anticipos en la primera de diciembre: salud y pensión 60 000 c/u para diciembre (los días de la segunda), 20 000 c/u para enero.
La segunda de diciembre retiene `4 % × 3 000 000 − 60 000 (a) − 60 000 (b) = 0`.

## Interacción con la primera y la segunda quincena

- R-14 sigue igual: la segunda del mes lee la primera. Si la primera paga vacaciones, la segunda lee de ella los acumulados (sin los
  anticipos) como término (a) y, por la foto, el anticipo del mes como término (b).
- La segunda del mes cuenta los días de vacaciones de la primera para el IBC, la ARL y los parafiscales del mes (de las fechas, no
  de un acumulado).
- Auxilio mensual (D-3): los días de vacaciones no lo causan en ninguna de las dos.
- Nada de esto toca las quincenas sin vacaciones: sin `Vacations` el motor da exactamente lo de hoy (los 29 casos de oro no cambian).

## Liquidar: la confirmación de ausencias

`LiquidateRequest.ConfirmNoAbsences` y el código `payroll.liquidation.absences_unconfirmed` se quedan: incapacidades y licencias
siguen sin existir. Cambia **lo que se confirma** (llamada 6): «fuera de las vacaciones registradas, ningún empleado tuvo
ausencias». Un empleado con vacaciones registradas que tocan la quincena cuenta como declarado **por esos días**. Si además tuvo
una incapacidad, sigue sin registrarse y quien liquida no debe confirmar. El detalle del período lista esas vacaciones para que el
diálogo las muestre. El evento y la auditoría siguen guardando `absencesConfirmed`.

## Contrato HTTP

Grupo de hoy: `/api/v1/tenants/{tenantId:guid}/payroll`. Tenant en la ruta, política y revalidación en el handler
(`PayrollAuthorization`, 403 nunca 404). Ruta base `/companies/{companyId:guid}/employees/{employeeId:guid}/vacations`, como los
recurrentes.

| Método y ruta | Política | Handler exige | Responde |
| --- | --- | --- | --- |
| `GET …/vacations` | `PeriodRead` | read | 200 `IReadOnlyList<VacationDto>`, del inicio más reciente al más viejo |
| `POST /companies/{companyId:guid}/employees/{employeeId:guid}/vacations/preview` | `PeriodManage` | read + manage | 200 `VacationPreviewDto`; no escribe |
| `POST …/vacations` | `PeriodManage` | read + manage | 201 la lista entera |
| `PUT …/vacations/{vacationId:guid}` | `PeriodManage` | read + manage | 200 la lista entera; `If-Match` |
| `DELETE …/vacations/{vacationId:guid}` | `PeriodManage` | read + manage | 200 la lista entera; `If-Match` |

`If-Match` con la `version` de las vacaciones (`PayrollIfMatch.Require`): sin él 428, vieja 412. Crear no lo lleva (como los
recurrentes). Las quincenas que cambian de estado suben su propia versión: una pestaña con el período viejo recibe 412 en su próxima
escritura.

`VacationRequest`: `{ origin, startDate, endDate, previousMonthIbc?, businessDays?, externalMonths? }`, con validador
FluentValidation (`validation.failed` y mapa `errors`): `origin` (`Qep` | `External`) y fechas obligatorias, `endDate ≥ startDate` y una duración de 366 días como máximo, inclusive (422 en `endDate`; decisión de la sesión (revisión final)),
`previousMonthIbc > 0` si viene. Con `External`: `previousMonthIbc` y `businessDays ≥ 1` obligatorios; `externalMonths` opcional,
`[{ month: 'yyyy-MM', vacationPay?, healthAdvance?, pensionAdvance?, solidarityFundAdvance? }]`, cada mes dentro del rango y sin
repetir, cada monto `≥ 0` (clave `externalMonths[i].<campo>`). Con `Qep`, `businessDays` y `externalMonths` deben venir vacíos.
Cambiar el origen al editar se permite con las mismas reglas de bloqueo.

`VacationPreviewDto`: `{ businessDays, returnDate, quincenas: [{ startDate, endDate, days }], months: ['yyyy-MM'], paidIn: { startDate, endDate } | null }`.
Con `External`, `businessDays` es el calculado por QEP (como referencia junto al tecleado), `months` son los meses que el diálogo
ofrece para los montos de Siigo, y `paidIn` es `null`.
Existe para que el diálogo muestre «15 días hábiles, regresa el 6 de enero» antes de guardar, sin contar en el navegador (BFF).

`VacationDto`:

```
{ id, employeeId, origin, startDate, endDate, businessDays, returnDate, previousMonthIbc,
  quincenas: [{ startDate, endDate, days }],          // días de nómina; la pantalla no los cuenta
  paidIn: { startDate, endDate, periodId?, status? } | null, // la quincena que paga; null si origin es External
  externalMonths: [{ month, vacationPay, healthAdvance, pensionAdvance, solidarityFundAdvance }], // vacío si origin es Qep
  version,
  can: { update, delete } }                           // falso si alguna quincena que tocan está Aprobada o Cerrada
```

Cambios en DTOs existentes:
- `PayrollEmployeeRowDto` gana `vacationDays: int?` (del resultado, como `workedDays`).
- `PayrollEmployeeDetailDto` gana `vacations: VacationDto[]`, las que tocan el período.
- `PayrollPeriodDetailDto` gana `vacations: [{ employeeId, fullName, startDate, endDate, days }]`, las que tocan el período, para el
  diálogo de liquidar.
- `PayrollTotalsDto` gana `vacationPay` y `vacationDays` sale en la fila. Un backend viejo no los manda: el frontend los lee como 0 o
  `null`.

### Códigos de error

| Código | HTTP | Cuándo |
| --- | --- | --- |
| `payroll.vacation.not_found` | 404 | El id no existe en esa empresa y empleado del tenant. Se lanza como `ResourceNotFoundException` (un `PayrollNotFound.Vacation(id)` nuevo, como `Recurring`); `ApiExceptionHandler.MapException` la responde 404. Un `PayrollDomainException` con ese código sería 422. |
| `payroll.vacation.overlap` | 409 | Se solapa con otras vacaciones del empleado en la empresa. Entra en `PayrollErrorCodes.Conflicts`, y `PayrollConflicts.Run` lo convierte en `ResourceConflictException` (409). |
| `payroll.vacation.starts_on_rest_day` | 422 | Sólo `Qep`: el inicio es día de descanso o festivo. |
| `payroll.vacation.no_business_days` | 422 | Sólo `Qep`: el rango no tiene ningún día hábil. |
| `payroll.vacation.outside_contract` | 422 | El contrato vigente el día del inicio no cubre el día del fin (o no hay contrato ese día). |
| `payroll.vacation.before_first_period` | 422 | `Qep`: el inicio cae antes de la primera quincena de la empresa en QEP (esas las pagó Siigo: se registran como `External`). `External`: el fin cae antes de esa quincena (no toca nada de QEP). |
| `payroll.vacation.not_paid_yet` | 422 | Sólo `Qep`. Al liquidar: la quincena que paga unas vacaciones de ésta no está liquidada y al día. |
| `payroll.vacation.paid_period_locked` | 422 | Sólo `Qep`. Al volver a liquidar la que paga: cambiaría lo que leyó una quincena Aprobada o Cerrada. |

Se reutilizan: `payroll.company.not_found`, `payroll.employee.not_found` (404), `payroll.company.inactive`,
`payroll.employee.not_liquidable` (el empleado no es liquidable en la quincena que paga) y `payroll.period.not_editable` (422).
Cada código nuevo va primero a la tabla del plan (regla de `PayrollErrorCodes`).

Si Hr acorta después el contrato y deja días de vacaciones fuera, el empleado queda **bloqueado** con un motivo nuevo de
`BlockerReason`, `VacationOutsideContract`, hasta corregir las vacaciones.

## Calendario de festivos (llamada 4)

Puerto `IPayrollHolidayCalendar` en Application (`HolidaysAsync(firstDay, lastDay)`), con adaptador en Bootstrapper sobre
`ColombianHolidays`. Es el patrón de `ISchedulingHolidayCalendar` y `SupportBusinessCalendarSource`. A diferencia de Agenda, **no
mira el país del tenant**: la nómina es ley colombiana (valores legales, PILA), y una empresa empleadora de QEP es colombiana.

Alternativa: mover `ColombianHolidays` a `BuildingBlocks.Domain`. Es más directo, pero toca Messaging, Scheduling y Support y sus
pruebas en un cambio de nómina. Si un cuarto módulo lo necesita, se mueve entonces.

## Excel

«Resumen de nómina» (`PayrollColumnLayouts`): «Días de vacaciones» después de «Días» y «Vacaciones disfrutadas» después de
«Salario». «Total devengado» las incluye. «Salud», «Pensión» y «FSP» son lo retenido en la colilla, anticipos incluidos. La columna
de provisión «Vacaciones» no cambia de nombre: es la provisión, no el pago. El generador sigue sin calcular: lee el resumen.

El Excel es para **control y conciliación contra Siigo**, no un layout de importación (decisión del owner, 2026-11-23). Las
quincenas tocadas por unas `External` muestran sus días de vacaciones, salario 0 por esos días y los aportes del mes, pero no una
«Vacaciones disfrutadas»: ese pago está en Siigo.

## Auditoría

Outbox (`IPayrollAuditPublisher`), en la misma transacción. Acciones nuevas en `PayrollAuditActions`: `payroll.vacation.created`,
`payroll.vacation.updated`, `payroll.vacation.deleted`, recurso `vacation`. Se guardan nombres de campos, nunca valores
(`origin`, `startDate`, `endDate`, `previousMonthIbc`, `businessDays`, `externalMonths`). Las quincenas que quedan desactualizadas dejan su evento de hoy en
`payroll.period_events`.

## Cómo se prueba

TDD estricto. Casos de oro nuevos, uno por regla:

- `vacation-crosses-month-paid-first-half`: los tres períodos del ejemplo (el de enero con valores legales de 2027 sintéticos).
- `vacation-whole-second-half`: 0 días trabajados, sin salario ni auxilio, salud y pensión netas en 0 por el anticipo.
- `vacation-starts-second-half-of-february`: el final absorbe la diferencia de febrero.
- `vacation-ibc-previous-month-from-qep` y `vacation-ibc-fallback-salary`: las fuentes del IBC anterior.
- `vacation-new-year-floor`: salario mínimo, enero entero de vacaciones, el piso del SMMLV nuevo deja diferencia, tope por neto.
- `vacation-external-november-into-december`: origen `External` del 2026-11-23 al 2026-12-04, IBC de noviembre tecleado, con y sin
  anticipo de diciembre tecleado: la primera de diciembre paga salario por los días trabajados, sin `vacation_pay`, y resta el
  anticipo tecleado como término (b).

La liquidación de vacaciones se verifica contra la exportación real de Siigo (Tarea 15 del plan base): Siigo es la nómina oficial y
los números de QEP tienen que coincidir con los suyos.

Pruebas de Application: días hábiles con descansos y festivos (sábado hábil con el default), solapamiento, un solo contrato,
desactualizar Liquidadas y Reabiertas (y no tocar Borradores), «pagada» con `Outdated` o `SourceChanged`, `not_paid_yet`. El
bloqueo se prueba **por huella**: con una quincena Aprobada, una edición que cambia su `InputFingerprint.Of` del empleado da
`payroll.period.not_editable` (y volver a liquidar la que paga, `paid_period_locked`); una que no la cambia (por ejemplo, mover el
fin dentro de una quincena no aprobada) pasa. Integración (Testcontainers) filtrada por nombre mientras se itera.

## Llamadas (mías; el owner puede cambiarlas)

1. **Sin saldo de días causados.** QEP no tiene historia antes de diciembre: el saldo habría que teclearlo. Se muestran los días
   hábiles registrados, pero no se validan contra un saldo. El saldo es del proyecto 2.
2. **Se pagan días de nómina del rango, no sólo los hábiles.** Con salario fijo el total es el mismo. Se verifica contra la
   exportación real de Siigo (Tarea 15).
3. **Anticipo de la seguridad social del trabajador** en la quincena que paga, con tope por neto en las quincenas sin devengo. Es la
   consecuencia del modo A: sin él, enero deja netos negativos.
4. **Festivos por puerto con adaptador en Bootstrapper**, siempre de Colombia.
5. **Provisiones:** el pago no entra en ninguna base; los días de vacaciones de la quincena sí, como tiempo de servicio.
6. **Confirmación de ausencias:** se mantiene y cambia el texto. Las vacaciones registradas cuentan como declaradas por sus días.
7. **El IBC anterior** es el del mes anterior al mes del inicio: de QEP si sus dos quincenas tienen resultado (Liquidada o más); si
   no, del valor tecleado; si tampoco, del salario (decisión de la sesión).
8. **El inicio debe ser día hábil** en unas `Qep`; el fin es libre (con o sin el fin de semana de cierre).
9. **Parafiscales por mes de causación**, no por mes de pago, igual que la seguridad social y la PILA.
10. **Bloqueo por huella, desactualizar con `MarkOutdated` de hoy, «pagada» sin `Outdated` ni `SourceChanged`, un solo contrato y
   anticipo guardado por mes** (decisiones de la sesión; ver sus secciones).
11. **Origen `External`** para lo que pagó Siigo: valores tecleados (IBC anterior obligatorio, días hábiles, y por mes lo pagado y
   lo retenido por adelantado, 0 si no se teclea), sin quincena que paga ni sus chequeos; el valor por día sale de lo pagado si se
   tecleó, si no del salario del inicio (decisiones de la sesión).

## Preguntas para el owner (cambian lo que se construye)

Ninguna abierta. Resueltas:

- Una segunda quincena entera de vacaciones con piso nuevo del SMMLV deja unos pesos sin retener: se aceptó no retenerlos y
  dejarlo en la nota de la línea («Tope por neto en la posterior»).
- Vacaciones de noviembre pagadas en Siigo que siguen en diciembre: existen al arrancar y se registran con origen `External`
  (owner, 2026-11-23: Siigo es la nómina oficial, QEP la replica para control y no se importa nada entre los dos).

## Lo que queda al implementador

Nombres de tablas e índices, la forma exacta de la foto de vacaciones en el resultado (columna JSON o tabla hija), handlers, el
orden de los campos en los DTOs y el detalle de los casos de oro además de los listados.

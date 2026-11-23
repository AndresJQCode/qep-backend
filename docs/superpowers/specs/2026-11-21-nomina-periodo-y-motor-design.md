# Nómina, proyecto 1: período quincenal, motor de liquidación y Excel

Fecha: 2026-11-21. Estado: borrador para aprobación del owner; sin código.
Repos: `qep-backend` (este documento) y `qep-frontend` (`docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md`,
pantallas). El contrato HTTP exacto vivirá en el plan de backend, sección «Contrato HTTP», como en la fase 1.

Este es el **primero de tres proyectos** de la fase 2 de RRHH (nómina): 1) período y motor base (este), 2) novedades de
ausencia (incapacidades, licencias, vacaciones), 3) retención en la fuente y préstamos. La plataforma reemplaza a Siigo
sólo cuando estén los tres; cada uno se diseña, se implementa y se mergea por separado.

## Fuente

La lógica de negocio sale de `lulo-erp/v2/docs`: la investigación de Aleluya (`research/02`, secciones 1 a 4, 7, 10, 11 y 12)
y el openspec `lulo-nomina-platform`, sobre todo `specs/payroll-period-lifecycle`, `specs/payroll-calculation-engine`,
`specs/legal-parameters` y `specs/payroll-outputs`. **De lulo-erp no se toma código, modelo, nombres ni patrones** (decisión
del owner, 2026-11-20); la implementación sigue los patrones de QEP. Donde la documentación y la fase 1 de QEP difieren en
datos del empleado, manda la fase 1.

## Intención

QCode liquida hoy la nómina en Siigo. Quiere **liquidarla en QEP, cada quincena, y exportar el resultado a Excel**; lo que
Siigo haga con ese archivo no es asunto de QEP, y su formato no se copia. Después, vender el módulo a los tenants de QEP.

El corazón del proyecto es que **los números salgan bien**: el cálculo legal colombiano, con valores por fecha, y una
explicación de cada línea que alguien pueda auditar.

## Objetivos, no objetivos, antiobjetivos

**Objetivos**
- Abrir, liquidar, revisar, aprobar, cerrar y reabrir una quincena por empresa empleadora.
- Calcular, por empleado, devengados, deducciones, aportes del empleador, provisiones, neto y costo empresa.
- Explicar cada línea (concepto, fórmula, datos, resultado, valor legal usado y su fuente).
- Exportar a Excel; el formato es reemplazable sin tocar el cálculo.
- Capturar lo variable de la quincena por empleado y por plantilla masiva de Excel.

**No objetivos (este proyecto)**
- Incapacidades, licencias y vacaciones (proyecto 2); retención en la fuente y préstamos (proyecto 3).
- Salario integral, medio tiempo, tiempo parcial, aprendices, estudiantes, contratistas, pensionados y extranjeros exentos de
  pensión; subtipo de cotizante distinto de estándar.
- Periodicidad mensual (el modelo la deja abierta), períodos históricos, liquidación final del contrato, prima y cesantías
  como pago, PILA, nómina electrónica, archivos de banco, interfaz contable, colillas en PDF, autoservicio del empleado.
- Pantalla de operador para cargar valores legales (se cargan por migración, como el SMMLV en la fase 1).

**Antiobjetivos** (fallan aunque «funcione»)
- Un número mal calculado sin aviso: lo que el motor no sabe calcular **no se liquida** y se dice por qué.
- Que cambiar un valor legal cambie un período ya cerrado.
- Que el navegador calcule, redondee o sume algo que el backend ya tiene.
- Que datos de nómina se crucen entre tenants.

## Enfoques

- **A. Módulo nuevo `payroll` junto a `hr` (elegido por el owner).** Depende de `hr`, lee empleados y contratos por un puerto,
  tiene permisos propios y clave de módulo propia: se activa y se vende aparte.
- B. Dentro de `hr`: menos cableado, pero `hr` crece varias veces, mezcla permisos y no se vende sola.
- C. Servicio aparte: descartado; el período, las provisiones y el Excel necesitan consistencia transaccional.

Decisiones mías, con la documentación de Lulo como respaldo: el motor es una **función pura** (sin base de datos ni reloj,
misma entrada ⇒ misma salida) y cada liquidación se guarda como **foto fija** (líneas, rastro, valores legales usados y
versión del motor), de modo que un período viejo nunca se recalcula distinto.

## A quién se liquida

Se liquida a un empleado cuando, en el período, su empresa empleadora es la del período, tiene un contrato que cubre al menos
un día del período y es: tipo **Empleado**, salario **Mensual**, subtipo de cotizante **estándar**, no exento de pensión. La
duración (indefinida, fija, obra o labor) no cambia el cálculo.

Tampoco se liquida (y se dice por qué) a quien tiene un **salario menor al SMMLV** o **dos o más contratos de la empresa en el
período** (uno termina y otro empieza dentro de la quincena): el piso del IBC y el reparto de tramos de otro contrato son reglas de otro proyecto.

Los demás **no se liquidan** y aparecen con el motivo en el validador previo; no bloquean el período. Un empleado liquidable
al que le falta algo que el cálculo necesita (centro de trabajo, clase de riesgo, salario vigente) **sí bloquea** hasta
corregirlo. Un contrato que termina dentro del período se liquida por los días trabajados y queda marcado «liquidación final
pendiente»: esa liquidación no es de este proyecto.

## Módulo y permisos

- Módulo `Modules.Payroll` (Domain, Application, Infrastructure, Api), esquema `payroll`, con su `PayrollLayerTests.cs`.
- Clave de tenant `TenantModuleKey.Payroll = "payroll"`, con su migración del `CK_tenant_modules_module_key`. **Depende de
  `hr`** (que depende de `companies`).
- Payroll lee empleados, contratos, salarios con vigencia, cargo, sede, centro de trabajo (clase ARL, alto riesgo de pensión)
  y caja por un **puerto propio hacia Hr**, con adaptador en el Bootstrapper; no comparte tabla ni referencia el módulo para
  leer. Los valores legales siguen siendo de Hr (la tabla de la fase 1); Payroll los resuelve por otro puerto.
- Permisos (todos sensibles: ven plata, no se enmascaran):
  - `payroll.period.read`: lista y detalle de períodos, rastro de cálculo y exportación a Excel.
  - `payroll.period.manage`: abrir, capturar lo variable, liquidar, cerrar, reabrir.
  - `payroll.period.approve`: aprobar.
  - `payroll.settings.manage`: configuración de nómina por empresa y conceptos recurrentes de cada empleado.
  - Roles por defecto: `admin` con los cuatro; los demás, ninguno.
- Auditoría en el handler. Cada transición de estado deja quién, cuándo, desde y hacia qué estado.

## Valores legales

Se amplía la tabla con fecha de vigencia de la fase 1; se carga por migración, para 2025 y 2026 (2024 no hace falta mientras
no haya períodos históricos). Cada año lleva un conjunto completo, aunque el valor no cambie.

| Valor | 2025 | 2026 | Base legal |
| --- | --- | --- | --- |
| Auxilio de transporte | 200.000 | 249.095 | Ley 15/1959 |
| Salud empleado / empleador | 4 % / 8,5 % | igual | Ley 100/1993 art. 204 |
| Pensión empleado / empleador | 4 % / 12 % | igual | Ley 100/1993 art. 204 |
| Pensión alto riesgo (total) | 26 % | igual | Decreto 2090/2003 |
| ARL por clase I a V | 0,522 / 1,044 / 2,436 / 4,35 / 6,96 % | igual | Decretos 1772/1994 y 1607/2002 |
| Caja / ICBF / SENA | 4 % / 3 % / 2 % | igual | Leyes 21/1982, 89/1988, 119/1994 |
| Provisión cesantías / prima / vacaciones / intereses | 8,33 / 8,33 / 4,17 / 1 % mensual | igual | Ley 50/1990 art. 99, CST art. 306, arts. 186 a 192, Ley 52/1975 |
| FSP, tramos (múltiplos de SMMLV) | 4–16: 1 %; 16–17: 1,2 %; 17–18: 1,4 %; 18–19: 1,6 %; 19–20: 1,8 %; 20–25: 2 % | igual | Ley 100/1993 art. 27 |
| Tope del auxilio de transporte | 2 SMMLV | igual | Ley 15/1959 |
| Tope de no salariales (Ley 1393) | 40 % | igual | Ley 1393/2010 art. 30 |
| Umbral de exoneración | menos de 10 SMMLV | igual | ET art. 114-1 |
| Recargo dominical y festivo | 80 % desde 2025-07-01 | 90 % desde 2026-07-01 (100 % desde 2027-07-01, ya cargado) | Ley 2466/2025 |
| Jornada semanal | 44 h desde 2025-07-15 | 42 h desde 2026-07-15 (47 h y 46 h de 2023 y 2024, ya cargadas) | Ley 2101/2021 art. 3 |
| Extra diurna 25 %, extra nocturna 75 %, recargo nocturno 35 % | igual | igual | CST |

Los tres tipos **dominicales se derivan, no se cargan como constantes**: extra dominical diurna = 1 + 25 % + recargo dominical vigente; extra dominical nocturna = 1 + 75 % + recargo dominical vigente; recargo nocturno dominical = 35 % + recargo dominical vigente. Con el 90 % vigente desde el 1 de julio de 2026 dan 2,15×, 2,65× y 1,25×; con el 80 % de antes darían 2,05×, 2,55× y 1,15×.

El SMMLV y su fecha ya están (fase 1). Los valores se resuelven **a la fecha del período**, nunca «el actual», y el conjunto
usado se guarda en la liquidación. Un valor publicado no se edita: se agrega una fila nueva con otra vigencia.

## Configuración de nómina por empresa

Por empresa empleadora, con estos valores por defecto (decisiones mías, ajustables):

| Interruptor | Opciones | Default |
| --- | --- | --- |
| Frecuencia del auxilio de transporte | quincenal: lo proporcional a los días de cada quincena; mensual: completo en la segunda | quincenal |
| Base para el tope del auxilio | sólo salario; salario más ingresos salariales sin extras; salario más ausencias pagas sin comisiones | sólo salario |
| Base de las horas extras | sólo salario; salario más ingresos salariales del mes (el auxilio nunca entra) | sólo salario |
| Empresa exonerada (ET art. 114-1) | sí / no | sí |
| Doble control en la aprobación | sí / no | no |

Sin doble control, quien liquida puede aprobar (una empresa de una sola persona no podría aprobar nada). Con él, **quien
liquidó por última vez no puede aprobar** el período, aunque lo haya reabierto él mismo. La periodicidad es quincenal y no
se configura. Los permisos de nómina valen para **todas las empresas del tenant**: QEP no tiene permisos por empresa.

**Ausencias.** Hasta el proyecto 2 el sistema no conoce incapacidades, licencias ni vacaciones. Para no pagar de más sin
aviso, **liquidar exige confirmar** que ningún empleado tuvo ausencias esa quincena; la confirmación queda en la auditoría.

## Período

- **Uno por empresa empleadora y quincena**: del 1 al 15 y del 16 al fin de mes, en el día del tenant (no UTC). Se crea sin
  que nadie lo pida cuando se abre la pantalla de nómina de la empresa (una operación idempotente; sin proceso programado).
  No se pueden saltar quincenas hacia atrás: la primera que se abre de una empresa es la actual, y **debe ser una primera
  quincena (del 1 al 15)**: la segunda necesita lo retenido en la primera, y lo que liquidó Siigo no está en QEP.
- **Dependencia dentro del mes.** La segunda quincena lee de la primera de la misma empresa lo ya retenido y devengado en el
  mes. Si se **reabre** o se vuelve a liquidar la primera mientras la segunda ya existe, la segunda queda **desactualizada**.
- **Estados:** *Borrador → Liquidado → Aprobado → Cerrado*. «Reabrir» (con motivo, auditada) lleva un período Aprobado o
  Cerrado a *Reabierto*, se corrige y se vuelve a liquidar. «Pagado» de la documentación no aplica: QEP no paga.
- Capturar o cambiar cualquier dato de un período Liquidado lo marca **desactualizado**: no se puede aprobar hasta
  liquidar de nuevo. Un período Aprobado o Cerrado no recibe capturas; para cambiarlo se reabre.
- **No se borra** un período. Los valores legales del período se fijan al liquidar por primera vez.
- Una versión por período, con `If-Match` en las escrituras, como la fase 1.

## Lo que se captura por empleado y período

Todo lo variable de la quincena se registra por empleado, o con una plantilla de Excel masiva (todo o nada, vista previa
y confirmar, como la importación de empleados):

- **Horas extras y recargos:** cantidad de horas por tipo (los siete predefinidos y los personalizados que cree la
  empresa con nombre y multiplicador).
- **Ingresos adicionales:** concepto, valor y si es **constitutivo de salario** o no. Predefinidos: comisiones, bonificación
  prestacional, anticipo de salario, otros ingresos; los no constitutivos pueden ser recurrentes u ocasionales. La empresa
  crea conceptos propios.
- **Deducciones puntuales:** concepto y valor.

**Conceptos recurrentes** (ficha del empleado, con `payroll.settings.manage`): ingreso o deducción, **fijo** (el mismo valor
cada quincena) o **proporcional** (a los días trabajados), con **tope máximo** acumulado: al llegarse al tope el concepto se
inactiva solo. Pueden activarse y desactivarse.

## Cálculo

Todo se redondea como manda la ley: el **IBC siempre hacia arriba al peso**, y **cada aporte del empleador y el fondo de
solidaridad hacia arriba al siguiente múltiplo de 100** (nunca al más cercano). **Salud y pensión del trabajador se descuentan
al peso** (mitad hacia arriba), como Siigo: la centena del Decreto 1990/2016 es de la cotización en la PILA, que asume el
empleador, no del descuento de nómina (decisión del owner, 2026-11-23; motor 2026.11.4). Cada línea del resultado lleva su rastro:
concepto, fórmula simbólica, datos de entrada, resultado, valor legal usado y su base legal («Ver cálculos»).

**Días.** El mes es de 30 días. Una quincena completa trabajada son **15 días**, también la segunda de un mes de 31 días o de
febrero. En el mes de ingreso o de retiro se cuentan los **días reales** del calendario trabajados dentro de la quincena
(máximo 15). Si el salario cambia dentro del período, se parte en tramos y cada tramo se paga con su salario.

**Fecha de los valores legales.** Todos se resuelven al **primer día del período**. La jornada de la Ley 2101 cambia el 15 de
julio, así que la primera quincena de julio usa la jornada anterior; es una aproximación aceptada, porque las horas se
capturan por total de la quincena y no día por día.

**Salario.** `salario mensual ÷ 30 × días trabajados`. Los **cambios de cargo y de lugar de trabajo sólo rigen desde el día 1
de un mes** (regla de la fase 1), así que dentro de una quincena el centro de trabajo, su clase de riesgo ARL y su alto
riesgo de pensión son constantes; sólo el salario se parte en tramos.

**Ingreso del mes.** Varias reglas se miden sobre el mes: tope del auxilio, regla del 40 %, FSP y exoneración. Se define así:
`ingreso salarial del mes = salario mensual completo vigente + ingresos salariales capturados en el mes hasta este período
inclusive`. Se usa el salario **completo**, no el devengado hasta ahora ni uno proyectado: así la primera quincena decide
igual que la segunda. Ejemplo: salario 3.000.000 y una comisión salarial de 500.000 en la primera quincena ⇒ ingreso del mes
3.500.000, que es menos de 2 SMMLV de 2026 (3.501.810) ⇒ conserva el auxilio.

**Auxilio de transporte.** Se concede solo si el ingreso salarial del mes (según la base configurada) es **hasta 2 SMMLV**, y
se quita solo cuando lo supera. Se paga según la frecuencia configurada, proporcional a los días. La primera quincena es
provisional y la segunda consolida el mes: si el mes supera el tope, la segunda paga 0 y no descuenta lo ya pagado (CST art.
149); si el mes queda por debajo, paga el mes completo menos lo pagado. Las horas extras y los recargos no cuentan para el tope.
Con frecuencia mensual, si el contrato termina en la primera quincena, el auxilio se paga en ella. (Decisiones del owner 2 y 3,
2026-11-22.) Si el salario vigente es de
teletrabajador, el **auxilio de conectividad lo reemplaza** (nunca los dos), por el **mismo valor** que el de transporte
(Ley 2121/2021, a confirmar con el caso real): ver «Llamadas y pendientes».

**Horas extras.** `valor hora = salario ÷ 30 ÷ (jornada semanal ÷ 6)`, con el **salario vigente al último día del período** y
la jornada vigente al primer día (42 h ⇒ divisor 210). Cada tipo multiplica el valor hora por su porcentaje de la tabla. La
base es la configurada; el auxilio nunca entra en ella. **Los topes legales de horas extras (2 diarias, 12 semanales) no se
validan**: el sistema paga lo que se captura.

**Ingresos.** Los constitutivos de salario se suman a la base salarial; los no constitutivos, no.

**IBC y regla del 40 %.** El IBC se calcula **sobre el mes acumulado hasta este período**: `IBC = salario + ingreso salarial +
no salarial − 40 % × (todo lo anterior)`, **sólo cuando los no salariales superan ese 40 %**; sin no salariales, el IBC es el
devengo salarial. Quedan fuera del IBC el auxilio de transporte y, en el proyecto 2, las vacaciones. Tiene **piso de
SMMLV ÷ 30 × días cotizados del mes** y **tope de 25 SMMLV** (Ley 797/2003, art. 5).

**Deducciones del empleado.** Salud 4 % y pensión 4 %, **acumuladas del mes menos lo ya retenido** en la quincena anterior
del mes: la primera quincena retiene sobre su propio IBC y la segunda ajusta la diferencia. **Fondo de Solidaridad
Pensional** cuando el IBC del mes llega a 4 SMMLV o más (el límite inferior de cada tramo se incluye), por tramos, a cargo del empleado: la primera quincena retiene sólo si el
salario base solo llega a 4 SMMLV o más; la **segunda calcula el IBC del mes y retiene la diferencia**, incluida una devolución si
sobró. Las deducciones recurrentes y puntuales se restan; **no puede quedar un neto negativo** (se avisa y se bloquea la
aprobación).

**Aportes del empleador.** Salud 8,5 %, pensión 12 % (22 % en un centro de alto riesgo: el 26 % total menos el 4 % del empleado), caja 4 %, ICBF 3 %, SENA 2 %, y ARL
por la clase del **centro de trabajo**, sobre el IBC de seguridad social; caja, ICBF y SENA sobre el devengo salarial del mes,
sin la regla del 40 % (decisión del owner 1, 2026-11-22). **Exoneración:** si la empresa es
exonerada y el devengo salarial del empleado es menor de 10 SMMLV, no se pagan salud del empleador, ICBF ni SENA; la caja
siempre se paga. La segunda quincena la decide sobre el mes; si el mes queda exonerado, devuelve lo aportado en la primera del
lado del empleador (decisión del owner 2).

**Provisiones del período.** Cesantías 8,33 %, prima 8,33 %, vacaciones 4,17 % e intereses a las cesantías 1 %, cada una sobre
su base legal (cesantías y prima incluyen el auxilio de transporte; vacaciones no), proporcionales al período.

**Resultado por empleado.** `neto = devengado − deducciones`; `costo empresa = devengado + aportes del empleador +
provisiones`. Los totales por período suman las líneas; el navegador nunca suma.

## Excel

«Resumen de nómina» por período: una hoja, **una fila por empleado**, una fila de totales y un encabezado con empresa,
período, estado y fecha de generación. Columnas iniciales: documento, nombre, cargo, centro de trabajo, días, salario,
auxilio, horas extras y recargos, otros ingresos salariales, ingresos no salariales, total devengado, salud, pensión, FSP,
otras deducciones, total deducciones, neto, aportes del empleador (salud, pensión, ARL, caja, ICBF, SENA), provisiones
(cesantías, intereses, prima, vacaciones) y costo empresa.

La salida está separada del cálculo: el generador recibe el resultado ya liquidado y un **diseño de columnas**. El formato
que pase el owner entra como otro diseño. Se exporta desde *Liquidado* en adelante y el archivo dice el estado del período;
exige `payroll.period.read`. Los empleados no liquidados se listan en una segunda hoja con su motivo.

## Lo que no debe pasar

- Que un empleado no soportado se liquide con reglas que no le corresponden.
- Que un valor legal nuevo cambie un período Aprobado o Cerrado (se verifica con una comparación de la foto de valores).
- Que el navegador calcule días, auxilio, aportes, redondeos, neto o totales.
- Que alguien sin `payroll.period.read` vea una liquidación, un rastro o el Excel.
- Que dos usuarios liquiden o cambien el mismo período a la vez sin que el segundo se entere (`If-Match`).
- Que se pueda aprobar un período desactualizado o con errores que bloquean.
- Que los datos de una empresa o un tenant aparezcan en la nómina de otro.

## Cómo se prueba

TDD estricto, con evidencia de RED y GREEN, como en todo QEP. El motor se prueba con **casos de oro**: un archivo JSON
versionado por caso (la entrada completa y el resultado esperado línea por línea) que se compara contra el rastro. Cada
regla de este documento tiene al menos un caso. **El owner aporta una quincena real ya liquidada en Siigo** (entradas y
resultado); es el caso de oro de mayor valor y el criterio de que el proyecto está bien. Rendimiento: 500 empleados en menos
de 10 segundos de motor, sin contar lecturas ni escrituras. Las pruebas de integración corren por proyecto, de a una, con
las reglas del workspace.

## Llamadas y pendientes (mías, para que el owner las revise)

1. **Días del mes de ingreso o retiro.** Se toma «días reales» (Aleluya, R02 §1). Se confirma con la quincena real de Siigo.
2. **Teletrabajador y conectividad.** La fase 1 guarda «teletrabajador» sí/no, pero no «aplicar auxilio de conectividad».
   Asumo que un salario vigente de teletrabajador con fecha del 25 de junio de 2025 en adelante recibe conectividad en vez de
   transporte, por el mismo valor. La fecha es la del **registro de salario vigente**, no la del período. Si no es así, hay
   que sumar el campo al salario.
3. **Empresa exonerada por defecto: sí.** Aleluya no documenta la etiqueta exacta del interruptor.
4. **Formato del Excel.** El inicial es el «Resumen de nómina» de la documentación; el definitivo lo pasa el owner.
5. **Arranque en una primera quincena.** QEP no conoce lo que Siigo ya retuvo, así que la primera quincena que se abre de
   una empresa tiene que ser una primera. Si el owner necesita arrancar a mitad de mes, hay que cargar por empleado lo
   retenido antes; no está diseñado.
6. **Reglas del mes acumulado** (IBC, 40 %, FSP, salud y pensión de la segunda quincena, piso y tope del IBC): son mi lectura
   de la documentación y de la ley. **No son definitivas hasta pasar los casos de oro de una quincena real de Siigo.**
7. **Estructura de los valores legales.** Hoy la tabla de la fase 1 guarda un valor escalar por clave. Los tramos de FSP, las
   clases ARL y los porcentajes con vigencia piden una estructura de listas; la forma queda al implementador, pero sigue
   siendo de Hr y llega a Payroll por un puerto.
8. **Rastro y foto fija crecen**: cientos de líneas por empleado y período. Se acepta a este volumen; revisar al pasar de
   miles de empleados.

## Lo que queda al implementador

Nombres de tablas, índices, handlers y DTOs; el diseño exacto de la plantilla masiva; el tamaño de página; el orden de las
columnas internas; cómo se versiona el motor; el detalle de los casos de oro además de los de la quincena real.

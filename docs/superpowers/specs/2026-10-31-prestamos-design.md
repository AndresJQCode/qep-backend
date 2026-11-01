# Préstamos: control del dinero prestado por el tenant

Fecha: 2026-10-31. Estado: diseño pendiente de aprobación. Sin código.
Repos: `qep-backend` y `qep-frontend`. Maqueta: pantallas por cobrar, préstamos, detalle, marcar
como pagado, abonar a capital y nuevo préstamo, con el lenguaje visual de Contratos de servicio.

## Intención

Hoy QCode lleva en un Excel lo que le prestó a Novasteel: capital prestado sin plazo, 1,5 % de
interés mensual, y sólo paga intereses. El módulo reemplaza ese Excel y funciona como Contratos de
servicio: el sistema genera solo la cuota de cada período y un usuario la marca como pagada.

## Objetivos

- Registrar préstamos de tipo "sólo interés" a un cliente, con tasa y corte propios de cada uno.
- Generar en cada corte una cuota con el interés del período, calculado por días.
- Marcar cuotas como pagadas, igual que un cobro de Contratos de servicio.
- Registrar abonos a capital, parciales o totales, como acción aparte.
- Ver cuánto capital falta, qué cuotas están pendientes y cómo se calculó cada una.
- Dejar el modelo listo para sumar después el tipo "interés y capital".

## Fuera de alcance

- El tipo "interés y capital" y sus plazos. Sólo se deja el campo `type`.
- Importación desde Excel en la app. La historia de Novasteel se carga por SQL (ver "Carga inicial").
- Pagos parciales de una cuota: una cuota se paga completa.
- Aumentar el capital o cambiar la tasa de un préstamo con movimiento: decisión del owner, cada uno
  de esos casos es un préstamo nuevo.
- Avisos al deudor por WhatsApp o correo, mora, y cualquier interés sobre interés.
- Contabilidad, facturación del interés y retenciones.
- Validar topes legales de tasa, como usura.
- Préstamos que el tenant recibe: sólo se controla el dinero que le deben.

## Lo que no debe pasar

- Que la pantalla muestre una cifra que no cuadre con el detalle del cálculo.
- Que el navegador calcule interés o totales. Todo lo calcula el backend.
- Que cambie el monto de una cuota ya generada sin una acción explícita del usuario.
- Que un módulo nuevo se vea distinto a Contratos de servicio.

## Reglas de la cuota

1. **Interés simple.** Una cuota sin pagar queda pendiente, sin mora y sin generar interés. No existe
   el estado "vencida": la cuota está pendiente o pagada.
2. **Por días.** Interés de un tramo = capital × tasa mensual × días del tramo ÷ días del período.
3. **Tramos.** Un período se parte en tramos cada vez que cambia el capital. El capital baja desde el
   día del abono inclusive: con abono el 15, el 14 es el último día con el capital anterior.
4. **Ejemplo.** Octubre (31 días), 1,5 %, capital 100.000.000, abono de 40.000.000 el día 15:
   14 días sobre 100.000.000 = 677.419,35, más 17 días sobre 60.000.000 = 493.548,39.
   Cuota: 1.170.967,74.
5. **Redondeo.** Cada tramo se redondea a 2 decimales y la cuota es la suma de sus tramos, de modo
   que el detalle siempre cuadra con el total.
6. **Corte.** Cada préstamo elige al crearse "último día del mes" o "día del desembolso". Con "día
   del desembolso", cada período empieza el día del desembolso y termina el día anterior al inicio
   siguiente: un préstamo del 12 va del 12 al 11. Si ese día no existe en el mes, el inicio se
   ajusta al último día: un préstamo del 31 de enero va del 31 ene al 27 feb y del 28 feb al 30 mar.
7. **Primer período.** Si el desembolso no coincide con el inicio de un período, el primer período
   es proporcional: el día del desembolso cuenta y el divisor son los días del período completo.
8. **Generación.** La cuota se genera el día siguiente al corte, no antes: recién ahí se conoce el
   monto, y un abono del último día del período todavía cabe en él. Queda fija: un abono posterior
   no la cambia.
9. **Mes en curso.** El interés del período abierto se muestra como "causado a hoy" y no se debe
   hasta que se genere la cuota.

## Reglas del pago de una cuota

1. "Marcar como pagado" pide fecha y nota, como `PayChargeDialog`, y paga la cuota completa.
2. La fecha no puede ser futura, en la zona horaria del tenant.
3. "Corregir" cambia fecha o nota del pago, y permite quitarlo para que la cuota vuelva a pendiente.
4. El orden de pago es libre: se puede marcar cualquier cuota pendiente, como en Contratos.

## Reglas del abono a capital

1. Es una acción aparte, con monto, fecha y nota. El capital sólo baja por esta vía.
2. Exige que el préstamo no tenga cuotas pendientes: así se respeta "primero intereses". Si las
   tiene, el diálogo lo dice y no ofrece confirmar.
3. La fecha debe caer dentro del período abierto: desde el inicio del período abierto hasta hoy. Un
   abono en un período ya cerrado cambiaría una cuota fija, y eso no se hace.
4. El monto no puede superar el capital pendiente.
5. **Saldar.** Si el abono lleva el capital a cero, el período se cierra en la fecha del abono y se
   genera de inmediato la última cuota, con el interés causado hasta el día anterior. El diálogo lo
   anuncia con su monto.
6. Se puede anular el abono más reciente mientras no se haya generado una cuota después de él.
   La anulación exige motivo, deja auditoría y no borra la fila.
7. Un préstamo pasa a inactivo cuando el capital está en cero y no quedan cuotas pendientes.
   Anular el abono que lo saldó no aplica: ya generó una cuota.

## Backend

Módulo nuevo `Loans` en `qep-backend`, hermano de `ServiceContracts`, con su mismo patrón. No se
extienden `ServiceContract` ni `Charge` porque no tienen capital, tasa, abonos ni monto variable,
y su generación es anticipada: hasta 5 días antes del vencimiento.

**Entidades**

- `Loan`: `Id`, `TenantId`, `CustomerId`, `Name`, `Type` (`InterestOnly`), `PrincipalAmount`,
  `Currency`, `MonthlyRatePercent`, `DisbursedOn`, `CutoffRule` (`EndOfMonth` | `DisbursementDay`),
  `AssigneeMemberId` opcional, `Note` opcional, `Status` (`Active` | `Settled`), `Version`.
  El capital pendiente y el fin del último período generado se guardan en el préstamo.
- `LoanInstallment` (cuota): `PeriodStart`, `PeriodEnd`, `Amount`, `Currency`, `Status`
  (`Pending` | `Paid`), `PaidOn`, `PaymentNote`, y `IsImported`.
- `LoanInstallmentSegment` (tramo): inicio, fin, capital, días, interés.
- `LoanCapitalPayment` (abono): `Amount`, `PaidOn`, `Note`, `BalanceAfter`, `VoidedAt`, `VoidReason`.

Dinero en `numeric(18,2)` más moneda de 3 letras, como `ServiceContracts`. Tasa en `numeric(7,4)`,
mayor que 0 y hasta 100. Índice único por `(LoanId, PeriodStart)` para que generar sea idempotente.

**Deudor.** Sólo `Customers`, por `CustomerId` validado activo al crear, con la misma forma de
lookup que `IServiceContractsCustomerLookup`. `Customer` ya cubre persona natural y jurídica.
Novasteel debe existir como cliente. Hay que registrar `Loan` en `CustomerMergeReferences.cs`.

**Generación.** Un worker diario por tenant, con el patrón de reclamo de
`ServiceContractsReminderProcessor`, genera las cuotas de los períodos cerrados con el cálculo por
tramos, que es una función pura. Si un período quedó sin generar por una caída, lo genera después
con su fecha real. No envía avisos.

**Endpoints**, bajo `/api/v1/tenants/{tenantId}/loans` (abreviado `/loans`), con `X-Tenant-Id`. El contrato exacto, que incluye `GET /loans/assignees`, está en el plan del backend:

| Método y ruta | Uso |
| --- | --- |
| `GET /loans?status&search&page&pageSize` | Lista y resumen por moneda |
| `POST /loans` | Crear |
| `GET /loans/{id}` | Detalle: condiciones, cifras, cuotas, tramos y abonos |
| `PUT /loans/{id}` | Editar, con `If-Match` |
| `DELETE /loans/{id}` | Eliminar, sólo sin cuotas pagadas ni abonos |
| `GET /loans/installments?search&page&pageSize` | "Por cobrar": cuotas pendientes y resumen por moneda |
| `POST /loans/{id}/installments/{installmentId}/pay` | Marcar como pagado |
| `PUT /loans/{id}/installments/{installmentId}/payment` | Corregir el pago |
| `DELETE /loans/{id}/installments/{installmentId}/payment` | Quitar el pago |
| `POST /loans/{id}/capital-payments/preview` | Capital resultante y última cuota, sin escribir |
| `POST /loans/{id}/capital-payments` | Abonar a capital |
| `POST /loans/{id}/capital-payments/{paymentId}/void` | Anular el abono más reciente |

`Editar`: mientras no haya cuotas generadas ni abonos se pueden cambiar todas las condiciones.
Después, sólo nombre, encargado y nota.

Los errores usan códigos `loans.*` y se mapean en `ApiExceptionHandler.cs`.

**Módulo y permisos.** Clave de tenant `loans` en `TenantModuleKey`, con su migración del `CHECK`.
Permisos `loans.loan.read` y `loans.loan.manage`. Se activa por tenant, como `billing`.

**Tocar al agregar el módulo**: `Backend.slnx`, `Api.csproj`, `Bootstrapper.csproj`, `Program.cs`,
`QepServiceCollectionExtensions.cs` (infra, handlers, roles, `PermissionDefinition`, policies),
`TenantModuleKey.cs`, `CustomerMergeReferences.cs`, `ArchitectureTests` con `LoansLayerTests.cs` y
`CompositionRootTests.cs`. Auditoría en el handler, como dicta `SDD-ADR-05`.

## Frontend

Feature `src/features/loans/`, con el árbol canónico. Rutas `/loans` (Por cobrar),
`/loans/list`, `/loans/new` y `/loans/$loanId` bajo `_authenticated`, con `ModuleGate` para `loans`.
En el sidebar, grupo "Préstamos" con "Por cobrar" y "Préstamos", igual que Contratos de servicio.
Se registra en `tenant-modules.ts`, `test/operator.ts` y `test/permissions.ts`.

**Homologación.** Lo que hoy está en `features/service-contracts` y Préstamos también consume, sale
a `src/components/` en vez de copiarse: `StatBand`, `StatCell`, `CurrencyLines` y `CustomerPicker`,
más el encabezado y los estados de pantalla si aplican. Es la regla de Screaming Architecture: dos
consumidores.

**Pantallas**, como en la maqueta:

- **Por cobrar.** Banda con cuotas pendientes y la más antigua, búsqueda, tabla con "Marcar como
  pagado" por fila y paginación. Mismo esquema que `ChargesPage`.
- **Préstamos.** Banda de 3 cifras, tabs Activos e Inactivos con el `Tabs` de la app, búsqueda por
  cliente, tabla y paginación.
- **Detalle.** Banda con capital pendiente, por cobrar e interés de este mes; tabla de cuotas con
  tramos desplegables; tabla de abonos; tarjeta de condiciones.
- **Marcar como pagado.** Diálogo igual a `PayChargeDialog`.
- **Abonar a capital.** Diálogo con monto, fecha, nota y lo que devuelve `preview`. Con cuotas
  pendientes muestra el aviso en vez del formulario.
- **Nuevo préstamo.** Formulario con cliente, nombre, capital, moneda, tasa, desembolso, corte,
  encargado y nota. No se muestra selector de tipo: hay uno solo.

Todo con carga, vacío, error y éxito. Sin `loans.loan.manage` las acciones se ocultan, no se
deshabilitan. Copy en español neutro con tuteo. El estado se muestra como "Activo" e "Inactivo".

Los valores usan `formatMoney` con `centsWhenPresent`: COP tiene 0 decimales en el catálogo y sin
esa opción los tramos dejarían de sumar a la vista. Los totales nunca se mezclan entre monedas.

## Carga inicial de Novasteel

No hay pantalla de importación. Cuando el owner entregue el Excel se escribe un script SQL que
inserta el `Loan`, sus cuotas con el monto del Excel (`IsImported`, sin tramos) y sus abonos. Las
cuotas guardan su monto, así que el Excel manda sobre el pasado y las reglas de este diseño rigen
desde el primer período generado por el sistema. El detalle muestra esas cuotas sin desglose.

## Despliegue y pruebas

Primero backend y migración, después frontend. Con TDD: el cálculo se cubre con casos tabulados,
incluidos el ejemplo de octubre, primer período parcial, febrero, desembolso un día 31 y saldar.
Integración con Testcontainers para endpoints y worker, incluida la idempotencia de la generación.

## Decisiones tomadas por el diseño

Estas no las pidió el owner; son criterio del diseño y quedan para su revisión:

- Fórmula de tramos, redondeo por tramo y que el capital baje el mismo día del abono.
- Primer período proporcional.
- Sin estado "vencida", porque no hay mora.
- Abono sólo con fecha dentro del período abierto.
- Saldar genera de inmediato la última cuota.
- Anulación sólo del abono más reciente.
- Editar condiciones y eliminar, sólo antes de que haya movimiento.
- Nombres de permisos, clave de módulo, rutas y extracción de componentes compartidos.

## Lo que decide quien construya

Estructura interna de handlers, forma exacta de los DTO, frecuencia del barrido del worker,
tamaños de página y orden de las migraciones.

# Nómina: abrir la quincena siguiente por adelantado

Estado: aprobado por el owner (2026-11-22, opción A). Cambio chico; backend y frontend.

## Por qué

Hoy sólo se abre la quincena que contiene el día del tenant (`PeriodPlan.Missing`, R-13 del spec
`2026-11-21-nomina-periodo-y-motor-design.md`), y una empresa sin quincenas sólo abre la primera si hoy cae en una primera
quincena: el 22 de noviembre la pantalla dice «La nómina de esta empresa empieza el 2026-12-01». El owner quiere liquidar
**antes** de que empiece la quincena: arrancar diciembre desde noviembre, y en general liquidar unos días antes de cada pago.
Lo único que depende de «hoy» es abrir; liquidar una quincena abierta no mira la fecha.

## Enfoques

- **A (elegido por el owner):** acción explícita «Abrir la quincena siguiente», como máximo **una** quincena por delante de la
  actual. La apertura automática de la actual no cambia.
- B: abrir siempre la siguiente de forma automática. Descartado: toda empresa vería una quincena futura abierta, la use o no.

## Contrato

`POST /api/v1/payroll/companies/{companyId}/periods/open-next`, sin cuerpo, mismo grupo y mismas reglas que `ensure-current`:
módulo `payroll`, `PeriodRead` **y** `PeriodManage`, empresa inactiva ⇒ 422 `payroll.company.inactive`. Responde
`PayrollPeriodListDto` (200), igual que `ensure-current`.

Con `current = QuincenaCalendar.Containing(today)` (día del tenant) y `target = QuincenaCalendar.After(current.Start)`:

- Sin quincenas: abre desde `current` si es primera quincena, si no desde `target` (una empresa siempre arranca en una primera
  quincena), hasta `target` inclusive.
- Con quincenas: abre todas las que faltan desde la siguiente a la última hasta `target` inclusive (sin huecos, como
  `ensure-current`).
- Nada que abrir (ya existe `target`): **idempotente**, responde la lista sin cambios. No hay código de error nuevo.
- Nunca abre más allá de `target`.
- Misma carrera que `ensure-current`: el índice único `IX_periods_tenant_company_start` resuelve dos solicitudes a la vez
  (la perdedora relee). Misma auditoría `PeriodCreated` por quincena abierta.

`PayrollPeriodListDto` gana `nextPeriodStartsOn: DateOnly?` = `target.Start` cuando no existe quincena con ese inicio; si
existe, `null`. Lo traen las dos rutas que responden la lista (`GET …/periods` y `ensure-current`) y `open-next`. Se calcula
también para una empresa inactiva (el frontend no ofrece la acción sin `PeriodManage` ni con la empresa inactiva).
`firstPeriodStartsOn` no cambia.

**Decisión de la sesión:** `PeriodPlan` gana un cálculo con `target` en vez de duplicar el bucle; `Missing` (la actual) no
cambia de comportamiento.

## Frontend (`qep-frontend`, `features/payroll`)

- `services/payroll.api.ts`: `openNextPeriod(tenantId, companyId)` → `POST …/periods/open-next`, parsea la lista como
  `ensureCurrentPeriod`; el parser lee `nextPeriodStartsOn` con `strOrNull` (ausente = backend viejo = `null`, sin botón).
- `types/payroll.ts`: `nextPeriodStartsOn: string | null` en la lista.
- Pantalla de la quincena (`pages/payroll-period-page.tsx`, container en `hooks/use-period-screen.tsx`): botón
  «Abrir la quincena siguiente» con la fecha (`formatDate(nextPeriodStartsOn)`, p. ej. «Abrir la quincena del 1 dic 2026»),
  visible sólo con `nextPeriodStartsOn`, permiso `PeriodManage` y empresa activa. También en el estado «La nómina de esta
  empresa empieza el …», que es justo el caso de diciembre. Mientras viaja, deshabilitado; al terminar se invalida la lista de
  quincenas y la pantalla muestra la quincena recién abierta. Un error se pinta con el mapeo existente de fallas de nómina.
- Pruebas: api (ruta y método), parser (`nextPeriodStartsOn` ausente ⇒ `null`), página (se ve y no se ve según permiso, campo
  y empresa inactiva; clic llama y muestra la nueva; error).

## Fuera de alcance

Abrir dos o más quincenas por delante, cerrar o borrar una quincena abierta por error, y cambiar la apertura automática.

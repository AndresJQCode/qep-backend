# Parámetros legales editables desde la consola del operador (backend)

Fecha: 2026-11-22. Estado: borrador para aprobación del owner. Repo: `qep-backend`. El frontend tiene su propio spec y
depende de éste.

## Intención

Hoy los valores legales de RRHH y nómina (SMMLV, auxilio de transporte, tasas de aportes, topes, recargos) viven en
`hr.legal_parameters` y sólo se cargan con una migración (`AddHrLegalParameters`, `AddPayrollLegalParameters`). Cada enero
exige código, revisión y despliegue. Si la fecha de inicio de un contrato cae donde no hay fila, `GET /hr/salary-rules`
responde `hr.legal_parameter.missing` («There is no SMMLV loaded for …») y el alta de empleados no avanza.

El dueño de la plataforma (operador) debe mantener esos valores desde la consola, sin migraciones ni despliegues, y sin
dejarlos fijos en el código.

## Metas

- El operador crea, edita y borra filas `(key, effective_from, value)` de `hr.legal_parameters` y los tramos del fondo de
  solidaridad de `hr.legal_parameter_brackets` (única clave de tramos hoy: `SolidarityFund`).
- Un cambio nunca altera una liquidación ya hecha.
- Cada cambio queda con quién, cuándo, valor anterior y valor nuevo.
- Lectura barata: la tabla (unas decenas de filas) se sirve de una caché compartida en Valkey, válida entre réplicas.
- Aviso de «falta el valor del año siguiente» para los valores anuales, igual que `nextYearMissing` de las unidades
  tributarias.

## No metas

- Corrección histórica y reliquidación: un valor que ya usó una nómina calculada no se corrige desde la consola (procedimiento
  extraordinario aparte).
- Claves nuevas. `LegalParameterKey` es un enum con `CHECK` en la tabla y texto en `LegalBasis`: una clave nueva sigue siendo
  código más migración. La pantalla edita las 30 existentes.
- Cambiar el motor de nómina o cómo resuelve (`LegalParameters.Resolve` no se toca).

## Antimetas

- Que un valor mal tecleado cambie una nómina cerrada sin que nadie lo vea.
- Que una réplica sirva un valor viejo durante horas después de un cambio.
- Que Valkey caído tumbe el alta de empleados o la nómina.

## Restricciones verificadas en el código

- `PayrollPeriod.LegalValuesJson` se escribe una sola vez (`??=`), al primer cálculo del periodo, y desde ahí
  `PayrollResultMapper.ParseLegal` lo usa en vez de la tabla. Un periodo ya calculado es inmune a la edición. Un periodo
  sin calcular lee la tabla viva (`legalValues.ResolveAsync(period.StartDate)`).
- **Nómina exige un valor de cada año.** `PayrollLegalValues` marca como faltante (y no liquida) toda clave «anual» cuya fila
  vigente sea de un año anterior al del periodo (`PayrollLegalValues.cs:29`). `IsYearly` excluye `SundaySurchargeRate`,
  `WeeklyHours` y `ConnectivityAllowanceSince`, pero el chequeo sólo corre sobre las claves que `LegalValueSet` lee con `Get(...)`
  (`PayrollLegalValues.cs:48-81`), y `IntegralSalaryFloor` no está entre ellas: **nómina exige 26 claves escalares por año**, no 27.
  `IntegralSalaryFloor` lo usa sólo Hr (`SalaryRules`), que no caduca la tabla (D-8). **Los tramos `SolidarityFund` también son anuales para nómina** (decisión del owner 6,
  2026-11-22; spec `2026-11-22-nomina-decisiones-del-owner-design.md`): faltan si el conjunto vigente es de un año anterior al del
  periodo. **Decidido por el owner (2026-11-22):** el aviso `nextYearMissing`/`currentYearMissing` los cubre.
  *(Corregido en la revisión: decía «27 claves escalares más los tramos, 28 cada enero».)* Qué cubre el aviso de «falta el año
  siguiente» es decisión del owner (ver «Pendiente del owner»).
- La foto del periodo (`LegalValueSet`) incluye los tramos de solidaridad y la fecha con que se resolvió
  (`PayrollPeriod.LegalValuesDate`).
- Hr valida salarios y la importación contra `SalaryRules`/`LegalParameters.Resolve` en vivo, a la fecha del registro.
- `ILegalParameterSource` es la costura de lectura: `LegalParameterSource` (EF) la implementa y la usan `SalaryRules`,
  `EmployeeImportAnalyzer`, `ImportEmployees` y `PayrollLegalValues` (Bootstrapper).
- Valkey se organiza en áreas con interruptor (`ValkeyArea`, `Valkey:Areas:*`), con implementación en memoria cuando el área
  está apagada y fallback en `ValkeyGateway` cuando Valkey falla. `MemoryPermissionCache` documenta el patrón de leer la
  versión **antes** de resolver para que un cambio en el medio deje la entrada nacida vieja.
- Contrato de operador a copiar: `operator-tax-units` (`/api/v1/tenants/{tenantId}/operator/tax-units`, permisos
  `operator.tax_units.read|manage`, códigos `catalog.tax_unit.*`). Ojo: su `nextYearMissing` se prende **sólo desde el 24 de
  diciembre** y mira el calendario del tenant operador (`TaxUnitValues.cs:33-42`, `TaxUnitOperator.cs:41`), no todo el año.
- Cada handler de Hr revisa el módulo `hr` del tenant (`ITenantModules`). Los handlers de operador **no** lo revisan: el tenant
  operador puede no tener `hr`. Autorizan con `OperatorAuthorization.EnsureAuthorized(executionContext, tenantId, permiso,
  operatorTenant)` antes de tocar un repositorio, como `TaxUnitOperator.cs:38`. Hr.Application ya referencia
  `Modules.Tenancy.Application` (`HrLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`), así que puede usarlo.

## Enfoques considerados

1. **Sólo los anuales** (SMMLV, auxilio de transporte, piso integral): lo más chico, pero la primera tasa que cambie vuelve
   a exigir migración. Descartado por el owner.
2. **Todo editable sin historial:** descartado, los valores alimentan liquidaciones y hace falta saber quién cambió qué.
3. **Todo editable con historial (elegido por el owner, 2026-11-22).**

Sobre la edición de vigencias pasadas, el owner eligió: **se permite editar o borrar una fila sólo si ningún periodo de nómina
ya calculado la usó.**

## Diseño

### Caché (área nueva `LegalParameters`)

- `ValkeyArea.LegalParameters` con su interruptor `Valkey:Areas:LegalParameters`, el patrón de `Sessions`/`Permissions`. Un área
  nueva toca cuatro lugares: el enum `ValkeyArea`, la propiedad `bool LegalParameters` de `ValkeyAreaOptions`, y los `switch` de
  `ValkeyAreas.TagOf` (etiqueta estable `legal_parameters` de `qep.valkey.fallbacks`) e `IsOn` (los dos lanzan con un valor sin
  rama). Las claves van en `ValkeyKeys` con el prefijo `qep:` (la ACL de producción sólo deja `~qep:*`).
- Se cachea la tabla **entera** (no el resultado por fecha: `Resolve` ya filtra por `EffectiveFrom`, así que cruzar de año no
  invalida nada).
- Clave versionada: un contador `qep:legalparams:ver` y la tabla en `qep:legalparams:{ver}`. El lector lee la versión **antes**
  de cargar; cada escritura sube la versión **después** del commit. Así una lectura que arrancó antes del commit nace con la
  versión vieja y queda inofensiva.
- TTL del valor: **1 h** (decidido por el owner, 2026-11-22, opción A; antes 24 h), red de seguridad. La frescura la da el
  contador; el TTL acota el daño si el contador falla (ver abajo). La tabla son unas decenas de filas: una lectura por hora y
  réplica no cuesta nada.
- Área apagada: caché en memoria del pod con TTL corto (60 s), porque sin Valkey no hay invalidación entre réplicas.
  *Mi decisión.*
- Valkey caído: se lee de la base y se sigue (fallback del gateway, con la métrica `qep.valkey.fallbacks`).
- Implementación: decorador de `ILegalParameterSource`; `SalaryRules`, la importación y nómina no se tocan. **No puede
  registrarse en `AddQepValkey`** *(corregido en la revisión)*: `AddQepValkey` corre en `QepServiceCollectionExtensions.cs:125` y
  `AddHrInfrastructure` en la línea 1896 *(Corregido en la implementación, 2026-11-23)*, con `AddScoped<ILegalParameterSource, LegalParameterSource>()`
  (`HrInfrastructureExtensions.cs:29`), que pisaría el decorador. Se registra **después** de `AddHrInfrastructure`, envolviendo el
  descriptor previo (`LegalParameterSource` es `internal` en Hr.Infrastructure y el Bootstrapper no lo puede nombrar).
- **Subir la versión es un puerto de Hr.Application** (`ILegalParameterCacheVersion.BumpAsync`, el patrón de `ITenantAccessVersion`
  de Tenancy): los casos de uso de escritura viven en Hr y no ven Valkey (`HrLayerTests` prohíbe `StackExchange.Redis`). Tres
  implementaciones en el Bootstrapper: Valkey (`INCR`, sin el token de quien llama, como `ValkeyTenantAccessVersion`), memoria del
  pod (área apagada: invalida la caché local del pod que escribió). *(Corregido en la implementación, 2026-11-23)* No hay implementación «ninguna para pruebas»: no habría servido,
  porque la caché del pod seguiría sirviendo la tabla; las pruebas que escriben SQL a mano suben la versión real después.
- **Riesgo residual, aceptado con TTL de 1 h:** si el `INCR` posterior al commit falla (Valkey caído en ese instante), las
  réplicas pueden servir la versión vieja **hasta 1 h** (antes 24 h, que contradecía la antimeta «un valor viejo durante
  horas»). No se elimina, se acota: `ValkeyTenantAccessVersion` lo acota con entradas de 60 s. El fallo del `INCR` se cuenta en
  `qep.valkey.fallbacks` y se loguea, y la respuesta de la escritura sigue siendo correcta (el dato ya está en la base).
- **Escrituras fuera de la consola** (migración que siembra el año, hotfix de SQL, restauración) no suben la versión: con el área
  prendida se ven hasta 1 h después, incluso tras un despliegue. Hacen falta `INCR qep:legalparams:ver` o un reinicio. El
  arranque sube la versión una vez (`LegalParameterCacheStartupBump`, sólo con el área prendida), así que un despliegue basta.

### Uso: qué es «una nómina ya calculada la usó»

- Puerto nuevo `ILegalParameterUsage` en `Modules.Hr.Application`: «¿algún periodo ya calculado se resolvió con esta fila?».
- Lo implementa el Bootstrapper (donde ya vive `PayrollLegalValues`, porque Hr no puede referenciar Payroll). El Bootstrapper
  tampoco lee `payroll.periods` por su cuenta: Payroll.Application expone un puerto de lectura (por ejemplo «las fechas distintas
  de `legal_values_date` no nulas», implementado en Payroll.Infrastructure) y el adaptador del Bootstrapper lo traduce a
  `ILegalParameterUsage`. Es una consulta **de todos los tenants** (los valores legales son de la plataforma); `PayrollDbContext`
  no tiene filtro global de tenant, así que no hay que saltarse nada, pero el puerto lo dice en su nombre. Para el `GET` se lee
  ese conjunto de fechas **una vez** y se cruza en memoria con todas las filas, no una consulta por fila.
- Verificado: `legal_values_date` es `period.StartDate` de cada quincena (`LiquidationPlanner.cs:42-43`,
  `LiquidatePeriod.cs:86-87`), se fija con `??=` en la primera liquidación (`PayrollPeriod.cs:170-171`) y reabrir y volver a
  liquidar no la cambia. La segunda quincena tiene su propia foto, resuelta el día 16: no hereda la de la primera.
- **Regla, sin leer el JSON:** la fila `(key, E)` rige desde `E` hasta la siguiente vigencia `N` de esa clave (o sin fin). Está en
  uso si existe un periodo con `legal_values_date` no nula tal que `E <= legal_values_date < N`. Sirve igual para escalares y
  para el conjunto de tramos, porque la foto se resolvió siempre con esa fecha. *Mi decisión:* no parsear `legal_values`; una
  consulta por fecha sobre `payroll.periods` basta.
- **Qué se rechaza con `in_use` — decidido por el owner (2026-11-22).** Crear, editar o borrar la fila `(key, E)` responde
  `hr.legal_parameter.in_use` (409) si **algún periodo ya calculado cae en el rango que la operación tocaría**: `[E, N)`, con
  `N` la siguiente vigencia existente de esa clave después de `E` (o sin fin). Al crear, la fila nueva cuenta como si ya
  existiera; al borrar, como si ya no. Esto cierra el hueco que encontró la revisión: una vigencia **retroactiva** ya no puede
  reescribir indirectamente la historia de una liquidación. Ejemplo: la quincena del 16 al 31 de enero de 2026 usó
  `(Smmlv, 2026-01-01)`; crear `(Smmlv, 2026-01-10)` toca `[01-10, …)`, que contiene el 16, y se **rechaza**. Una vigencia futura
  sin periodos calculados en su rango (p. ej. 2027-01-01) se permite. Se rechaza aunque el valor nuevo sea igual al vigente
  (`Carry`) si cae en un rango usado (*mi decisión*, por simplicidad); la carga normal del 1 de enero del año siguiente no cae
  en ninguno.
- **Qué devuelve el 409** *(decidido por el owner, 2026-11-22, opción A: sólo conteos y rango)*: el código y, como extensiones,
  `periodsAffected` (cuántos periodos), `from` y `to` (primera y última `legal_values_date` afectadas) y `companiesAffected`
  (cuántas empresas). **Sin lista de nóminas ni nombres de empresa:** un periodo es de una empresa, y listarlos expone
  datos de empresas dentro de la consola del operador y exige una lectura de todos los tenants. Por eso el 409 sólo trae esos tres
  números y el rango; la lista con empresa y periodo (con tope y permiso propio) queda descartada por ahora.
- **Corrección histórica: fuera de este spec.** Un valor que ya usó una nómina calculada **no se corrige desde esta pantalla**.
  Eso es un procedimiento extraordinario de corrección y reliquidación, aparte, que no se diseña aquí.
- Consecuencia asumida: el operador puede cambiar libremente un valor que sólo afecta periodos futuros o sin calcular. Un
  periodo sin calcular que cruce el cambio usa el valor nuevo, que es lo correcto.
- Consecuencia a saber: editar el SMMLV no revalida salarios de empleados ya guardados. No se hace.

### API (Hr, grupo de operador)

Base `/api/v1/tenants/{tenantId}/operator/legal-parameters`, mismo estilo que unidades tributarias.

| Método | Ruta | Permiso | Qué hace |
| --- | --- | --- | --- |
| GET | `/` | `operator.legal_parameters.read` | Las 30 claves con sus filas `{effectiveFrom, value, inUse, updatedAt}`, y los tramos `SolidarityFund` agrupados por vigencia `{effectiveFrom, brackets[], inUse, updatedAt}`. Cada clave anual trae `nextYearMissing`. |
| PUT | `/{key}/{effectiveFrom}` | `.manage` | Crea la fila o cambia su valor. Cuerpo `{ value }`. |
| DELETE | `/{key}/{effectiveFrom}` | `.manage` | Borra la fila. |
| PUT | `/brackets/SolidarityFund/{effectiveFrom}` | `.manage` | Crea o **reemplaza el conjunto entero** de tramos de esa vigencia. Cuerpo `{ brackets: [{ lowerBound, upperBound, rate }] }`. |
| DELETE | `/brackets/SolidarityFund/{effectiveFrom}` | `.manage` | Borra el conjunto. |
| GET | `/history` | `.read` | Cambios, del más reciente al más viejo, paginado. |

- **`nextYearMissing`** (corregido tras leer `PayrollLegalValues`): se prende cuando no hay fila con vigencia exactamente el 1 de
  enero del año siguiente: nómina marca faltante a una clave cuya fila vigente sea de un año anterior, y la primera quincena de
  enero resuelve con su `StartDate` (el día 1), que sólo se cubre con una fila de ese día. Las tres no anuales no lo llevan.
  *Revisión:* las claves que nómina exige cada año son 26, no 27 (ver «Restricciones»);
  **decidido por el owner (2026-11-22):** el aviso cubre esas 26 más `IntegralSalaryFloor` (27 claves, las que el DTO marca
  `yearly: true`). **Tramos `SolidarityFund`: re-decidido por el owner (2026-11-22), sí llevan aviso** — con la decisión 6 de
  nómina son anuales y un enero sin conjunto del año no liquida. El conjunto lleva `nextYearMissing` y `currentYearMissing` con la
  misma regla que las claves: no hay conjunto con vigencia exactamente el 1 de enero del año correspondiente. Cuenta como una
  entrada más en `nextYearMissingCount`.
  **Decidido por el owner (2026-11-22):** `nextYearMissing` se prende **desde el 1 de diciembre** (no los 12 meses; el de UVT usa el
  24) y se agrega `currentYearMissing`, **siempre activo**, verdadero cuando una clave anual no tiene fila vigente del año en curso
  (lo que hace fallar la nómina de enero). Cada uno trae su conteo, que calcula el backend.
  «Hoy» sale del calendario del tenant operador (`ITenantClock`), como en UVT.
- **Rutas:** `{key}` es el nombre exacto del enum (`Smmlv`, `ArlClassIIRate`); uno desconocido es 404 `key_unknown`.
  `{effectiveFrom}` es `yyyy-MM-dd`; una fecha que no parsea es 422 `date_invalid`. Ninguna ruta `GET` choca con
  `/{key}/{effectiveFrom}` (sólo `PUT`/`DELETE`). El estado HTTP lo decide el tipo de excepción, no el código
  (`ApiExceptionHandler.MapException`): `in_use` es `ResourceConflictException` (409), `key_unknown` `ResourceNotFoundException`
  (404), los demás `HrDomainException` (422).
- **Fecha de vigencia:** cualquier fecha. *Descarté mi decisión anterior* de exigir el 1 de enero: una ley puede cambiar una
  tasa a mitad de año (la fila nueva se agrega junto a la del 1 de enero). El 1 de enero lo cubre el aviso, no la validación.
- **Tramos:** el conjunto se valida entero: al menos un tramo, ordenados por `lowerBound`, el primero con `lowerBound >= 0`,
  contiguos (`lowerBound` de cada uno igual al `upperBound` del anterior), `upperBound > lowerBound` (`null` sólo en el último) y
  `rate` en `(0, 1]`. *Mi propuesta*: la tabla no lo exige hoy, pero `ResolveBrackets` supone un conjunto coherente. Precisión
  de la base: límites `numeric(9,4)`, tasa `numeric(18,6)`.
- **Validación por clave** en el dominio (`LegalParameterRules`): `*Rate` es fracción en `(0, 1]`; `Smmlv`, `IntegralSalaryFloor`,
  `TransportAllowance` > 0; `*Smmlv` (topes en múltiplos) > 0; `WeeklyHours` en `(0, 168]`; `ConnectivityAllowanceSince` vale
  exactamente `1`. Un valor fuera de rango es 422, no se corrige en silencio. *Revisión:* la columna es `numeric(18,6)`
  (`HrDbContext.cs:88`); un valor con más de 6 decimales también es `value_invalid` (Postgres lo redondearía sin avisar).
  `IntegralSalaryFloor` es un monto en pesos (16.900.000 en 2024), no un múltiplo. Los datos sembrados cumplen todas estas
  reglas y la de tramos (`SundaySurchargeRate` 2027 vale exactamente `1.00`, el borde superior).
- **Borrar:** no se puede borrar la única fila de una clave (el resolver necesita al menos una), ni una fila en uso.
- Códigos de error: `hr.legal_parameter.in_use` (409), `.value_invalid` (422), `.date_invalid` (422), `.last_row` (422),
  `.key_unknown` (404). Existente: `hr.legal_parameter.missing`. *(Corregido en la implementación, 2026-11-23)* El mapeo no es por `code`: lo decide el tipo de
  excepción en `ApiExceptionHandler.MapException` (`ResourceConflictException` 409, `ResourceNotFoundException` 404,
  `HrDomainException` 422) y no se agregó ninguna entrada por código al manejador.
- **Código de la validación de tramos** *(Corregido en la implementación, 2026-11-23)*: el `PUT` manual de un conjunto que no pasa las reglas responde
  `hr.legal_parameter.value_invalid` (el spec no nombraba código); en la vista previa del Excel el mismo mensaje viaja como el código
  de fila `hr.legal_parameter.import.row.brackets_invalid`.
- Concurrencia: dos operadores editando la misma fila. *Mi decisión:* la última escritura gana y las dos quedan en el
  historial; el volumen (pocos cambios al año) no justifica `If-Match`.

#### Forma de las respuestas (agregada en la revisión)

El borrador no fijaba los DTO y el frontend no puede planificarse sin ellos. Lo de abajo es **propuesta del revisor**, en
camelCase como el resto de la API; lo marcado ❓ es decisión del owner.

```jsonc
// GET /  → LegalParametersDto
{
  "today": "2026-12-05",          // calendario del tenant operador; el frontend no usa el reloj del navegador
  "currentYear": 2026,
  "currentYearMissingCount": 0,   // siempre activo: claves anuales sin fila vigente del año en curso
  "nextYear": 2027,              // nextYearMissingCount vale 0 antes del 1 de diciembre
  "nextYearMissingCount": 28,     // 27 claves más el conjunto de tramos; el navegador no cuenta
  "parameters": [                 // las 30, en el orden de LegalParameterKey
    {
      "key": "Smmlv",
      "name": "Salario mínimo mensual legal vigente",  // de LegalParameterLabels (Hr.Domain)
      "group": "SalaryAndCaps",                        // de LegalParameterLabels (Hr.Domain)
      "unit": "Currency",          // Currency | Fraction | SmmlvMultiple | Hours | Flag
      "yearly": true,
      "nextYearMissing": true,
      "currentYearMissing": false,     // false en las no anuales
      "current": { "effectiveFrom": "2026-01-01", "value": 1750905 },  // la fila vigente a «today»; null si ninguna
      "rows": [                    // de la vigencia más reciente a la más vieja
        { "effectiveFrom": "2026-01-01", "value": 1750905, "inUse": true,  "updatedAt": null },
        { "effectiveFrom": "2025-01-01", "value": 1423500, "inUse": true,  "updatedAt": null }
      ]
    }
  ],
  "brackets": [
    {
      "key": "SolidarityFund",
      "name": "Fondo de solidaridad pensional",
      "nextYearMissing": true,     // los tramos llevan aviso (re-decidido 2026-11-22, decisión 6 de nómina)
      "currentYearMissing": false,
      "current": { "effectiveFrom": "2026-01-01" },
      "sets": [
        { "effectiveFrom": "2026-01-01", "inUse": false, "updatedAt": null,
          "brackets": [ { "lowerBound": 4, "upperBound": 16, "rate": 0.01 } ] }
      ]
    }
  ]
}
```

- `updatedAt` es **nulo** en las filas sembradas por migración. `current`, `inUse`, `nextYearMissing` y el conteo los calcula el
  backend.
- `PUT` y `DELETE` responden el `LegalParametersDto` entero (una sola fuente para la pantalla; son pocas decenas de filas).
  *Mi decisión de revisor:* así el frontend escribe la respuesta en caché sin recomponer `inUse` ni `current`.
- `GET /history?page=1&pageSize=50` responde `{ items, total, page, pageSize }`, como `OperatorHistoryPageDto` de Tenancy. Cada
  ítem: `{ id, key, effectiveFrom, action, oldValue, newValue, changedAt, changedBy: { subjectId, displayName }, source,
  batchId, fileName }`. **Valores tipados** (*decidido 2026-11-22*): cada ítem trae `kind`
  (`Parameter | Brackets`); un `Parameter` lleva `oldValue`/`newValue` como `number` (nulos en «Creó» y «Borró»), y un `Brackets`
  lleva `oldBrackets`/`newBrackets` como lista de tramos. El frontend nunca parsea JSON. En la base, las columnas son `jsonb`. *(Corregido en la implementación, 2026-11-23)* `kind` **se deriva de `key`** (los nombres de
  `LegalParameterKey` y `LegalBracketKey` no se repiten): la tabla no tiene columna de tipo.
- **Nombres y grupos:** la plantilla de Excel tiene columna `Nombre`, y la arma Hr.Infrastructure, que **no puede leer**
  `LegalBasis` (vive en `Modules.Payroll.Domain.Engine`; `HrLayerTests` prohíbe referenciar otro módulo de negocio). Así que los
  nombres en español tienen que vivir también en Hr (por ejemplo `LegalParameterLabels` en Hr.Domain) o pasar a
  BuildingBlocks. Ya que existen en Hr, el `GET` los manda con el grupo y la unidad. **Grupos decididos (2026-11-22):**
  `SalaryAndCaps` (`Smmlv`, `IntegralSalaryFloor`, `TransportAllowanceCapSmmlv`, `ExonerationThresholdSmmlv`, `IbcCapSmmlv`,
  `NonSalaryCapRate`), `Allowances` (`TransportAllowance`, `ConnectivityAllowanceSince`), `Contributions` (salud, pensión, alto
  riesgo, ARL I-V, caja, ICBF, SENA), `Provisions` (cesantías, prima, vacaciones, intereses a las cesantías) y `WorkingTime`
  (`WeeklyHours`, `SundaySurchargeRate`, `ExtraDayRate`, `ExtraNightRate`, `NightSurchargeRate`); los tramos van en su propia
  sección. `LegalParameterLabels` es la **única** fuente de nombre, grupo y unidad; los nombres se copian de `LegalBasis` para que
  la consola y el rastro de nómina digan lo mismo, y una prueba compara las dos listas de claves. *(Corregido en la implementación, 2026-11-23)* Esa prueba vive en `Bootstrapper.UnitTests`
  (`LegalParameterLabelsMatchLegalBasisTests`), no en Hr: necesita `LegalBasis` y Hr no puede referenciar Payroll.

### Carga por Excel (el flujo de enero)

Decisión del owner (2026-11-22): la investigación de los valores nuevos la hace él por fuera, arma un Excel y lo carga. **No
hay copiloto ni IA en este spec.** Cada enero son 26 claves que nómina exige más el conjunto de tramos (28 entradas con `IntegralSalaryFloor`): el Excel evita
teclearlas una por una.

Copia el patrón de la importación de empleados: tres rutas, un puerto de Aplicación para Excel (`IExcelLegalParameterImporter`)
con su adaptador ClosedXML en Infrastructure, y analizador, parser y DTO separados (`EmployeeImportAnalyzer`, `…RowParser`).

| Método | Ruta | Permiso | Qué hace |
| --- | --- | --- | --- |
| GET | `/import/template` | `.manage` | Descarga el Excel con el estado actual. |
| POST | `/import/preview` | `.manage` | Analiza el archivo **sin escribir** y devuelve qué haría. |
| POST | `/import` | `.manage` | Reenvía el **mismo archivo**, lo vuelve a analizar y aplica todo o nada. |

- **Plantilla** (*mi decisión*): dos hojas. «Parámetros»: una fila por clave con `Clave`, `Nombre`, `Valor vigente`,
  `Vigente desde` (sólo lectura) y las dos columnas que se llenan, `Rige desde` y `Valor nuevo`. Viene **precargada** con el 1 de
  enero del año siguiente y con el valor vigente, para que el operador sólo cambie lo que cambió. *(Corregido en la implementación, 2026-11-23)* Sólo las **27 claves
  anuales** vienen precargadas; las tres que no son anuales (`SundaySurchargeRate`, `WeeklyHours`, `ConnectivityAllowanceSince`)
  salen con `Rige desde` y `Valor nuevo` vacíos: arrastrar `ConnectivityAllowanceSince` al 1 de enero movería la fecha desde la que
  rige la conectividad, y `WeeklyHours` cambia el 15 de julio, no el 1 de enero. La plantilla trae las 30 filas y la vista previa de
  una plantilla sin tocar da 28 `Carry` (27 claves más el conjunto). El valor «vigente» que arrastra es el que rige el 1 de enero
  siguiente (la última fila con fecha menor o igual), no el de hoy, para que una fila futura ya cargada no la pise. «Tramos»: una fila por tramo
  (`Clave`, `Rige desde`, `Desde`, `Hasta`, `Tasa`), precargada con el conjunto vigente. Una fila con `Valor nuevo` vacío se
  ignora.
- **`Valor nuevo` viene precargado — decidido por el owner (2026-11-22).** La plantilla trae en `Valor nuevo` el valor vigente, y
  en `Rige desde` el 1 de enero del año siguiente. Importa porque nómina exige una fila **del año** aunque el valor no cambie: una
  tasa que sigue igual en 2027 necesita la fila `(clave, 2027-01-01)`, y subir la plantilla sin tocarla la **crea** (acción
  `Crear`, no `SinCambio`: la fila `(clave, fecha)` no existía). El mockup se corrigió: esas filas salen «Crear · igual que hoy».
  **Acción `Carry` — decidido por el owner (2026-11-22, opción A).** Una fila que **no existía** `(clave, fecha)` y cuyo valor es
  igual al que ya regía a esa fecha se marca `Carry` («Se mantiene el valor»), no `Crear`. Para los tramos, el conjunto es igual al
  vigente. `Carry` **sí escribe** la fila y **sí** deja historial (acción `Created`): sólo cambia la presentación y el conteo. El
  backend la calcula; el navegador no la deduce. Así la vista previa de enero distingue «1 cambia · 25 se mantienen».
- **Qué devuelve la vista previa:** por fila `{hoja, fila, clave, rigeDesde, acción, valorAnterior, valorNuevo, código,
  mensaje}`, con `acción` en `Crear | Carry | Actualizar | SinCambio | Error`; totales por acción; y avisos que no bloquean: las claves
  anuales que seguirían sin fila del 1 de enero del año siguiente después de aplicar.
- *Revisión:* los tramos se validan **por conjunto** `(SolidarityFund, rigeDesde)`, no por fila, así que la vista previa los
  devuelve como **una entrada por conjunto** con las filas de la hoja que lo forman (el mockup aprobado ya lo muestra así:
  «Hoja Tramos · filas 2 a 7 · 6 tramos»). Forma propuesta:

  ```jsonc
  // POST /import/preview → LegalParameterImportPreviewDto
  {
    "fileName": "parametros-2027.xlsx",
    "summary": { "create": 1, "carry": 25, "update": 0, "unchanged": 0, "error": 1 },   // lo cuenta el backend
    "canApply": false,                                                        // false si hay algún Error
    "items": [
      { "kind": "Parameter", "sheet": "Parámetros", "rows": [2], "key": "Smmlv", "name": "…",
        "effectiveFrom": "2027-01-01", "action": "Create", "oldValue": 1750905, "newValue": 2000000,
        "code": null, "message": null },
      { "kind": "Brackets", "sheet": "Tramos", "rows": [2,3,4,5,6,7], "key": "SolidarityFund", "name": "…",
        "effectiveFrom": "2027-01-01", "action": "Create", "oldBrackets": null, "newBrackets": [ … ],
        "code": null, "message": null }
    ],
    "warnings": [ { "code": "hr.legal_parameter.import.next_year_missing", "key": "PensionEmployerRate", "message": "…" } ]
  }
  ```

  `action` en inglés (`Create | Carry | Update | Unchanged | Error`), como los demás enums de la API; el español es del frontend.
  `oldValue` (*decidido 2026-11-22*) es el valor **vigente a esa fecha antes de aplicar** (lo que muestra el mockup): en `Update`
  coincide con la fila de esa misma fecha, y en `Create` y `Carry` es la fila anterior. Nulo sólo si no había ninguna vigente. `message` va en español para la persona, como en la
  importación de empleados (`EmployeeImportRowIssueDto.Message`). `POST /import` responde `{ fileName, receivedAt, batchId,
  created, carried, updated, unchanged }` y el frontend relee el `GET`.
- **Qué bloquea:** cualquier fila en `Error` (valor fuera de rango, fecha inválida, clave desconocida, tramos incoherentes, fila ya
  usada por una nómina calculada que se intenta editar). Un solo error y no se aplica nada (todo o nada, como los empleados). Las
  mismas reglas de validación y de «en uso» de la edición manual, sin duplicarlas: el importador llama a los mismos casos de uso.
- **Fila sin cambio:** si `(clave, fecha)` ya existe con el mismo valor, se cuenta como `SinCambio` y no escribe ni genera historial.
- **Atomicidad y caché:** una sola transacción; la versión de la caché sube una vez, al terminar.
- **Historial:** todas las filas de una carga comparten un `batch_id` y llevan `source = Excel` (más el nombre del archivo), para
  poder ver y entender una carga de enero de un vistazo.
- Límites (*mi decisión*): archivo `.xlsx` de hasta 1 MB y 500 filas. *Revisión:* decía además «el implementador replica los
  topes de la importación de empleados», que son otros (5 MB y 1000 filas, `HrImportColumns.cs:15-17`): rigen 1 MB y 500, en
  constantes propias. Lo que sí se replica es el mecanismo (endpoint con `IFormFile?` y `DisableAntiforgery()`,
  `HrEndpoints.cs:223-235`; sin archivo, 422 y no 400 del binding).
- Códigos de error `hr.legal_parameter.import.*`. *(Corregido en la implementación, 2026-11-23)* No se mapean por `code`: el status
  sale del tipo de la excepción (`HrDomainException` ⇒ 422), igual que el resto de los códigos de Hr, y `ApiExceptionHandler` no
  lleva entradas por código. *Revisión:* el borrador listaba `file_required`,
  `file_invalid`, `header_missing`, `too_many_rows`; se alinean con los de empleados (`HrErrorCodes.cs:57-64`) para que el
  frontend reuse el mapeo: `file_required`, `extension_invalid`, `too_large`, `unreadable`, `header_mismatch`, `empty`,
  `too_many_rows`, y **`rows_invalid`**, que faltaba: es lo que responde `POST /import` cuando el reanálisis encuentra una fila en
  `Error` (todo o nada). Los de fila (`hr.legal_parameter.import.row.*`) viajan en la vista previa con su `message`, no como error
  HTTP. *(Corregido en la implementación, 2026-11-23)* Lista cerrada de los códigos de fila: `key_unknown` (la clave no es el nombre exacto de un valor ni de un
  conjunto), `date_invalid` (`Rige desde` falta o no es `yyyy-MM-dd`), `value_invalid` (no es un número o no pasa
  `LegalParameterRules`, con su mensaje), `brackets_invalid` (el conjunto no pasa las reglas de tramos o trae una celda que no es un
  número; un conjunto es **un solo elemento** con todas sus filas), `duplicated` (el mismo `(clave, fecha)` otra vez en el archivo:
  falla la segunda aparición) e `in_use` (la fila cae en el rango de una nómina ya calculada, con el mensaje del 409). El aviso
  `hr.legal_parameter.import.next_year_missing` va aparte, en `warnings`, y no bloquea.
- La plantilla `GET /import/template` responde el archivo con `Results.File(…, ExcelContentType, fileName)` como la de empleados
  (`HrEndpoints.cs:467-473`); *propuesta*: `parametros-legales-<año siguiente>.xlsx`.

### Datos

- Tabla nueva `hr.legal_parameter_changes`: `id`, `key`, `effective_from`, `action` (`Created|Updated|Deleted`),
  `old_value`, `new_value` (`jsonb`: el número, o el conjunto de tramos), `changed_by` (miembro), `changed_by_name`, `changed_at`, `source` (`Manual|Excel`), `batch_id` (nulo en lo manual) y `file_name` (sólo Excel). Sólo inserta.
  *Revisión:* `changed_by` es el `SubjectId` del usuario (`IExecutionContext.SubjectId`), no un miembro: el operador actúa desde el
  tenant operador y el registro es de la plataforma, sin `tenant_id`. El nombre se resuelve como en `ActivityWriter` (Hr) y se
  guarda en `changed_by_name`. `key` es `varchar(32)` como en las otras dos tablas (`SolidarityFund` cabe).
- `hr.legal_parameters` gana `updated_at` y `updated_by`, **anulables** (las filas sembradas no tienen ni uno ni otro). Sin
  cambio de datos sembrados. *Revisión:* el `GET` devuelve `updatedAt` también por conjunto de tramos, y
  `hr.legal_parameter_brackets` no tiene esas columnas; se agregan igual (anulables) en esa tabla, o el `GET` las toma del
  último cambio del historial de ese conjunto. *Mi decisión de revisor:* columnas en las dos tablas, que es lo simple.
- Migración: la última de Hr es `20261122015359_AddPayrollLegalParameters` (cambió `value` a `numeric(18,6)` y rehízo el `CHECK`
  de `key`). La nueva va después, no toca el `CHECK` (no hay claves nuevas) y su `Down` sólo quita la tabla y las columnas.
- El historial se escribe en la misma transacción que el cambio (auditoría en el handler, regla de la arquitectura del repo).

### Permisos

`operator.legal_parameters.read` y `.manage` en `HrOperatorPermissions` (Hr.Application), con el mismo registro y el mismo modo
de otorgarlos al rol de operador que `CatalogOperatorPermissions`. *Revisión, verificado:* `operator.tax_units.*` se registra en
tres lugares de `src/Bootstrapper/QepServiceCollectionExtensions.cs`, y los dos nuevos van en los mismos:

1. En la lista de permisos del rol `admin` de fábrica (línea ~2884) *(Corregido en la implementación, 2026-11-23)*: el rol vive en código, sin migración. No hay un «rol de
   operador»: `OperatorPermissionFilter` (prefijo `operator.`) deja vivos estos permisos sólo en el tenant operador.
2. Un `PermissionDefinition` por permiso (línea ~3724) *(Corregido en la implementación, 2026-11-23)*: categoría `"Operator"`, `RequiredModules: []` (núcleo; **no** el módulo
   `hr`, o el enmascarado por módulos los apaga en el tenant operador sin `hr`), riesgo `"medium"` para `read` y `"high"` para
   `manage` (mueve la nómina de todos los tenants), `Shape = new("operator.legal_parameters", "Parámetros legales", "read"|"manage")`.
3. La política de autorización (línea ~4672) *(Corregido en la implementación, 2026-11-23)*: `AddPermissionRequirement(policy, …)`.

Segunda capa en cada handler: `OperatorAuthorization.EnsureAuthorized`.

## Pruebas (TDD, presupuesto del workspace)

- Dominio: validación por clave, regla de fecha anual, regla de última fila.
- Aplicación: editar/borrar una fila en uso responde `in_use`; sin uso, guarda y escribe el historial; cada escritura sube la
  versión de la caché.
- Caché: lectura con versión vieja no sobrescribe la nueva; Valkey caído lee de la base; área apagada usa memoria con TTL
  corto.
- Importación: la vista previa no escribe; un error en una fila no aplica ninguna; una fila `SinCambio` no genera historial; la
  carga completa crea un solo `batch_id` y sube la versión de la caché una vez; las claves anuales sin fila del 1 de enero
  salen como aviso.
- Integración: un periodo calculado no cambia al editar el SMMLV de su vigencia; el alta con una fecha antes sin valor deja de
  fallar tras crearlo. Filtrar por nombre de prueba mientras se itera; una corrida de integración a la vez.
- `ArchitectureTests`: Hr.Application no referencia Payroll ni Valkey; el puerto de uso se implementa en el Bootstrapper.
  *Revisión:* las dos ya están cubiertas (`HrLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` y
  `ApplicationDoesNotReferencePersistenceLibraries`, que incluye `StackExchange.Redis`; `PayrollLayerTests.NoPayrollLayerReferencesHr`
  cubre el otro lado). No hace falta prueba nueva; sí registrar los puertos nuevos en `CompositionRootTests` si esa prueba enumera
  los de Hr.
- Caché: una prueba de registro que resuelva `ILegalParameterSource` y verifique que es el decorador (el orden de registro es la
  trampa encontrada en la revisión).
- Uso: la regla por fecha con primera y segunda quincena (16 de enero) y, según decida el owner, la vigencia retroactiva.

## Despliegue

Backend primero (migración de `legal_parameter_changes` y columnas), luego frontend. El interruptor
`Valkey:Areas:LegalParameters` arranca apagado y se prende después de verificar `qep_valkey_connected = 1`, igual que las otras
áreas.

## Pendiente del owner

- Aprobado por el owner (2026-11-22): TTL de **1 h** (bajado desde 24 h) con fallback de memoria de 60 s, y «la última escritura gana» sin `If-Match`.
  **Cambiaron** tras leer el código, y hay que reconfirmarlos: `nextYearMissing` en las anuales y no sólo en tres (*revisión:* son 26 para nómina, no 28; ver el punto 1 de abajo) y fecha de
  vigencia libre (ya no el 1 de enero obligatorio).
- Por decidir en la carga por Excel (mis decisiones, marcadas arriba): plantilla **precargada** con el valor vigente y el 1 de
  enero siguiente (el operador cambia sólo lo que cambió), y límites de 1 MB y 500 filas.
- ~~Reglas de validación de los tramos~~ **Cerrado (2026-11-22):** contiguos, el primero desde 0 o más, `upperBound` mayor que
  `lowerBound` y vacío sólo en el último. Coincide con los datos sembrados (2025 y 2026: de 4 a 25 SMMLV, contiguos, el último con
  tope 25).

### Agregado en la revisión (2026-11-22), en orden de importancia

1. ~~Alcance de `nextYearMissing`~~ **Decidido (2026-11-22): las 26 que nómina exige más `IntegralSalaryFloor` = 27 claves,
   más el conjunto de tramos = 28 entradas.** Planteamiento original: nómina exige 26 claves por año, no 27, y no exige tramos anuales. ¿El aviso cubre las 26,
   también `IntegralSalaryFloor` (anual de hecho, deriva del SMMLV) y/o los tramos? *Recomendación:* 26 + `IntegralSalaryFloor`;
   tramos sin aviso. **Re-decidido (2026-11-22)** para los tramos: la premisa («no exige tramos anuales») cambió con la
   decisión 6 de nómina (spec `2026-11-22-nomina-decisiones-del-owner-design.md`), y el owner aceptó que lleven aviso.
2. ~~Desde cuándo se prende y el año en curso~~ **Decidido (2026-11-22):** `nextYearMissing` desde el 1 de diciembre, y
   `currentYearMissing` siempre activo, que es el que hace fallar la nómina de enero.
3. ~~Vigencia retroactiva sobre una nómina calculada~~ **Decidido (2026-11-22): se rechaza con `in_use`.** De las nóminas
   afectadas se muestran **sólo conteos y rango de fechas** (decidido, opción A; ver «Qué devuelve el 409»).
4. ~~`Valor nuevo` precargado en la plantilla~~ **Decidido: precargado**, y **decidido** que el backend manda una acción `Carry`
   aparte de `Crear` para las filas que repiten el valor.
5. ~~TTL de 24 h con bump fallido~~ **Decidido (2026-11-22): TTL de 1 h** (opción A). Sin reintento del `INCR` ni borrado de la
   clave: el daño máximo de un bump fallido queda en 1 h.
6. ~~Grupos y nombres~~ **Decidido (2026-11-22):** cinco grupos más tramos, en `LegalParameterLabels` (ver «Nombres y grupos»).
7. ~~Historial tipado o texto~~ **Decidido (2026-11-22):** tipado (`kind` más número o lista de tramos), columnas `jsonb`.
8. **«Antes» en la vista previa: decidido (2026-11-22)** el valor vigente a esa fecha.
9. **Tasas en el Excel: decidido (2026-11-22).** Las celdas de tasa de la plantilla vienen con formato de porcentaje (`0.00%`):
   el operador ve y teclea «12 %» y la celda guarda `0.12`, que es lo que lee el importador. Un valor mayor que 1 en una tasa
   sigue siendo `Error`, con el mensaje «La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de
   porcentaje?». Así formulario y Excel muestran las tasas igual.

### Agregado en la implementación (2026-11-23), para el owner

- ❓ **Borrar un `(clave, fecha)` que no existe responde 404 `key_unknown`.** El código lo hace así (`LegalParameterWriter`: «No hay
  una fila de X que rija desde …»), porque el recurso que no existe es la fila; el spec no decía nada. El nombre del código es
  engañoso (la clave sí existe, la fila no). Alternativa: un código propio (`row_unknown`) con el mismo 404.
- ❓ **`MemoryLegalParameterCache` es `PerPodByDesign` en `MultiReplicaInventory`.** Con el área `LegalParameters` apagada y más de una
  réplica, el pod que escribe se invalida al instante pero los demás pueden servir la tabla vieja **hasta 60 s**. Está aceptado
  mientras el área esté apagada; con ella prendida la versión compartida cubre todos los pods.
- **El `INCR` de Valkey** (`ValkeyLegalParameterCacheVersion`) también fija un `EXPIRE` de 30 días y siembra la clave con la base de
  tiempo de `TenantAccessVersionBase` (`SET NX`), para que una clave desalojada no vuelva a contar desde 1 y alcance la versión de
  una tabla sobreviviente. Todo en un `MULTI`; si falla se cuenta en `qep.valkey.fallbacks` y se loguea, y la escritura sigue siendo 200.

## Cambios de la revisión (2026-11-22)

Revisión contra el código de `develop`. Cada cambio está marcado en su sección:

- «Restricciones»: nómina exige 26 claves por año (`IntegralSalaryFloor` no está en `LegalValueSet`) y los tramos no eran anuales
  (`PayrollLegalValues.cs:29,38-42`; desde la decisión 6 de nómina, 2026-11-22, sí lo son). Agregados: `nextYearMissing` de UVT sólo desde el 24/12 y handlers de operador sin módulo `hr`.
- «Caché»: el decorador no puede ir en `AddQepValkey` (lo pisa `AddHrInfrastructure`, registrado después); falta el puerto de
  versión en Hr.Application; los cuatro lugares de un área nueva; el riesgo del TTL.
- «Uso»: puerto de lectura en Payroll.Application; verificación de `legal_values_date` por quincena; hueco de la vigencia retroactiva.
- «API»: formato de `{key}` y `{effectiveFrom}`, estados por tipo de excepción, y la sección nueva «Forma de las respuestas».
- «Excel»: los topes de empleados son 5 MB/1000, no 1 MB/500; códigos alineados con `hr.import.*` más `rows_invalid`; tramos por
  conjunto en la vista previa; la ambigüedad de `Valor nuevo`.
- «Datos»: `updated_*` anulables y también en tramos; `changed_by` es el usuario; orden de la migración.
- «Permisos»: los tres lugares exactos de `QepServiceCollectionExtensions.cs`, sin módulo requerido.

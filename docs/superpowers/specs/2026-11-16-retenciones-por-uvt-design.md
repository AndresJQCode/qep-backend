# Retenciones por UVT: el veredicto de cada tasa — diseño backend

Fecha: 2026-11-16. Trabajo que cruza repos: este spec cubre `qep-backend` y **manda el contrato**; la mitad del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-16-retenciones-por-uvt-design.md`. Salen juntos, backend primero (§10).

Estado: **diseño aprobado por el owner 2026-11-16 (maqueta), spec sin revisar, sin construir**. Rama `feature/retenciones-uvt`
desde `develop` (`cf367779`). Las rutas y líneas citadas son de ese commit.

Maqueta aprobada: canvas «Retenciones por UVT» (artboards «Crear / editar», «Detalle», «Estados de una tasa»).

Marcas: **[owner]** decisión del owner; **[agente]** propuesta de este spec, revisable; **NUEVO** = no existe hoy en el código.
Todo lo que no lleva NUEVO se verificó en el archivo citado.

## 1. Intención

En las seis pantallas de cotización y pedido (crear, editar, detalle) el asesor tiene que ver, por cada tasa de retención
parametrizada en el documento, **si aplica o no y por qué**: la base mínima en UVT, ese umbral en dinero, la base del documento
y el valor de la UVT con su año. Hoy una tasa que no llega a la base mínima desaparece sin explicación, y nadie se entera
cuando el documento usó la UVT de un año anterior porque la del año en curso no estaba cargada.

El backend decide todo: si aplica, el motivo, el umbral en dinero y si la UVT está desactualizada. El frontend sólo pinta.

## 2. Objetivos

1. Un campo NUEVO en las respuestas de cotización y pedido con **todas** las tasas de la fuente vigente, apliquen o no, cada
   una con su veredicto (§6).
2. Congelar el **año** de la UVT junto a su valor (§5.2), para poder mostrarlo y detectar la desactualizada.
3. Una sola regla para «entra al cálculo» y «por qué no entra»: el veredicto sale de la misma función que hoy filtra
   candidatas (§5.3), nunca de una copia.

## 3. No objetivos

- **El PDF no se toca** [owner 2026-11-16].
- No cambia cómo se calcula una retención: umbral inclusivo, sin redondear, monto a dos decimales (`DocumentTotals.cs:63-71`).
- No cambia `withholdings` (sólo las aplicadas) ni las listas de tasas congeladas: el campo nuevo se suma, no reemplaza.
- No cambia la consola del operador ni el aviso de UVT del año siguiente (`TaxUnitValues.NextYearMissing`).

## 4. Restricciones verificadas

- **Todas las tasas congeladas ya se guardan, apliquen o no.** `DocumentWithholding` (`DocumentWithholding.cs:12-21`) tiene
  `MinimumBase`, `MinimumBaseUnit`, `TaxUnitValue` y `Applied`. Tablas `quotations.quotation_withholdings` y
  `quotations.order_withholdings` (`QuotationsDbContext.cs:210-276, 672-678`).
- **El año de la UVT no se guarda.** `QuotationWithholdingLookup.Photo` (`QuotationWithholdingLookup.cs:118-135`) congela sólo el
  valor de `TaxUnitValues.ValueFor`.
- **`ValueFor` cae al año anterior** (`TaxUnitValues.cs:8-20`): si el año del documento no está cargado usa el mayor año
  anterior, sin avisar. Es el caso que este trabajo hace visible.
- **La foto no se mueve al cargar otra UVT** (`WithholdingRate.cs:5-9`): se congela al reemplazar las tasas de la fuente
  (`DocumentWithholding.ReplaceSource`).
- **Qué entra al cálculo** lo decide `WithholdingCandidates.For` (`WithholdingCandidates.cs:19-45`): consumidor final → nada;
  sin empresa o país fuera de la tabla, o moneda distinta a la que discrimina impuestos → nada; sólo tasas del país de la empresa;
  una municipal sólo si es del municipio de la operación. Después, `DocumentTotals.Withhold` (`DocumentTotals.cs:64-71`) aplica
  las que tienen `Threshold` y `subtotal >= Threshold`.
- `WithholdingRate.Threshold` (`WithholdingRate.cs:42-48`): 0 sin base; `base × UVT` con unidad; la base en dinero en un país sin
  unidad; `null` si hay base en unidades y no hay valor (no aplica nunca).
- `BaseAmount` de una fila no aplicada se guarda en 0 (`DocumentWithholding.Settle`): para la base del veredicto se usa el
  subtotal del documento, que es lo que se compara (`DocumentTotals.cs:57`).
- Las respuestas se arman en `QuotationResponseComposer.cs:78-133` y `OrderResponseComposer.cs:157-159`, que reusan
  `ToWithholdingResponse`.

## 5. Diseño

### 5.1 El veredicto [agente]

Por cada fila de la **fuente vigente** (cotización: `ActiveWithholdings`, `Quotation.cs:386`; pedido: su única fuente
`Document`), un veredicto NUEVO:

| `reason` | Cuándo | `applies` |
|---|---|---|
| `Applied` | Entró al cálculo y `subtotal >= Threshold` | true |
| `BelowMinimumBase` | Entró al cálculo y `subtotal < Threshold` | false |
| `NoTaxUnitValue` | Base en UVT y ningún valor cargado (`Threshold` null) | false |
| `FinalConsumer` | El documento factura a consumidor final | false |
| `NoBillingCompany` | Sin empresa facturadora, o su país no está en `TaxCountries` | false |
| `CurrencyWithoutTax` | La moneda del documento no es la que discrimina impuestos en el país | false |
| `OtherMunicipality` | Municipal de un municipio distinto al de la operación | false |

Orden de evaluación: el de `WithholdingCandidates.For` (consumidor final, empresa, moneda, municipio) y después el umbral.

Las tasas de **otro país** que el cliente tiene asignadas (D16 de impuestos por tipo) **no** van en la lista [agente]: nunca
aplican a este documento y serían ruido. Siguen en `customerWithholdingTaxRates` como hoy.

### 5.2 Año de la UVT congelado — NUEVO

- `TaxUnitValues.ValueFor` pasa a devolver valor **y año** (`(decimal Value, int Year)?`).
- `WithholdingRate` suma `int? TaxUnitYear`; `DocumentWithholding` lo guarda en la columna NUEVA `tax_unit_year smallint NULL`
  en las dos tablas.
- `taxUnitOutdated = TaxUnitYear < año del documento`. El año del documento es el mismo `documentDate` con el que se congeló
  la foto [verificar en el plan qué fecha pasan los handlers; el compositor municipal usa `DateOf(CreatedAt)`,
  `QuotationResponseComposer.cs:75`]. Con `TaxUnitYear` null, `false`.

### 5.3 Una sola regla [agente]

`WithholdingCandidates` gana `Explain(...)`, que devuelve un veredicto por tasa con los mismos parámetros de `For`. `For` pasa a
ser `Explain(...)` filtrado a las que superan los pasos 1-4, para que «entra» y «por qué no entra» no puedan divergir. El
umbral (`Applied`/`BelowMinimumBase`/`NoTaxUnitValue`) lo decide la misma comparación de `DocumentTotals.Withhold`, extraída a un
método que usen los dos.

### 5.4 Lo que no cambia

`withholdings`, `customerWithholdingTaxRates`, `billingWithholdingTaxRates`, `municipalWithholdings`, totales, PDF e historial.

## 6. Contrato HTTP

Campo NUEVO `withholdingBreakdown` en toda respuesta que hoy trae `withholdings` (detalle, edición, vistas previas y
conversión de cotización y pedido; el plan enumera los endpoints). Arreglo en el orden de la fuente:

```jsonc
{
  "taxRateId": "…",
  "name": "Retefuente servicios",
  "type": "Retefuente",
  "country": "CO",
  "percentage": 4.0,
  "catalogPercentage": 4.0,
  "percentageOverridden": false,
  "cityId": null,
  "applies": false,
  "reason": "BelowMinimumBase",
  "base": 150000.00,             // el subtotal sin IVA que se comparó
  "minimumBase": 4,              // null = sin base mínima
  "minimumBaseUnit": "UVT",      // null = base en dinero o sin base
  "minimumBaseAmount": 199196.00,// el umbral en dinero (Threshold); null sin valor de UVT
  "taxUnitValue": 49799.00,      // null sin unidad
  "taxUnitYear": 2025,           // null sin unidad o fila legada (§8.2)
  "taxUnitOutdated": true,
  "amount": 0.00
}
```

Un frontend viejo ignora el campo. Un `reason` que el frontend no conozca se pinta como «No aplica» sin motivo (lado frontend).

## 7. Errores

Ninguno nuevo: el campo es de lectura.

## 8. Migración

### 8.1 Esquema (expansión)

`ALTER TABLE quotations.quotation_withholdings ADD tax_unit_year smallint NULL;` y lo mismo en `order_withholdings`.

### 8.2 Filas existentes [pendiente del owner, P2]

Propuesta [agente]: rellenar `tax_unit_year` donde `tax_unit_value` no es null, buscando en `catalog.tax_unit_values` el año
con ese país y ese valor (el mayor año ≤ el del documento). Lo que no case queda null: se muestra el valor sin año y sin aviso.

## 9. Pruebas (TDD: RED antes que GREEN)

Unitarias de dominio (sin contenedores):
- `WithholdingCandidates.Explain`: un caso por `reason`, más el orden (consumidor final gana sobre umbral).
- `For` = `Explain` filtrado: misma salida que hoy en los casos existentes.
- Umbral inclusivo: `subtotal == Threshold` → `Applied`.
- `ValueFor` devuelve el año: año exacto, caída al anterior, ninguno.
- `taxUnitOutdated`: año igual → false; anterior → true; null → false.

Integración filtrada por nombre (`--filter "FullyQualifiedName~…"`):
- Cotización con Retefuente servicios por debajo de 4 UVT: `withholdingBreakdown` trae la fila con `BelowMinimumBase`, y
  `withholdings` sigue vacío.
- Pedido convertido hereda el desglose.
- Documento de 2026 con sólo la UVT de 2025 cargada → `taxUnitYear: 2025`, `taxUnitOutdated: true`.
- Migración: una fila legada recibe su año.

Suite completa de los módulos tocados (Quotations, Catalog) sólo antes de mergear, y sólo si el owner la pide.

## 10. Despliegue

Backend primero: el campo nuevo no rompe al frontend actual. Después el frontend.

## 11. Decisiones pendientes del owner

- **P1. ¿Se refresca la UVT de un documento editable cuando el operador carga la del año?** Hoy no: la foto es fija
  (`WithholdingRate.cs:5-9`, decisión del spec 2026-11-10 §7.5). Sin refresco, el aviso sirve para los documentos nuevos pero la
  cotización que lo mostró sigue con la UVT vieja hasta que cambien cliente o empresa. Opciones: (a) no se refresca;
  (b) cada recálculo de una cotización editable o un pedido `Pending` vuelve a leer la UVT. Recomendación [agente]: (b).
- **P2. Relleno del año en filas existentes** (§8.2). Recomendación [agente]: sí, por coincidencia de valor.

# Alerta de UVT al convertir cotización en pedido — diseño backend

Fecha: 2026-11-16. Rama: `feature/alerta-uvt-conversion`. Spec gemelo en `qep-frontend` (mismo nombre).
Sigue a `2026-11-16-retenciones-por-uvt-design.md` (en adelante «spec UVT»).

Marcas: **[owner]** decidido por el owner en el brainstorming; **[agente]** decisión del agente, corregible.

## 1. Intención

Una cotización con retenciones de base mínima en UVT creada en un año (2026) y convertida en el siguiente (2027) copia
la foto de la UVT de 2026 al pedido (`Order.FromQuotation`, `Order.cs:417`). El pedido toma su año de `order.CreatedAt`
(`TaxUnitRefresh.DocumentDate`), así que nace con `taxUnitOutdated: true` y su primer guardado (`SaveOrderEdits`) refresca a
la UVT de 2027: la base mínima en dinero sube, alguna retención puede dejar de aplicar y el neto que paga el cliente
sube. El tenant no puede asumir ese excedente, y hoy el asesor convierte sin enterarse.

Además, el refresco de hoy depende de la suerte: un pedido que se aprueba o se prepara sin haberse guardado conserva la
UVT vieja para siempre, y el estado de pago (calculado contra el neto) puede cambiar solo cuando alguien lo guarda.

La cotización nunca avisa: su año es el de su `CreatedAt`, así que una cotización de 2026 con la UVT de 2026 da
`taxUnitOutdated: false` aunque se mire en 2027.

## 2. Objetivos

- El pedido nace con la UVT de su año: la conversión refresca antes de guardar **[owner, decisión 1 = A]**.
- El asesor ve, antes de confirmar, qué cambia: años y valores de la UVT, retenciones que cambian de monto o de veredicto,
  neto antes y después. Lo calcula el backend **[owner, decisión 2]**.
- Cada fila de `withholdingBreakdown` dice el año más nuevo cargado (`taxUnitLatestYear`), para que el frontend distinga
  «falta cargar la UVT», «se aplicará al guardar» y «el documento conserva su UVT» **[owner, decisión 4]**.

## 3. No objetivos

- Leyenda en el PDF o aviso al cliente final.
- Persistir la confirmación del asesor: la casilla «Ya le avisé al cliente» es sólo de interfaz **[owner, decisión 3]**.
- Guarda de carrera en el `POST` de conversión (§5.5) **[agente]**.
- Refrescar documentos cerrados: siguen con su foto (spec UVT §5.4).
- Cambiar el año de la cotización: sigue siendo el de su `CreatedAt`.

## 4. Restricciones verificadas

- `ConvertQuotationToOrderHandler` (`ConvertQuotationToOrder.cs`) no refresca: llama `Order.FromQuotation` y guarda.
- `TaxUnitRefresh.RefreshAsync(Order, lookup, documentDate, ct)` existe y devuelve si alguna fila cambió de valor o año.
  `UnitsAsync` no consulta nada si ninguna fila tiene base en unidades.
- `IQuotationWithholdingLookup.TaxUnitsAsync(countries, documentDate)` devuelve por país la `TaxUnitPhoto` (valor y año) que
  hoy daría `ValueFor`: la del año del documento o la mayor anterior. Un país sin ningún valor no está en el diccionario.
- `QuotationResponseComposer.ToBreakdownResponse` arma `WithholdingBreakdownResponse` para cotización y pedido
  (`OrderResponseComposer.cs:185`). `DocumentYearAsync` lee el calendario sólo si alguna fila trae año.
- Totales del pedido: `RetentionAmount` y `NetTotal` (`OrdersDtos.cs:50-51`).
- Permiso de convertir: `OrdersPermissions.OrderManage` (`OrderEndpoints.cs:195`).

## 5. Diseño

### 5.1 Una pieza compartida: armar el pedido en memoria [agente]

Se extrae de `ConvertQuotationToOrderHandler` lo que no tiene efectos: construir el `Order` con `Order.FromQuotation` y
refrescar su UVT con `TaxUnitRefresh.RefreshAsync(order, lookup, fecha de hoy del tenant)`. La usan el handler de
conversión (§5.2) y el de vista previa (§5.3), para que lo que se muestra y lo que se guarda no puedan divergir.

La fecha es `DocumentDates.LocalDate(calendar, now)` con el mismo `now` que recibe `FromQuotation` como `occurredAt`: es la
que después tendrá el pedido como `CreatedAt`, y la que usa `TaxUnitRefresh.DocumentDate` en los guardados siguientes.

La vista previa necesita un `Order` sin número ni comprobantes. El plan decide cómo (número vacío o marcador, lista de
comprobantes vacía); el número nunca sale del contador en la vista previa.

### 5.2 La conversión refresca [owner A]

`ConvertQuotationToOrderHandler` refresca la UVT del pedido recién creado antes de `SaveChangesAsync`. Sin historial propio
(spec UVT §5.4: el refresco es parte del recálculo). El lookup de UVT es una lectura a Catalog y va **antes** de abrir la
transacción del contador, como las demás lecturas del handler; el refresco en sí es dominio puro y puede ir adentro.

La cotización no se toca: pasa a `Converted` con su foto.

### 5.3 Vista previa de la conversión — endpoint NUEVO

`GET /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/preview` (mismo grupo que `POST …/order`), permiso
`OrdersPermissions.OrderManage`. No escribe, no toma el contador, no copia comprobantes.

Valida lo mismo que la conversión antes de pedir el número: cotización encontrada (404), `EnsureConvertibleToOrder` (422 con
los códigos de hoy). La elegibilidad del cliente y el tipo de persona **no** se validan en la vista previa [agente]: son
errores del `POST`, y el asistente ya los muestra ahí.

Respuesta 200:

```jsonc
{
  "retentionAmount": 0.00,     // del pedido refrescado
  "netTotal": 205000.00,       // del pedido refrescado: lo que el paso 1 compara contra los comprobantes
  "withholdingBreakdown": [ … ],  // del pedido refrescado, misma forma que en el detalle (con taxUnitLatestYear, §5.4)
  "taxUnitChange": null | {
    "taxUnits": [               // un elemento por país cuya UVT cambió de valor o de año
      { "country": "CO", "fromYear": 2026, "fromValue": 49799.00, "toYear": 2027, "toValue": 52374.00 }
    ],
    "withholdings": [           // sólo las filas cuyo monto o `applies` cambió; mismo orden que el desglose
      {
        "taxRateId": "…", "name": "Retefuente servicios", "type": "Retefuente",
        "minimumBase": 4, "minimumBaseUnit": "UVT",
        "fromMinimumBaseAmount": 199196.00, "toMinimumBaseAmount": 209496.00,
        "base": 205000.00,
        "fromApplies": true, "toApplies": false,
        "fromAmount": 6000.00, "toAmount": 0.00
      }
    ],
    "fromNetTotal": 199000.00,
    "toNetTotal": 205000.00
  }
}
```

- `taxUnitChange` es `null` cuando ninguna fila cambió de valor ni de año de UVT (el refresco devolvió `false`).
- «Antes» es el pedido tal como lo arma `FromQuotation` (la foto de la cotización), «después» el mismo pedido refrescado.
  Las dos se calculan en el backend; el frontend no resta ni compara montos.
- `taxUnits` cambia y `withholdings` vacío con netos iguales es posible (el subtotal queda lejos de la base mínima): el
  frontend no muestra alerta en ese caso (spec frontend).
- Nombres de campo: los del plan pueden ajustarse a las convenciones del módulo, pero los conceptos son estos.

### 5.4 `taxUnitLatestYear` en `withholdingBreakdown` [owner, decisión 4]

Campo NUEVO en `WithholdingBreakdownResponse`, en toda respuesta que hoy trae el desglose (cotización y pedido, detalle,
edición, vistas previas, conversión y §5.3):

- `int?`: el año de la `TaxUnitPhoto` que `TaxUnitsAsync` devuelve para el país de la fila con la fecha del documento (la
  misma con que se decide `taxUnitOutdated`). Es el año que tomaría la fila si hoy se refrescara.
- `null` si la fila no tiene base en unidades, o si el país no tiene ningún valor cargado.
- Lectura perezosa, como `DocumentYearAsync`: sólo se consulta Catalog si alguna fila trae `minimumBaseUnit`. Una consulta
  por respuesta, con los países distintos de las filas.

Con eso el frontend distingue (sin calcular):

| `taxUnitOutdated` | `taxUnitLatestYear` vs `taxUnitYear` | Documento | Significa |
|---|---|---|---|
| true | igual (o null) | cualquiera | no está cargada la UVT del año del documento |
| true | mayor | editable | ya está cargada; se aplicará al guardar |
| true | mayor | cerrado | conserva su UVT a propósito |

### 5.5 Sin guarda de carrera [agente]

Si soporte carga la UVT nueva entre la vista previa y el `POST`, el pedido igual nace correcto (refresca al guardar) y la
respuesta del `POST` trae el neto real, que el paso 3 del asistente muestra. Es un caso de una vez al año; una guarda
exigiría un código de error nuevo.

## 6. Errores

Ninguno nuevo. La vista previa reusa 404 (`QuotationNotFound`) y los 422 de `EnsureConvertibleToOrder`.

## 7. Migración

Ninguna: no hay columnas nuevas.

## 8. Pruebas (TDD: RED antes que GREEN)

Unitarias (sin contenedores):
- La pieza de §5.1: cotización con foto 2026 y UVT 2027 cargada → pedido con `TaxUnitYear` 2027 y umbral recalculado; sin
  UVT 2027 → conserva 2026; sin filas con unidad → no consulta el lookup.
- El cálculo de `taxUnitChange`: `null` sin cambio; fila que deja de aplicar; fila que cambia de umbral pero sigue
  aplicando con el mismo monto → no aparece en `withholdings`.
- `taxUnitLatestYear`: país con valor del año, con valor anterior, sin valor; fila sin unidad → null.

Integración filtrada por nombre (`--filter "FullyQualifiedName~…"`):
- Convertir una cotización de 2026 en 2027 con la UVT 2027 cargada → pedido con `taxUnitYear: 2027`,
  `taxUnitOutdated: false`, neto recalculado.
- `GET …/order/preview` del mismo caso → `taxUnitChange` con los dos años, la fila que deja de aplicar y los dos netos; no
  gasta consecutivo (convertir después da el número esperado); 403 sin `quotations.order.manage`; 422 sobre una convertida.
- Detalle de un pedido con UVT 2026 congelada y 2027 cargada → `taxUnitLatestYear: 2027`.

El año del tenant se fija con el reloj de pruebas que ya usan las de retenciones por UVT [verificar en el plan].
Suite completa de Quotations sólo si el owner la pide.

## 9. Despliegue

Backend primero. El campo y el endpoint son nuevos y no rompen al frontend actual; el refresco al convertir cambia el
resultado de la conversión (para bien) aunque el frontend todavía no muestre la alerta.

## 10. Decisiones del owner (2026-11-16)

1. El pedido nace con la UVT de su año (refresco al convertir) — A.
2. La alerta muestra años y valores de UVT, sólo las retenciones que cambian y el neto antes/después; lo calcula el backend
   con una vista previa de la conversión.
3. Casilla obligatoria «Ya le avisé al cliente», sólo de interfaz.
4. El backend agrega `taxUnitLatestYear` por fila.
5. Construcción: pieza compartida + endpoint de vista previa (no dry-run del `POST`, no cálculo en el `GET` de la
   cotización).

# Descuento global por escala en cotización y pedido

Fecha: 2026-09-22
Estado: diseño aprobado, pendiente de plan de implementación
Repositorio: `qep-backend` (el select vive en `qep-frontend` y va aparte)

## Problema

Hoy el descuento de una línea lo decide su propia cantidad contra las escalas del producto
(`QuotationDiscountResolver`), con el rescate por agrupación que agrega
`QuotationScaleGroupPricing`. No hay forma de que el asesor diga "esta cotización va con el
descuento del tramo de mil" cuando las líneas, una por una, no llegan a ese volumen.

El descuento nunca es editable a mano y eso no cambia: lo que se agrega es la posibilidad de
**elegir qué tramo del catálogo se usa**, no de escribir un porcentaje.

## Decisiones tomadas

| Decisión | Valor | Por qué |
| --- | --- | --- |
| Qué identifica el tramo | El piso (`FromUnit`) | Las escalas son por producto; el piso es el único dato comparable entre productos distintos. |
| Restricción de cantidad | Se sigue exigiendo | El global mueve **qué** tramo se usa, no afloja `Multiple` ni `PackagingUnit`. |
| Contra el descuento propio | Gana el mayor | El global es un piso, no un techo: nadie pierde descuento por activarlo. |
| Origen de las opciones | Los productos de esta cotización | No se ofrece un piso que ningún producto de la cotización tiene. |

## Alcance

El pedido **no tiene líneas propias**: `AddOrderItemsHandler` agrega sobre
`quotation.AddItemAfterConversion`. Cotización y pedido comparten el agregado `Quotation`, así
que "global en la cotización y en el pedido" es un solo campo y un solo punto de aplicación.

Los ocho llamadores de `QuotationPricingRecalculation.ApplyAsync` ya cubren los dos caminos:

    AddQuotationItem, UpdateQuotationItem, RemoveQuotationItem, BatchUpdateQuotationItems,
    QuotationEditsApplication, AddOrderItems, SaveOrderEdits, PreviewOrderEdits

## Enfoque elegido

Campo persistido en el agregado, aplicado dentro de la resolución de descuentos.

Descartados:

- **Parámetro efímero por request.** El recálculo lo disparan ocho handlers; el piso tendría
  que viajar en todos, y una cotización reabierta perdería el criterio con que se valorizó.
- **Escribir el descuento a mano en cada línea.** Rompe "el descuento nunca es editable a
  mano" y el primer recálculo lo pisa.

## 1. Dominio

`Quotation.GlobalScaleFloor` (`int?`). `null` significa sin descuento global.

Dos mutadores, espejando el par que ya existe para las líneas:

- `SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)` — pasa por
  `EnsureEditable()`, o sea `Draft` y `Sent`.
- `SetGlobalScaleFloorAfterConversion(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)`
  — para el pedido, mismo criterio que `AddItemAfterConversion`. El handler comprueba que el
  pedido esté en `Pending`.

Los dos suben `Version`, `UpdatedAt` y `UpdatedBy`: es una edición del encabezado. Distinto de
`ApplyGroupDiscounts`, que a propósito no toca la versión porque es recálculo y no edición.

Historial: entrada `QuotationHistoryEventType.Edited` con un resumen nuevo,
`QuotationChangeSummary.GlobalScaleFloorChanged(int? from, int? to)`.

Auditoría: `quotation.quotation.global_scale_changed`, publicada por
`IQuotationAuditPublisher` en la misma unidad de trabajo, como el resto.

## 2. Persistencia

Una sola migración, dos columnas, las dos en el esquema `quotations`:

| Tabla | Columna | Tipo |
| --- | --- | --- |
| `quotations.quotations` | `global_scale_floor` | `integer NULL` |
| `quotations.quotation_items` | `discount_origin` | `text NOT NULL`, default `Own` |

La segunda es la que exige `QuotationItemDto.DiscountOrigin` (ver §3): `ToDto` lee la línea
persistida y no el resultado del recálculo, así que el origen tiene que quedar guardado.

`discount_origin` va como texto y no como entero, igual que `QuotationHistoryEventType`
(`HasConversion<string>()`): sumar un valor al enum no va a necesitar migración. Las filas que
ya existen quedan en `Own`, que es exactamente lo que son — se valorizaron antes de que
existiera el global, y la agrupación no marcaba nada.

Migración generada con el factory de diseño — `Api.csproj` no referencia
`Microsoft.EntityFrameworkCore.Design`:

```powershell
dotnet ef migrations add AddQuotationGlobalScaleFloor --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

## 3. Resolución del descuento

`QuotationScaleGroupPricing.Resolve` recibe un tercer parámetro `int? globalFloor`.

Por línea, un tercer candidato después del propio y del de grupo:

```
scale := la escala del producto de la línea cuyo FromUnit == globalFloor
  si no existe   -> el producto no participa del global
  restricción    -> QuotationScaleRestrictionRule.Evaluate(scale, cantidad PROPIA de la línea)
                    no satisfecha -> no participa
  gana si scale.Discount > el mejor descuento hasta ahora  (estrictamente)
```

La comparación estricta es la misma regla que ya usa `Upgrade`: con empate gana lo que la
línea consiguió antes, así no queda marcada con un origen que no le cambió nada.

Orden de evaluación: propio, después grupo, después global. Con empate entre grupo y global
queda el grupo.

### Origen del descuento

`QuotationLinePricing.Grouped` (bool) pasa a
`QuotationDiscountOrigin { Own, Group, GlobalFloor }`.

Su documentación dice que `Grouped` "viaja a la respuesta", pero hoy es falso: `grep -rn
"Grouped" src/` no da ningún uso fuera de `QuotationScaleGroupPricing.cs`. Es un campo muerto,
así que cambiarle la forma no rompe ningún contrato. Tres estados mutuamente excluyentes no
son dos bools.

El origen sí se expone ahora: `QuotationItemDto.DiscountOrigin` (string, el nombre del enum —
los enums viajan con su nombre porque el diccionario lo tiene el frontend). Sin esto la
pantalla no puede explicar por qué una línea de 3 unidades descuenta 12%, que es justamente lo
que este feature introduce.

Esto obliga a que `QuotationItem` guarde el origen junto al descuento, porque `ToDto` lee la
línea persistida y no el resultado del recálculo. `ApplyGroupDiscounts` pasa a recibir el
origen además del porcentaje, y la línea gana una columna para él.

### Mínimo de compra

Sin cambios. `QuotationMinimumPurchase` se evalúa sobre el total resultante, después de
aplicar todos los descuentos, y si no alcanza los quita todos — incluido el global.

## 4. API

Prefijos reales: `/api/v1/tenants/{tenantId:guid}/quotations` y
`/api/v1/tenants/{tenantId:guid}/orders`.

| Verbo | Ruta | Handler |
| --- | --- | --- |
| `PUT` | `/quotations/{quotationId:guid}/global-scale` | `SetQuotationGlobalScaleHandler` |
| `PUT` | `/orders/{orderId:guid}/global-scale` | `SetOrderGlobalScaleHandler` |
| `GET` | `/quotations/{quotationId:guid}/global-scale-floors` | `GetQuotationGlobalScaleFloorsHandler` |

`PUT` y no `PATCH`: Vercel no soporta `PATCH` en el rewrite (`d3aff26`).

Cuerpo del `PUT`: `{ "floor": 1000 }`. `{ "floor": null }` lo quita.

Respuesta del `PUT`: la cotización completa ya recalculada (`QuotationDto`), y en el caso del
pedido el par `OrderDto` + `QuotationDto` que ya devuelve `AddOrderItems`. Una colección que
se edita vuelve entera y en orden, que es lo que el formulario repinta.

Respuesta del `GET`: `{ "floors": [100, 500, 1000] }`, ordenado ascendente, **completo incluso
vacío**. Sale de los `FromUnit` distintos de las escalas de los productos que la cotización
tiene cargados, vía `IQuotationProductPricingLookup.FindManyAsync`.

### Por qué los pisos van en un endpoint propio y no en `QuotationDto`

`QuotationMapping.ToDto` es una extensión pura sobre el agregado con alrededor de diez
llamadores, y la mitad de ellos —`SaveQuotation`, `ChangeQuotationClient`, `SendQuotation`,
`VoidQuotation`— no tiene el lookup del catálogo. Meter el arreglo ahí obliga a cablear el
lookup en handlers que no lo necesitan, o a devolver un arreglo vacío que miente: "no hay
pisos disponibles" cuando en realidad nadie los buscó. El select lo abre el asesor
deliberadamente, así que un `GET` en ese momento es barato.

`QuotationDto.GlobalScaleFloor` (`int?`) sí va en el DTO: es estado del agregado y `ToDto` ya
lo tiene a mano, sin llamadores nuevos que tocar.

### Permisos

Los existentes: `QuotationsPermissions`/`OrdersPermissions` según la ruta, igual que el resto
de cada grupo. **No se crea un permiso nuevo.** Un permiso necesita dos mitades —la constante
y su política en `AddAuthorization`— y olvidar la segunda da 500, no 403.

Como en todo el módulo, el handler revalida tenant y permiso antes de tocar el repositorio
(`QuotationsAuthorization.EnsureAuthorized`) y devuelve 403, nunca 404.

### Errores

| Código | Cuándo |
| --- | --- |
| `quotation.global_scale.floor_not_available` | 422. El piso pedido no es el `FromUnit` de ninguna escala de ningún producto de la cotización. |
| `order.order.not_pending` | 422. Ya existe; lo reusa el `PUT` del pedido. |

El piso llega de una lista que el propio backend acaba de dar, así que mandar uno que no está
es un bug del cliente y se dice como tal. Es un código de dominio: sin mapa `errors`, porque
no hay un campo del formulario al que apuntar.

Validación de forma en FluentValidation: `floor`, cuando no es nulo, `GreaterThanOrEqualTo(1)`
— mismo piso que `PriceScaleRequestRules.FromUnit`.

## 5. Caso que queda vivo a propósito

Agregar después una línea de un producto que no tiene ese tramo **no** limpia el piso elegido.
El piso sigue puesto y esa línea simplemente no lo recibe. Limpiarlo solo sería borrar una
decisión del asesor por un efecto colateral de otra acción.

Lo mismo al revés: si se quitan todas las líneas que respaldaban el piso, el piso queda. La
pantalla lo muestra igual, y el `GET` de pisos devolverá una lista que ya no lo contiene — el
front decide si lo marca como sin efecto.

## 6. Pruebas

TDD: RED antes que GREEN, con evidencia literal de las dos corridas.

### Unitarias — `QuotationScaleGroupPricingTests`

1. Producto sin escala con ese `FromUnit`: la línea resuelve como hoy.
2. El global gana: línea de 3 unidades, tramo global de 1000 al 12%, sin tramo propio → 12%.
3. El propio gana: línea de 2000 que cae sola en 1000-5000 al 12%, global en el tramo 100 al
   5% → 12%.
4. Empate: el origen queda en `Own`, no en `GlobalFloor`.
5. El múltiplo bloquea: tramo global con `Multiple = 10` y línea de 25 → sin descuento global.
6. `PackagingUnit` bloquea igual.
7. Global y grupo conviviendo: el mayor gana; con empate queda `Group`.
8. `globalFloor = null`: resultado idéntico al de hoy (prueba de no regresión).

### Integración

1. El `PUT` fija el piso y la respuesta trae las líneas ya recalculadas.
2. El piso sobrevive la conversión a pedido.
3. El `PUT` del pedido lo cambia estando `Pending`, y lo rechaza en cualquier otro estado.
4. El `PUT` de cotización lo rechaza en `Converted`.
5. `422 quotation.global_scale.floor_not_available` con un piso inexistente.
6. El `GET` de pisos devuelve los `FromUnit` distintos, ordenados y sin repetir.
7. El `GET` de pisos devuelve `[]` en una cotización sin líneas.
8. El historial anota la entrada `Edited` con su resumen.

Cuidado conocido: agregar un campo requerido o una precondición rompe las pruebas de
integración que arman el cuerpo a mano. Acá `floor` es opcional y el campo nuevo es nullable,
así que no debería haber barrido — pero `ApplyGroupDiscounts` sí cambia de firma, y hay que
revisar sus llamadores, que son los ocho del recálculo.

El stub de desarrollo concede sólo los permisos de tenancy por defecto: las pruebas que
ejercen estas rutas tienen que pedir su permiso por `X-Permissions`, o el 403 va a venir del
permiso faltante y no de lo que creen estar probando.

## 7. Fuera de alcance

- El `<select>` en la SPA, que vive en `qep-frontend` y necesita su propio trabajo: consumir
  el `GET` de pisos, mandar el `PUT` y mostrar `DiscountOrigin` por línea.
- Cualquier noción de piso global a nivel tenant o catálogo. Las opciones salen de los
  productos de la cotización y de ningún otro lado.
- Tocar `QuotationDiscountResolver`, que sigue resolviendo por cantidad y no sabe del global.

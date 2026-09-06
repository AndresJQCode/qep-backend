# Agrupación de escalas de precio entre líneas de una cotización

**Fecha:** 2026-09-06
**Módulos:** Catalog, Quotations
**Estado:** aprobado, pendiente de plan de implementación

## Problema

Una escala de precio (`CAT-09`) restringe qué cantidades son pedibles: `Multiple` con su paso
o `PackagingUnit` con el tamaño del empaque. Desde `5a76b07` esa restricción se comprueba
**por línea**, y una línea que no la cumple recibe 422.

El negocio no la cuenta así cuando la restricción es `Multiple`. La cantidad mínima que exige
un proceso de producción se cumple **entre varios productos**: tres líneas de 10, 8 y 12
unidades sobre la misma escala de a 3 suman 30, que sí es múltiplo de 3, y las tres deben
recibir su descuento aunque ninguna lo cumpla por separado.

Hoy eso es imposible, y no por falta de un campo: con validación bloqueante por línea el
destino es **inalcanzable**. Para llegar a A=10 / B=8 no existe ningún camino de estados
intermedios válidos, porque cada línea se rechaza al agregarse.

## Decisiones

Todas tomadas con el developer durante el brainstorming del 2026-09-06.

### D1 — La restricción deja de bloquear: decide si la escala aplica

Cuando la restricción `Multiple` no se cumple, la línea **se guarda igual**, con descuento 0 y
precio base — exactamente como ya ocurre con una cantidad que no cae en ninguna escala. No hay
422. El motivo del incumplimiento viaja en la respuesta.

Es lo único que hace construible el grupo: A=10 se agrega solo (sin descuento), luego B=8 y
C=12, y al completarse el total las tres pasan a tener el suyo.

### D2 — El grupo son las líneas de una escala idéntica y agrupable

Una `PriceScale` pertenece a un `Product`: no existe una escala compartida entre productos. Dos
líneas agrupan cuando sus escalas aplicables coinciden en `FromUnit`, `ToUnit` y `Multiple`, la
restricción es `Multiple` y las dos tienen la agrupación activada.

El flag es condición de pertenencia, no del grupo: si dos escalas son idénticas en los tres
campos pero sólo una tiene la agrupación activada, **no** agrupan. La que la tiene forma grupo
con las demás que la tengan —sola, si no hay ninguna— y la que no la tiene valida su múltiplo
por línea.

El **descuento queda fuera de la clave del grupo**: es parámetro de cada línea. Dos productos
con 5-48 múltiplo 3 agrupan aunque uno descuente 10% y el otro 15%, y cada línea conserva el
descuento de su propia escala. La agrupación decide **si** la escala aplica, nunca **cuál**.

`Quotation.AddItem` prohíbe el mismo producto dos veces
(`quotation.item.duplicate_product`), así que un grupo nunca tiene líneas repetidas.

### D3 — La escala de cada línea la decide su propia cantidad, nunca la suma

Primero cada línea resuelve su escala por su cantidad (`QuotationDiscountResolver`). Recién
después, y sólo para validar el múltiplo, se agrupan las que comparten escala. La suma **no**
mete una línea en una escala que su cantidad no alcanza, ni cambia el descuento que le
corresponde, ni se comprueba contra `ToUnit`.

### D4 — `Multiple` se cuenta sobre la cantidad cruda

`5a76b07` contaba el paso **desde `FromUnit`** (`(quantity - FromUnit) % multiple`), heredado
del CRM: "la cantidad de entrada al rango siempre entra". Se revierte. `Multiple` se cuenta
ahora sobre la cantidad cruda, **agrupada o no**.

Consecuencia verificada, no colateral: en una escala 5-48 paso 3, hoy 8 unidades es válida
(8−5=3) y pasa a no serlo (8 % 3 = 2). Es el ejemplo explícito del requisito.

### D5 — `PackagingUnit` no se toca

Su cálculo (`quantity % packagingUnit`, por línea, sin agrupar) y su **422**
(`quotation.item.quantity_not_packaging_unit`) quedan idénticos. El switch de agrupación no se
puede activar en una escala `PackagingUnit`.

Esto deja dos modelos de falla conviviendo —`Multiple` no bloquea, `PackagingUnit` sí— y es
deliberado: el requisito exige compatibilidad total con el comportamiento actual del empaque.

**Ese 422 se queda exactamente donde está hoy: en la línea que el comando toca.** Hoy sólo se
valida la línea que se agrega o se edita, y así sigue. El recalculador, que ahora recorre
*todas* las líneas, **nunca lanza**: si encontrara una línea vieja que incumple el empaque
—sólo posible si la escala cambió en el catálogo después de agregarla— le quita el descuento,
no aborta la operación. Lo contrario haría que quitar una línea sana fallara con el 422 de otra
línea, un error que además nadie puede corregir desde la cotización.

### D8 — La agrupación rescata, nunca hunde

Añadida el 2026-09-06, después de implementar la Task 4. Resuelve la tensión que el requisito
dejaba abierta entre su sección 5 ("una línea que cumple no debe invalidarse porque otra no
cumpla") y su Caso 2, que sumaba 6 + 10 = 16 y hacía caer a las dos.

**Gana la sección 5.** Una línea que cumple el múltiplo por su propia cantidad conserva su
escala aunque el total del grupo falle, y ni siquiera queda marcada como agrupada. Sólo las que
no cumplen solas se juegan al total.

No le cambia el veredicto a ninguna otra línea, y eso es aritmética, no criterio: con el
múltiplo contado sobre la cantidad cruda (D4), una línea que cumple es congruente con 0 módulo
el paso, así que entra o sale de la suma sin mover el resto. El caso estrella del requisito
—10 + 8 + 12 = 30— sigue dando descuento a las tres; lo único que cambia es que la de 12, que
cumplía sola, llega por su cuenta y no por el grupo.

**El total sí sigue sumando todas las líneas del grupo**, incluidas las que cumplen: es el
número que la pantalla muestra para explicar el faltante, y es como lo cuenta el requisito.

Lo que esta decisión descarta es el otro modelo defendible: que la restricción represente un
lote de producción y que, si el total no cierra, nadie del grupo reciba el precio de escala.
Decisión explícita del developer.

### D6 — Quitar una línea también recalcula

El requisito nombra agregar y actualizar. Quitar cambia el total del grupo igual: borrar B de
10+8+12=30 deja 22, que no es múltiplo de 3. Sin recálculo, la cotización queda persistida con
descuentos que ya no corresponden. `RemoveQuotationItem` entra al alcance.

### D7 — Alcance del recálculo

Corre en cada mutación de línea mientras la cotización sea editable (`Draft` o `Sent`,
`EnsureEditable`). Una cotización convertida, anulada o vencida no se toca.

## Diseño

### Catalog — la configuración

`PriceScale` gana `AllowGrouping` (`bool`, no nulo, default `false`) y `PriceScaleInput` el
campo equivalente, **opcional con default `false`**: hacerlo requerido tumbaría las pruebas de
integración de Catalog que arman el cuerpo a mano (gotcha conocido del `CLAUDE.md`).

`PriceScale.Create` lo valida junto a la exclusión que ya hace: `true` con restricción
`PackagingUnit` es `catalog.product.price_scale.grouping_not_allowed`. Rechazarlo es lo que
impide guardar un dato que ninguna regla honra.

Migración con el factory de diseño sobre `CatalogDbContext`, columna `NOT NULL DEFAULT false`:
las escalas existentes quedan sin agrupación, que es el comportamiento actual.

Archivos: `PriceScale.cs`, `ProductPricing.cs`, `CatalogDtos.cs`, `ProductPricingMapping.cs`,
`ProductMapping.cs`, `CatalogDbContext.cs`, migración nueva, `Seed/Data/catalog-products.json`.

### El puerto hacia Quotations

`QuotationPriceScaleRef` gana `AllowGrouping`; `QuotationPriceScaleMapping` (Bootstrapper) lo
traduce. Es el único puente: `Modules.Quotations.Application` sigue sin referenciar Catalog, y
`QuotationsLayerTests` lo sigue verificando.

### `QuotationScaleRestrictionRule` — dos modelos de falla

- `PackagingUnit`: intacto, sigue lanzando `QuotationsDomainException` → 422.
- `Multiple`: deja de lanzar. Devuelve un resultado —cumple / no cumple, con el total evaluado
  y cuánto falta para el siguiente múltiplo— y cuenta sobre la cantidad cruda.

El guard actual contra un `Multiple` no positivo se conserva: una fila que desmienta la
invariante de Catalog no puede bloquear una línea con un dato que nadie corrige desde la
cotización, ni provocar una división por cero.

### `QuotationScaleGroupPricing` — el recalculador

Servicio nuevo en `Modules.Quotations.Application`. Recibe **todas** las líneas resultantes de
la mutación y las escalas de sus productos, y ejecuta:

1. Por línea: `QuotationDiscountResolver.Resolve` elige su escala por su propia cantidad.
2. `PackagingUnit` → la misma cuenta de hoy, pero **sin lanzar**: cumple o no cumple (ver D5).
3. `Multiple` + `AllowGrouping` → agrupa por (`FromUnit`, `ToUnit`, `Multiple`), suma las
   cantidades originales, valida el múltiplo sobre el total.
4. `Multiple` sin agrupación → valida por línea.
5. Devuelve, por línea, el descuento efectivo (el de **su** escala si cumple, `0` si no) y el
   motivo del incumplimiento.

`QuotationProductPricingResolver` se queda con lo que resuelve bien por línea —producto existe,
está activo, tiene precio COP, su impuesto, y el 422 de `PackagingUnit` sobre la línea que el
comando toca— y **deja de decidir el descuento**. Un solo lugar
lo calcula: dos lugares divergen en cuanto uno se actualiza y el otro no.

Las escalas se leen en lote. `IQuotationProductLookup.FindManyAsync` ya devuelve
`QuotationProductRef` con sus `Scales` y el composer ya lo llama por cada respuesta, así que no
se agrega un N+1.

### El agregado

Método nuevo `Quotation.ApplyItemDiscounts(...)`, con `EnsureEditable()`, que reasigna los
descuentos de las líneas y recalcula los totales. `AddQuotationItem`, `UpdateQuotationItem` y
`RemoveQuotationItem` lo llaman tras mutar.

### La respuesta (BFF)

`QuotationItemPriceScaleResponse` gana `AllowGrouping`. Cada línea lleva además el estado de su
restricción: si la escala se aplicó y, si no, el código, el total evaluado (línea o grupo), el
múltiplo y cuánto falta.

El motivo se escribe en el DTO: sin ese estado, la pantalla muestra un precio sin descuento y
nadie sabe por qué — y como la restricción ya no da 422, no queda ningún otro canal por donde
enterarse.

## Pruebas

TDD, RED antes que GREEN, con evidencia literal de ambos.

**Unitarias nuevas** — `QuotationScaleGroupPricingTests`: los cuatro casos del requisito
(agrupada que cumple, agrupada que no, no agrupada por línea, `PackagingUnit` sin cambios),
más grupos distintos coexistiendo en una cotización y el efecto de quitar una línea.

**Unitarias a reescribir** — `QuotationScaleRestrictionRuleTests` (100 líneas) afirma el conteo
desde `FromUnit` de D4. Las aserciones se reescriben, no se adaptan.

**Integración** — `QuotationItemApiTests`: armar 10+8+12 y verificar el descuento en las tres;
quitar una y verificar que se revierte; verificar que `Multiple` incumplido devuelve 200 con la
línea guardada sin descuento, no 422. Parte de las 191 líneas actuales afirma el 422 que D1
retira.

**Barrido obligatorio** de cuerpos crudos en `ProductPricingApiTests` por el campo nuevo, aunque
sea opcional.

**Arquitectura** — sin cambios de capa. Ningún archivo de `ArchitectureTests` se agrega ni se
modifica.

## Riesgos

- **D4 revierte comportamiento mergeado el 2026-09-06** (`5a76b07`, del día anterior). Una
  cotización creada entre ese merge y este cambio pudo guardarse con un descuento que la regla
  nueva ya no concede. No se migran datos: las cotizaciones existentes conservan sus descuentos
  hasta que alguien edite una línea, momento en el que se recalculan.
- **Dos modelos de falla en el mismo formulario** (D5). Es el costo aceptado de no tocar
  `PackagingUnit`.
- **Recalcular puede mover el descuento de una línea que nadie tocó.** Si la escala de su
  producto cambió en el catálogo desde que se agregó, la mutación de *otra* línea la actualiza
  de rebote. Es inherente a que el recálculo exista y vale igual para `Multiple` y para
  `PackagingUnit`; el precio unitario, en cambio, sigue siendo el snapshot del día que se
  agregó la línea (`QuotationItem.UnitPrice`) y no se toca.
- **El descuento de una línea pasa a depender de otra.** Cualquier camino que mute líneas sin
  pasar por el recalculador deja totales incorrectos. Por eso el descuento sale de un solo
  servicio y `RemoveQuotationItem` entra al alcance.

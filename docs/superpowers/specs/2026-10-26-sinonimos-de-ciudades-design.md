# Sinónimos de ciudades

**Fecha:** 2026-10-26
**Módulos:** Geography (sinónimos, bandeja de pendientes, endpoints de operador), Shopify (reporta lo que no casa y aplica la
corrección), Quotations y Customers (reciben la corrección por puertos), Bootstrapper (adaptadores), Authorization (permisos de
operador).
**Repos:** `qep-backend` y `qep-frontend` (pestaña «Ciudades» de la consola de operador).
**Estado:** borrador para revisión. Las reglas de §2 son del owner (conversación del 2026-10-26). Las que tomó el spec están en §9
(D-C1…), a ratificar.
**Bocetos:** canvas «Sinónimos de ciudades», https://claude.ai/artifact/Y33VQAiihySYBj6stgcrce (aprobados por el owner el
2026-10-26).

## 1. Contexto

Un pedido de Shopify trae la ciudad de envío como **texto libre**. Hoy QEP la busca en Geography (DIVIPOLA, solo municipios) con
igualdad exacta después de normalizar, contra el nombre que se muestra («CALI») y el oficial del DANE («SANTIAGO DE CALI»).
Normalizar quita tildes, mayúsculas y espacios de los extremos (`NameMatching.Normalize`). Verificado en el código el 2026-10-26:

| Paso | Dónde | Qué hace |
|---|---|---|
| Departamento | `ColombiaProvinceCodes.DepartmentNameFor` | `provinceCode` ISO (`VAC`, `ANT`) → nombre DIVIPOLA. Si no resuelve, prueba con `province` como nombre |
| Ciudad | `ShopifyCustomerResolver.ResolveCityAsync` → `ShopifyGeographyLookup` → `CityRepository.FindByNameAsync` | Igualdad normalizada dentro del departamento |
| Sin ciudad: pedido | `ShopifyOrderImporter.ShippingPartyAsync` | No crea la parte de envío; `Notes` agrega «Dirección de envío de Shopify: …» con la dirección cruda |
| Sin ciudad: cliente | `ShopifyCustomerResolver.CompleteDataAsync` | El cliente nace `Incomplete` aunque traiga documento, dirección y clasificación |

El pedido **no se frena**: entra, pero sin parte de envío. Esto hace que la columna «Ciudad Coordinadora» del Excel de pedidos salga
vacía, y la guía de la transportadora se arma a mano. «CALLI», «Cali, Valle», «Bogota DC» o «Medelin» no casan, y hoy no hay forma
de enseñarle al sistema que sí son una ciudad conocida sin editar código y desplegar.

`CityRepository.FindByNameAsync` lo usan también la importación de clientes por Excel (`CustomerGeographyLookup.FindCityByNameAsync`)
y otros consumidores de Geography: cambiarlo cambia todos.

## 2. Reglas del owner (2026-10-26)

- **O1.** Lo que no casa queda en una **bandeja de pendientes** y una persona decide. El sistema **sugiere** ciudades por parecido,
  pero **nunca asigna solo**, aunque la similitud sea alta.
- **O2.** La bandeja la administra el **operador de QCode** desde la consola de operador («usuario de plataforma»), no los tenants.
  Un sinónimo es global: vale para todos los tenants.
- **O3.** Confirmar un sinónimo **corrige lo que ya entró**, pero solo los pedidos en **`Pending`**. `Approved`, `Invoiced` y
  `Cancelled` no se tocan.
- **O4.** El operador ve la **lista de sinónimos** y puede **corregir** o **borrar** uno.
- **O5.** Corregir un sinónimo (de CALIMA a CALI) vuelve a corregir los pedidos `Pending` que ese sinónimo había corregido. Los que
  ya no están en `Pending` no se tocan, pero la pantalla le dice al operador cuáles son, en qué tenant y con qué número.
- **O6.** **Borrar un sinónimo no deshace** lo que ya corrigió. Solo deja de casar: el próximo pedido con ese texto vuelve a la
  bandeja.
- **O7.** Los bocetos del canvas son la referencia de la pantalla (cuatro artboards: bandeja, confirmar, sinónimos, corregir).
- **O8.** Corregir un sinónimo también corrige los pedidos que **casaron por ese sinónimo al importarse**, después de confirmado, no
  solo los que arregló el día que se confirmó. Con las mismas reglas de O5: solo `Pending`, y lo demás se lista.

## 3. Objetivos, no-objetivos y antiobjetivos

**Objetivos**

1. Que un texto de ciudad mal escrito o con otro nombre se resuelva **una vez** y quede resuelto para siempre, en todos los tenants.
2. Que los pedidos `Pending` y los clientes afectados se arreglen solos al confirmar, sin que nadie tenga que buscarlos.
3. Que el operador sepa, antes de confirmar, exactamente qué va a cambiar (impacto del artboard 2).

**No-objetivos**

- Administrar Geography en sí: renombrar, crear o borrar ciudades o departamentos. Siguen saliendo del archivo del DANE y de
  `common-names.json`.
- Ciudades fuera de Colombia (la dirección extranjera ya viaja como texto en `CityName`).
- Corregir pedidos que ya no están en `Pending` (O3).
- Que otras fuentes (POS, WhatsApp, importación por Excel) **alimenten** la bandeja. La importación por Excel sí **usa** los
  sinónimos (D-C8).
- Resolver el departamento cuando Shopify no lo manda o manda uno desconocido (D-C11).

**Antiobjetivos** (fracasos aunque funcione)

- **Una ciudad equivocada es peor que una vacía.** Cualquier camino que asigne una ciudad sin que una persona la haya confirmado es
  un fracaso, por más alta que sea la similitud: la guía sale a otro municipio y nadie lo nota.
- Pisar lo que una persona ya arregló a mano: la parte de envío que editó un asesor o el cliente que alguien completó.
- Que un sinónimo «tape» una ciudad real: «CALIMA» no puede ser sinónimo de CALI, porque CALIMA existe en ese departamento.
- Que un fallo al corregir un pedido de un tenant tumbe la corrección de los demás o deshaga el sinónimo.

## 4. Comportamiento

### 4.1 Cómo casa una ciudad

Dentro del departamento ya resuelto, en este orden:

1. El nombre que se muestra (`City.Name`), normalizado.
2. El oficial del DANE (`City.DivipolaName`), normalizado.
3. Un sinónimo de ese departamento cuyo texto normalizado sea igual.

La normalización de los tres es la misma función (D-C1). El sinónimo **solo se consulta si los dos nombres no casan**, así que nunca
le gana a una ciudad real.

### 4.2 Qué entra a la bandeja

Un texto entra a la bandeja cuando:

- el pedido es de Colombia (`countryCode = CO`),
- el departamento resolvió,
- la ciudad **no** casó por ninguno de los tres caminos de §4.1,
- y **la ciudad es lo que impidió** la parte de envío o el alta completa del cliente. Si además falta el teléfono o la dirección,
  igual entra a la bandeja (la ciudad sigue sin resolver), pero confirmar no le crea la parte de envío a ese pedido (§4.4).

La bandeja agrupa por **(departamento, texto normalizado)**: cinco pedidos con «CALLI» en el Valle son **una** fila con contadores
(pedidos, tenants, primera y última vez) y el texto crudo más reciente como muestra («Cra 5 # 10-20, CALLI, Valle del Cauca, CO»).
Cada pedido queda registrado como una **ocurrencia** de esa fila (tenant, pedido, cliente si nació incompleto por la ciudad), que es
lo que luego se corrige.

Una ocurrencia es idempotente por pedido: reimportar el mismo pedido, o que lo traiga la conciliación, no la duplica.

### 4.3 Sugerencias

Para una fila pendiente, hasta tres ciudades del mismo departamento ordenadas por similitud con el texto, cada una con su porcentaje
(artboard 1). La similitud se calcula contra los dos nombres de la ciudad y gana el mayor. Si ninguna pasa un piso mínimo, la fila
dice «Sin sugerencias». El operador siempre puede buscar cualquier ciudad del departamento por nombre. Las sugerencias son solo
ayuda visual: no se persisten ni deciden nada (O1).

### 4.4 Confirmar un sinónimo

El operador elige la ciudad y ve el impacto antes de guardar (artboard 2):

| Grupo | Qué pasa |
|---|---|
| Pedidos `Pending` de las ocurrencias, sin parte de envío | Se les crea la parte de envío desde el snapshot de Shopify guardado en el espejo (`ShopifyOrderIngestion.ReadPayload`), con la misma regla de hoy: nombre, teléfono y dirección presentes, o no se crea. Se quita de `Notes` la línea que empieza con `RawAddressLabel` |
| Pedidos `Pending` que ya tienen parte de envío | No se tocan: una persona la puso a mano |
| Pedidos en otro estado | No se tocan. Se cuentan y se listan (tenant, número, estado) |
| Clientes de las ocurrencias, todavía `Incomplete`, no fusionados y sin cambios desde que los creó Shopify | Se completan si la ciudad era lo único que faltaba (los mismos datos que habría usado `CompleteDataAsync`: documento, clasificación por defecto de la tienda, nombre, dirección). Emiten CUC |
| Clientes a los que les falta algo más | Siguen incompletos y se cuentan aparte |

Al guardar, el sinónimo queda activo de inmediato (el próximo pedido con ese texto ya casa) y la fila sale de la bandeja. La
corrección de pedidos y clientes corre **en segundo plano** (D-C6). Cada pedido o cliente corregido deja **auditoría en su tenant**
(«ciudad resuelta por sinónimo»), con el operador que confirmó y el sinónimo.

Confirmar se rechaza (422) si el texto normalizado es igual al nombre de una ciudad de ese departamento: ya casaría solo (antiobjetivo).

### 4.5 Descartar

«No es una ciudad» manda la fila a «Descartados» sin crear sinónimo. Las ocurrencias nuevas con ese texto se suman a la fila
descartada y no la devuelven a pendientes (D-C9). Desde «Descartados» el operador puede devolverla a pendientes.

### 4.6 Corregir un sinónimo

Cambiar la ciudad de un sinónimo (artboard 4). El departamento y el texto no cambian: si cambia el texto, es otro sinónimo.

| Grupo | Qué pasa |
|---|---|
| Pedidos `Pending` que este sinónimo corrigió y cuya parte de envío sigue como la dejó la corrección | Cambian a la ciudad nueva |
| Pedidos `Pending` cuya parte de envío alguien editó después | Se respetan |
| Pedidos en otro estado | No se tocan. Se listan con tenant, número y estado, para que el operador avise |
| Clientes que este sinónimo completó y que nadie editó después | Cambian a la ciudad nueva. El CUC **no** cambia: lleva el código del departamento (`CucFormatter`), y el sinónimo está atado a él |
| Ocurrencias del sinónimo que no se habían podido corregir (faltaba el teléfono, por ejemplo) | Siguen igual |

«Lo que corrigió este sinónimo» incluye dos orígenes (O8): lo que arregló la corrección al confirmarlo y **todo pedido o cliente
que casó por él al importarse** después. Para saber eso y «si alguien lo editó después», cada uno guarda qué escribió y con qué
sinónimo (D-C4, D-C13).

### 4.7 Borrar un sinónimo

Deja de casar desde ese momento. No deshace ninguna corrección (O6). Las correcciones que hizo quedan registradas, con referencia al
sinónimo borrado, para la auditoría. El próximo pedido con ese texto crea una fila pendiente nueva.

## 5. Datos (propuesta)

Todo lo global vive en el esquema `geography`, sin `tenant_id`, igual que departamentos y ciudades:

- `geography.city_synonyms`: `id`, `department_id`, `normalized_text`, `display_text` (como lo escribió el primero que llegó),
  `city_id`, `created_by` (sujeto del operador), `created_at`, `updated_by`, `updated_at`, `deleted_at`. Índice único
  `(department_id, normalized_text)` filtrado a no borrados.
- `geography.unresolved_cities`: la fila de la bandeja. `id`, `department_id`, `normalized_text`, `sample_text`,
  `status` (`Pending` | `Resolved` | `Discarded`), `synonym_id` (si se resolvió), `first_seen_at`, `last_seen_at`. Único
  `(department_id, normalized_text)` entre las no resueltas.
- `geography.unresolved_city_occurrences`: `id`, `unresolved_city_id`, `tenant_id`, `source` (`shopify`), `source_ref` (id de la fila
  del espejo de Shopify), `order_id`, `customer_id` (null si el cliente no nació incompleto por la ciudad), `seen_at`. Único
  `(source, source_ref)`.

Lo que se corrigió, por tenant, vive en el módulo que hace la corrección (Shopify, D-C3):

- `shopify.city_corrections`: `id`, `tenant_id`, `synonym_id`, `origin` (`correction` | `import`), `order_id` (null si solo se
  tocó el cliente), `customer_id`, `city_id` escrita, la huella de lo que se escribió (los campos de la parte de envío, o la
  versión del cliente y de su dirección principal), `applied_at`. Una fila por pedido o cliente que una corrección arregló **o**
  que casó por un sinónimo al importarse (O8).

Los nombres y la forma exacta los decide quien construye; lo que no es negociable es poder responder «qué corrigió el sinónimo X y si
alguien lo tocó después» sin heurísticas.

## 6. API (propuesta)

Endpoints de operador bajo el mismo patrón de `OperatorEndpoints` (Tenancy): la ruta lleva el tenant desde el que actúa el operador
y el handler exige que sea el tenant operador y que tenga el permiso. El tenant operador sale de la configuración
`Platform:OperatorTenantId` y se pregunta con `IOperatorTenant.IsOperator` (`OperatorTenantOptions.cs`), **no** se compara contra
`TenancySeeder.OperatorTenantId`, que es solo el id que siembra la semilla:

```
GET    /api/v1/tenants/{tenantId}/operator/cities/unresolved?status=&departmentId=&search=&page=&pageSize=
GET    /api/v1/tenants/{tenantId}/operator/cities/unresolved/{id}            ← detalle + sugerencias
POST   /api/v1/tenants/{tenantId}/operator/cities/unresolved/{id}/impact     ← { cityId } → impacto (artboard 2), sin escribir
POST   /api/v1/tenants/{tenantId}/operator/cities/unresolved/{id}/resolve    ← { cityId }
POST   /api/v1/tenants/{tenantId}/operator/cities/unresolved/{id}/discard
POST   /api/v1/tenants/{tenantId}/operator/cities/unresolved/{id}/restore
GET    /api/v1/tenants/{tenantId}/operator/cities/synonyms?departmentId=&search=&page=&pageSize=
POST   /api/v1/tenants/{tenantId}/operator/cities/synonyms/{id}/impact       ← { cityId } → impacto (artboard 4)
PUT    /api/v1/tenants/{tenantId}/operator/cities/synonyms/{id}              ← { cityId, expectedVersion }
DELETE /api/v1/tenants/{tenantId}/operator/cities/synonyms/{id}
GET    /api/v1/tenants/{tenantId}/operator/cities/summary                    ← los tres contadores del artboard 1
```

Permisos nuevos con prefijo `operator.` para que `OperatorPermissionFilter` los deje vivos solo en el tenant operador:
`operator.cities.read` y `operator.cities.manage`. Cada uno con su constante **y** su política en `AddAuthorization`
(`QepServiceCollectionExtensions`), y concedidos al rol de fábrica `admin`, que es global: lo que los deja vivos solo en el tenant
operador es `OperatorPermissionFilter`, igual que los `operator.*` de Tenancy. Sin la política el síntoma es 500, no 403.

Las respuestas siguen el criterio BFF del repo: el impacto viaja con todos sus grupos aunque estén en cero, y la lista de pedidos que
no se tocan viaja completa (tenant, número, estado), porque la pantalla la dibuja tal cual.

## 7. Frontend

Pestaña «Ciudades» en la consola de operador (`src/features/operator/`), al lado de «Tenants», con tres vistas: Pendientes,
Sinónimos y Descartados. Los diálogos de confirmar y corregir muestran el impacto antes de guardar. Todo según los artboards de O7.
Copia en español colombiano, tuteando. Va en su propio slice del ledger del frontend (regla de un slice por repo).

## 8. Restricciones

- **Capas y módulos.** Geography no referencia Shopify, Quotations ni Customers. Shopify no referencia módulos de negocio
  (`ShopifyLayerTests`): lo que necesita de Geography, Quotations y Customers entra por puertos de `ShopifyPorts.cs`, con adaptador
  en `src/Bootstrapper/Shopify*.cs`. Cada módulo que se toque trae sus pruebas de capa al día.
- **Todo cambio de una fila del espejo pasa por `ShopifyOrderIngestion.ApplyAsync`** (gotcha del CLAUDE.md). La corrección no cambia
  el espejo: escribe en el pedido y en el cliente por puertos. Si en algún momento necesita reimportar, va por ese método.
- **Concurrencia.** La corrección de un pedido respeta su versión: si el pedido cambió entre leerlo y escribirlo, se relee y se
  vuelve a evaluar (puede haber dejado de ser `Pending`, o un asesor pudo haber agregado la parte de envío).
- **Regresión en la importación por Excel.** Cambiar `FindByNameAsync` cambia la importación de clientes: sus pruebas de integración
  tienen que seguir verdes, y una prueba nueva demuestra que un sinónimo casa ahí también.
- **TDD obligatorio**, RED antes que GREEN con evidencia literal. Pruebas del status HTTP **y** del efecto (el pedido corregido, la
  auditoría escrita, el sinónimo que no tapa una ciudad real).
- En local, las pruebas de integración necesitan `$env:Seed__Enabled = "false"`.

## 9. Decisiones del spec (a ratificar)

- **D-C1. Normalización compartida.** Sinónimos, nombres de ciudad y texto de Shopify se normalizan con la misma función. Además de lo
  que ya hace `NameMatching.Normalize`, colapsa los espacios internos repetidos («Santiago  de Cali»). Eso solo agrega
  coincidencias, nunca quita, y aplica también a la importación por Excel.
- **D-C2. El sinónimo nunca le gana a un nombre real** (§4.1), y confirmar uno que choque con una ciudad real del departamento es 422.
- **D-C3. Quién corrige.** Geography guarda sinónimos, bandeja y ocurrencias, y publica un evento por outbox al confirmar o corregir
  un sinónimo. Shopify lo consume por inbox y corrige pedido por pedido, por puertos hacia Quotations (parte de envío y notas) y
  Customers (completar o cambiar ciudad), con adaptadores en Bootstrapper. Shopify reporta las ocurrencias a Geography por un puerto
  en el momento de importar. Es la misma forma que ya usan Pipeline y Quotations (evento con payload como contrato), pero
  **Geography no tiene hoy esa infraestructura**: `GeographyDbContext` solo mapea departamentos y ciudades, y
  `AddGeographyInfrastructure` no registra unidad de trabajo, outbox ni auditoría. Hay que construirla (proyección del outbox y un
  puerto de auditoría propio, porque el `IAuditRecorder` compartido está ligado a `TenancyDbContext`), y eso agranda el primer
  slice.
- **D-C3b. Quién calcula el impacto.** Los endpoints viven en Geography, y el impacto (estado y número de los pedidos, nombre del
  tenant, si el cliente sigue incompleto, las huellas de `shopify.city_corrections`) entra por un puerto de Geography con adaptador
  en Bootstrapper que lee esos esquemas por nombre. Es el precedente de `CustomerMergeReferences` y `OrdersReportSource`, con su
  misma fragilidad: lo amarra una prueba contra el esquema migrado.
- **D-C4. Huella de la corrección.** «Nadie lo editó después» se decide comparando contra lo que la corrección escribió: para el
  pedido, los campos de su parte de envío; para el cliente, su `Version` después de completarlo. Si no coincide, se respeta.
- **D-C4b. La ciudad del cliente vive en dos lugares.** `Customer.Complete` crea la dirección principal con la misma ciudad
  (`Customer.cs:451-456`). Corregir un sinónimo cambia `Customer.CityId` y la ciudad de esa dirección, cada una solo si sigue como
  la dejó la corrección: la huella guarda también la versión o los campos de la dirección, que es otra entidad.
- **D-C5. Completar el cliente** usa los mismos datos y la misma regla que `CompleteDataAsync`, releídos del snapshot del espejo y de
  la configuración actual de la tienda. Si la tienda ya no tiene clasificación por defecto, el cliente sigue incompleto.
- **D-C6. Segundo plano.** El clic del operador guarda el sinónimo y el evento en una transacción y responde. La corrección corre en
  el worker del inbox de Shopify, un pedido por unidad de trabajo, para que un fallo en uno no afecte a los demás. El impacto que vio
  el operador es una estimación al momento de confirmar: si un pedido cambió de estado en el medio, manda el estado real.
  Como un evento abarca muchos pedidos de muchos tenants y el inbox reclama por mensaje, el consumidor no corrige: **siembra una fila
  de trabajo por ocurrencia** en Shopify (`Pending` → `Applied` | `Skipped` | `Failed`), y un worker las procesa una por unidad de
  trabajo, con reintentos y un tope de intentos. Una `Failed` se ve en la fila del sinónimo («1 pedido no se pudo corregir») y el
  operador puede reintentarla.
- **D-C7. Auditoría.** Cada corrección deja una entrada en la auditoría del tenant del pedido o cliente, con actor de sistema y en el
  detalle el operador que confirmó y el sinónimo. Confirmar, corregir, borrar y descartar dejan su propia entrada en la auditoría
  del tenant operador.
- **D-C8. Excel usa sinónimos.** Como el sinónimo vive en `FindByNameAsync`, la importación de clientes por Excel lo aprovecha sin
  cambios. Lo que no casa ahí sigue respondiendo `city_not_found` por fila y **no** alimenta la bandeja.
- **D-C9. Descartados acumulan.** Una ocurrencia nueva de un texto descartado suma a esa fila y no la devuelve a pendientes. El
  operador puede restaurarla.
- **D-C10. Similitud en memoria.** Las sugerencias se calculan en C# sobre las ciudades del departamento (a lo sumo unas pocas
  decenas, el mismo argumento de `NameMatching`), con similitud de trigramas o distancia de edición sobre el texto normalizado. Así
  Geography no depende de `pg_trgm` ni de `unaccent`. El piso y el algoritmo exactos son de quien construye.
- **D-C11. Sin departamento no hay bandeja.** Si el departamento no resuelve, el texto no entra: sin departamento no hay en qué lista
  sugerir ni a qué atar el sinónimo. El pedido sigue entrando como hoy.
- **D-C12. Historia previa.** Una vez, al desplegar, se recorren las filas del espejo de Shopify ya importadas cuyo pedido está en
  `Pending`, no tiene parte de envío y tiene la línea de `RawAddressLabel` en las notas, y se reporta su ocurrencia. Así la bandeja
  arranca con lo que ya está mal y no solo con lo que llegue después.

- **D-C13. El match dice por dónde casó.** Para cumplir O8, la búsqueda de ciudad deja de devolver solo departamento y ciudad: dice
  también si casó por un sinónimo y por cuál. Eso cambia `ICityRepository.FindByNameAsync`, `IShopifyGeographyLookup.FindCityAsync`
  y `ShopifyCityRef` (hoy `ShopifyGeographyLookup.cs:23` devuelve solo el par), y el importador escribe la fila de
  `city_corrections` con `origin = import` en la misma unidad de trabajo del pedido. La importación por Excel no deja huella: sus
  clientes no entran en O8 (D-C8).

## 10. Lo que queda a quien construye

- Nombres de tablas, columnas, eventos, DTO y clases, dentro de los patrones del repo.
- El algoritmo y el piso de similitud (D-C10).
- Paginación y orden de las listas (por defecto: pendientes por `last_seen_at` descendente).
- Cómo se dispara el recorrido de D-C12 (migración de datos, tarea al arrancar, o un endpoint de operador de una sola vez).
- La partición en slices de cada repo y su orden. El backend va primero: el frontend necesita los endpoints.

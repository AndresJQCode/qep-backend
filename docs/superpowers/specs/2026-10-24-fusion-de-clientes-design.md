# Fusión de clientes

**Fecha:** 2026-10-24
**Módulos:** Customers (fusión, redirección del fusionado, permiso nuevo), Quotations, Pos, Pipeline, Messaging y Shopify
(sus tablas se actualizan dentro de la transacción de la fusión), Bootstrapper (adaptadores y conteos de la vista previa).
**Repos:** `qep-backend` y `qep-frontend`.
**Estado:** borrador para revisión. Las reglas de §2 y la transacción única de §4 son del owner (conversación del 2026-10-24); las que tomó el spec
están en §10 (D-F1…), a ratificar.
**Rama:** `feature/fusion-clientes` desde `develop` `adfe0ba9`.
**Viene de:** `2026-10-24-clientes-duplicados-design.md` §8 (spec 1), que dejó la pestaña «Anomalías» lista para esto.

## 1. Contexto

El spec 1 detecta duplicados pero no los resuelve: un grupo como GYSELYS VIVAS PACHECO (Nit 92-1396892) y GISELY (Ce
92-1396892) se queda para siempre en «Anomalías», con las cotizaciones repartidas entre dos fichas. Este spec agrega la
acción de fusionar: uno queda, el otro desaparece, y todo lo del que desaparece pasa al que queda.

Lo que hace difícil la fusión es que el cliente está referenciado desde seis módulos, todos con referencias blandas (sin FK
entre esquemas), y **no existe hoy ninguna escritura entre módulos en una sola transacción** (§4 la introduce). Lo que se mueve, verificado en
el código el 2026-10-24:

| Módulo | Dónde | Notas |
|---|---|---|
| Customers | `customer_addresses`, `customer_notes`, `customer_tags` | FK con `CASCADE`; `customer_tags` tiene PK `(customer_id, tag_id)` y un tope por cliente (`Tag.cs`) |
| Customers | `whatsapp_user_id`, `instagram_user_id` | Únicos parciales por tenant |
| Quotations | `quotations.client_id`, `orders.client_id` | Vivos; las partes (`quotation_parties`, `order_parties`) no llevan id de cliente |
| Pos | `sales.customer_id` | Con foto del nombre y el documento (`customer_name`, …), que no se toca |
| Pipeline | `deals.customer_id`, `deal_quotations.customer_id`, `deal_orders.customer_id` | Las dos últimas tienen que coincidir con la del negocio (`DealQuotationCustomerMismatch`) |
| Pipeline | `triage_decisions.customer_id` | Historial |
| Messaging | `conversations.customer_id` | Vivo |
| Messaging | `campaigns.audience_customer_ids` | Lista viva de una campaña en borrador o programada |
| Messaging | `campaign_recipients.customer_id` | Foto de un envío; único `(campaign_id, customer_id)` |
| Shopify | `shopify.orders.qep_customer_id` | La reusa un reintento (`ShopifyPorts.cs:164`) |
| Audit | `audit.entries.resource_id` | Historial: **no se mueve**; la ficha del fusionado conserva el suyo (D-F10) |

## 2. Reglas (decisión del owner)

1. **Uno queda y los otros se fusionan en él.** Se puede fusionar más de un cliente a la vez en el que queda.
2. **El fusionado no se borra:** queda marcado «fusionado en {el que queda}», inactivo y fuera de listas y buscadores.
   Su ficha redirige al que queda y su CUC sigue resolviendo hacia él (los PDFs y el Excel ya impresos lo llevan).
3. **Todo lo que cuelga del fusionado pasa al que queda:** cotizaciones, pedidos, conversaciones, ventas del POS, negocios del
   embudo, notas, direcciones y etiquetas.
4. **Los datos del que queda mandan.** Los campos vacíos del que queda se llenan con los del fusionado; nada se pisa.
5. **No se puede deshacer.** La confirmación lo dice antes de fusionar.
6. **Lo puede hacer el asesor**, no sólo el administrador.
7. **Dos entradas:** el grupo de «Anomalías» y «Fusionar con…» en la ficha de un cliente (con buscador, para duplicados
   que no comparten número ni documento).

### Metas

- Que un grupo de «Anomalías» se pueda resolver en un paso y desaparezca de la lista.
- Que después de fusionar nadie vea al fusionado en un buscador, y que nada nuevo se le asigne.
- Que antes de confirmar se vea qué se va a mover.

### No metas

- Deshacer una fusión.
- Elegir campo por campo qué datos quedan (regla 4 basta).
- Fusionar empresas (`Companies`), proveedores o miembros.
- Unir dos negocios abiertos del embudo en uno: quedan los dos, a nombre del que queda (D-F7).

### Anti-metas

- Una fusión a medias: todo se mueve en una transacción (§4).
- Un 404 donde corresponde 403 (aislamiento de tenant, CLAUDE.md).

## 3. Customers

### 3.1 El fusionado

Columnas nuevas en `customers.customers`: `merged_into_id uuid NULL` (el que quedó) y `merged_at timestamptz NULL`. Un cliente
con `merged_into_id` está fusionado: `IsActive = false`, conserva su CUC (único, y es lo que está impreso) y **libera su
documento**: `IX_customers_tenant_identification` pasa a ser parcial (`WHERE merged_into_id IS NULL`), así que el que queda
puede tomar o corregirse al documento del fusionado y una búsqueda por documento nunca devuelve a un fusionado (D-F11). Un
fusionado rechaza toda escritura con `customers.customer.merged` (422): `Update`, `Complete`, `Activate`, opt-in/opt-out,
direcciones, notas, etiquetas y el `UPDATE` condicional que le pone un BSUID (`TryAttachWhatsAppUserIdAsync`); hoy
`EnsureActive` sólo frena las tres primeras. `merged_into_id` apunta
siempre a un cliente **no fusionado**: si C se fusionó en B y después B se fusiona en A, la fusión reescribe C → A en la misma
transacción (D-F2).

### 3.2 La fusión

```
POST /api/v1/tenants/{tenantId}/customers/{survivorId}/merge
If-Match: "<version del que queda>"
{ "mergedCustomerIds": ["…", "…"] }
```

- Permiso nuevo `customers.customer.merge` (constante + política en `AddAuthorization`), concedido por defecto a `admin` y a
  `advisor` (regla 6, D-F1). El handler revalida tenant y permiso antes de tocar el repositorio.
- Validación (FluentValidation, 422 `validation.failed`): entre 1 y 10 ids, sin repetir, sin el `survivorId`.
- Reglas de dominio (422 con código):
  - `customers.merge.customer_not_found`: un id que no existe en el tenant.
  - `customers.merge.already_merged`: el que queda o uno de los fusionados ya está fusionado.
  - `customers.merge.survivor_incomplete`: el que queda está incompleto y alguno de los fusionados está completo: el CUC y
    la ficha completa son del completo, así que el que queda tiene que ser él (D-F3).
  - `customers.merge.survivor_inactive`: el que queda está inactivo. Lo movido terminaría en un cliente al que Quotations y
    el POS no le pueden escribir. Un fusionado sí puede estar inactivo (D-F12).
- 412 si la versión del que queda cambió; 428 sin `If-Match`. La versión sale de la vista previa (§3.4), que el frontend
  vuelve a pedir cada vez que cambia el que queda: `CustomerResponse` no la trae hoy y no hace falta agregarla (D-F13).
- En **una transacción explícita** (dos `SaveChanges` en orden fijo, como `BusinessHoursRepository.SwapDefaultAsync`: EF
  ordena los `UPDATE` por clave primaria y el BSUID tiene que salir del fusionado antes de entrar al que queda; CLAUDE.md):
  1. Llena los campos vacíos del que queda con el primero no vacío de los fusionados, en el orden en que vienen: `Email`,
     `BusinessName`, `Phone` (con `PhoneE164`), `WhatsAppUserId`, `InstagramUserId` (y `InstagramUsername`) y, si el que
     queda es incompleto, el documento (tipo y número), el país y la dirección. **No** se llena `ClassificationId`: cambiaría
     el prefijo del CUC del que queda, que ya está impreso (D-F14). El BSUID, el IGSID y el documento se quitan del fusionado
     antes de ponérselos al que queda. Si el que queda ya tiene un BSUID o un IGSID, el del fusionado se queda en el fusionado
     y la redirección de §3.3 lo resuelve.
  2. Si algún fusionado tiene opt-out de marketing y el que queda no, el que queda lo hereda con el origen y la fecha del
     fusionado (`manual`, `meta` o `button`): el opt-out es de la persona (spec 1 §4.4) y no hace falta un origen nuevo.
  3. Mueve las notas y las direcciones de envío del fusionado al que queda. La principal del que queda sigue siendo la
     principal; las del fusionado llegan como no principales.
  4. Une las etiquetas sin repetir. Si la unión pasa el tope por cliente, entran primero las del que queda y después las del
     fusionado hasta el tope; las que no caben salen en la respuesta (`droppedTags`) (D-F5).
  5. Marca cada fusionado (`merged_into_id`, `merged_at`, `IsActive = false`) y reescribe a los que ya apuntaban a él (D-F2).
     Los ids de esos que ya apuntaban entran también en el movimiento de §4, para que nada de C se quede en B.
  6. Audita `customers.customer.merged` en el que queda y en cada fusionado, con el actor.
  7. Mueve lo de los otros módulos (§4) en la misma transacción.
- Responde `200` con el `CustomerResponse` del que queda (versión nueva) más `droppedTags`. Cuando responde, todo ya se
  movió.

### 3.3 Redirección y exclusión

Un fusionado **no aparece** en: el listado y la búsqueda de clientes (`CustomerRepository.FilteredQuery`, que hoy no filtra
por activo), la exportación, `SearchSelectableAsync`, el POS, el buscador de cotizaciones, la audiencia de campañas, las
anomalías (§3.3 del spec 1: si no, el grupo nunca se va), las coincidencias de documento y los reportes.

Un fusionado **se redirige al que quedó** donde se resuelve un cliente por una identidad externa: por BSUID e IGSID
(`CustomerWhatsAppDirectory`, `CustomerInstagramDirectory`), por teléfono (`CustomerPhoneDirectory`, que además no debe
elegirlo como «el más viejo»), y en el resolver de Shopify (documento, teléfono, correo e id). Así un mensaje que llega por el
BSUID que se quedó en el fusionado, o un pedido de Shopify que reusa su `qep_customer_id`, cae en el que quedó.

`GET /customers/{id}` de un fusionado responde `200` con su ficha y un campo `mergedInto: { id, name }` (siempre presente,
`null` si no está fusionado): el frontend redirige (BFF: la pantalla no adivina por un 404). La búsqueda por CUC de
cotizaciones y pedidos (`SearchIdsByCucAsync`, `OrderListing.ResolveClientIdsByCucAsync`) devuelve también el id del que quedó
cuando el CUC es de un fusionado. La importación de Excel que trae el CUC de un fusionado responde por fila
`customers.import.row.customer_merged` con el CUC del que quedó, en vez de actualizarlo (D-F6).

### 3.4 Vista previa

```
GET /api/v1/tenants/{tenantId}/customers/{survivorId}/merge-preview?mergedCustomerIds=…&mergedCustomerIds=…
```

Permiso `customers.customer.merge`. Responde lo que verá la confirmación, con las mismas validaciones que la fusión pero sin
escribir nada:

```json
{
  "survivor": { "id": "…", "name": "GYSELYS VIVAS PACHECO", "identificationType": "NIT", "identificationNumber": "92-1396892", "cuc": "MED00000105", "isComplete": true },
  "merged": [ { "id": "…", "name": "GISELY", "identificationType": "CE", "identificationNumber": "92-1396892", "cuc": "PEQ00000194", "isComplete": true } ],
  "documentsDiffer": false,
  "moves": [
    { "kind": "Quotations", "count": 3 }, { "kind": "Orders", "count": 1 }, { "kind": "Conversations", "count": 2 },
    { "kind": "PosSales", "count": 0 }, { "kind": "Deals", "count": 1 }, { "kind": "Notes", "count": 4 },
    { "kind": "Addresses", "count": 1 }, { "kind": "Tags", "count": 2 }
  ],
  "filledFields": ["Email"]
}
```

`moves` viaja **completo, incluso en cero** (las ocho clases siempre). `documentsDiffer` es `true` si algún fusionado tiene
un número de documento distinto del que queda al normalizarlo (spec 1 §3.2): la confirmación usa un aviso más fuerte, porque
puede ser otro cliente real (Luis Alberto y ZIBA Tienda). Los conteos de otros módulos entran por un puerto de Customers
(`ICustomerMergeReferences.CountAsync`, §4).

## 4. El resto de los módulos: en la misma transacción (decisión del owner)

Todo lo de §1 se mueve **en la misma transacción** de la fusión (§3.2), con SQL directo sobre las tablas de los otros módulos
(decisión del owner, 2026-10-24): la fusión no se puede deshacer, así que tiene que quedar completa o no pasar. Hoy sólo
Pipeline consume eventos de otro módulo; hacerlo por evento exigía cuatro bandejas de entrada nuevas (Quotations, Pos,
Messaging, Shopify) y dejaba ventanas a medias.

- Customers declara el puerto `ICustomerMergeReferences` con `MoveAsync(tenantId, survivorId, mergedIds, ct)` y
  `CountAsync(…)` (la vista previa de §3.4). El adaptador vive en Bootstrapper y **corre en la conexión y la transacción del
  `CustomersDbContext`**, como `CatalogVariantUsageChecker` corre en la del `PUT` de producto: todos los módulos están en la
  misma base, cada uno en su esquema.
- Cada `UPDATE` filtra por `tenant_id` y por `<col> = ANY(@merged)`:

| Esquema | Qué mueve |
|---|---|
| `quotations` | `quotations.client_id`, `orders.client_id` |
| `pos` | `sales.customer_id` (la foto del nombre no cambia: es lo que se cobró) |
| `pipeline` | `deals.customer_id`, `deal_quotations.customer_id`, `deal_orders.customer_id`, `triage_decisions.customer_id` |
| `messaging` | `conversations.customer_id`; en las campañas `Draft`/`Scheduled`, reemplaza en `audience_customer_ids` y quita repetidos. `campaign_recipients` **no** se toca: es la foto de un envío y el índice único `(campaign_id, customer_id)` chocaría |
| `shopify` | `orders.qep_customer_id` |

- Un error de cualquiera deshace todo (incluidos los cambios de Customers). Un índice único que choque sale como 409 por el
  nombre del índice, en Infrastructure (CLAUDE.md); con la lista de arriba no se espera ninguno.
- **El costo de este camino**, para que quede escrito: la fusión conoce las tablas de seis módulos por nombre. Renombrar una
  de esas columnas compila y rompe la fusión en ejecución. Igual que con `CatalogVariantUsageChecker`, una prueba resuelve el
  adaptador real del contenedor y lo corre contra el esquema migrado de todos los módulos; y CLAUDE.md suma la entrada.

**Lo que llega tarde.** Un evento de Quotations que ya estaba en vuelo con el `clientId` del fusionado (spec 2026-10-13
§4.5) puede crear un negocio a su nombre después de la fusión, o desenlazar una cotización del negocio ya movido (la
comparación de `QuotationEvents.cs:206`). Por eso `QuotationEventApplier` resuelve `fact.ClientId` al que quedó **antes** de
usarlo, por un método nuevo del puerto de Customers de Pipeline (`PipelineCustomerDirectory` hoy sólo trae nombre y
completitud). Ningún otro módulo escribe a nombre de un cliente sin una acción humana o un resolver de §3.3: Quotations y Pos
rechazan un cliente inactivo al escribir (`QuotationCustomerEligibility.cs:46`, `CreatePosSale.cs:158`).

No hay evento `customers.customer.merged.v1`: nadie lo necesita.

## 5. Frontend (`qep-frontend`)

### 5.1 El diálogo de fusión

Uno solo, con dos entradas. Muestra a los candidatos y deja elegir con un radio **cuál queda**: por defecto el completo y,
entre completos, el más viejo. Debajo, la vista previa (§3.4): qué se mueve (las ocho líneas, las que están en cero en gris),
qué campos se llenan, y —con `documentsDiffer`— un aviso en rojo: «Estos clientes tienen documentos distintos. Revisa que sean
la misma persona o empresa.». Cerca del botón, siempre: «La fusión no se puede deshacer.» y una casilla «Entiendo que no se
puede deshacer» que habilita «Fusionar» (D-F8). `customers.merge.survivor_incomplete` marca el radio del incompleto con el
motivo, antes de mandar.

Al terminar: toast «Se fusionaron {n} clientes en {nombre}.», navegación a la ficha del que quedó y, si `droppedTags` no viene
vacío, un aviso con las etiquetas que no cupieron.

### 5.2 Entradas

- **«Anomalías»:** cada grupo suma «Fusionar…», que abre el diálogo con los clientes del grupo marcados.
- **Ficha del cliente:** un menú «⋯» junto a «Editar cliente» con «Fusionar con…»; abre un buscador de clientes (el
  listado, que ya excluye fusionados) y después el diálogo con el cliente de la ficha y los elegidos.
- Las dos sólo con `customers.customer.merge`.

### 5.3 Ficha de un fusionado

Con `mergedInto` no nulo, la ficha navega a la del que quedó y avisa «{nombre} se fusionó en {nombre del que quedó}.».

## 6. Pruebas (TDD, RED antes de GREEN, con evidencia literal)

- Fusión: campos que se llenan y no se pisan; BSUID que pasa y que se queda; opt-out que pasa; notas, direcciones y
  etiquetas (con tope y `droppedTags`); C → B → A reescrito; cada 422 de §3.2; 412/428; otro tenant → 403; auditoría y
  outbox en la misma transacción.
- Redirección y exclusión: cada lugar de §3.3 tiene su prueba (un fusionado no aparece; un BSUID/teléfono/Shopify del
  fusionado resuelve al que quedó; `mergedInto`; búsqueda por CUC; fila de importación).
- Vista previa: los ocho conteos, en cero también; `documentsDiffer`; ningún efecto.
- Movimiento: cada tabla de §4 pasa al que quedó, otro tenant intacto, Messaging no toca `campaign_recipients`, un error
  en el último `UPDATE` deja todo como estaba (Customers incluido); la prueba del adaptador real contra el esquema migrado;
  Pipeline resuelve al que quedó un evento de Quotations que llega después.
- Frontend: el diálogo (radio por defecto, casilla, aviso de documentos distintos, `survivor_incomplete`), las dos entradas y
  la redirección de la ficha.

## 7. Despliegue

Una migración en Customers: `merged_into_id`, `merged_at`, índice parcial por `merged_into_id`, e
`IX_customers_tenant_identification` recreado como parcial (`WHERE merged_into_id IS NULL`). El permiso nuevo va en los roles
de sistema (en código, sin migración). Ninguna migración en los otros módulos. Backend y frontend salen juntos. Sin backfill.

## 8. Fuera de alcance

Deshacer, fusionar entre tenants, campo por campo, unir negocios, y migrar las conversaciones con BSUID distinto en una sola
(quedan dos conversaciones a nombre del que queda).

## 9. Precedentes en el código

`PosCustomerIdentificationTakenException` (422 con extensión), `CatalogVariantUsageChecker` (SQL en otros esquemas dentro de
la transacción del llamador), `BusinessHoursRepository.SwapDefaultAsync` (dos `SaveChanges` en una transacción explícita),
`ConversationConcurrency.EnsureVersion` (If-Match).

## 10. Decisiones que tomó el spec (a ratificar)

| Id | Decisión | Alternativa |
|---|---|---|
| D-F1 | Permiso propio `customers.customer.merge` en `admin` y `advisor` | Reusar `customers.customer.manage` |
| D-F2 | `merged_into_id` apunta siempre a un no fusionado; una fusión en cadena se reescribe | Seguir la cadena al leer |
| D-F3 | Si hay un completo, el que queda tiene que ser completo | Dejar que el incompleto herede el documento (exige liberar el índice) |
| D-F4 | **Owner, 2026-10-24:** una sola transacción con SQL directo sobre los seis esquemas desde Bootstrapper | Por evento, con cuatro bandejas de entrada nuevas |
| D-F5 | Etiquetas por encima del tope: entran las del que queda primero y se informan las que no caben | Rechazar la fusión |
| D-F6 | Importar con el CUC de un fusionado da error de fila con el CUC del que quedó | Actualizar al que quedó |
| D-F7 | Dos negocios abiertos quedan los dos, a nombre del que queda | Unirlos |
| D-F8 | Casilla «Entiendo que no se puede deshacer» | Escribir el nombre del que queda |
| D-F9 | Hasta 10 fusionados por operación | Sin tope |
| D-F10 | La auditoría no se mueve | Copiarla al que queda |
| D-F11 | El fusionado libera su documento (índice parcial) | Que lo conserve (bloquea corregir el documento del que queda) |
| D-F12 | El que queda tiene que estar activo; un fusionado puede estar inactivo | Reactivar al que queda al fusionar |
| D-F13 | La versión para `If-Match` sale de la vista previa | Agregar `version` a `CustomerResponse` |
| D-F14 | No se llena la clasificación (cambiaría el CUC) | Llenarla y reescribir el CUC |
| D-F15 | Una cotización o un pedido ya enviado del fusionado se vuelve a generar con los datos del que queda (Quotations lee el cliente en vivo; sólo las partes guardan foto) | Guardar una foto al fusionar |

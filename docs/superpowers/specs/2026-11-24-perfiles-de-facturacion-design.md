# Perfiles de facturación del cliente (frontend) — diseño

Fecha: 2026-11-24. Repo: `qep-frontend`. Rama sugerida: `feature/billing-profiles`. Contrato HTTP: lo define el backend
en `docs/superpowers/specs/2026-11-24-perfiles-de-facturacion-backend-handoff.md` (este repo; se copia a `qep-backend`
como su spec). Plan: `docs/superpowers/plans/2026-11-24-perfiles-de-facturacion-frontend.md`.
Decisiones del owner incorporadas el 2026-11-24 (P-1..P-5 de la primera versión de este spec).

## Por qué

Un cliente factura con distintos datos según el negocio: la sociedad, la holding, una sucursal con otro correo de
facturación. Hoy la sección «Facturación» de la cotización y del pedido sólo ofrece el switch «Facturar con los datos
del cliente» y, apagado, una copia puntual que se escribe a mano cada vez (`quote-billing-party.tsx`). La entrega ya
resolvió el mismo problema con la libreta de direcciones (`quote-shipping-party.tsx`): una lista de guardadas, la fila
«Otra dirección…» y el switch «Guardar esta dirección en el cliente». El owner pide la misma mecánica para facturación.

## Objetivos

- Con «Facturar con los datos del cliente» apagado, la sección muestra «¿A quién se factura?» con los perfiles
  guardados del cliente y la fila «Otro perfil…».
- Elegir un perfil **copia** sus datos —también sus retenciones y su excedente de IVA— en la facturación del documento
  y oculta los campos de la parte (como una dirección guardada). Retenciones y excedente de IVA siguen visibles y
  editables en ese documento. El documento nunca guarda el id del perfil.
- «Otro perfil…» muestra los campos de hoy y el switch «Guardar este perfil en el cliente». Prendido, al grabar el
  documento el perfil se agrega al cliente.
- Funciona en nueva cotización, editar cotización, nuevo pedido y editar pedido (pendiente, con backend que recibe
  `billing`).
- La ficha del cliente gana la tarjeta «Perfiles de facturación» para agregar, editar, borrar y marcar principal.
- Tope de 50 perfiles por cliente: con el tope alcanzado no se ofrece guardar.
- Se corrigen tres huecos del guardado de direcciones (H-1..H-3, abajo) en la misma rama.
- En «Editar pedido» la entrega (recoger o enviar, y la dirección de envío) pasa a ser editable mientras el pedido sea
  editable, con el mismo selector de direcciones y el mismo switch de guardar que «Nuevo pedido» (sección «Entrega
  editable en Editar pedido»). Requiere backend nuevo; con uno viejo queda de sólo lectura como hoy.
- Con un backend que todavía no manda `billingProfiles`, todo queda como hoy.

## No objetivos

- Alias del perfil: no hay. El `name` es a quién se factura (decisión del owner).
- Retenciones municipales (ReteICA) en el perfil: son de la empresa facturadora y del municipio de la operación, nunca
  del cliente (el backend ya rechaza una municipal en la ficha, `customers.customer.withholding_tax_rate_municipal`).
- Cambiar «Facturar a consumidor final», el selector de razón social ni la forma de entrega.
- El detalle de cotización y de pedido no edita partes: nada que guardar ahí.

## Enfoques considerados

1. **Sub-recurso propio en Customers, guardado después de grabar el documento (elegido).**
   `/customers/{id}/billing-profiles`, calcado de `/addresses` (`CustomerEndpoints.cs:101-133`). *A favor:* misma
   mecánica que la libreta; Customers es el dueño; Quotations no escribe en otro módulo; sirve a la tarjeta de la ficha.
   *En contra:* no es atómico: el documento puede grabarse y el perfil fallar (ver «Errores»).
2. **Bandera en el request del documento** y el backend crea el perfil en la misma transacción. *A favor:* atómico.
   *En contra:* Quotations escribiría en Customers (hoy sólo lee por `IQuotationCustomerLookup`), cambian cuatro
   handlers, y la ficha necesitaría igual el sub-recurso.
3. **Reusar la libreta de direcciones con un tipo.** *En contra:* una dirección no tiene documento, tipo de persona,
   correo ni impuestos; dos reglas distintas en una fila.

Tampoco se reutiliza `CustomerBillingProfile` de ServiceContracts (cobros recurrentes, otro concepto).

## Qué guarda un perfil

Los campos de la parte de facturación con datos propios del documento, verificados en el código:

| Campo del documento hoy | Dónde | En el perfil |
| --- | --- | --- |
| `billing.name`, `identificationNumber`, `personType`, `identificationType`, `phone`, `email`, `address`, `country`, `departmentId`/`cityId` (CO) o `cityName` + `regionId` (fuera de CO) | `QuotePartyFormValues` (`quote-parties-form.ts:30-51`); `QuotationPartyRequest` (`QuotationsDtos.cs:367-387`) | Sí, los mismos |
| `billingWithholdingTaxRateIds` — una tasa por tipo, del país de la empresa facturadora | `QuotePartiesFormValues` (`quote-parties-form.ts:83-86`); `QuotationPartiesRequest.BillingWithholdingTaxRateIds` (`QuotationsDtos.cs:419-423`) | Sí, como `withholdingTaxRates` (ids + datos para pintarlos), **sin municipales** |
| `billingVatSurplus` — `null` = sin contestar | `quote-parties-form.ts:87-90`; `QuotationsDtos.cs:416-418` | Sí, como `vatSurplus: boolean` (un perfil ya contestó) |
| `billingUsesBusinessName` | sólo con los datos del cliente | No |
| Retenciones municipales (`setMunicipalWithholding`, `editar.tsx:483`) | mismo mapa por tipo | No (se quitan con `withoutMunicipalTypes`, `utils/municipal-withholdings.ts:39`) |

## Diseño de UI

### En el documento (sección «Facturación»)

```
[ ] Facturar a consumidor final
(●) Facturar con los datos del cliente          ← apagado:
    Se factura a otros datos.

¿A quién se factura?
┌──────────────────────────────────────────────────────┐
│ (●) Comercial Andina SAS          [Principal]         │
│     NIT 900123456 · Medellín · facturas@andina.co      │
├──────────────────────────────────────────────────────┤
│ ( ) Andina Holding SAS                                  │
│     NIT 901555000 · Bogotá · contabilidad@holding.co   │
├──────────────────────────────────────────────────────┤
│ ( ) Otro perfil…                                        │
│     Se escribe aquí abajo, sólo para este documento.   │
└──────────────────────────────────────────────────────┘
Retenciones  [Retefuente: 2,5 % compras ▾]     ← precargadas del perfil, editables
[●] Tiene excedente de IVA                      ← precargado del perfil, editable
```

Con «Otro perfil…» aparecen los campos de hoy, las retenciones, el excedente de IVA y al pie:

```
┌ (○) Guardar este perfil en el cliente ─────────────────┐
│ Al guardar queda en el cliente para las próximas. No se │
│ marca como principal.                                    │
└─────────────────────────────────────────────────────────┘
```

Apagado: «Se usa sólo aquí y no toca la ficha del cliente.». Con el tope alcanzado el switch no se pinta y en su lugar:
«Este cliente ya tiene 50 perfiles de facturación. Para guardar otro, borra uno desde su ficha.».

- **Fila:** título `name`; detalle `«<tipo> <número>»`, ciudad y email unidos por ` · `. Insignia «Principal».
- **Orden:** el del backend (principal primero, luego por nombre).
- **Sin perfiles:** no se pinta la lista; se ven los campos y el switch.
- **Switch de guardar** sólo con `customers.customer.manage`, cliente activo y menos de 50 perfiles.
- **Retenciones y excedente de IVA** visibles también con un perfil elegido. Hoy `QuotePartyFields` oculta
  `extraFields` con una guardada (`quote-party-fields.tsx:527`); la entrega no usa `extraFields`, así que pintarlos
  siempre no la cambia.
- `QuotePartyFields` generaliza el selector: hoy el texto «¿A dónde se entrega?» y el `name` del radio
  (`quote-shipping-address`) están fijos (`quote-party-fields.tsx:214,227`). Con dos selectores en la misma pantalla el
  mismo `name` uniría los dos grupos: el `name` sale de `idPrefix` y los textos llegan por props.

### Precarga al elegir un perfil

- Los campos de la parte se copian (`billingProfilePartyValues`).
- `billingVatSurplus = profile.vatSurplus`.
- Retenciones: por cada tipo, la tasa del perfil **activa** del país de la empresa facturadora elegida; sin empresa
  elegida, todas las activas del perfil (el `useKeepWithholdingsInCountry` existente quita las de otro país al elegir
  empresa, `editar.tsx:238-246`). Una tasa del perfil desactivada no se precarga: el documento sólo acepta tasas
  activas (`QuotationsDtos.cs:419-420`) y precargarla daría un 422 en cada guardado. Los tipos que el perfil no tiene
  quedan en «Sin retención».
- Tocar después las retenciones o el excedente de IVA **no** deselecciona el perfil: son del documento. Por eso la
  coincidencia que reconoce el perfil de un documento guardado compara sólo los campos de la parte.

Por pantalla:

| Pantalla | Dónde viven retenciones y excedente | Precarga |
| --- | --- | --- |
| Nueva y editar cotización | `parties.billingWithholdingTaxRateIds` / `parties.billingVatSurplus` | las dos |
| Editar pedido | `draft.withholdings` (sección «Retenciones», `order-edit-draft.ts:210-242`) y `parties.billingVatSurplus` | las dos: la pantalla despacha `setWithholdings` además de `setBilling` |
| Nuevo pedido | no manda retenciones: nace con las del cliente congeladas y se ajustan al editar (`new-order-draft.ts:369-372`) | sólo el excedente de IVA |

### Al apagar el switch «Facturar con los datos del cliente»

Si la facturación nunca se escribió y el cliente tiene perfil principal, queda elegido el principal (con su precarga).
Si no, «Otro perfil…» con lo de hoy (`partyValuesOrCustomer`, `quote-parties-form.ts:194-202`).

El switch se queda: prendido no guarda fila de facturación y el documento sigue a la ficha del cliente, con la razón
social y las retenciones del cliente. No es una fila más de la lista porque sus semánticas son otras.

### En la ficha del cliente: tarjeta «Perfiles de facturación»

Debajo de «Direcciones» en `customer-form.tsx` (`:773`), mismo patrón que `CustomerAddressesCard`
(`customer-addresses-card.tsx`): lista, «Agregar perfil», y por fila «Editar», «Marcar como principal» y «Borrar».

```
Perfiles de facturación                         [+ Agregar perfil]
┌───────────────────────────────────────────────────────────────┐
│ Comercial Andina SAS   [Principal]                 ✎  🗑       │
│ NIT 900123456 · Medellín · facturas@andina.co                  │
│ Retefuente 2,5 % · Con excedente de IVA                         │
├───────────────────────────────────────────────────────────────┤
│ Andina Holding SAS                            ☆  ✎  🗑         │
└───────────────────────────────────────────────────────────────┘
```

- Formulario (en línea, como el de direcciones): nombre, tipo de persona, tipo y número de documento, teléfono, email,
  país, región/ciudad (`CityCascade`), dirección, retenciones (`CustomerWithholdingsField`, el mismo de la ficha) y
  excedente de IVA. Obligatorios: nombre, tipo y número de documento, dirección, ciudad. Teléfono y email opcionales.
- Retenciones elegibles sólo con `catalog.tax_rate.read` (mismo gate que la ficha,
  `customer-form.tsx:221`); sin él se ven las guardadas y no se cambian.
- Sin `customers.customer.manage`, o con la ficha deshabilitada (`disabled`, cliente inactivo), la tarjeta es de sólo
  lectura: sin botones.
- «Borrar» pide confirmación; la principal no ofrece «Borrar» (el backend la rechaza: primero se marca otra).
- Con 50 perfiles «Agregar perfil» se deshabilita con la razón visible.
- Estados: **carga** (la tarjeta vive dentro de la ficha ya cargada: el estado es el `isPending` de cada operación, con
  el botón deshabilitado), **vacío** («Este cliente no tiene perfiles de facturación.» y, con permiso, «Agregar
  perfil»), **error** (mensaje bajo el formulario o la fila, por `code`; un 422 con `errors` marca el campo), **éxito**
  (toast y la lista repintada con la respuesta).
- Sin `If-Match`: las operaciones de libreta no lo usan (`CustomerEndpoints.cs:101-133`, `customers.api.ts:893-949`);
  por eso no hay 412 que tratar. Cada respuesta es el cliente entero y se escribe en la caché.
- Copy por i18n en el namespace `customers`, como la tarjeta de direcciones (`useTranslation('customers')`).

## Modelo de datos (frontend)

```ts
export interface CustomerBillingProfileDto {
  id: string
  name: string
  personType: PersonType | null
  identificationType: IdentificationType | null
  identificationNumber: string
  phone: string | null
  email: string | null
  country: string
  cityId: string | null
  cityName: string | null       // CO: nombre DIVIPOLA resuelto; fuera: el escrito
  departmentId: string | null
  departmentName: string | null
  region: RegionRef | null
  address: string
  /** Same shape as the customer's (`CustomerWithholdingTaxRate`, `customer.ts:94-103`). */
  withholdingTaxRates: CustomerWithholdingTaxRate[]
  vatSurplus: boolean
  isPrincipal: boolean
}
```

- `CustomerDto.billingProfiles?` y `QuoteClient.billingProfiles?`: **`undefined` = backend viejo**, `[]` = sin
  perfiles. Más `billingProfileLimit?: number` en los dos (el tope lo dice el backend; ausente ⇒ no se aplica tope en
  el navegador y el 422 lo dice). `QuoteCustomerView` copia los dos.
- `QuotePartiesFormValues` suma, como estado de pantalla: `billingProfileId: string` (`'new'` = «Otro perfil…») y
  `saveBillingToCustomer: boolean`.
- Helpers en `quote-parties-form.ts`: `billingProfilePartyValues`, `matchingBillingProfileId` (compara sólo la parte),
  `billingValuesOnSwitchOff`, `billingProfileSelection(profile, companyCountry)` (retenciones a precargar).
- `quotePartiesToInput` y `sameQuoteParties` no cambian. Los reductores que deciden «cambió algo» comparan los dos
  campos nuevos (`samePartiesForm`, `quote-edit-draft.ts:228`; `setBilling`, `order-edit-draft.ts:530-535`).

## Flujo de guardado (documento)

Una sola pieza, `usePersistPartiesToCustomer`, corre **después** de que el documento se grabó en las pantallas que
editan partes, con una foto de las partes tomada antes de guardar:

1. **Perfil:** con datos propios, «Otro perfil…» y el switch prendido ⇒ `POST …/billing-profiles` con los campos, las
   retenciones del documento sin municipales y `vatSurplus = billingVatSurplus ?? false`. Desde «Nuevo pedido», que no
   pide retenciones, viaja `withholdingTaxRateIds: []`.
2. **Dirección:** con «Otra dirección…» y su switch prendido ⇒ `POST …/addresses` (lo de hoy, ahora en todas las
   pantallas, ver H-1).
3. Éxito: la respuesta (cliente entero) va a la caché del cliente y por `publishCustomerSaved`; en las pantallas de
   edición se parcha además el bloque `client` del documento en su caché (`quoteClientFromCustomer`) y el borrador
   queda con la fila guardada elegida y el switch apagado (H-3). Toasts «Perfil de facturación guardado en el
   cliente.» / «Dirección guardada en el cliente.».
4. En las pantallas de alta se navega al detalle sin esperar: los callbacks viven en el `useMutation` del hook, que
   corren aunque la pantalla se desmonte.

El `POST` de perfil es idempotente por contenido en el backend (handoff §3.2). Guardar nunca marca principal; el
primer perfil del cliente sí queda principal.

## Hallazgos corregidos en esta rama

- **H-1.** El switch «Guardar esta dirección en el cliente» se pinta en nueva cotización y nuevo pedido pero sólo
  editar cotización lo cumple (`useCustomerAddresses(...).add` sólo en `quotes/$quoteId/editar.tsx:273`). Se cablea en
  las dos pantallas de alta y en «Editar pedido», que con esta rama gana la entrega editable (sección siguiente).
- **H-2.** Ese switch se pinta sin mirar `customers.customer.manage` y el `mutate` no tiene `onError`
  (`editar.tsx:273-305`). Se pinta sólo con permiso y cliente activo, y un fallo avisa con toast.
- **H-3.** Tras guardar la dirección en editar cotización, el selector apunta a un id que no está en la lista: se
  actualiza la caché del cliente pero la pantalla lee `quote.client.addresses`. Se parcha el bloque `client` del
  documento y se marca la guardada leyendo el borrador vigente, no el de la clausura anterior al `reset`.

## Entrega editable en «Editar pedido»

**Revierte por orden del owner (2026-11-24)** la regla «el pedido no edita sus partes» —«flete §9» en el frontend
(`order-edit-page.tsx:590-596`, `modeLocked`) y «H5» en el backend (`SaveOrderEdits.cs:292`; `OrdersDtos.cs:297-299`:
«No cambia recoger o enviar, que el pedido no edita»)— para la entrega. La facturación ya era editable (spec
2026-10-26).

**Condición, la misma en spec, handoff y plan:** recoger/enviar y la dirección de envío se editan sólo si el pedido
**no está facturado y no está preparado** (y está abierto, que es lo que ya exige abrir la pantalla). En el frontend es
`isOrderEditable` (`features/orders/utils/order-state.ts:48-60`: `Open`, `NotPrepared`, `invoiced === false`). El
backend lo valida por su cuenta (`Order.IsPrepared`, `Order.IsInvoiced`, `Order.cs:364-366`) y responde 422
`order.order.already_prepared` / `order.order.already_invoiced` si el cuerpo trae la entrega y el pedido está preparado o
facturado (handoff de entrega, §10).

Qué se ve: en vez de `modeLocked`, `DocumentDeliverySection` editable, igual que en «Nuevo pedido»: «Recoger en tienda»
/ «Enviar», el switch «Enviar a la dirección del cliente», el selector «¿A dónde se entrega?» con la libreta, «Otra
dirección…» y «Guardar esta dirección en el cliente» (con permiso, cliente activo; se cumple con
`usePersistPartiesToCustomer` después de guardar, H-1). La transportadora, quién paga y el valor siguen como hoy.

Efectos:

- **De enviar a recoger:** se limpian transportadora, quién paga, valor del envío y contraentrega (el borrador deja
  `shipping: null`, como hoy con un pedido de recogida; el backend aplica `ShippingDetails.None`, porque con recogida
  rechaza cualquier envío, `ShippingDetails.cs:31-36`). El total baja y el estado de pago se recalcula en el backend.
- **De recoger a enviar:** exige a dónde: la dirección del cliente (switch prendido) o una parte completa
  (`quotePartiesIssues().shipping`, que ya bloquea el guardado con «Otra dirección…» incompleta). Quién paga arranca
  con el por defecto, como en «Nuevo pedido» (`applyDefaultChargeMode`, `new-order-draft.ts:132-139`). Si el cliente
  no tiene domicilio y el switch está prendido, el backend responde 422 `order.delivery.address_required`.
- **Contraentrega (R16):** pasar a recoger la apaga. Prenderla después de volver a enviar sigue la regla de hoy: sólo
  con el pago sin empezar (`Order.SetShipping`, `Order.cs:1334-1343`; frontend `orderEditBlocksCashOnDelivery`).
- **Comprobantes:** no se tocan. Si el pedido queda con menos total, el backend recalcula el estado de pago como con
  cualquier edición.
- **Historial:** una fila `PartiesChanged` con el antes y el después de la entrega (p. ej. «Entrega: Enviar a Cra 7 #
  12-30, Medellín → Recoger en tienda»), además de la de envío si cambió.
- **Cotización de origen:** no cambia; el pedido guarda su copia.

Capacidad: el frontend sólo quita `modeLocked` si el pedido trae `order.deliveryEditable === true` (campo nuevo del
backend, handoff §10). Ausente (backend viejo) o `false` ⇒ sólo lectura como hoy.

## Tolerancia a backend viejo

- Sin `billingProfiles` en el cliente o en `client` del documento: sin selector ni switch de perfiles; sin tarjeta en
  la ficha.
- Backend primero en el despliegue.

## Permisos

| Acción | Permiso | Fuente |
| --- | --- | --- |
| Ver perfiles | el que ya lee el cliente o el documento | — |
| Guardar perfil o dirección desde el documento; agregar, editar, borrar, marcar principal en la ficha | `customers.customer.manage` | `CustomerEndpoints.cs:104`; `CUSTOMER_PERMISSIONS.manage` (`customer-engagement.ts:9`) |
| Elegir retenciones en la tarjeta | `catalog.tax_rate.read` | `customer-form.tsx:221` |

Nada se ofrece sin su permiso.

## Errores

| Caso | Qué se ve |
| --- | --- |
| El documento falla | Lo de hoy. No se guarda ni perfil ni dirección. |
| Documento OK, perfil o dirección fallan | El documento queda guardado. Toast «Se guardó el documento, pero no pudimos guardar el perfil en el cliente.» (o «…la dirección…») con el motivo por `code`. En edición el switch sigue prendido para reintentar. |
| 422 `customers.billing_profile.limit_reached` | «Este cliente ya tiene 50 perfiles de facturación. Para guardar otro, borra uno desde su ficha.» (documento: toast; ficha: bajo el formulario). |
| 422 con `errors` en la ficha | Se marca el campo (mapa por campo como `addressFieldErrors`, `customers.api.ts:1561-1601`). |
| 422 `customers.billing_profile.principal_not_removable` | «Marca otro perfil como principal antes de borrar este.» (no se debería ver: la principal no ofrece «Borrar»). |
| 403 | «No tienes permiso para editar este cliente.» |
| El documento responde 422 por la facturación de un perfil elegido | Lo de hoy (`quotePartiesFieldErrors`); para corregir se elige «Otro perfil…» o se edita el perfil en la ficha. |

## Qué cubren las pruebas

- Parsers: `billingProfiles`/`billingProfileLimit` ausentes ⇒ `undefined`; filas mal formadas descartadas.
- Helpers: copia CO/ES, coincidencia que ignora retenciones, selección de retenciones (activas, país de la empresa,
  sin empresa), principal al apagar el switch.
- `QuotePartyFields`: `name` del radio por prefijo, textos por props, `extraFields` siempre.
- `QuoteBillingParty`: backend viejo, lista, precarga de retenciones y excedente, switch con y sin permiso, con el tope.
- Reductores: elegir perfil y prender el switch cuentan como cambio; en editar pedido elegir perfil precarga
  `withholdings`.
- `usePersistPartiesToCustomer`: sólo después del documento; cuerpo sin municipales; parche del `client`; error con toast.
- Rutas: nueva/editar cotización, nuevo/editar pedido guardan el perfil; nueva cotización y nuevo pedido guardan la
  dirección (H-1); sin permiso no hay switch de dirección (H-2); en editar cotización la dirección guardada queda
  marcada (H-3).
- Tarjeta de la ficha: vacío, lista, agregar, editar, marcar principal, borrar con confirmación, 422 por campo, tope,
  sólo lectura sin permiso.
- Editar pedido, entrega: sin `deliveryEditable` queda de sólo lectura; con él, pasar a recoger limpia el envío y manda
  `delivery.isStorePickup: true` sin `shipping`; pasar a enviar exige dirección y arranca quién paga por defecto; la
  entrega viaja sólo si cambió; un 422 `already_prepared`/`already_invoiced` se muestra; guardar la dirección en el
  cliente después del pedido.

## Decisiones tomadas en este diseño

- **D-1.** Enfoque 1: sub-recurso en Customers, guardado después del documento.
- **D-2.** El switch «Facturar con los datos del cliente» se queda como perfil implícito.
- **D-3.** Apagar el switch elige el principal sólo si la facturación nunca se escribió.
- **D-4.** Al precargar, una tasa del perfil desactivada no se precarga; las municipales nunca se guardan en un perfil.
- **D-5.** Desde «Nuevo pedido» el perfil se guarda sin retenciones (esa pantalla no las pide).
- **D-6.** El tope lo manda el backend (`billingProfileLimit`); el frontend no escribe el 50.
- **D-7.** La tarjeta de la ficha edita en línea, como la de direcciones, sin diálogo.
- **D-8.** La entrega editable en «Editar pedido» se detecta con un campo nuevo `order.deliveryEditable` (el detalle
  del pedido no tiene hoy una bandera de capacidad; la facturación se infiere de la sesión,
  `orders/$orderId/editar.tsx:218`). Viaja en un bloque `delivery` nuevo y sólo si cambió.
- **D-9.** De recoger a enviar con «la dirección del cliente» y un cliente sin domicilio lo rechaza el backend
  (`order.delivery.address_required`); el frontend no lo adivina.

## Decisiones del owner (2026-11-24)

Tarjeta en la ficha: sí. Retenciones y excedente de IVA en el perfil: sí, editables por documento. Alias: no. Corregir
H-1..H-3 en esta rama: sí. Tope de 50: sí. Sin auditoría de perfiles (paridad con direcciones). Teléfono y email
opcionales en el perfil. Clave de dedupe completa (handoff §3.2). En «Editar pedido» la entrega es editable mientras el
pedido no esté facturado ni preparado, validado también en el backend (revierte «flete §9» / «H5»). La entrega se edita
en todo origen, también Shopify y mayorista (sección «Entrega editable por origen del pedido»).

## Entrega editable por origen del pedido

**Decisión del owner (2026-11-24):** la entrega se edita en todo origen —también `Shopify` y `Wholesale`— mientras el
pedido no esté facturado ni preparado. `deliveryEditable` no depende del origen. Lo que eso exige, verificado en el
código:

### Pedidos de Shopify

Hoy (backend `qep-backend`):

- Ya existe una corrección de la dirección de envío desde el detalle del pedido: el panel de Shopify
  (`features/shopify/components/shopify-order-correction-form.tsx`) llama `PUT …/shopify/orders/{id}/corrections`,
  que escribe el pedido y **encola un envío a Shopify** del grupo `ShippingAddress` (`ShopifyOrderCorrections.cs:355-408`;
  grupos posibles: `ShippingAddress`, `Note`, `Attributes`, `ShopifyOrderPush.cs:4-9`). El worker arma un `orderUpdate`
  que reemplaza la dirección entera y conserva `company` y `countryCode` del último snapshot
  (`ShopifyOrderPushProcessor.cs:101-125`). Esa corrección es **sólo para Colombia** (sin país, plan D21,
  `ShopifyOrderCorrections.cs:42-43`), y el worker exige una ciudad DIVIPOLA: con otra lanza
  (`ShopifyOrderPushProcessor.cs:106-109`).
- Un cambio hecho en QEP por otro camino (hoy, el `PUT /orders/{id}` del envío) **no** se empuja a Shopify, pero tampoco
  se pisa: la detección de ediciones compara el pedido de QEP de ahora con la proyección de lo que QEP escribió desde
  Shopify, y si difieren el grupo de Shopify queda en conflicto `qep_changed` y gana QEP (`ShopifyEditPlanner.cs:39-48`;
  la corrección del asesor no mueve `applied_projection`, `.claude/rules/shopify.md` del backend). O sea: hoy un cambio
  de entrega en «Editar pedido» quedaría **divergente** con Shopify.
- «Recoger o enviar» (grupo `Shipping` del diff, `ShopifyOrderDiff.cs:7-15`) no tiene grupo de envío: `orderUpdate` no
  cambia el método de entrega. Ese cambio siempre queda sólo en QEP.

Además, fuera de Colombia (verificado para un tenant de España con tienda de España):

- **El importador hoy no guarda parte de envío para una dirección de otro país.** La ciudad sólo se resuelve en Colombia
  (`ShopifyCustomerResolver.ResolveCityAsync`, `ShopifyCustomerResolver.cs:159-165`: fuera de `CO` devuelve «no
  colombiana»); sin ciudad no hay parte (`ShopifyOrderImporter.cs:290-298`), la dirección va a las notas y el pedido
  lleva el aviso `shipping_address_unresolved` (`ShopifyOrderImporter.cs:208-215`; `ShopifyWarnings.cs:8`). El borrador
  de la parte de Shopify (`ShopifyPartyDraft`, `ShopifyPorts.cs:430-431`) ni siquiera tiene país: es sólo Colombia.
- **El worker sólo sabe armar una dirección colombiana** (ciudad DIVIPOLA y código de departamento,
  `ShopifyOrderPushProcessor.cs:101-125`). La mutación sí admite cualquier país: `orderUpdate` con `MailingAddressInput`
  manda `address1`, `address2`, `city`, `countryCode`, `provinceCode` (opcional), `phone`, `firstName`, `lastName` y
  `company` (`ShopifyOrderUpdateClient.cs:123-149`). **No manda `zip`**, y como `orderUpdate` reemplaza la dirección entera
  (`.claude/rules/shopify.md` del backend), hoy un envío borra el código postal que tenía el pedido en Shopify. En Colombia
  casi no se nota; en España sí.
- `provinceCode` de Shopify es ISO 3166-2 sin el prefijo del país (`ShopifyOrderSnapshot.cs:95-96`, V-4: «M» para
  Madrid). La región guardada en QEP (`geography.regions`) trae el código **INE** de 2 dígitos, no el ISO
  (`Region.cs:28-29`): hace falta una tabla INE → ISO por país, como `ColombiaProvinceCodes` lo es para Colombia.
- QEP no tiene código postal en ninguna parte (`QuotationPartyDetails`, `QuotePartyFormValues`).

Comportamiento definido aquí:

1. **Dirección de envío cambiada en «Editar pedido» de un pedido de Shopify, de cualquier país:** el backend encola el
   mismo envío `ShippingAddress` que la corrección del panel, por un evento al outbox en la misma transacción del guardado
   (handoff §11.1). La entrega queda igual en los dos lados y una edición posterior de Shopify sobre la dirección sigue
   siendo conflicto (gana QEP).
2. **El worker arma también direcciones de otros países** (handoff §11.1): `countryCode` = país de la parte; `city` =
   `cityName`; `provinceCode` = la región guardada traducida a ISO con la tabla del país; `zip` = el del último snapshot
   de la parte (`postalCode`, spec de código postal) y, si está vacío, el del último snapshot **sólo si** país y región no
   cambiaron respecto de ese snapshot (si no, el código postal viejo describiría otra provincia).
3. **Lo que no se puede mapear se rechaza en el guardado, con 422 y `code`, nunca como falla del worker:**
   - `order.delivery.shopify_region_required` — país con catálogo de regiones (hoy España) y la parte sin región.
   - `order.delivery.shopify_region_unmapped` — la región no está en la tabla INE → ISO de ese país (o el país tiene
     catálogo y todavía no tiene tabla).
   - `order.delivery.shopify_postal_code_unknown` — país que exige código postal (hoy España, en la lista del handoff) y
     la parte no trae `postalCode` y no hay uno conservable del snapshot. Desde el spec `2026-11-24-codigo-postal-envio-design.md`
     (S-3 resuelto) la parte tiene código postal y tiene prioridad sobre el del snapshot; el 422 pide escribirlo.
   - Un país sin catálogo de regiones manda sin `provinceCode` y sin `zip` salvo el conservable: Shopify decide.
4. **Recoger ↔ enviar en un pedido de Shopify:** se guarda sólo en QEP (`orderUpdate` no cambia el método de entrega).
   La sección «Entrega» muestra un aviso fijo para `source === 'Shopify'`: «Este pedido viene de Shopify. La dirección se
   actualiza allá al guardar; recoger o enviar sólo cambia aquí.». Con la conexión inactiva el envío queda retenido
   (`Held`) y lo suelta «Sincronizar», como hoy.
5. **Siempre una parte propia en un pedido de Shopify:** el worker la necesita, así que no se ofrece «Enviar a la
   dirección del cliente» (backend: 422 `order.delivery.shopify_party_required`). Un pedido importado de otro país no
   tiene parte (punto anterior): la sección arranca en «Otra dirección…» precargada con el cliente.
6. **Precarga de un cliente de otro país:** hoy toda parte se precarga colombiana (`customerPartyValues`,
   `quote-parties-form.ts:182-186`: «precargar la de uno de afuera es otro slice»). Para que un tenant de España pueda
   corregir la dirección, esta rama precarga la parte con el país, la ciudad escrita (`cityName`) y la región del cliente
   (`QuoteCustomerView.country`, `.cityName`, `.region`). `CityCascade` ya decide por país (catálogo de regiones de España)
   y `regionIdToSend` ya manda `regionId` fuera de Colombia (`quote-parties-form.ts:317-337`), así que el envío de la parte
   no cambia. Esto también mejora la precarga en cotización y pedido nuevos para clientes de afuera.
7. **Transportadora reconocida:** pasar a recoger limpia la transportadora; el título que trajo la tienda
   (`shipping.externalShippingTitle`, `OrdersDtos.cs:367-384`) sigue en el espejo. «Reconocer este nombre» no se ofrece
   con recogida.

**La corrección del panel sigue siendo sólo de Colombia** (`ShopifyOrderCorrections.cs:42-43`, plan D21) y no se amplía:
no estorba —para otro país la ruta es «Editar pedido»— y no se contradice con esto, porque las dos encolan el mismo envío y
el worker ya sabrá armar los dos tipos de dirección. El panel no ofrece corregir una dirección extranjera; «Editar pedido»
sí.

### Pedidos mayoristas

Verificado: el pedido del mayorista nace **sin** direcciones propias —«el distribuidor no manda direcciones propias en esta
fase» (`IWholesaleOrderGateway.cs:188-189`)— y `WholesaleOrderDraft` no trae entrega (`IWholesaleOrderGateway.cs:13-23`).
El evento de aprobación sólo lleva líneas (`QuotationLifecycleEventPublisher.cs:109-115`). Nada de la entrega se espeja ni
se propaga a la orden de compra del distribuidor, así que cambiarla no desincroniza nada. Un pedido aprobado ya no es
editable (`Order.IsEditable`, `Order.cs:368-375`; frontend `isOrderEditable(order, wholesale)`, `order-state.ts:48-60`), y
eso cubre el caso «Por aprobar» (editable entero) vs aprobado. **No hace falta corrección.** El distribuidor no se entera
del cambio de entrega y no se agrega en esta rama (owner, S-2).

### Otras partes revisadas

- **PDF y envío por WhatsApp del pedido:** se arman con lo guardado; reflejan el cambio sin tocar nada.
- **Siigo:** la entrega sólo es editable antes de facturar; al facturar se lee la vigente.
- **Excel de pedidos y reportes:** leen las partes guardadas (`IQuotationCustomerLookup.FindManyAsync`,
  `IQuotationCustomerLookup.cs:49-57`).
- **Tienda pública (`Store`):** la entrega del checkout no se espeja en ningún lado; editable como cualquier pedido.

## Resueltos por el owner (2026-11-24)

- **S-3 (resuelto):** la parte de envío gana `postalCode` opcional, en un spec aparte:
  `docs/superpowers/specs/2026-11-24-codigo-postal-envio-design.md` (+ handoff y plan). Va **antes** que esta rama.
- **S-4 (resuelto):** el importador guarda la parte de envío de los pedidos de otros países, en un spec aparte:
  `docs/superpowers/specs/2026-11-24-shopify-parte-de-envio-otros-paises-design.md` (+ handoff; sin plan de frontend). Su
  único texto de frontend (el aviso `shipping_region_unresolved`) va en la Task 16 de este plan.

## No objetivos de esta sección

- Avisar al distribuidor cuando el mayorista cambia la entrega de un pedido «Por aprobar» (owner, 2026-11-24, S-2: no).
- Ampliar la corrección del panel de Shopify a otros países.

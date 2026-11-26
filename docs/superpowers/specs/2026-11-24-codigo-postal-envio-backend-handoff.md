# Código postal en la parte de envío — instrucciones para `qep-backend`

Fecha: 2026-11-24. Para: quien implemente en `qep-backend`. Origen: `qep-frontend/docs/superpowers/specs/2026-11-24-codigo-postal-envio-design.md`
(decisión del owner sobre S-3 de la rama de perfiles de facturación). Se copia a `qep-backend/docs/superpowers/specs/`.
**Se despliega primero el backend.** Líneas de `develop` al 2026-11-24; confírmalas antes de editar.

## 1. Qué se pide

Un `PostalCode` opcional en la parte de **envío** de cotización y pedido: se recibe, se guarda, se devuelve, se copia al
convertir, se imprime en el PDF y el worker de Shopify lo manda como `zip`. Sin formatos por país.

## 2. Hoy

- La parte no tiene código postal: `QuotationPartyDetails` (`Modules.Quotations.Domain/QuotationPartyDetails.cs:9-60`),
  `QuotationPartyRequest` (`Modules.Quotations.Application/QuotationsDtos.cs:367-387`), el mapeo `ToDomain`
  (`QuotationMapping.cs:230-247`) y las tablas `quotations.quotation_parties` y `quotations.order_parties`
  (`QuotationsDbContext.cs:431-484`, `:956-1004`).
- La conversión copia las partes con `ToDetails()` (`Order.cs:526-527`): una propiedad nueva viaja sola.
- El PDF arma la ubicación de la parte con dirección, y fuera de Colombia ciudad y país
  (`QuotationPdfDocumentMapper.cs:205-224`); `QuotationPdfParty` tiene `Location` como texto (`QuotationPdfDocument.cs:138-143`).
- Shopify: el snapshot no lee `zip` (`ShopifyAddress`, `Modules.Shopify.Application/ShopifyOrderSnapshot.cs:98-107`;
  parsers `RestOrderParser.cs:50-59`, `GraphQlOrderParser.cs:~60-66`) y `orderUpdate` no lo manda
  (`ShopifyOrderUpdateClient.cs:123-149`). Como `orderUpdate` reemplaza la dirección entera (`.claude/rules/shopify.md`),
  hoy un envío borra el código postal en Shopify.
- La libreta (`CustomerAddress`, `Modules.Customers.Domain/CustomerAddress.cs:16-85`) y el domicilio no tienen código
  postal (P-1 del spec del frontend: fuera de esta entrega).

## 3. Contrato

- `QuotationPartyRequest` gana `string? PostalCode = null` al final. **Semántica de reemplazo**, como `RegionId`
  (`QuotationsDtos.cs:385-387`): omitirlo lo borra. Riesgo aceptado: un frontend viejo que guarda una entrega con código
  postal lo borraría; la ventana es la del despliegue (backend primero, frontend inmediatamente después).
- Se aplica en todas las rutas que reciben partes: `POST /quotations`, `PUT /quotations/{id}`, sus `preview`,
  `POST /orders` y `/orders/preview`, y el `PUT /orders/{id}` + preview (con el bloque `delivery` de la rama de perfiles).
- `QuotationPartyResponse` gana `string? PostalCode` (las dos partes; en facturación siempre `null`).
- Capacidad: la respuesta de la cotización y la del pedido (`OrderResponse`) ganan `bool PostalCodeSupported = true`.
  El frontend sólo pinta y manda el campo con ese `true` (un documento nuevo no tiene partes donde mirar). Anótalo en el
  DTO (BFF).

## 4. Reglas

- Sólo la parte de envío lo guarda; en la de facturación se descarta, como la entrega descarta el documento
  (`QuotationEdits.cs:107-108`; `QuotationParty` limpia los campos de facturación en la entrega).
- Normalización en `QuotationPartyDetails.Normalized()`: recorte; vacío ⇒ `null`; tope `PostalCodeMaxLength = 20`
  (constante nueva; 422 `quotations.party.postal_code_too_long` con la clave `Parties.Shipping.PostalCode` en `errors`
  desde el validador de partes, `QuotationEdits.cs:113-165`). Sin validación de formato por país: no hay tabla y no se
  inventa.
- Sin regla de carril: vale en Colombia y fuera.
- Igualdad de la parte (`Order.SetBilling` compara `ToDetails()`, `Order.cs:918`; la entrega hará lo mismo): el código
  postal cuenta.

## 5. Shopify

- `ShopifyAddress` suma `Zip`; los dos parsers lo leen (`zip`). El query de GraphQL del pedido pide `zip` (a confirmar el
  archivo del query junto a `GraphQlOrderParser`).
- `ShopifyAddressUpdate` (`ShopifyPorts.cs:323-332`) suma `Zip`; `ShopifyOrderUpdateClient.Input` lo manda como `zip`
  cuando no es `null`.
- `ShopifyOrderState.ShippingParty` (el estado del pedido para Shopify) suma `PostalCode`.
- Prioridad en `ShopifyOrderPushProcessor.BuildAsync` (`:101-125`): `PostalCode` de la parte si no está vacío; si no, el
  `Zip` del último snapshot sólo si país y provincia de la parte coinciden con los del snapshot; si no, `null`.
- `order.delivery.shopify_postal_code_unknown` (rama de perfiles, handoff §11.1 punto 4) queda: país que lo exige, sin
  `PostalCode` y sin `Zip` conservable. Ahora el usuario puede escribirlo.
- La corrección del panel (`PUT …/shopify/orders/{id}/corrections`, `ShopifyOrderCorrections.cs:355-358`) no recibe código
  postal (`ShopifyPartyInput`, `:42-43`): al reescribir la parte, el escritor **conserva** el `PostalCode` que tenía
  (si no, cada corrección lo borraría). Prueba: `ACorrectionKeepsThePostalCode`.
- Importador: lo llena la rama `2026-11-24-shopify-parte-de-envio-otros-paises` (con el `Zip` del snapshot).

## 6. Migración

`AddPartyPostalCode` (Quotations): `ALTER TABLE quotations.quotation_parties ADD postal_code varchar(20) NULL` y lo
mismo en `quotations.order_parties`. Expand puro, sin backfill. Lee `.claude/rules/build-y-despliegue.md` antes. Si
alguna consulta de otro módulo lee estas tablas por SQL crudo (Excel de pedidos, reportes en Bootstrapper), revisa
`.claude/rules/sql-entre-modulos.md`: una columna nueva no rompe un `SELECT` por nombre, pero un `SELECT *` mapeado sí.

## 7. PDF

`PartyFor` (`QuotationPdfDocumentMapper.cs:205-224`) agrega el código postal a `Location` después de la dirección, en los
dos carriles (CO: «dirección · código postal»; fuera: «dirección · código postal · ciudad · país»). Sin cambio de
plantilla: `Location` ya es texto. El caché del PDF se invalida por versión del documento, como hoy.

## 8. Pruebas esperadas

Unitarias: normalización (recorte, vacío ⇒ `null`, 21 caracteres ⇒ `postal_code_too_long`); la facturación lo descarta;
`ToDetails` lo incluye; la conversión cotización → pedido lo copia.

Integración (Quotations): crear, editar y preview de cotización y pedido guardan y devuelven `postalCode` en la entrega;
omitido lo borra; facturación lo devuelve `null`; `postalCodeSupported: true` en las respuestas; PDF con el código postal
en la ubicación (CO y ES).

Shopify: parser lee `zip` (REST y GraphQL); el envío manda el de QEP antes que el del snapshot; sin el de QEP y con
provincia cambiada no manda `zip`; la corrección del panel conserva el código postal; el `zip` del snapshot ya no se
borra en un envío de Colombia con el mismo departamento.

## 9. Orden de despliegue y de ramas

Backend primero, luego frontend. Orden recomendado de las tres ramas: **código postal** → **perfiles de facturación** →
**importador de otros países** (este último puede adelantarse si crea `SpainProvinceCodes`).

## 10. Decisiones del owner (2026-11-24)

- **P-1.** Código postal en la libreta de direcciones: sí, en un spec aparte. En el domicilio del cliente: después. Nada
  de eso entra en esta entrega.
- **P-2.** Sin código postal en facturación por ahora.

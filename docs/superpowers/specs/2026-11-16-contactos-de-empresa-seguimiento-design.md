# Contactos de empresa: seguimiento (decisiones del owner, 2026-11-16)

Cierra tres decisiones sobre «Contactos de empresa» (`Customer.CompanyId`, spec
`2026-11-10-contactos-de-empresa-backend-design.md`, mergeado a `develop` el 2026-11-11). El resto de ese
diseño no cambia.

## 1. Triage por contacto, con elección del cliente del negocio

**Antes.** La conversación de un contacto dejaba de estar pendiente si **la empresa** tenía cualquier
negocio abierto (§4.5), y el negocio del triage nacía siempre en la empresa (G11).

**Ahora.**

- La empresa no bloquea nada. Una conversación con cliente está pendiente, como antes del 2026-11-10,
  salvo que **el contacto** tenga un negocio abierto propio. Se agrega un caso que no existía: tampoco está
  pendiente si sigue abierto un negocio con `OriginConversationId` igual a la conversación, esté en el
  contacto o en la empresa. Sin ese caso, el negocio que el triage abrió en la empresa volvería a pedir
  triage con cada mensaje entrante. Lo decidió el owner.
- `POST /pipeline/triage/{conversationId}` acepta `customerId` opcional: el contacto de la conversación
  o su empresa. Si no viene, el negocio nace en la empresa si la hay y si no en el contacto, igual que hoy.
  Cualquier otro id (incluida una «empresa» que el contacto no tiene) responde
  **422 `pipeline.triage.deal_customer_invalid`**.
- El título por defecto es el nombre de quien recibe el negocio: la etiqueta de la empresa (A4) o el
  nombre del contacto.
- La decisión de triage sigue guardando al contacto (G11).
- La ficha (`triage` del resumen de negocios) usa la misma regla.

## 2. Un cliente inactivo no recibe negocios nuevos

| Camino | Con quien recibe el negocio inactivo |
| --- | --- |
| Triage (`create`) | 422 `pipeline.deal.customer_inactive`; no nace ni el negocio ni la decisión |
| `POST /pipeline/deals` (ficha) | 422 `pipeline.deal.customer_inactive` |
| Evento de cotización enviada o convertida, o de pedido sin cotización | No se abre un negocio nuevo; la cotización o el pedido queda sin enlazar, como cuando hay varios negocios abiertos. Si el cliente ya tiene un negocio abierto, se enlaza a ese como siempre. |

- Se mira **quien recibe** el negocio. Con la empresa inactiva, el negocio puede nacer en el contacto
  activo, y al revés.
- «Incompleto» no bloquea nada.
- Cotizaciones y pedidos no se bloquean.
- Descartar en el triage no se bloquea.
- Ningún mensaje entrante de Mensajería crea negocios, así que la recepción no se toca.

## 3. `company_changed` en fusión e importación

En la fusión, cada vínculo que cambia deja un `customers.customer.company_changed` con la misma forma que el de
`PUT /customers/{id}/company` (`SetCustomerCompanyHandler`): el cliente como recurso, `success`, sin
`source`. Esa forma **no lleva antes/después**: `ICustomersAuditPublisher.Publish` no tiene dónde
guardarlo, y el evento de `SetCustomerCompanyHandler` tampoco lo lleva.

- **Fusión.** Cambian tres vínculos:
  - el fusionado que era contacto deja de serlo;
  - el que queda pierde la empresa que se fusionó en él;
  - cada contacto de un fusionado pasa al que queda (`RepointContactsAsync` ahora devuelve sus ids).
- **Importación.** Sin un evento por contacto: el único evento del archivo (`customers.customer.imported`)
  suma la cuenta en su `resourceId`, por ejemplo `clientes.xlsx (imported=1000, errors=3, companyChanged=850)`.
  Un vínculo repetido no cambia nada y no cuenta. Decisión del owner, para no dejar mil eventos en el outbox
  por un archivo de mil contactos.

## Pendiente

- Frontend: ofrecer la elección «contacto o empresa» en el triage y mostrar los dos códigos nuevos.
- `pipeline.deals` no tiene índice en `origin_conversation_id`. La consulta nueva filtra por tenant y
  negocios abiertos; si crece, el índice va en una migración aparte.

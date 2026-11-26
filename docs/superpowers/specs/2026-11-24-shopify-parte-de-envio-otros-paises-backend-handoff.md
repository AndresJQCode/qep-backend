# Shopify: parte de envío de pedidos de otros países — instrucciones para `qep-backend`

Fecha: 2026-11-24. Para: quien implemente en `qep-backend`. Diseño: `qep-frontend/docs/superpowers/specs/2026-11-24-shopify-parte-de-envio-otros-paises-design.md`.
Se copia a `qep-backend/docs/superpowers/specs/`. Líneas de `develop` al 2026-11-24; confírmalas. Lee antes
`.claude/rules/shopify.md` (regla del único camino de la ingesta, puertos de `ShopifyPorts.cs`, `ShopifyLayerTests`).

## 1. Hoy

| Pieza | Comportamiento | Fuente |
| --- | --- | --- |
| Resolución de ciudad | Sólo Colombia; fuera de `CO` devuelve «no colombiana» | `Modules.Shopify.Application/ShopifyCustomerResolver.cs:159-165` |
| Parte de envío del importador | Sin ciudad DIVIPOLA ⇒ `null` | `ShopifyOrderImporter.cs:290-298` |
| Sin parte | Aviso `shipping_address_unresolved`, cruda en las notas | `ShopifyOrderImporter.cs:208-215`, `:446`, `:467-476`; `Modules.Shopify.Domain/ShopifyWarnings.cs:8` |
| Borrador de la parte | Sin país, ciudad ni región en texto | `ShopifyPartyDraft`, `ShopifyPorts.cs:430-431` |
| Ediciones | Dirección extranjera ⇒ conflicto `city_unresolved` | `ShopifyEditPlanner.cs:185-190` |
| Snapshot | Trae `CountryCode` y `ProvinceCode` (ISO 3166-2 sin prefijo); no trae `Zip` | `ShopifyOrderSnapshot.cs:95-107`; `RestOrderParser.cs:50-59`; `GraphQlOrderParser.cs` (`provinceCode`, `countryCodeV2`) |
| Regiones | Código INE de 2 dígitos | `Modules.Geography.Domain/Region.cs:28-29` |

## 2. Cambios

1. **`ShopifyPartyDraft`** suma `Country`, `CityName`, `RegionId` y `PostalCode`. Colombia sigue con `DepartmentId` y
   `CityId`. El adaptador de Bootstrapper que crea el pedido (`IShopifyOrderWriter`) los pasa a `QuotationPartyDetails`
   (`Country`, `CityName`, `RegionId`, y `PostalCode` del spec de código postal).
2. **Importador:** si el país del snapshot no es `CO`:
   - `CityName` = `City` recortada (≤ 120). Vacía ⇒ sin parte (como hoy), con `shipping_address_unresolved`.
   - `RegionId`: sólo si Geography tiene catálogo para el país. `ProvinceCode` → INE con `SpainProvinceCodes` en sentido
     inverso (tabla de la rama de perfiles, handoff de perfiles §11.1; si esta rama va antes, créala aquí con su prueba de
     paridad contra `RegionSeeder` en Bootstrapper.UnitTests). Luego un puerto nuevo de Geography
     `IShopifyGeographyLookup.FindRegionAsync(countryCode, regionCode)` (junto a `LookupCityAsync`, `ShopifyPorts.cs:294-297`;
     adaptador en `Bootstrapper/ShopifyGeographyLookup.cs`). Sin código, sin traducción o sin región: parte **sin región**
     y aviso nuevo `ShopifyWarnings.ShippingRegionUnresolved = "shipping_region_unresolved"`.
   - `PostalCode` = `Zip` del snapshot (el spec de código postal suma `Zip` a `ShopifyAddress` y a los parsers).
   - Nombre, teléfono, dirección y correo: `ShippingPartyFields` (`ShopifyOrderImporter.cs:300-311`).
   - Con parte, la cruda no va a las notas (`Notes(snapshot, null)`).
3. **Ediciones (`ShopifyEditPlanner`):** el grupo `ShippingAddress` de otro país arma el candidato con la misma regla del
   punto 2 en lugar de pedir `ResolveCityAsync` (`:185`). Proyección, `qep_changed` y eco sin cambios. Sin ciudad sigue
   siendo `city_unresolved`; sin región, se aplica igual (sin región) y no es conflicto.
4. **Worker (`ShopifyOrderPushProcessor`):** nada nuevo aquí; el multipaís lo hace la rama de perfiles (handoff de perfiles
   §11.1). Esta rama sólo asegura que la parte importada tenga lo que el worker necesita.
5. **Quotations:** sin cambios de contrato: la parte de otro país ya existe (`QuotationPartyDetails.cs:36-50`) y
   `PartyRegionRules` valida la región contra el país (`PartyRegionRules.cs:14-30`); el adaptador llama a la creación como
   hoy, y una región inválida sería un error del adaptador (pruébalo).

## 3. Pedidos ya importados

**Decisión del owner (B-1): sin backfill.** Razones: un backfill escribiría el pedido fuera de
`ShopifyOrderIngestion.ApplyAsync` (la regla del único camino, `.claude/rules/shopify.md`), podría pisar correcciones hechas
a mano, y la próxima edición de Shopify sobre la dirección ya se aplica (pedido sin parte y proyección sin parte: no es
conflicto). Los no facturados ni preparados se corrigen en «Editar pedido». Si más adelante se pide, el camino correcto es
re-ingestar la última versión de cada fila del espejo por `ApplyAsync`, no un `UPDATE`.

## 4. Pruebas esperadas

Unitarias (Shopify):

- Pedido de España con `province_code: "M"`, ciudad, `zip`: la parte sale con `Country = "ES"`, `CityName`, `RegionId` de
  Madrid y `PostalCode`; sin notas crudas ni aviso.
- Sin `province_code`, o con uno fuera de la tabla: parte sin región y aviso `shipping_region_unresolved`.
- Sin ciudad: sin parte y `shipping_address_unresolved` (como hoy).
- País sin catálogo (p. ej. `FR`): parte con `CityName`, sin región, sin aviso de región.
- Colombia: sin cambios (las pruebas actuales del importador siguen verdes).
- Planificador: una edición de Shopify de la dirección de un pedido de España se aplica si el asesor no la tocó, es
  conflicto `qep_changed` si la tocó, y eco si coincide.
- `SpainProvinceCodes` inverso cubre todas las regiones de `ES` sembradas (paridad).

Integración: el webhook de un pedido de España crea el pedido de QEP con la parte de envío (detalle con `country`,
`cityName`, `region`, `postalCode`).

## 5. Orden

Código postal → perfiles de facturación → esta (o esta antes que perfiles si crea `SpainProvinceCodes`). Sin frontend:
el texto del aviso nuevo va en la Task 16 del plan de perfiles.

## 6. Decisiones del owner (2026-11-24)

- **B-1.** Sin backfill de pedidos ya importados.

## Correcciones contra el código (2026-11-26, `develop` en `257e47663`)

Después del merge de perfiles de facturación (`d65658756`) manda el código. Las líneas citadas arriba están corridas.

- **Ya existen:** `ShopifyPartyDraft.PostalCode`; `Zip` en `ShopifyAddress` y en los parsers; `SpainProvinceCodes` (sólo
  INE → ISO, `IsoFor`); `ShopifyPushAddress.IsForeignEchoAsync`, que `ShopifyEditPlanner` ya usa; `Country`, `CityName` y
  `RegionId` en `ShopifyShippingFingerprint` y en `ExternalShippingParty`.
- **§2.1:** el adaptador es `ShopifyOrderWriter`, más `ShopifyOrderCorrectionWriter` y `ShopifyCityCorrectionWriter`.
- **§2.5:** el alta y la edición de un pedido externo (`ExternalOrderService`) **no** pasan por `PartyRegionRules`. La región
  es válida porque Shopify la busca en Geography por país y código; Quotations no cambia.
- **Diseño, «Ediciones»:** `ShopifyOrderDiff.AddressRows` compara provincia, pero **no** país (decisión pendiente P-4 del plan).

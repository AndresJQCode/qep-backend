# Cotizaciones ocultables sin afectar los pedidos (backend)

Fecha: 2026-11-21. Decisión del owner: la consola de plataforma debe poder ocultar el módulo de
cotizaciones a un tenant, y los pedidos no pueden quedar colgados de él, porque un pedido ya se crea
sin cotización (spec 2026-10-15). No todo tenant con cotizaciones tiene pedidos, ni al revés.

## Qué cambia

1. **`orders` deja de depender de `quotations`.** `TenantModuleKeys.Dependencies[Orders]` pasa de
   `[Quotations]` a `[Catalog, Customers, Companies]`, lo que `quotations` ya exigía. Apagar
   `quotations` ya no apaga en cascada `orders`, `shopify`, `store`, `wholesale` ni `siigo`. No hay
   migración: el `CHECK` de claves y las filas de `tenant_modules` no cambian.
2. **Lo que comparten los dos módulos se abre con cualquiera de los dos.** Nueva guarda
   `TenantModuleGuard.EnsureAnyEnabledAsync` y conjunto `QuotationsModuleSets.QuotationsOrOrders`.
   La usan: transportadoras (`List`, `Create`, `Update`, `Reorder`, `SetActive`), tasa y modos del
   flete, `GetShippingOptions`, contenido del PDF (`GET`/`PUT`) y canales de WhatsApp (`GET`/`PUT`).
   Los permisos de esos handlers son de Tenancy (`SettingsRead`/`SettingsUpdate`), que no dependen de
   ningún módulo; sólo cambia la guarda de módulo.
3. **`/shipping-options` acepta `QuotationRead` o `OrderRead`.** Política nueva
   `QuotationsPolicies.ShippingOptionsRead`, registrada en `AddAuthorization`. Sin ella, un tenant que
   oculta cotizaciones pierde `QuotationRead` por `ModuleEntitlementMask` y su formulario de pedido
   no puede dibujar la entrega.
4. **Zenvia lo consumen cotizaciones y pedidos** (`ConsumingModules = [Quotations, Orders]`): envía
   los dos documentos y la plantilla de pedido es un campo de la conexión.

## Qué no cambia

- `GetQuotationSettings` / `UpdateQuotationSettings` siguen exigiendo `quotations`: son de la
  cotización (vigencia, mínimos).
- Los permisos `quotations.order.*` son del módulo `orders`; ocultar cotizaciones no los quita.
  Sólo desaparecen `quotations.quotation.*`.
- `ChangeTenantModules` no necesitó cambios: valida con `DependenciesOf`, así que sigue la
  dependencia nueva.

## Consecuencia a conocer

Un tenant que ya tenía `orders` y se queda sin `quotations` conserva sus pedidos, su Excel, el PDF
del pedido y Shopify, pero sus cotizaciones quedan inaccesibles (403 `tenancy.module_not_enabled`),
igual que hoy. `ListQuotations` ya oculta el pedido de una cotización cuando `orders` está apagado;
el caso inverso no escribe nada.

## Pruebas

`TenantModuleKeysTests`, `TenantModuleSetTests`, `TenantModuleGuardTests` (Tenancy),
`SharedShippingModuleGuardTests` (Quotations, unitarias), `TenantModulesQuotationsApiTests`
(integración: `OrdersAndShippingOptionsSurviveHidingQuotations`, `ShippingOptionsSurviveHidingOrders`,
`ShippingOptionsAreForbiddenWithoutQuotationsNorOrders`) e `IntegrationCatalogTests`
(`ZenviaIsVisibleWithOnlyOrdersOrOnlyQuotations`).

## Pendiente (otro repo)

`qep-frontend`, con su propio spec: ocultar el menú y las rutas de cotizaciones cuando el módulo esté
apagado, y que las pantallas compartidas (transportadoras, fletes, contenido del PDF) sigan visibles
si está prendido cualquiera de los dos. El front hoy puede suponer que `orders` implica `quotations`:
hay que verificarlo en su código, no se verificó acá.

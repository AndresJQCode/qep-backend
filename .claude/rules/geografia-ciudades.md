---
paths:
  - "src/Modules/Geography/**"
  - "tests/Modules/Geography/**"
  - "src/Bootstrapper/**/City*.cs"
  - "src/Bootstrapper/SiigoGeographyLookup.cs"
  - "src/Bootstrapper/SiigoForeignCityInbox.cs"
  - "src/Modules/Shopify/**/*City*"
---

# Geografía y sinónimos de ciudades

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El sinónimo de una ciudad casa después de los dos nombres, y confirmar uno igual a una ciudad real es 422** (spec 2026-10-26).
  `CityRepository.MatchByNameAsync` compara el nombre, luego el oficial del DANE y sólo si ninguno casa busca el sinónimo vivo:
  una fila de sinónimo con el texto de una ciudad real —sólo llega por SQL— nunca le gana. `resolve` e `impact` lo rechazan con
  `geography.synonym.matches_city`. Si inviertes el orden, el sinónimo le quita el pedido a una ciudad legítima sin error. Lo
  amarran `ARealCityNameWinsOverASynonymWithTheSameText` y `CityOperatorApiTests`.
- **El payload de `geography.city_synonym.*` es contrato con Shopify y vive literal en dos pruebas.** `GeographyEventPublisher`
  lo escribe y `CitySynonymEventPayloads` lo lee por nombre: renombrar un campo compila, pasa las pruebas de cada lado por
  separado y deja el mensaje marcado como procesado sin trabajos sembrados. Se cambia en los dos lados y en las dos pruebas con el
  mismo literal, `GeographyEventPublisherTests` y `CitySynonymEventPayloadsTests`.
- **Un trabajo de corrección de ciudad aplica la ciudad _actual_ del sinónimo, y uno borrado salta lo pendiente.**
  `ShopifyCityCorrectionProcessor` relee el sinónimo al procesar: si el operador lo corrigió entre el evento y el trabajo manda la
  ciudad nueva, y si lo borró, el trabajo termina `Skipped` con `synonym_deleted` y el pedido queda como estaba. Por eso borrar
  no publica nada ni deshace lo ya corregido, y por eso `IShopifyCitySynonymLookup` devuelve también los borrados: filtrarlos
  haría que el trabajo no supiera por qué saltó. Lo amarran `CitySynonymEndToEndTests.DeletingWhileJobsArePendingSkipsThem` y
  `ShopifyCityCorrectionProcessorTests`.
- **La lista de Siigo (`geography.billing_cities`) no se corrige y el seeder nunca borra** (spec 2026-11-26). Los códigos viajan tal
  cual vienen —Miami trae `city_code = "1"`, Quito queda en Pastaza— y un sinónimo del exterior apunta a una fila de ahí con
  `ON DELETE RESTRICT`: si el seeder borrara o renumerara una fila que dejó de venir, el sinónimo quedaría huérfano o el seed
  fallaría. «Arreglar» un código de la lista es cambiar lo que se factura, sin que nadie lo elija.
- **Un nombre repetido en un país nunca coincide solo, y confirmarlo no es `matches_city`.** Boston (`Us`, `02119` y `2101`) o Berlin
  y Berlín (`De`) dan dos ciudades: `ForeignBillingCityResolver` los manda a la bandeja y sólo un sinónimo vivo los resuelve. Por eso
  `geography.synonym.matches_city` del exterior se lanza únicamente cuando el texto coincide con **una** ciudad del país; con dos o
  más el operador sí puede vincularlo a cualquiera. `IX_billing_cities_country_name` no es único por la misma razón. Lo amarran
  `ForeignBillingCityResolverTests` y las pruebas del operador del exterior.
- **El candado del exterior lleva el prefijo `foreign:` y no comparte llave con Colombia.** `LockForeignCityTextAsync` toma
  `foreign:{país}:{texto}` y el colombiano `{departmentId}:{texto}`. El reportero y confirmar un texto toman la misma llave: sin ella,
  un reporte que cruza la confirmación deja una ocurrencia nueva en una fila ya resuelta. Si le quitas el prefijo, un texto del
  exterior podría serializarse contra uno colombiano sin motivo (o chocar con él).

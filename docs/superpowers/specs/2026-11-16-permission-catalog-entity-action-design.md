# Catálogo de permisos con entidad y acción

Fecha: 2026-11-16. Repo: `qep-backend`. Spec hermano en `qep-frontend`:
`docs/superpowers/specs/2026-11-16-roles-permission-matrix-design.md`.

## Por qué

El editor de roles de `/roles` pasa de una lista plana de 85 casillas a una matriz: módulo › entidad en
las filas y acción en las columnas, con casillas que conceden un grupo entero (una acción en todo, un
módulo, una entidad) antes de bajar al permiso suelto. La maqueta aprobada por el owner es la
dirección D del lienzo «Rediseño Roles y permisos».

Para armar esa matriz la pantalla necesita saber, de cada permiso, a qué entidad pertenece, qué acción
es y cómo se llaman las dos en pantalla. Hoy `GET /api/v1/tenants/{tenantId}/authorization/catalog`
manda sólo `permission`, `displayName`, `description`, `category` y `riskLevel`.

Las claves ya tienen casi siempre la forma `módulo.entidad.acción` (`customers.customer.delete`,
`pos.sale.void`), pero el front no debe partirlas ni ponerles nombre con un diccionario propio: cada
permiso nuevo aparecería sin nombre o en el lugar equivocado sin que nada falle (regla BFF del
`CLAUDE.md` del frontend). La estructura la publica el backend.

## Objetivos

- Cada permiso del catálogo trae su entidad y su acción, con clave estable y nombre visible.
- El catálogo dice qué acciones son **columnas** de la matriz y en qué orden; las demás van a «Otros».
- El nombre visible del módulo (`category`) viaja en la respuesta, para que el front deje de traducirlo.
- Ningún permiso queda sin entidad, sin acción o sin nombre: lo verifica una prueba.

## No objetivos

- No cambian las claves de permiso, los roles, las membresías ni cómo se autoriza un request.
- No se agregan dependencias entre permisos («gestionar implica ver»). La matriz no las aplica y el
  backend tampoco; si algún día se quieren, es otro spec.
- No cambia `GET /authorization/roles` ni `GET /authorization/me`.
- No se agrega el conteo de miembros por rol (quedó como idea de la dirección B, sin aprobar).

## Anti-objetivos

- Que el front tenga que adivinar algo de la clave: si la respuesta no lo dice, no existe.
- Que agregar un permiso nuevo sin entidad o sin acción compile y llegue a producción.

## Contrato

`PermissionCatalogItemResponse` suma cinco campos; los cinco de hoy siguen igual:

```json
{
  "permission": "customers.customer.merge",
  "displayName": "Fusionar clientes",
  "description": "Permite fusionar clientes duplicados en uno. No se puede deshacer.",
  "category": "Customers",
  "riskLevel": "high",
  "categoryLabel": "Clientes",
  "entity": "customers.customer",
  "entityLabel": "Clientes",
  "action": "merge",
  "actionLabel": "Fusionar"
}
```

`AuthorizationCatalogResponse` suma `actions`, la lista ordenada de las acciones que son columna:

```json
"actions": [
  { "key": "read", "label": "Ver" },
  { "key": "manage", "label": "Gestionar" },
  { "key": "delete", "label": "Borrar" },
  { "key": "import", "label": "Importar" }
]
```

Reglas del contrato:

- `entity` es una clave estable y única en todo el catálogo; la convención es `módulo.entidad`.
  `entityLabel` es el nombre de la fila en pantalla, en español neutro.
- `action` es la clave de la acción; `actionLabel` es su nombre corto, que es el texto del chip cuando
  la acción no es columna («Fusionar», «Anular», «Facturar»).
- Un permiso cuya `action` está en `actions` ocupa la celda de esa columna en la fila de su entidad. Uno
  cuya `action` no está va a «Otros» de su fila. Dos permisos de la misma entidad nunca comparten
  `action`.
- `categoryLabel` es el nombre del módulo en pantalla. Hoy lo traduce el front
  (`PERMISSION_CATEGORY_LABEL` en `roles-page.tsx`); pasa a ser del backend.
- Los reportes no son una columna: `reporting.orders.read` es la entidad «Pedidos» del módulo
  «Reportes», acción `read`.

## Decisiones tomadas

- **Explícito en la definición, no parseado.** Entidad y acción se declaran en cada `PermissionDefinition`
  (o en una tabla al lado, dentro del mismo módulo de Authorization) en vez de partir la clave. Las
  claves de dos partes (`advisorship.read`, `advisorship.invite`, `advisorship.manage`) no se pueden
  partir, y una excepción escondida en un parser es justo lo que no se nota al agregar un permiso.
- **Las acciones columna las fija el backend.** Propuesta inicial: `read`, `manage`, `delete`, `import`,
  en ese orden. El owner puede ajustarla; el front pinta lo que llegue.
- **Qué es «Gestionar» (owner, 2026-11-16).** La clave de `action` no tiene que copiar el verbo de la clave
  del permiso. `tenancy.settings.update` va con `action: "manage"`: es editar datos y nada más. Los
  permisos del punto de venta (`pos.sale.create`, `pos.sale.void`, `pos.sale.discount`,
  `pos.sale.customer`, `pos.register.operate`) **no** son `manage`: van a «Otros» con su propia acción,
  porque son operaciones de caja, y darlos de un clic a quien gestiona el resto concedería de más.
- **Clientes viejos no se rompen.** Los campos se suman; ninguno cambia de nombre ni de tipo.

## Lo que queda para quien construye

- Dónde viven entidad, acción y sus nombres: campos nuevos en `PermissionDefinition` o una tabla aparte
  en Authorization. Cualquiera sirve si la prueba de completitud la cubre.
- La agrupación concreta de las 85 definiciones de `QepServiceCollectionExtensions.cs` en entidades y
  acciones, incluidas las de dos partes (propuesta: `advisorship.read|manage` → entidad «Miembros»,
  `advisorship.invite` → «Invitar» en Otros de Miembros; `advisorship.roles.manage` → entidad «Roles»).
- Las claves que no calzan limpio en entidad y acción (`catalog.product.cost.read|manage`,
  `reporting.all_advisors.read`, `reporting.margin.read`, `customers.data_consent.revoke`): se ubican con
  el mismo criterio que «Gestionar» —la acción dice qué hace, no copia la clave—, y la prueba de
  unicidad frena una agrupación que no cierra.
- `storage.file.upload` y los demás verbos de escritura que no son editar datos quedan en «Otros», salvo
  que sean editar, como `update`. Decisión de quien construye; se marca en el plan para el owner.
- Si el `catalogVersion` debe cambiar con los campos nuevos.
- Las claves de `operator.*` no llegan como casillas (`OfferedAsCheckbox`), pero igual llevan entidad y
  acción para que la prueba de completitud no tenga excepciones.

## Pruebas

- Unitaria: toda `PermissionDefinition` tiene entidad, acción y los dos nombres no vacíos; ninguna
  entidad repite acción; toda `category` tiene `categoryLabel`.
- Integración (filtrada por nombre, Testcontainers): `GET …/authorization/catalog` trae los campos nuevos
  y `actions` en orden, y un permiso fuera de `actions` (p. ej. `customers.customer.merge`) llega con su
  `actionLabel`.

## Despliegue

Backend primero. El front nuevo tiene un camino para el catálogo sin estos campos (ver su spec), así que
el orden no rompe nada, pero sin el backend la matriz no aparece.

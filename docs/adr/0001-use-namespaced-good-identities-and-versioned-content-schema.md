# Use namespaced good identities and a strict versioned content schema

Goods use one stable `GoodId` across settlements, transport, markets, and content catalogs, while a separate closed `GoodCategory` expresses classifications such as natural resources. Built-in and mod definitions share the `<namespace>:<local-id>` identity shape and a strict, explicitly versioned `goods` JSON schema so content references remain unambiguous and incompatible data fails visibly rather than being interpreted differently by source or load order.

## Consequences

Built-in IDs use the `core` namespace, mod namespaces come from their manifest UUID, and definition files contain only local IDs. Natural resources are modeled by composition as goods whose category is `natural_resource`; folders remain an authoring convention rather than a type boundary. Unknown fields, unknown schema versions, duplicate definitions, and invalid category-specific components invalidate the owning content package.
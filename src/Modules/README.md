# Module assembly boundaries

The approved architecture defines these business modules:

- Identity
- Catalog
- Inventory
- Pricing
- Customers
- Cart
- Checkout
- Orders
- Payments
- Shipping
- Taxes
- Promotions
- Reviews
- Wishlist
- Media
- Content
- Notifications
- Reporting

A module project is introduced when its implementation phase begins, rather than creating empty placeholder assemblies for all future modules in Phase 1. This keeps the solution buildable and avoids boilerplate projects with no contracts or behavior.

Each module will own its Domain, Application, Infrastructure, persistence schema/context and public composition contract as those parts are implemented. The API host may reference only the module's public composition/endpoint contract; module internals remain encapsulated. Cross-module dependencies must follow the approved architecture rules in [ARCHITECTURE.md](../../docs/ARCHITECTURE.md).

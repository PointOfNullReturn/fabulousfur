# FabulousFur — A Multi-Tenant SaaS for Pet Groomers

A multi-tenant SaaS platform that lets independent pet grooming businesses run their bookings, clients, and service catalog. Built as a modular monolith in .NET 10, paired with a serverless notification system on Azure Functions, deployed to Azure with full CI/CD.

> 🔗 **Live demo:** [add URL after Phase 4]
> 📸 *[add a screenshot or short Loom video here once Phase 1 is done — this lifts the whole repo]*

---

## What it does

FabulousFur is a SaaS product. Any pet grooming business can sign up as a **tenant**, onboard their groomers, register pet owners and pets, build a service catalog with pricing rules by pet size and coat type, and take bookings. When appointments are booked, completed, or due soon, FabulousFur sends notifications using each tenant's own branding and templates — handled by a separate serverless app reacting to events from the core system.

Every tenant's data is isolated. A grooming business in Seattle never sees data from one in Miami, enforced at three layers: application code, EF Core global query filters, and Postgres row-level security as defense-in-depth.

## Why this project exists

This repo demonstrates a full cloud-native .NET SaaS application built the right way:

- **A modular monolith** with four modules (Tenants, Clients, Scheduling, Services) communicating only through public interfaces and events — no shared tables, no reaching across boundaries.
- **Real multi-tenancy** — shared-database architecture with tenant resolution from JWT claims, EF Core global query filters, and Postgres row-level security policies.
- **Boundary enforcement at build time** — an architecture test fails the build if a module references another module's internals.
- **Hybrid architecture** — the core is a modular monolith, but Notifications is serverless (Azure Functions) because the workload (event-reactive, bursty, tenant-aware) fits it well.
- **Event-driven communication via Azure Service Bus** — tenant-aware events connecting the monolith to the Functions app.
- **The full cloud-native pipeline** — containerized, deployed to managed Azure services, with automated CI/CD on every push to `main`.

The tagged release history (`v1-monolith` → `v5-cicd`) tells the modernization story step by step.

---

## Architecture at a glance

```
┌──────────────────────────────────────┐         ┌──────────────────────────┐
│   FabulousFur (Modular Monolith)        │         │  FabulousFur.Notifications  │
│   Azure Container Apps               │         │  Azure Functions         │
│                                      │         │                          │
│   ┌─────────┐  ┌─────────┐           │         │  • OnAppointmentBooked   │
│   │ Tenants │  │ Clients │           │         │  • OnAppointmentCompleted│
│   └─────────┘  └─────────┘           │         │  • SendDailyReminders    │
│                                      │         │       (timer, per tenant)│
│   ┌─────────────┐  ┌──────────┐      │         │                          │
│   │ Scheduling  │  │ Services │      │         │  Uses TenantId on every  │
│   └─────────────┘  └──────────┘      │         │  event to pick correct   │
│                                      │         │  template + branding.    │
│   Tenant resolution middleware       │         └────────────▲─────────────┘
│   ↓                                  │                      │
│   ICurrentTenant (DI scoped)         │                      │ Subscribes
│   ↓                                  │                      │
│   Publishes tenant-aware events ─────┼──┐                   │
└────────────▲─────────────────────────┘  │                   │
             │                            ▼                   │
             │            ┌───────────────────────────────────┴───┐
             │            │   Azure Service Bus                   │
             │            │   topics: appointments, reminders     │
             │            │   (every message includes TenantId)   │
             │            └───────────────────────────────────────┘
             │
       Managed Postgres
       (FabulousFur DB)
       Every row scoped by tenant_id.
       Row-level security enforced.
```

---

## The Modules

| Module | Owns | Talks to others via |
|---|---|---|
| **Tenants** | tenant signup, tenant settings, tenant admin users, branding, notification templates | exposes `ITenantsModule` public API; foundational — every other module depends on it implicitly via `ICurrentTenant` |
| **Clients** | owners, pets, pet attributes (breed, size, coat, medical notes) — all scoped per tenant | exposes `IClientsModule` public API |
| **Scheduling** | appointments, groomer calendars, time slots, appointment lifecycle — per tenant | asks Clients about pets via the public API; publishes tenant-aware appointment events to Service Bus |
| **Services** | service catalog (bath, full groom, nail trim, etc.) and pricing rules — per tenant | receives pet attributes when asked to quote — never reads them |

**The boundary rule:** a module may only reach another module through its **public interface** or via **events**. No module queries another module's tables.

**The tenant rule:** every aggregate root carries `TenantId`. Every query is filtered by current tenant. No exceptions, enforced at three layers.

### Modeling decisions worth noting

- **Pets live inside Clients**, owned by an Owner aggregate. Pets and owners both carry `TenantId`.
- **Services doesn't own pet data** but needs pet size and coat type to price a groom. Solved by *passing* the attributes into a quote request — not by sharing the data.
- **Scheduling owns the appointment lifecycle** (booked → checked in → in progress → completed) and emits tenant-aware events at each transition.
- **Tenants module is special.** It's the only module that operates *across* tenants (during signup, admin operations, billing). Its queries are *not* filtered by `ICurrentTenant` in the same way — they use explicit tenant IDs. This is intentional and documented in an ADR.
- **Notifications is deliberately outside the monolith.** Purely reactive, owns no upstream state, tenant-aware via event payloads, bursty workload — a textbook fit for serverless.

---

## How multi-tenancy works

This is the architectural spine of the project. Three layers of enforcement:

### Layer 1: Tenant resolution at the host

Middleware in `FabulousFur.Host` runs before any module code:

1. Extract `tenant_id` claim from the authenticated JWT (or subdomain in some setups).
2. Look up the tenant (cached) and verify it's active.
3. Populate a scoped `ICurrentTenant` service that's injected into module DbContexts and handlers.

### Layer 2: EF Core global query filters

Every module's `DbContext` configures a global filter on every tenant-aware entity:

```csharp
modelBuilder.Entity<Pet>().HasQueryFilter(p => p.TenantId == _currentTenant.Id);
```

After this, *every* query in *every* module automatically filters by current tenant. A developer can't accidentally fetch another tenant's data with normal LINQ — EF refuses to generate the query without the filter.

### Layer 3: Postgres row-level security (defense in depth)

Even if a developer bypasses EF (raw SQL, a Dapper query, a stored procedure), Postgres itself enforces tenant isolation. Each table has a row-level security policy:

```sql
ALTER TABLE clients.pets ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON clients.pets
  USING (tenant_id = current_setting('app.current_tenant')::uuid);
```

The Container App sets `SET app.current_tenant = '<tenant-id>'` at the start of every request, and Postgres physically refuses to return rows for any other tenant. If application code has a bug, the database is the last line of defense. **This is the senior-level touch most SaaS projects skip.**

### Tenant-aware events

Every event published to Service Bus carries `TenantId` in its payload and in the message's application properties (so the Functions app can filter without deserialization). The Functions app uses `TenantId` to load the correct tenant's notification templates and branding from the Tenants module's API.

---

## Roadmap — 5 Phases

Each phase is a tagged release. With multi-tenancy added, total timeline is now ~6–8 weeks of evening/weekend work.

### Phase 1 — Multi-Tenant Modular Monolith (`v1-monolith`)
One deployable, cleanly split into four modules with enforced boundaries and tenant isolation from day one.

- ASP.NET Core (.NET 10). One project per module + a thin host/API project.
- `FabulousFur.SharedKernel` building block with `TenantId`, `TenantAwareEntity`, `ICurrentTenant`.
- **Tenants** module first: signup, tenant settings, the seed admin user.
- **Clients** and **Scheduling** next, both inheriting from `TenantAwareEntity`.
- **Services** with the quote-by-pet-attributes pattern.
- Tenant resolution middleware in the host.
- EF Core global query filters on every tenant-aware entity (Layer 2).
- **Architecture test** (NetArchTest) failing the build if a module references another's internals. ⭐ One of the highest-signal pieces in the repo.
- **Tenant-isolation test** failing the build if a `TenantAwareEntity` subclass is missing a query filter. ⭐ The other highest-signal piece.

**Stretch:** seed data for 2–3 demo tenants + a minimal UI showing tenant-scoped views.

### Phase 2 — Containerize (`v2-containerized`)
The monolith runs anywhere via Docker.

- Multi-stage `Dockerfile` (using `mcr.microsoft.com/dotnet/aspnet:10.0` and `sdk:10.0` base images).
- `docker-compose.yml` running the app + a Postgres container with row-level security configured.
- **All** config externalized to environment variables.
- Health checks at `/health`.
- No local-disk writes, no in-memory session.
- **Add Postgres row-level security policies** (Layer 3) via an init script.

### Phase 3 — Service Bus + Azure Functions Notifications (`v3-events`)
The monolith publishes tenant-aware events; a separate serverless app reacts.

- Add **`FabulousFur.Contracts`** project with event contracts (including `TenantId` on every event).
- Service Bus publisher in the monolith. Scheduling publishes `AppointmentBookedEvent`, `AppointmentCompletedEvent` to a Service Bus topic, with `TenantId` in message properties.
- New **`FabulousFur.Notifications`** Azure Functions project (.NET 10 isolated worker) with three functions:
  - **`OnAppointmentBooked`** — Service Bus topic trigger; reads tenant's notification template from monolith API, logs the notification.
  - **`OnAppointmentCompleted`** — Service Bus topic trigger; same pattern.
  - **`SendDailyReminders`** — timer trigger; for each active tenant, queries tomorrow's appointments and logs reminders. Demonstrates a tenant-aware cross-cutting workflow.
- Notifications are *logged stubs* (`📧 EMAIL [tenant: AcmeGrooming]: appointment_confirmation to jane@example.com`).
- For local dev: Azure Service Bus Emulator + Functions Core Tools.

### Phase 4 — Deploy to the Cloud (`v4-deployed`)
Both apps run in Azure with real URLs and proper tenant isolation in production.

- **Monolith:** GitHub Container Registry → **Azure Container Apps**.
- **Notifications:** **Azure Function App** on Flex Consumption plan.
- **Service Bus:** namespace with topics and subscriptions.
- **Database:** **Azure Database for PostgreSQL Flexible Server** with row-level security enabled and policies applied via migrations.
- **Secrets** in **Azure Key Vault**, accessed via managed identity from both apps.
- **Authentication:** Microsoft Entra External ID (or Auth0/Clerk) for tenant signups and JWT issuance with `tenant_id` claim.
- **Bicep IaC** in `deploy/bicep/` provisioning all of this.
- **Update the README with the live URL** plus a sentence about how to create a demo tenant.

### Phase 5 — CI/CD (`v5-cicd`)
Push to `main` → both apps build and deploy.

- **GitHub Actions** with two deployment paths and path-filter triggers.
- Tests run on every push, including the architecture test *and* the tenant-isolation test.
- Green CI badge in the README.

---

## Possible "future work" bullets (don't build, just list)

- Observability via OpenTelemetry — distributed traces across the monolith → Service Bus → Functions hop, with `TenantId` propagated as a trace attribute
- Real notification delivery via Azure Communication Services or SendGrid
- A Billing module for tenant subscription management (per-tenant billing, plan tiers, usage metering)
- Per-tenant data residency / geo-partitioning
- Extract Notifications further — separate Service Bus subscriptions per tenant for noisy-neighbor isolation
- Move to Kubernetes (AKS) when scale justifies it
- Postgres connection pooling via PgBouncer

---

## Suggested repo structure

```
fabulousfur/
  src/
    FabulousFur.Host/              # thin API host, tenant resolution middleware, DI wiring
    FabulousFur.Contracts/         # event contracts shared with Functions app
    FabulousFur.SharedKernel/      # TenantId, TenantAwareEntity, ICurrentTenant
    Modules/
      Tenants/                  # tenant signup, settings, admin users
      Clients/
      Scheduling/
      Services/
    BuildingBlocks/             # Service Bus publisher, common abstractions
    FabulousFur.Notifications/     # Azure Functions app (.NET 10 isolated worker)
  tests/
    FabulousFur.ArchitectureTests/ # boundary + tenant-filter enforcement
    FabulousFur.ModuleTests/
    FabulousFur.Notifications.Tests/
  deploy/
    Dockerfile
    docker-compose.yml          # monolith + Postgres (with RLS) + Service Bus emulator
    bicep/                      # IaC: Container App, Function App, Service Bus, Key Vault, Postgres, Entra
    sql/
      enable-rls.sql            # Postgres row-level security policies
  .github/workflows/
    ci-cd.yml
  docs/
    decisions/
      0001-why-modular-monolith.md
      0002-why-multi-tenant-saas.md
      0003-why-shared-database-tenancy.md
      0004-why-azure-container-apps.md
      0005-why-notifications-is-serverless.md
      0006-why-service-bus-over-storage-queues.md
      0007-why-postgres-with-row-level-security.md
      0008-why-tenants-module-bypasses-current-tenant.md
  README.md
```

The `docs/decisions/` ADR folder is now substantially more valuable — each one of these is a real senior decision a hiring manager will look for. The "why shared-database tenancy" and "why row-level security" ADRs are particularly strong signals.

---

## How to make this resume-ready

- **The README is the project.** Most visitors never look at the code. Lead with the SaaS framing, architecture diagram, screenshot, and live URL.
- **Tag every phase.** The tag list visibly tells the modernization story.
- **Squash messy WIP commits.** A coherent journey beats a noisy log.
- **One screenshot or 30-second Loom video** showing a tenant signing up and using the app.
- **Pin the repo on your GitHub profile** once Phase 4 is done.
- **Multi-tenancy is the headline.** Lead with it. "Multi-tenant SaaS for pet groomers, built as a cloud-native modular monolith in .NET 10" is a one-line summary that will get someone to click.

---

## Reference material

- **Modular monolith & boundaries:** Kamil Grzybek's *Modular Monolith Primer* + his `modular-monolith-with-ddd` repo on GitHub. Milan Jovanović's modular monolith articles. Anton Martyniuk's modular monolith walkthrough.
- **Multi-tenancy in .NET:** Microsoft's "Multi-tenancy in SaaS apps" architecture guide; Andrew Lock's blog series on multi-tenant ASP.NET Core; Finbuckle.MultiTenant library docs (worth reading even if you don't use it).
- **Postgres row-level security:** the official Postgres docs on RLS; the "multi-tenant Postgres with RLS" pattern writeups from Crunchy Data and Supabase.
- **Cloud native .NET:** Microsoft's free e-book *Architecting Cloud Native .NET Applications for Azure*.
- **Azure Functions in .NET:** Microsoft Learn — "Develop Azure Functions using the isolated worker model"; Service Bus trigger docs.
- **Azure Service Bus:** the official docs' messaging overview; the `Azure.Messaging.ServiceBus` SDK guide.
- **Identity:** Microsoft Entra External ID docs (the modern name for what was Azure AD B2C); the multi-tenant JWT claim pattern.

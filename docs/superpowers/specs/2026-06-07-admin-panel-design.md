# Admin Panel — Design

Date: 2026-06-07
Status: Approved for planning

## Purpose

Provide a single-user admin surface to browse and manage all stored items
across Toimi's tool servers — memories (muistio), reminders (muistutin),
schedules + run history (ajastin), and skills (taidot) — with a unified
search/recents view across stores plus domain-tailored detail pages.

## Goals

- Browse, edit, and delete items in all four stores from one UI.
- Unified search and "recent activity" across stores.
- Each tool server stays the sole owner of its data shape and domain
  actions; no centralized admin service or shared schema.
- Adding a new tool server to the admin panel requires zero code in
  `toimi.web` — only a config entry.

## Non-goals

- Conversation history management (deferred; revisit when needed).
- Multi-user auth/RBAC (Toimi is single-user; access control stays at
  the network boundary).
- Bulk operations across stores (out of scope; per-store delete is
  sufficient for current need).

## Architecture

```
                          ┌─────────────────────────────────────────┐
                          │            toimi.web (SPA + API)         │
                          │  /admin/* React pages                    │
                          │  /api/admin/summary  (fan-out aggregator)│
                          │  /api/admin/{tool}/* (generic forwarder) │
                          └────────────┬────────────────────────────┘
                                       │ typed HttpClient per tool
              ┌────────────┬───────────┼───────────┬────────────┐
              ▼            ▼           ▼           ▼            ▼
        ┌────────┐   ┌─────────┐  ┌──────────┐ ┌────────┐
        │muistio │   │muistutin│  │ ajastin  │ │ taidot │
        │ /admin │   │ /admin  │  │ /admin   │ │ /admin │
        │ /mcp   │   │ /mcp    │  │ /mcp     │ │ /mcp   │
        └────────┘   └─────────┘  └──────────┘ └────────┘

         shared DTOs: toimi.core/Admin/  (POCOs only, no behavior)
```

Key constraints expressed in the diagram:
- Admin endpoints are siblings of MCP on each tool server, not stacked.
  They share the tool's `DbContext` but have independent route handlers.
- `toimi.web` does not own per-tool routing code; one generic forwarder
  + one aggregator covers all tools.
- The only thing shared in code is a small DTO set in `toimi.core`.

## Shared contract — `toimi.core/Admin/`

Two POCOs and one aggregator response shape. No interfaces, no base
classes, no behavior.

```csharp
// AdminSummaryDto.cs
public record AdminSummaryDto(
  string Id,
  string Kind,          // "memory" | "reminder" | "schedule" | "skill"
  string Title,
  string? Subtitle,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt
);

// AdminError.cs
public record AdminError(string Tool, string Message);

public record AggregatedSummary(
  IReadOnlyList<AdminSummaryDto> Items,
  IReadOnlyList<AdminError> Errors
);
```

An optional convenience extension lives next to the DTOs so the
`/admin` prefix isn't a magic string across four projects:

```csharp
public static class AdminEndpointBuilder
{
  public static RouteGroupBuilder MapAdmin(this IEndpointRouteBuilder app)
    => app.MapGroup("/admin");
}
```

Tools and `toimi.web` both reference `toimi.core` already (for
`ToimiConfiguration`), so this introduces no new dependency edge.

## Per-tool admin endpoints — uniform path scheme

Every tool server exposes the same endpoint shape:

| Method | Path                              | Returns                          |
|--------|-----------------------------------|----------------------------------|
| GET    | `/admin/summary?q=&since=&limit=` | `AdminSummaryDto[]`              |
| GET    | `/admin/items?page=&size=&q=`     | `PagedResult<TItem>` (typed)     |
| GET    | `/admin/items/{id}`               | `TItem`                          |
| PUT    | `/admin/items/{id}`               | `TItem` (updated)                |
| DELETE | `/admin/items/{id}`               | 204                              |
| POST   | `/admin/items/{id}/{action}`      | domain-specific                  |

`PagedResult<TItem>` is a thin wrapper:

```csharp
public record PagedResult<T>(
  IReadOnlyList<T> Items, int Page, int Size, int Total
);
```

defined per tool (it's generic and trivial; no need to share).

### Per-tool `TItem` shapes and details

**muistio — memories**
- `TItem = MemoryItem { Id, Content, Source, Confidence, ExpiresAt?,
  CreatedAt, UpdatedAt }`
- `q` text-searches `Content` (ILIKE or full-text).
- Summary: `Title = Content[0..60]`,
  `Subtitle = "from {Source}, conf {Confidence:0.00}"`.
- No domain actions.

**muistutin — reminders**
- `TItem = ReminderItem { Id, Text, DueAt, RecurrenceRule?,
  CompletedAt?, CreatedAt, UpdatedAt }`
- `q` text-searches `Text`.
- Summary: `Title = Text`, `Subtitle = DueAt local + recurrence label;
  "completed" when applicable`.
- Domain action: `POST /admin/items/{id}/complete`.

**ajastin — schedules**
- `TItem = ScheduleItem { Id, Name, CronExpression, Prompt, Enabled,
  LastRunAt?, CreatedAt, UpdatedAt }`
- Secondary endpoint: `GET /admin/items/{id}/runs?limit=` returns
  recent `ScheduleRun` entries for the detail view (paginated like
  items).
- Summary: `Title = Name`,
  `Subtitle = CronExpression + " (disabled)"` when not enabled.
- Domain actions: `POST /admin/items/{id}/run-now`,
  `POST /admin/items/{id}/enable`, `POST /admin/items/{id}/disable`.
- The existing `/api/runs` endpoint stays for the homepage
  `ActivityList`; admin routes are scoped under `/admin/...` and do not
  touch it.

**taidot — skills**
- `TItem = SkillItem { Id, Name, Description, Body, Tags[], CreatedAt,
  UpdatedAt }`
- `q` searches name + description (Qdrant semantic search optional;
  ILIKE is fine for v1).
- Summary: `Title = Name`, `Subtitle = Description[0..80]`.
- No domain actions.

### Concurrency control

PUT and POST-action endpoints require an `If-Unmodified-Since` header
carrying the `UpdatedAt` the client last saw. Server compares against
the stored value:
- Match → apply, return updated `TItem` with new `UpdatedAt`.
- Mismatch → `409 Conflict` with body `{ error: "stale", currentUpdatedAt }`.
- Missing header on PUT → `428 Precondition Required`.

`UpdatedAt` is already in the summary DTO and every `TItem`, so the
client always has the value to send back. The React layer surfaces 409
with a "this item changed elsewhere — reload?" prompt.

### Endpoint registration

Each tool adds one file `src/toimi.tools.<x>/Admin/AdminEndpoints.cs`
that defines a `MapAdminEndpoints` extension. `Program.cs` calls it
alongside `MapMcp()`:

```csharp
app.MapMcp();
app.MapAdminEndpoints();   // new
app.MapGet("/health", () => Results.Ok());
```

Handlers consume the tool's existing `DbContext` and repositories via
DI — no new repositories needed.

## toimi.web — aggregator + generic forwarder

Two endpoints. No per-tool routing code.

### Config

```jsonc
"Toimi": {
  "Admin": {
    "Tools": ["muistio", "muistutin", "ajastin", "taidot"]
  }
}
```

On startup, `toimi.web` registers one named `HttpClient` per tool in a
loop:

```csharp
foreach (var tool in opts.Tools)
{
  builder.Services.AddHttpClient($"admin-{tool}", c =>
    c.BaseAddress = new Uri(
      $"http://toimi-tools-{tool}.apps.svc.cluster.local"));
}
```

This replaces the standalone `AddHttpClient("ajastin", …)` already
present in `Program.cs`. The existing `/api/activity` endpoint
continues to use the `admin-ajastin` client (or is renamed for clarity
during implementation — decision left to the plan).

### `/api/admin/summary` — aggregator

Fans out summary calls to every registered tool in parallel, merges,
sorts by `UpdatedAt` desc, applies a global `limit`:

- Query params: `q`, `limit` (default 50).
- Per-tool failures are caught individually; the failed tool
  contributes an empty list and an `AdminError` entry.
- Response: `AggregatedSummary { Items, Errors }`.
- 200 even if some tools failed (errors live in the body so the UI can
  render partial data).

### `/api/admin/{tool}/{**path}` — generic forwarder

Generic proxy: forwards method, query string, body, and content-type
to the upstream tool server's matching `/admin/{path}`.

- `{tool}` validated against `opts.Tools`; unknown → 404 immediately.
- Status code, response headers, and body streamed through.
- `If-Unmodified-Since` header preserved (carries optimistic
  concurrency through the proxy unchanged).
- Failures from upstream pass through with their status code; transport
  errors → 502.

The forwarder is implemented once with `app.Map("/api/admin/{tool}/{**path}", …)`.
No per-tool branching.

## React UI

Add `react-router-dom` to the SPA. New route tree alongside the
existing chat:

```
/                       chat (existing ToimiView)
/admin                  AdminLayout — sidebar nav + <Outlet />
  /admin                Dashboard (global search + recents)
  /admin/muistio        MemoriesPage
  /admin/muistutin      RemindersPage
  /admin/ajastin        SchedulesPage
  /admin/ajastin/:id    ScheduleDetailPage (with runs sub-table)
  /admin/taidot         SkillsPage
```

Top-level nav adds an "Admin" link next to the existing conversation
UI.

### Shared primitives — `ClientApp/src/admin/`

- `useAdmin.ts` — `useAdmin<TItem>(tool, path)` and matching
  mutations; thin wrapper around `fetch('/api/admin/{tool}/...')` with
  typed generic, handles `If-Unmodified-Since` round-trip.
- `useAdminSummary.ts` — hits `/api/admin/summary?q=`, returns
  `{ items, errors }`.
- `DataTable.tsx`, `ConfirmDelete.tsx`, `EmptyState.tsx`,
  `ErrorBanner.tsx`, `StaleConflictModal.tsx`.

### Per-store pages

Each store's page owns:
- Column definitions for `DataTable`.
- Form component for edit dialog (typed against its `TItem`).
- Action buttons for domain actions (e.g. "Run now" on schedules).

No shared "field renderer" or generic form library. Per-store pages
are small (~150 lines each) and stay readable.

### Dashboard

Single search box + a list grouped by `Kind`. Each row links to its
domain page's detail/edit view. Errors render as a dismissible banner
at the top ("muistutin currently unavailable").

## Auth

No app-level auth — admin inherits the existing chat UI's stance,
relying on the network boundary (VPN, reverse proxy, LAN). If auth is
introduced later, it covers both surfaces uniformly. Explicit
assumption, not an oversight.

## Testing strategy

- **Per tool server** — extend each tool with an `<tool>.Tests`
  project (pattern from `toimi.tools.ruutu.Tests`). Use
  `WebApplicationFactory<Program>` against an ephemeral Postgres
  (Testcontainers) to cover: summary projection shape, items
  pagination, GET/PUT/DELETE round-trip, optimistic-concurrency 409,
  domain actions.
- **toimi.web aggregator** — unit test the fan-out with stubbed
  `IHttpClientFactory`: fixtures + one throwing handler; assert merged
  ordering by `UpdatedAt` and `Errors` array contents.
- **toimi.web forwarder** — integration test with a stub upstream:
  method/query/body/headers/status passthrough; unknown-tool → 404.
- **React** — Vitest + RTL for the dashboard partial-failure
  rendering and one CRUD page covering the deletion-with-confirm flow.
  No exhaustive per-page UI tests.
- **No e2e** — single-user app, chat has none, don't introduce a layer
  for admin alone.

## Open follow-ups (not blocking v1)

- Conversation history management.
- Bulk operations.
- App-level auth (would cover chat + admin uniformly).
- Promoting search to Qdrant semantic where it currently uses ILIKE
  (taidot — already has Qdrant; would just plumb through).

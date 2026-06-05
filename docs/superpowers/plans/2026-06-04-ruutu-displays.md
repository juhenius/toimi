# Ruutu Displays Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `toimi.tools.ruutu`, a new MCP tool server that lets Toimi push capability-aware HTML content (modern/legacy tiers) to user-owned web displays (e.g., old iPads), with template-based rendering, SSE push transport, and tap-back interactions.

**Architecture:** Sibling tool server alongside `koti`, `muistutin`, etc. Single .NET 10 ASP.NET Core app exposing (a) MCP tool surface at `/sse`, (b) display HTML shell at `/ruutu/<id>`, (c) display REST/SSE API at `/ruutu/api/displays/<id>/*`. State in a new PostgreSQL database (`ruutu`). Templates stored in DB with both `modern_html` and `legacy_html` Scriban variants, seeded from code (idempotent). Layout templates compose other templates by convention (data values shaped `{template, data}` become rendered slots).

**Tech Stack:** .NET 10, ASP.NET Core, ModelContextProtocol 1.1.0, EF Core 10.0.1 (snake_case naming), PostgreSQL via Npgsql, Scriban (templating), xUnit (new test project for deterministic logic). Hand-written ES5 + XHR for the display shell (no build step — must run on iOS Safari 9).

**Spec:** `docs/superpowers/specs/2026-06-04-ruutu-displays-design.md` is the source of truth. Re-read sections referenced in each task before implementing.

---

## File structure

### New project: `src/toimi.tools.ruutu/`

```
toimi.tools.ruutu.csproj           (Web SDK; adds Scriban + JsonSchema.Net)
Program.cs                         MCP server bootstrap, EF migrate-on-start, MapMcp(), MapControllers(), MapStaticAssets()
Dockerfile                         multi-stage build from repo root
appsettings.json                   minimal (Logging + AllowedHosts only)

Data/
  RuutuDbContext.cs                DbSets: Displays, Templates, DisplayEvents
  Entities/
    Display.cs                     POCO, no logic
    Template.cs                    POCO
    DisplayEvent.cs                POCO
  Configurations/
    DisplayConfiguration.cs        IEntityTypeConfiguration<Display>, indexes, defaults
    TemplateConfiguration.cs
    DisplayEventConfiguration.cs
  Repositories/
    DisplayRepository.cs           scoped: CRUD + overlay-stack mutations
    TemplateRepository.cs          scoped: lookup, upsert, soft constraint on is_seeded
    DisplayEventRepository.cs      scoped: append + range query
  TemplateSeeder.cs                idempotent upsert of seeded templates on startup
  Migrations/                      InitialCreate via `dotnet ef migrations add`

Rendering/
  CapabilityClassifier.cs          payload → "modern" | "legacy"
  CapabilityPayload.cs             record for incoming capability detection
  TierBriefs.cs                    LEGACY_TIER_BRIEF and MODERN_TIER_BRIEF string constants
  TierLinter.cs                    regex rules, returns issues
  LintResult.cs                    record { bool Valid, IReadOnlyList<LintIssue> Issues }
  ScribanRenderer.cs               leaf render + recursive composite render, depth cap
  RenderContext.cs                 record (Template lookup func, tier, display vars, depth)
  RenderResult.cs                  record { string Html, IReadOnlyList<string> Warnings }

Transport/
  DisplayApiController.cs          [Route("ruutu")] GET /<id>, POST /api/displays/<id>/capabilities, POST /api/displays/<id>/events
  SseHub.cs                        singleton: per-display Channel<SseEvent>, fan-out helpers
  SseEvent.cs                      record { string Type, string Json } - serialized SSE payload
  DisplayStreamEndpoint.cs         endpoint route handler that opens SSE response and pipes from hub
  ContentPushService.cs            scoped facade used by tools: renders + pushes via hub + persists current_*

Tools/
  DisplayManagementTools.cs        [McpServerToolType] register/unregister/list/set_tier
  DisplayContentTools.cs           [McpServerToolType] show/overlay/clear
  TemplateTools.cs                 [McpServerToolType] list/get/create/update/delete/preview/get_tier_brief
  DisplayEventsTools.cs            [McpServerToolType] get_events

wwwroot/
  shell.html                       ES5 template, identifier injected via {ID} placeholder server-side
  shell.css                        ~50-line reset + utility CSS
```

### New test project: `src/toimi.tools.ruutu.Tests/`

```
toimi.tools.ruutu.Tests.csproj     xUnit + Microsoft.NET.Test.Sdk; references toimi.tools.ruutu
Rendering/
  CapabilityClassifierTests.cs
  TierLinterTests.cs
  ScribanRendererTests.cs           leaf + composite + depth cap + missing template + render error
  OverlayStackTests.cs              push, pop, eviction, replay on reconnect
```

### Modifications

| file | change |
|---|---|
| `toimi.sln` | add `toimi.tools.ruutu` + `toimi.tools.ruutu.Tests` project entries |
| Every existing `src/toimi.tools.*/Dockerfile` + `src/toimi.web/Dockerfile` | add `COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/` before `dotnet restore` |
| `infrastructure/base/helm/postgresql-values.yaml` | add `CREATE DATABASE ruutu;` to `initdbScripts.create-databases.sql` |
| `scripts/dev-setup.sh` | add `ruutu` to the `for DB_NAME in ...` loop |
| `k8s/base/kustomization.yaml` | add `- tools-ruutu` to resources list |
| `k8s/base/tools-ruutu/` (new dir) | `kustomization.yaml`, `deployment.yaml`, `service.yaml`, `ingress.yaml` |
| `src/toimi.web/appsettings.json` | add ruutu McpServers entry |
| `src/toimi.tools.taidot/Skills/SkillSeeder.cs` | append `use-displays` skill entry |

---

## Phases

The plan progresses in 7 phases. Each phase produces a meaningfully observable state.

| phase | tasks | outcome |
|---|---|---|
| 1. Scaffold | 1–4 | Empty pod boots, `/health` returns 200, `/sse` lists zero tools |
| 2. Data layer | 5–7 | EF Core migration creates `ruutu` DB; entities + repositories unit-callable |
| 3. Rendering core (test-first) | 8–12 | Classifier, linter, renderer, overlay stack all green under xUnit |
| 4. Seeded templates | 13–15 | 8 leaves + 3 layouts upserted on startup; renders verifiable via `display_preview` |
| 5. Display shell + REST/SSE | 16–21 | Open URL in a browser → capability POSTed → SSE stream connects → splash visible |
| 6. MCP tool surface | 22–27 | `mcp` client lists tools, can register a display + push templates |
| 7. Integration & smoke test | 28–31 | Wired into `toimi.web`, taidot skill seeded, end-to-end demo in dev cluster |

---

## Phase 1: Scaffold

### Task 1: Create the empty ruutu project

**Files:**
- Create: `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj`
- Create: `src/toimi.tools.ruutu/appsettings.json`
- Create: `src/toimi.tools.ruutu/appsettings.Development.json`
- Create: `src/toimi.tools.ruutu/Program.cs`
- Modify: `toimi.sln`

- [ ] **Step 1: Create csproj**

Write `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="EFCore.NamingConventions" Version="10.0.1" />
    <PackageReference Include="JsonSchema.Net" Version="7.3.4" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="ModelContextProtocol" Version="1.1.0" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.1.0" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
    <PackageReference Include="Scriban" Version="5.12.1" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create appsettings.json**

Write `src/toimi.tools.ruutu/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Write `src/toimi.tools.ruutu/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Ruutu": "Host=localhost;Port=5432;Database=ruutu;Username=postgres;Password=postgres"
  }
}
```

- [ ] **Step 3: Create minimal Program.cs (boots, exposes /health, /sse with no tools yet)**

Write `src/toimi.tools.ruutu/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "ruutu",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
```

- [ ] **Step 4: Add project to toimi.sln**

Open `toimi.sln`. Locate the existing `toimi.tools.muistutin` entry. Immediately after `EndProject` for muistutin, insert:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "toimi.tools.ruutu", "src\toimi.tools.ruutu\toimi.tools.ruutu.csproj", "{C4C91449-6684-4E56-8577-49F99CD78E5D}"
EndProject
```

Then add corresponding configuration entries inside the `GlobalSection(ProjectConfigurationPlatforms)` block (mirror the format used by muistutin's GUID — typically four lines: Debug|Any CPU.ActiveCfg, Debug|Any CPU.Build.0, Release|Any CPU.ActiveCfg, Release|Any CPU.Build.0). Use GUID `{C4C91449-6684-4E56-8577-49F99CD78E5D}`.

- [ ] **Step 5: Verify it builds**

Run: `dotnet build src/toimi.tools.ruutu/toimi.tools.ruutu.csproj`
Expected: succeeds, no errors.

- [ ] **Step 6: Verify it runs locally (without DB yet)**

Run: `cd src/toimi.tools.ruutu && ASPNETCORE_URLS=http://localhost:8081 dotnet run`
In another terminal: `curl http://localhost:8081/health`
Expected: `200 OK`, empty body.

Stop the process. (DB hookup comes in Task 5.)

- [ ] **Step 7: Commit**

```bash
git add toimi.sln src/toimi.tools.ruutu/
git commit -m "feat(ruutu): scaffold empty toimi.tools.ruutu MCP server"
```

---

### Task 2: Add Dockerfile and update all existing tool Dockerfiles

The pattern in this repo: each tool's Dockerfile pre-copies every `*.csproj` in the solution (so `dotnet restore` against `toimi.sln`'s graph works). Adding ruutu means every existing Dockerfile needs one new COPY line, *and* ruutu needs its own Dockerfile.

**Files:**
- Create: `src/toimi.tools.ruutu/Dockerfile`
- Modify: `src/toimi.tools.ajastin/Dockerfile`
- Modify: `src/toimi.tools.koti/Dockerfile`
- Modify: `src/toimi.tools.muistio/Dockerfile`
- Modify: `src/toimi.tools.muistutin/Dockerfile`
- Modify: `src/toimi.tools.taidot/Dockerfile`
- Modify: `src/toimi.tools.verkko/Dockerfile`
- Modify: `src/toimi.web/Dockerfile`

- [ ] **Step 1: Write ruutu's Dockerfile**

Write `src/toimi.tools.ruutu/Dockerfile`:

```dockerfile
# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.tools.ruutu/Dockerfile -t <registry>/<image>:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.ajastin/toimi.tools.ajastin.csproj src/toimi.tools.ajastin/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.muistio/toimi.tools.muistio.csproj src/toimi.tools.muistio/
COPY src/toimi.tools.muistutin/toimi.tools.muistutin.csproj src/toimi.tools.muistutin/
COPY src/toimi.tools.taidot/toimi.tools.taidot.csproj src/toimi.tools.taidot/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.ruutu/toimi.tools.ruutu.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.ruutu/toimi.tools.ruutu.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.ruutu.dll"]
```

- [ ] **Step 2: Update each existing tool/web Dockerfile**

For each of these 7 Dockerfiles:
- `src/toimi.tools.ajastin/Dockerfile`
- `src/toimi.tools.koti/Dockerfile`
- `src/toimi.tools.muistio/Dockerfile`
- `src/toimi.tools.muistutin/Dockerfile`
- `src/toimi.tools.taidot/Dockerfile`
- `src/toimi.tools.verkko/Dockerfile`
- `src/toimi.web/Dockerfile`

Find the line `COPY src/toimi.tools.taidot/toimi.tools.taidot.csproj src/toimi.tools.taidot/` and insert directly after it:

```dockerfile
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
```

- [ ] **Step 3: Verify ruutu's Docker build (optional but recommended; skip if no docker locally)**

Run: `docker build -f src/toimi.tools.ruutu/Dockerfile -t toimi-tools-ruutu:dev .` (from repo root)
Expected: image builds successfully.

- [ ] **Step 4: Spot-check one other tool's Docker build still works**

Run: `docker build -f src/toimi.tools.muistutin/Dockerfile -t toimi-tools-muistutin:dev .`
Expected: image builds successfully (the new COPY line did not break restore).

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Dockerfile src/toimi.tools.*/Dockerfile src/toimi.web/Dockerfile
git commit -m "feat(ruutu): add Dockerfile, register csproj in all sibling Dockerfiles"
```

---

### Task 3: Add ruutu database to infrastructure and dev setup

**Files:**
- Modify: `infrastructure/base/helm/postgresql-values.yaml`
- Modify: `scripts/dev-setup.sh`

- [ ] **Step 1: Add ruutu to postgresql initdb script**

Locate the `initdbScripts.create-databases.sql` block. Add `CREATE DATABASE ruutu;` immediately after the existing CREATE statements:

```yaml
initdbScripts:
  create-databases.sql: |
    CREATE DATABASE muistio;
    CREATE DATABASE muistutin;
    CREATE DATABASE ajastin;
    CREATE DATABASE toimi;
    CREATE DATABASE ruutu;
```

- [ ] **Step 2: Add ruutu to dev-setup.sh DB loop**

In `scripts/dev-setup.sh`, locate `for DB_NAME in muistio muistutin ajastin toimi; do`. Replace with:

```bash
for DB_NAME in muistio muistutin ajastin toimi ruutu; do
```

- [ ] **Step 3: Run dev-setup.sh (only if you have a local kind cluster running)**

Run: `bash scripts/dev-setup.sh` and confirm output mentions `ruutu` being created (or already existing).

If not running a cluster yet, skip — the loop will pick it up next time.

- [ ] **Step 4: Commit**

```bash
git add infrastructure/base/helm/postgresql-values.yaml scripts/dev-setup.sh
git commit -m "feat(ruutu): provision ruutu PostgreSQL database"
```

---

### Task 4: K8s base manifests for ruutu pod

**Files:**
- Create: `k8s/base/tools-ruutu/kustomization.yaml`
- Create: `k8s/base/tools-ruutu/deployment.yaml`
- Create: `k8s/base/tools-ruutu/service.yaml`
- Create: `k8s/base/tools-ruutu/ingress.yaml`
- Modify: `k8s/base/kustomization.yaml`

- [ ] **Step 1: kustomization.yaml**

Write `k8s/base/tools-ruutu/kustomization.yaml`:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

resources:
  - deployment.yaml
  - service.yaml
  - ingress.yaml
```

- [ ] **Step 2: deployment.yaml**

Write `k8s/base/tools-ruutu/deployment.yaml`:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: toimi-tools-ruutu
  namespace: apps
  labels:
    app: toimi-tools-ruutu
spec:
  replicas: 1
  selector:
    matchLabels:
      app: toimi-tools-ruutu
  template:
    metadata:
      labels:
        app: toimi-tools-ruutu
    spec:
      containers:
        - name: toimi-tools-ruutu
          image: ${IMAGE_REGISTRY}/toimi-tools-ruutu:latest
          ports:
            - containerPort: 8080
          env:
            - name: ConnectionStrings__Ruutu
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: ruutu-connection-string
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 3
            periodSeconds: 5
```

- [ ] **Step 3: service.yaml**

Write `k8s/base/tools-ruutu/service.yaml`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: toimi-tools-ruutu
  namespace: apps
spec:
  selector:
    app: toimi-tools-ruutu
  ports:
    - port: 80
      targetPort: 8080
```

- [ ] **Step 4: ingress.yaml**

Write `k8s/base/tools-ruutu/ingress.yaml`:

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-tools-ruutu
  namespace: apps
spec:
  ingressClassName: traefik
  rules:
    - host: ${TOIMI_HOST}
      http:
        paths:
          - path: /ruutu
            pathType: Prefix
            backend:
              service:
                name: toimi-tools-ruutu
                port:
                  number: 80
```

Note: Traefik's longest-prefix-wins routing means this ingress and the existing `toimi-web` ingress (`/` Prefix) coexist — anything under `/ruutu/*` goes here, everything else falls through to web.

- [ ] **Step 5: Register tools-ruutu in base kustomization**

In `k8s/base/kustomization.yaml`, add `- tools-ruutu` to the `resources` list (alphabetic position after `tools-muistutin`):

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

resources:
  - web
  - tools-koti
  - tools-muistio
  - tools-muistutin
  - tools-ruutu
  - tools-taidot
  - tools-ajastin
  - tools-verkko
```

- [ ] **Step 6: Add ruutu-connection-string to local secrets template**

Locate `k8s/overlays/dev/secrets.env.example` (and `server/secrets.env.example`). Add a line:

```
ruutu-connection-string=Host=postgresql.data;Port=5432;Database=ruutu;Username=postgres;Password=REPLACE_ME
```

Tell the user (in the commit message body or PR description) that real `k8s/overlays/<env>/secrets.env` files need this value added before deploy.

- [ ] **Step 7: Lint manifests (if yamllint available)**

Run: `bash scripts/lint.sh`
Expected: passes for ruutu yaml files.

- [ ] **Step 8: Commit**

```bash
git add k8s/base/tools-ruutu/ k8s/base/kustomization.yaml k8s/overlays/dev/secrets.env.example k8s/overlays/server/secrets.env.example
git commit -m "feat(ruutu): k8s base manifests (deployment, service, ingress)"
```

---

## Phase 2: Data layer

### Task 5: Define entities + EF configurations

**Files:**
- Create: `src/toimi.tools.ruutu/Data/Entities/Display.cs`
- Create: `src/toimi.tools.ruutu/Data/Entities/Template.cs`
- Create: `src/toimi.tools.ruutu/Data/Entities/DisplayEvent.cs`
- Create: `src/toimi.tools.ruutu/Data/Configurations/DisplayConfiguration.cs`
- Create: `src/toimi.tools.ruutu/Data/Configurations/TemplateConfiguration.cs`
- Create: `src/toimi.tools.ruutu/Data/Configurations/DisplayEventConfiguration.cs`

- [ ] **Step 1: Display entity**

Write `src/toimi.tools.ruutu/Data/Entities/Display.cs`:

```csharp
namespace toimi.tools.ruutu.Data.Entities;

public class Display
{
  public int Id { get; set; }
  public required string Identifier { get; set; }
  public string? Tier { get; set; }                     // "modern" | "legacy" | null
  public bool TierOverride { get; set; }
  public string? LastUserAgent { get; set; }
  public int? ViewportWidth { get; set; }
  public int? ViewportHeight { get; set; }
  public string? Orientation { get; set; }              // "landscape" | "portrait" | null
  public string? CurrentTemplate { get; set; }
  public string? CurrentData { get; set; }              // jsonb stored as string
  public DateTimeOffset? CurrentPushedAt { get; set; }
  public string OverlayStack { get; set; } = "[]";      // jsonb: array of {template, data, enqueued_at}
  public string? IdleTemplate { get; set; }
  public string? IdleData { get; set; }                 // jsonb stored as string
  public DateTimeOffset? LastSeenAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }

  public ICollection<DisplayEvent> Events { get; set; } = [];
}
```

- [ ] **Step 2: Template entity**

Write `src/toimi.tools.ruutu/Data/Entities/Template.cs`:

```csharp
namespace toimi.tools.ruutu.Data.Entities;

public class Template
{
  public int Id { get; set; }
  public required string Name { get; set; }
  public required string Description { get; set; }
  public required string SchemaJson { get; set; }       // JSON Schema
  public string? ModernHtml { get; set; }
  public string? LegacyHtml { get; set; }
  public bool IsSeeded { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 3: DisplayEvent entity**

Write `src/toimi.tools.ruutu/Data/Entities/DisplayEvent.cs`:

```csharp
namespace toimi.tools.ruutu.Data.Entities;

public class DisplayEvent
{
  public long Id { get; set; }
  public int DisplayId { get; set; }
  public required string EventType { get; set; }       // "tap" | "check" | "dismiss" | "overlay_dropped"
  public string? Target { get; set; }
  public string? Value { get; set; }                    // jsonb stored as string
  public DateTimeOffset CreatedAt { get; set; }

  public Display? Display { get; set; }
}
```

- [ ] **Step 4: DisplayConfiguration**

Write `src/toimi.tools.ruutu/Data/Configurations/DisplayConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class DisplayConfiguration : IEntityTypeConfiguration<Display>
{
  public void Configure(EntityTypeBuilder<Display> builder)
  {
    builder.HasKey(d => d.Id);

    builder.HasIndex(d => d.Identifier).IsUnique();

    builder.Property(d => d.Identifier).IsRequired();

    builder.Property(d => d.CurrentData).HasColumnType("jsonb");
    builder.Property(d => d.OverlayStack).HasColumnType("jsonb").HasDefaultValue("[]");
    builder.Property(d => d.IdleData).HasColumnType("jsonb");

    builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");

    builder.HasMany(d => d.Events)
      .WithOne(e => e.Display)
      .HasForeignKey(e => e.DisplayId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
```

- [ ] **Step 5: TemplateConfiguration**

Write `src/toimi.tools.ruutu/Data/Configurations/TemplateConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
  public void Configure(EntityTypeBuilder<Template> builder)
  {
    builder.HasKey(t => t.Id);

    builder.HasIndex(t => t.Name).IsUnique();

    builder.Property(t => t.Name).IsRequired();
    builder.Property(t => t.Description).IsRequired();
    builder.Property(t => t.SchemaJson).HasColumnType("jsonb").IsRequired();

    builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
    builder.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");
  }
}
```

- [ ] **Step 6: DisplayEventConfiguration**

Write `src/toimi.tools.ruutu/Data/Configurations/DisplayEventConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class DisplayEventConfiguration : IEntityTypeConfiguration<DisplayEvent>
{
  public void Configure(EntityTypeBuilder<DisplayEvent> builder)
  {
    builder.HasKey(e => e.Id);

    builder.Property(e => e.EventType).IsRequired();
    builder.Property(e => e.Value).HasColumnType("jsonb");
    builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

    builder.HasIndex(e => new { e.DisplayId, e.CreatedAt })
      .HasDatabaseName("idx_display_events_display_created")
      .IsDescending(false, true);
  }
}
```

- [ ] **Step 7: Verify build**

Run: `dotnet build src/toimi.tools.ruutu/`
Expected: succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/toimi.tools.ruutu/Data/
git commit -m "feat(ruutu): add Display, Template, DisplayEvent entities and EF configurations"
```

---

### Task 6: DbContext + Program.cs wiring + initial migration

**Files:**
- Create: `src/toimi.tools.ruutu/Data/RuutuDbContext.cs`
- Modify: `src/toimi.tools.ruutu/Program.cs`
- Create: `src/toimi.tools.ruutu/Data/Migrations/*` (generated)

- [ ] **Step 1: DbContext**

Write `src/toimi.tools.ruutu/Data/RuutuDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data;

public class RuutuDbContext(DbContextOptions<RuutuDbContext> options) : DbContext(options)
{
  public DbSet<Display> Displays => Set<Display>();
  public DbSet<Template> Templates => Set<Template>();
  public DbSet<DisplayEvent> DisplayEvents => Set<DisplayEvent>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(RuutuDbContext).Assembly);
  }
}
```

- [ ] **Step 2: Update Program.cs to register DbContext + run migrations on start**

Replace `src/toimi.tools.ruutu/Program.cs` with:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ruutu")
  ?? throw new InvalidOperationException("ConnectionStrings:Ruutu is required");

builder.Services.AddDbContext<RuutuDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "ruutu",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<RuutuDbContext>();
  await dbContext.Database.MigrateAsync();
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
```

- [ ] **Step 3: Generate initial EF migration**

Ensure a `ruutu` database exists locally (e.g. `createdb ruutu` against your local Postgres, or run dev-setup.sh).

Run from repo root:
```bash
dotnet ef migrations add InitialCreate \
  --project src/toimi.tools.ruutu/toimi.tools.ruutu.csproj \
  --output-dir Data/Migrations
```

Expected: new files appear under `src/toimi.tools.ruutu/Data/Migrations/`.

- [ ] **Step 4: Smoke-test migration application**

Run: `cd src/toimi.tools.ruutu && ASPNETCORE_URLS=http://localhost:8081 ASPNETCORE_ENVIRONMENT=Development dotnet run`

In another terminal, verify tables exist:
```bash
psql -h localhost -U postgres -d ruutu -c "\dt"
```
Expected: `displays`, `templates`, `display_events`, `__EFMigrationsHistory` listed.

Stop the dotnet process.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Data/RuutuDbContext.cs src/toimi.tools.ruutu/Data/Migrations/ src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add RuutuDbContext, wire DbContext into Program.cs, generate InitialCreate migration"
```

---

### Task 7: Repositories

These are thin scoped facades the tools and renderer use; we test the higher-level logic, not these directly.

**Files:**
- Create: `src/toimi.tools.ruutu/Data/Repositories/DisplayRepository.cs`
- Create: `src/toimi.tools.ruutu/Data/Repositories/TemplateRepository.cs`
- Create: `src/toimi.tools.ruutu/Data/Repositories/DisplayEventRepository.cs`
- Modify: `src/toimi.tools.ruutu/Program.cs`

- [ ] **Step 1: DisplayRepository**

Write `src/toimi.tools.ruutu/Data/Repositories/DisplayRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public class DisplayRepository(RuutuDbContext db)
{
  public Task<Display?> GetAsync(string identifier, CancellationToken ct = default) =>
    db.Displays.FirstOrDefaultAsync(d => d.Identifier == identifier, ct);

  public Task<List<Display>> ListAsync(CancellationToken ct = default) =>
    db.Displays.OrderBy(d => d.Identifier).ToListAsync(ct);

  public async Task<Display> RegisterAsync(string identifier, string? tierOverride, CancellationToken ct = default)
  {
    var existing = await GetAsync(identifier, ct);
    if (existing is not null) return existing;
    var display = new Display
    {
      Identifier = identifier,
      Tier = tierOverride,
      TierOverride = tierOverride is not null,
      CreatedAt = DateTimeOffset.UtcNow
    };
    db.Displays.Add(display);
    await db.SaveChangesAsync(ct);
    return display;
  }

  public async Task<bool> UnregisterAsync(string identifier, CancellationToken ct = default)
  {
    var display = await GetAsync(identifier, ct);
    if (display is null) return false;
    db.Displays.Remove(display);
    await db.SaveChangesAsync(ct);
    return true;
  }

  public async Task SetCurrentSceneAsync(string identifier, string template, string dataJson, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct) ?? throw new InvalidOperationException($"Display '{identifier}' not registered");
    d.CurrentTemplate = template;
    d.CurrentData = dataJson;
    d.CurrentPushedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
  }

  public async Task UpdateLastSeenAsync(string identifier, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct);
    if (d is null) return;
    d.LastSeenAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
  }

  public async Task RecordCapabilitiesAsync(
    string identifier,
    string? tier,
    string userAgent,
    int viewportWidth,
    int viewportHeight,
    string orientation,
    CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct) ?? throw new InvalidOperationException($"Display '{identifier}' not registered");
    if (!d.TierOverride) d.Tier = tier;
    d.LastUserAgent = userAgent;
    d.ViewportWidth = viewportWidth;
    d.ViewportHeight = viewportHeight;
    d.Orientation = orientation;
    d.LastSeenAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
  }

  public async Task<bool> SetTierAsync(string identifier, string tier, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct);
    if (d is null) return false;
    d.Tier = tier;
    d.TierOverride = true;
    await db.SaveChangesAsync(ct);
    return true;
  }
}
```

- [ ] **Step 2: TemplateRepository**

Write `src/toimi.tools.ruutu/Data/Repositories/TemplateRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public class TemplateRepository(RuutuDbContext db)
{
  public Task<Template?> GetAsync(string name, CancellationToken ct = default) =>
    db.Templates.FirstOrDefaultAsync(t => t.Name == name, ct);

  public Task<List<Template>> ListAsync(CancellationToken ct = default) =>
    db.Templates.OrderBy(t => t.Name).ToListAsync(ct);

  public async Task UpsertSeededAsync(
    string name, string description, string schemaJson,
    string modernHtml, string legacyHtml,
    CancellationToken ct = default)
  {
    var existing = await GetAsync(name, ct);
    var now = DateTimeOffset.UtcNow;
    if (existing is null)
    {
      db.Templates.Add(new Template
      {
        Name = name,
        Description = description,
        SchemaJson = schemaJson,
        ModernHtml = modernHtml,
        LegacyHtml = legacyHtml,
        IsSeeded = true,
        CreatedAt = now,
        UpdatedAt = now
      });
    }
    else
    {
      existing.Description = description;
      existing.SchemaJson = schemaJson;
      existing.ModernHtml = modernHtml;
      existing.LegacyHtml = legacyHtml;
      existing.IsSeeded = true;
      existing.UpdatedAt = now;
    }
    await db.SaveChangesAsync(ct);
  }

  public async Task<Template> UpsertAiAsync(
    string name, string description, string schemaJson,
    string? modernHtml, string? legacyHtml,
    CancellationToken ct = default)
  {
    var existing = await GetAsync(name, ct);
    var now = DateTimeOffset.UtcNow;
    if (existing is null)
    {
      var t = new Template
      {
        Name = name, Description = description, SchemaJson = schemaJson,
        ModernHtml = modernHtml, LegacyHtml = legacyHtml,
        IsSeeded = false, CreatedAt = now, UpdatedAt = now
      };
      db.Templates.Add(t);
      await db.SaveChangesAsync(ct);
      return t;
    }
    if (existing.IsSeeded)
      throw new InvalidOperationException($"Cannot modify seeded template '{name}'");
    existing.Description = description;
    existing.SchemaJson = schemaJson;
    if (modernHtml is not null) existing.ModernHtml = modernHtml;
    if (legacyHtml is not null) existing.LegacyHtml = legacyHtml;
    existing.UpdatedAt = now;
    await db.SaveChangesAsync(ct);
    return existing;
  }

  public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
  {
    var t = await GetAsync(name, ct);
    if (t is null) return false;
    if (t.IsSeeded) throw new InvalidOperationException($"Cannot delete seeded template '{name}'");
    db.Templates.Remove(t);
    await db.SaveChangesAsync(ct);
    return true;
  }
}
```

- [ ] **Step 3: DisplayEventRepository**

Write `src/toimi.tools.ruutu/Data/Repositories/DisplayEventRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public class DisplayEventRepository(RuutuDbContext db)
{
  public async Task<DisplayEvent> AppendAsync(
    int displayId, string eventType, string? target, string? valueJson,
    CancellationToken ct = default)
  {
    var e = new DisplayEvent
    {
      DisplayId = displayId,
      EventType = eventType,
      Target = target,
      Value = valueJson,
      CreatedAt = DateTimeOffset.UtcNow
    };
    db.DisplayEvents.Add(e);
    await db.SaveChangesAsync(ct);
    return e;
  }

  public Task<List<DisplayEvent>> GetSinceAsync(int displayId, DateTimeOffset? since, CancellationToken ct = default)
  {
    var q = db.DisplayEvents.Where(e => e.DisplayId == displayId);
    if (since.HasValue) q = q.Where(e => e.CreatedAt > since.Value);
    return q.OrderByDescending(e => e.CreatedAt).Take(200).ToListAsync(ct);
  }
}
```

- [ ] **Step 4: Register repositories in Program.cs**

In `src/toimi.tools.ruutu/Program.cs`, after the `AddDbContext` call and before the `AddMcpServer` call, insert:

```csharp
builder.Services.AddScoped<DisplayRepository>();
builder.Services.AddScoped<TemplateRepository>();
builder.Services.AddScoped<DisplayEventRepository>();
```

Add to top:
```csharp
using toimi.tools.ruutu.Data.Repositories;
```

- [ ] **Step 5: Verify build**

Run: `dotnet build src/toimi.tools.ruutu/`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.ruutu/Data/Repositories/ src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add Display, Template, DisplayEvent repositories"
```

---

## Phase 3: Rendering core (test-first)

This phase builds the pure, deterministic logic that's the heart of ruutu. Each task follows strict TDD.

### Task 8: Set up the test project

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj`
- Create: `src/toimi.tools.ruutu.Tests/SmokeTest.cs`
- Modify: `toimi.sln`

- [ ] **Step 1: Create test csproj**

Write `src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../toimi.tools.ruutu/toimi.tools.ruutu.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Sanity test**

Write `src/toimi.tools.ruutu.Tests/SmokeTest.cs`:

```csharp
namespace toimi.tools.ruutu.Tests;

public class SmokeTest
{
  [Fact]
  public void Arithmetic_Works()
  {
    Assert.Equal(4, 2 + 2);
  }
}
```

- [ ] **Step 3: Add to sln**

Open `toimi.sln`. After the new ruutu Project entry, add:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "toimi.tools.ruutu.Tests", "src\toimi.tools.ruutu.Tests\toimi.tools.ruutu.Tests.csproj", "{D5DA2558-7795-4F67-8688-5AD9B0A7B6E6}"
EndProject
```

Add matching `ProjectConfigurationPlatforms` entries (same Debug/Release pattern as other projects) using GUID `{D5DA2558-7795-4F67-8688-5AD9B0A7B6E6}`.

- [ ] **Step 4: Run**

```bash
dotnet test src/toimi.tools.ruutu.Tests/
```

Expected: 1 test, passes.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu.Tests/ toimi.sln
git commit -m "test(ruutu): scaffold xUnit test project"
```

---

### Task 9: CapabilityClassifier (test-first)

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/CapabilityClassifierTests.cs`
- Create: `src/toimi.tools.ruutu/Rendering/CapabilityPayload.cs`
- Create: `src/toimi.tools.ruutu/Rendering/CapabilityClassifier.cs`

- [ ] **Step 1: Write failing tests**

Write `src/toimi.tools.ruutu.Tests/Rendering/CapabilityClassifierTests.cs`:

```csharp
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tests.Rendering;

public class CapabilityClassifierTests
{
  private static CapabilityPayload Caps(bool flex, bool fetch, bool promise) =>
    new(flex, CssGrid: flex, Fetch: fetch, Promise: promise,
        ViewportWidth: 1024, ViewportHeight: 768, UserAgent: "Test");

  [Fact]
  public void Classifies_modern_when_all_features_present()
  {
    Assert.Equal("modern", CapabilityClassifier.Classify(Caps(true, true, true)));
  }

  [Fact]
  public void Classifies_legacy_when_fetch_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(true, false, true)));
  }

  [Fact]
  public void Classifies_legacy_when_flexbox_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(false, true, true)));
  }

  [Fact]
  public void Classifies_legacy_when_promise_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(true, true, false)));
  }

  [Fact]
  public void Derives_orientation_landscape_when_width_gt_height()
  {
    Assert.Equal("landscape", CapabilityClassifier.DeriveOrientation(1024, 768));
  }

  [Fact]
  public void Derives_orientation_portrait_when_height_gt_width()
  {
    Assert.Equal("portrait", CapabilityClassifier.DeriveOrientation(768, 1024));
  }

  [Fact]
  public void Derives_orientation_landscape_on_square()
  {
    Assert.Equal("landscape", CapabilityClassifier.DeriveOrientation(1000, 1000));
  }
}
```

- [ ] **Step 2: Run, see them fail**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter CapabilityClassifierTests
```
Expected: build errors (`CapabilityClassifier` doesn't exist).

- [ ] **Step 3: Implement payload + classifier**

Write `src/toimi.tools.ruutu/Rendering/CapabilityPayload.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public record CapabilityPayload(
  bool Flexbox,
  bool CssGrid,
  bool Fetch,
  bool Promise,
  int ViewportWidth,
  int ViewportHeight,
  string UserAgent);
```

Write `src/toimi.tools.ruutu/Rendering/CapabilityClassifier.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public static class CapabilityClassifier
{
  public static string Classify(CapabilityPayload caps)
  {
    return caps.Flexbox && caps.Fetch && caps.Promise ? "modern" : "legacy";
  }

  public static string DeriveOrientation(int width, int height)
  {
    return height > width ? "portrait" : "landscape";
  }
}
```

- [ ] **Step 4: Run, see them pass**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter CapabilityClassifierTests
```
Expected: 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Rendering/ src/toimi.tools.ruutu.Tests/Rendering/
git commit -m "feat(ruutu): add CapabilityClassifier with modern/legacy tier rules"
```

---

### Task 10: TierLinter + TierBriefs (test-first)

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/TierLinterTests.cs`
- Create: `src/toimi.tools.ruutu/Rendering/LintResult.cs`
- Create: `src/toimi.tools.ruutu/Rendering/TierLinter.cs`
- Create: `src/toimi.tools.ruutu/Rendering/TierBriefs.cs`

- [ ] **Step 1: Write failing tests**

Write `src/toimi.tools.ruutu.Tests/Rendering/TierLinterTests.cs`:

```csharp
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tests.Rendering;

public class TierLinterTests
{
  [Fact]
  public void Both_tiers_reject_script_tags()
  {
    var modernResult = TierLinter.Lint("modern", "<div><script>x()</script></div>");
    Assert.False(modernResult.Valid);
    Assert.Contains(modernResult.Issues, i => i.Rule == "no-script");

    var legacyResult = TierLinter.Lint("legacy", "<div><script>x()</script></div>");
    Assert.False(legacyResult.Valid);
    Assert.Contains(legacyResult.Issues, i => i.Rule == "no-script");
  }

  [Fact]
  public void Legacy_rejects_display_flex()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"display: flex\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-flex-or-grid");
  }

  [Fact]
  public void Legacy_rejects_display_grid()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"display:grid\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-flex-or-grid");
  }

  [Fact]
  public void Legacy_rejects_css_variables()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"color: var(--primary)\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-css-variables");
  }

  [Fact]
  public void Legacy_rejects_webp_images()
  {
    var result = TierLinter.Lint("legacy", "<img src=\"a.webp\">");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-webp");
  }

  [Fact]
  public void Legacy_rejects_font_face_and_import()
  {
    var importResult = TierLinter.Lint("legacy", "<style>@import 'x.css';</style>");
    Assert.Contains(importResult.Issues, i => i.Rule == "no-import-or-font-face");

    var faceResult = TierLinter.Lint("legacy", "<style>@font-face{font-family:X}</style>");
    Assert.Contains(faceResult.Issues, i => i.Rule == "no-import-or-font-face");
  }

  [Fact]
  public void Modern_accepts_what_legacy_rejects()
  {
    var html = "<div style=\"display: flex; color: var(--p)\"><img src=\"a.webp\"></div>";
    var result = TierLinter.Lint("modern", html);
    Assert.True(result.Valid);
  }

  [Fact]
  public void Clean_html_passes_both_tiers()
  {
    var html = "<table><tr><td>Hello</td></tr></table>";
    Assert.True(TierLinter.Lint("legacy", html).Valid);
    Assert.True(TierLinter.Lint("modern", html).Valid);
  }

  [Fact]
  public void Issues_include_line_numbers()
  {
    var html = "<div>\n<div>\n<script>x</script>\n</div>";
    var result = TierLinter.Lint("modern", html);
    Assert.Contains(result.Issues, i => i.Line == 3);
  }
}
```

- [ ] **Step 2: Run, see them fail**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter TierLinterTests
```
Expected: build errors.

- [ ] **Step 3: Implement LintResult**

Write `src/toimi.tools.ruutu/Rendering/LintResult.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public record LintIssue(int Line, string Rule, string Message);

public record LintResult(bool Valid, IReadOnlyList<LintIssue> Issues)
{
  public static LintResult Ok() => new(true, Array.Empty<LintIssue>());
  public static LintResult Failed(IReadOnlyList<LintIssue> issues) => new(false, issues);
}
```

- [ ] **Step 4: Implement TierLinter**

Write `src/toimi.tools.ruutu/Rendering/TierLinter.cs`:

```csharp
using System.Text.RegularExpressions;

namespace toimi.tools.ruutu.Rendering;

public static class TierLinter
{
  private record Rule(string Name, Regex Pattern, string Message, bool ModernToo);

  private static readonly Rule[] Rules =
  [
    new("no-script",            new(@"<script\b", RegexOptions.IgnoreCase),               "Templates must be declarative; no <script> tags.", ModernToo: true),
    new("no-flex-or-grid",      new(@"display\s*:\s*(flex|grid)\b", RegexOptions.IgnoreCase), "Legacy tier cannot use flexbox or CSS grid.", ModernToo: false),
    new("no-css-variables",     new(@"var\(\s*--", RegexOptions.IgnoreCase),              "Legacy tier cannot rely on CSS variables.", ModernToo: false),
    new("no-webp",              new(@"\.webp\b|image/webp", RegexOptions.IgnoreCase),      "Legacy tier does not support WebP images.", ModernToo: false),
    new("no-import-or-font-face", new(@"@import\b|@font-face\b", RegexOptions.IgnoreCase),  "Legacy tier cannot load external CSS or fonts.", ModernToo: false),
    new("no-clamp-min-max-fn",  new(@"\b(clamp|min|max)\s*\(", RegexOptions.IgnoreCase),   "Legacy tier cannot use clamp()/min()/max() CSS functions.", ModernToo: false),
    new("no-has-is-where",      new(@":has\(|:is\(|:where\(", RegexOptions.IgnoreCase),    "Legacy tier cannot use :has() / :is() / :where().", ModernToo: false)
  ];

  public static LintResult Lint(string tier, string html)
  {
    if (string.IsNullOrEmpty(html)) return LintResult.Ok();
    var legacyMode = tier == "legacy";
    var issues = new List<LintIssue>();
    var lines = html.Split('\n');

    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      foreach (var rule in Rules)
      {
        if (!legacyMode && !rule.ModernToo) continue;
        if (rule.Pattern.IsMatch(line))
        {
          issues.Add(new LintIssue(i + 1, rule.Name, rule.Message));
        }
      }
    }

    return issues.Count == 0 ? LintResult.Ok() : LintResult.Failed(issues);
  }
}
```

- [ ] **Step 5: Implement TierBriefs**

Write `src/toimi.tools.ruutu/Rendering/TierBriefs.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public static class TierBriefs
{
  public const string MODERN = """
    MODERN tier targets Safari 14+, Chrome 90+ (≈2020+).
    Allowed: flexbox, CSS grid, gap, position: sticky, vw/vh/rem/clamp()/min()/max(),
    CSS variables, modern color syntax (rgb(0 0 0 / 50%) and rgba()), system fonts +
    optional web fonts, JPG/PNG/SVG/WebP, @keyframes, transitions, transforms.
    Avoid: :has() (Safari 15.4+, conservative skip).
    Assume responsive viewport between 768 and 1920 px in either orientation.
    Use class names and data-* attributes for interactivity selectors.
    """;

  public const string LEGACY = """
    LEGACY tier targets iOS Safari 9-12 (iPad 2/3/4/Air 1, ≈2015–2018).
    Disallowed (linter will reject): flexbox, CSS grid, var(--*), @import, @font-face,
    WebP images, clamp()/min()/max() CSS functions, :has() / :is() / :where().
    Layout: tables (yes, deliberately), floats, inline-block.
    Units: px / em / % / vw / vh only.
    Colors: hex and rgba() only.
    Fonts: system stack only — do not use @font-face or web fonts.
    Selectors: tag, class, id, :hover. Avoid pseudo-class combinators newer than CSS2.
    Animations: basic @keyframes and transitions only; no 3D transforms.
    Assume viewport ≈ 1024 × 768 in either orientation; design for both.
    Templates are declarative HTML only — no <script> tags (the shell handles interactivity).
    Use data-tap, data-target, data-value attributes on tappable elements.
    """;
}
```

- [ ] **Step 6: Run, see them pass**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter TierLinterTests
```
Expected: 9 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/toimi.tools.ruutu/Rendering/ src/toimi.tools.ruutu.Tests/Rendering/TierLinterTests.cs
git commit -m "feat(ruutu): add TierLinter (regex rules) and TierBriefs constants"
```

---

### Task 11: ScribanRenderer with composite recursion (test-first)

The renderer takes (template name, data JSON, tier, depth) and returns HTML. Sub-template detection: any data value shaped `{template: string, data: object}` is rendered recursively and exposed to the parent template as a `<key>_html` Scriban variable. Arrays of such objects become `<key>_html` arrays. Nesting depth capped at 3.

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/ScribanRendererTests.cs`
- Create: `src/toimi.tools.ruutu/Rendering/RenderContext.cs`
- Create: `src/toimi.tools.ruutu/Rendering/RenderResult.cs`
- Create: `src/toimi.tools.ruutu/Rendering/RenderException.cs`
- Create: `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs`

- [ ] **Step 1: Write failing tests**

Write `src/toimi.tools.ruutu.Tests/Rendering/ScribanRendererTests.cs`:

```csharp
using System.Text.Json;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tests.Rendering;

public class ScribanRendererTests
{
  private static (string modern, string legacy) Tpl(string body) => (body, body);

  private static IRenderTemplateSource Source(params (string name, string modern, string legacy)[] tpls) =>
    new InMemorySource(tpls.ToDictionary(t => t.name, t => (t.modern, t.legacy)));

  private sealed class InMemorySource(IReadOnlyDictionary<string, (string Modern, string Legacy)> map) : IRenderTemplateSource
  {
    public bool TryGet(string name, out string modern, out string legacy)
    {
      if (map.TryGetValue(name, out var pair)) { modern = pair.Modern; legacy = pair.Legacy; return true; }
      modern = legacy = ""; return false;
    }
  }

  private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

  [Fact]
  public void Renders_a_leaf_template_with_substitution()
  {
    var src = Source(("greet", "<p>Hello {{ name }}</p>", "<p>Hello {{ name }}</p>"));
    var html = ScribanRenderer.Render("greet", Json("""{ "name": "World" }"""), "modern", src);
    Assert.Contains("Hello World", html);
  }

  [Fact]
  public void Picks_legacy_html_for_legacy_tier()
  {
    var src = Source(("greet", "<modern/>", "<legacy/>"));
    Assert.Equal("<legacy/>", ScribanRenderer.Render("greet", Json("{}"), "legacy", src));
    Assert.Equal("<modern/>", ScribanRenderer.Render("greet", Json("{}"), "modern", src));
  }

  [Fact]
  public void Throws_on_unknown_template()
  {
    var src = Source();
    var ex = Assert.Throws<RenderException>(() => ScribanRenderer.Render("missing", Json("{}"), "modern", src));
    Assert.Contains("missing", ex.Message);
  }

  [Fact]
  public void Throws_on_scriban_syntax_error()
  {
    var src = Source(("bad", "{{ this is not valid }", "{{ this is not valid }"));
    Assert.Throws<RenderException>(() => ScribanRenderer.Render("bad", Json("{}"), "modern", src));
  }

  [Fact]
  public void Renders_composite_with_sub_template_slot()
  {
    var src = Source(
      ("inner", "<span>{{ msg }}</span>", "<span>{{ msg }}</span>"),
      ("outer", "<div>{{ slot_html }}</div>", "<div>{{ slot_html }}</div>"));
    var data = Json("""{ "slot": { "template": "inner", "data": { "msg": "hi" } } }""");
    var html = ScribanRenderer.Render("outer", data, "modern", src);
    Assert.Contains("<div><span>hi</span></div>", html);
  }

  [Fact]
  public void Renders_array_of_sub_templates_into_array_variable()
  {
    var src = Source(
      ("item", "<li>{{ label }}</li>", "<li>{{ label }}</li>"),
      ("list", "<ul>{{ for it in items_html }}{{ it }}{{ end }}</ul>", "<ul>{{ for it in items_html }}{{ it }}{{ end }}</ul>"));
    var data = Json("""
      { "items": [
          { "template": "item", "data": { "label": "a" } },
          { "template": "item", "data": { "label": "b" } }
      ] }
      """);
    var html = ScribanRenderer.Render("list", data, "modern", src);
    Assert.Equal("<ul><li>a</li><li>b</li></ul>", html);
  }

  [Fact]
  public void Caps_recursion_depth_at_three()
  {
    var src = Source(
      ("leaf", "leaf", "leaf"),
      ("wrap", "[{{ inner_html }}]", "[{{ inner_html }}]"));
    var deep = Json("""
      { "inner": { "template": "wrap", "data": {
          "inner": { "template": "wrap", "data": {
            "inner": { "template": "wrap", "data": {
              "inner": { "template": "leaf", "data": {} } } } } } } }
      """);
    var ex = Assert.Throws<RenderException>(() => ScribanRenderer.Render("wrap", deep, "modern", src));
    Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Plain_scalar_values_pass_through_unchanged()
  {
    var src = Source(("t", "n={{ count }} f={{ flag }}", "n={{ count }} f={{ flag }}"));
    var html = ScribanRenderer.Render("t", Json("""{ "count": 5, "flag": true }"""), "modern", src);
    Assert.Equal("n=5 f=true", html);
  }
}
```

- [ ] **Step 2: Run, see them fail**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter ScribanRendererTests
```
Expected: build errors (types missing).

- [ ] **Step 3: Implement support types**

Write `src/toimi.tools.ruutu/Rendering/RenderException.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public class RenderException : Exception
{
  public RenderException(string message) : base(message) { }
  public RenderException(string message, Exception inner) : base(message, inner) { }
}
```

Write `src/toimi.tools.ruutu/Rendering/RenderContext.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public interface IRenderTemplateSource
{
  bool TryGet(string name, out string modernHtml, out string legacyHtml);
}
```

Write `src/toimi.tools.ruutu/Rendering/RenderResult.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public record RenderResult(string Html);
```

- [ ] **Step 4: Implement ScribanRenderer**

Write `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs`:

```csharp
using System.Text.Json;
using Scriban;
using Scriban.Runtime;

namespace toimi.tools.ruutu.Rendering;

public static class ScribanRenderer
{
  private const int MaxDepth = 3;

  public static string Render(string templateName, JsonElement data, string tier, IRenderTemplateSource source)
  {
    return RenderInternal(templateName, data, tier, source, 0);
  }

  private static string RenderInternal(string name, JsonElement data, string tier, IRenderTemplateSource source, int depth)
  {
    if (depth > MaxDepth)
      throw new RenderException($"Template recursion exceeded max depth of {MaxDepth} (at '{name}')");

    if (!source.TryGet(name, out var modern, out var legacy))
      throw new RenderException($"Template '{name}' not found");

    var html = tier == "legacy" ? legacy : modern;
    if (string.IsNullOrEmpty(html))
      throw new RenderException($"Template '{name}' has no '{tier}' variant");

    var enriched = EnrichDataWithSlots(data, tier, source, depth);

    Template template;
    try { template = Template.Parse(html); }
    catch (Exception ex) { throw new RenderException($"Template '{name}' parse error: {ex.Message}", ex); }
    if (template.HasErrors)
      throw new RenderException($"Template '{name}' parse error: {string.Join("; ", template.Messages)}");

    var scriptObj = new ScriptObject();
    foreach (var (k, v) in enriched) scriptObj[k] = v;
    var context = new TemplateContext { StrictVariables = false };
    context.PushGlobal(scriptObj);

    try { return template.Render(context); }
    catch (Exception ex) { throw new RenderException($"Template '{name}' render error: {ex.Message}", ex); }
  }

  private static Dictionary<string, object?> EnrichDataWithSlots(
    JsonElement data, string tier, IRenderTemplateSource source, int depth)
  {
    var result = new Dictionary<string, object?>();
    if (data.ValueKind != JsonValueKind.Object) return result;

    foreach (var prop in data.EnumerateObject())
    {
      result[prop.Name] = JsonToScalar(prop.Value);

      if (IsSlotRef(prop.Value, out var subName, out var subData))
      {
        var subHtml = RenderInternal(subName!, subData, tier, source, depth + 1);
        result[$"{prop.Name}_html"] = subHtml;
      }
      else if (prop.Value.ValueKind == JsonValueKind.Array)
      {
        var rendered = new List<string>();
        var anySlot = false;
        foreach (var item in prop.Value.EnumerateArray())
        {
          if (IsSlotRef(item, out var iName, out var iData))
          {
            anySlot = true;
            rendered.Add(RenderInternal(iName!, iData, tier, source, depth + 1));
          }
        }
        if (anySlot) result[$"{prop.Name}_html"] = rendered;
      }
    }
    return result;
  }

  private static bool IsSlotRef(JsonElement v, out string? name, out JsonElement data)
  {
    name = null; data = default;
    if (v.ValueKind != JsonValueKind.Object) return false;
    if (!v.TryGetProperty("template", out var tEl) || tEl.ValueKind != JsonValueKind.String) return false;
    if (!v.TryGetProperty("data", out var dEl)) return false;
    name = tEl.GetString();
    data = dEl;
    return !string.IsNullOrEmpty(name);
  }

  private static object? JsonToScalar(JsonElement v) => v.ValueKind switch
  {
    JsonValueKind.String => v.GetString(),
    JsonValueKind.Number => v.TryGetInt64(out var n) ? n : v.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Null => null,
    JsonValueKind.Array => v.EnumerateArray().Select(JsonToScalar).ToList(),
    JsonValueKind.Object => v.EnumerateObject().ToDictionary(p => p.Name, p => JsonToScalar(p.Value)),
    _ => null
  };
}
```

- [ ] **Step 5: Run, see them pass**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter ScribanRendererTests
```
Expected: 8 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.ruutu/Rendering/ src/toimi.tools.ruutu.Tests/Rendering/ScribanRendererTests.cs
git commit -m "feat(ruutu): add ScribanRenderer with composite slot recursion (depth 3 cap)"
```

---

### Task 12: OverlayStack semantics (test-first)

The overlay stack lives as JSON inside `displays.overlay_stack`. A pure helper handles push/pop/eviction so it's deterministic and unit-testable.

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/OverlayStackTests.cs`
- Create: `src/toimi.tools.ruutu/Rendering/OverlayStack.cs`
- Create: `src/toimi.tools.ruutu/Rendering/OverlayFrame.cs`

- [ ] **Step 1: Failing tests**

Write `src/toimi.tools.ruutu.Tests/Rendering/OverlayStackTests.cs`:

```csharp
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tests.Rendering;

public class OverlayStackTests
{
  [Fact]
  public void Push_makes_new_overlay_the_top()
  {
    var stack = OverlayStack.Parse("[]");
    var (next, _) = OverlayStack.Push(stack, new OverlayFrame("notification", "{\"x\":1}", DateTimeOffset.UnixEpoch));
    Assert.Single(next);
    Assert.Equal("notification", next[0].Template);
  }

  [Fact]
  public void Push_keeps_newest_on_top_lifo()
  {
    var stack = OverlayStack.Parse("[]");
    (stack, _) = OverlayStack.Push(stack, new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch));
    (stack, _) = OverlayStack.Push(stack, new OverlayFrame("b", "{}", DateTimeOffset.UnixEpoch.AddSeconds(1)));
    Assert.Equal("b", stack[0].Template);
    Assert.Equal("a", stack[1].Template);
  }

  [Fact]
  public void Pop_removes_top_and_returns_remaining_top()
  {
    var stack = new[]
    {
      new OverlayFrame("b", "{}", DateTimeOffset.UnixEpoch.AddSeconds(1)),
      new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch)
    };
    var (next, top) = OverlayStack.Pop(stack);
    Assert.Single(next);
    Assert.Equal("a", top!.Template);
  }

  [Fact]
  public void Pop_on_empty_returns_empty_and_null()
  {
    var (next, top) = OverlayStack.Pop(Array.Empty<OverlayFrame>());
    Assert.Empty(next);
    Assert.Null(top);
  }

  [Fact]
  public void Pop_returns_null_top_when_only_one_frame()
  {
    var stack = new[] { new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch) };
    var (next, top) = OverlayStack.Pop(stack);
    Assert.Empty(next);
    Assert.Null(top);
  }

  [Fact]
  public void Push_evicts_oldest_when_cap_exceeded()
  {
    var stack = Array.Empty<OverlayFrame>();
    OverlayFrame? evicted = null;
    for (var i = 0; i < OverlayStack.MaxDepth; i++)
      (stack, _) = OverlayStack.Push(stack, new OverlayFrame($"t{i}", "{}", DateTimeOffset.UnixEpoch.AddSeconds(i)));

    (stack, evicted) = OverlayStack.Push(stack, new OverlayFrame("new", "{}", DateTimeOffset.UnixEpoch.AddSeconds(100)));

    Assert.Equal(OverlayStack.MaxDepth, stack.Length);
    Assert.Equal("new", stack[0].Template);
    Assert.NotNull(evicted);
    Assert.Equal("t0", evicted!.Template);
  }

  [Fact]
  public void Serialize_and_parse_round_trip()
  {
    var stack = new[] { new OverlayFrame("a", "{\"k\":1}", DateTimeOffset.UnixEpoch) };
    var json = OverlayStack.Serialize(stack);
    var parsed = OverlayStack.Parse(json);
    Assert.Single(parsed);
    Assert.Equal("a", parsed[0].Template);
    Assert.Equal("{\"k\":1}", parsed[0].DataJson);
  }
}
```

- [ ] **Step 2: Run, see fail**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter OverlayStackTests
```

- [ ] **Step 3: Implement OverlayFrame**

Write `src/toimi.tools.ruutu/Rendering/OverlayFrame.cs`:

```csharp
namespace toimi.tools.ruutu.Rendering;

public record OverlayFrame(string Template, string DataJson, DateTimeOffset EnqueuedAt);
```

- [ ] **Step 4: Implement OverlayStack**

Write `src/toimi.tools.ruutu/Rendering/OverlayStack.cs`:

```csharp
using System.Text.Json;

namespace toimi.tools.ruutu.Rendering;

public static class OverlayStack
{
  public const int MaxDepth = 10;

  public static OverlayFrame[] Parse(string json)
  {
    if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OverlayFrame>();
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<OverlayFrame>();
    var frames = new List<OverlayFrame>();
    foreach (var el in doc.RootElement.EnumerateArray())
    {
      var template = el.GetProperty("template").GetString() ?? "";
      var data = el.GetProperty("data").GetRawText();
      var enq = el.GetProperty("enqueued_at").GetDateTimeOffset();
      frames.Add(new OverlayFrame(template, data, enq));
    }
    return frames.ToArray();
  }

  public static string Serialize(IReadOnlyList<OverlayFrame> frames)
  {
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
      w.WriteStartArray();
      foreach (var f in frames)
      {
        w.WriteStartObject();
        w.WriteString("template", f.Template);
        w.WritePropertyName("data");
        using var d = JsonDocument.Parse(f.DataJson);
        d.RootElement.WriteTo(w);
        w.WriteString("enqueued_at", f.EnqueuedAt.UtcDateTime.ToString("o"));
        w.WriteEndObject();
      }
      w.WriteEndArray();
    }
    return System.Text.Encoding.UTF8.GetString(ms.ToArray());
  }

  /// <summary>Push onto top of LIFO stack. Returns new stack and evicted frame (or null) if oldest was dropped.</summary>
  public static (OverlayFrame[] Stack, OverlayFrame? Evicted) Push(IReadOnlyList<OverlayFrame> current, OverlayFrame frame)
  {
    var list = new List<OverlayFrame>(current.Count + 1) { frame };
    list.AddRange(current);
    OverlayFrame? evicted = null;
    if (list.Count > MaxDepth)
    {
      evicted = list[^1];
      list.RemoveAt(list.Count - 1);
    }
    return (list.ToArray(), evicted);
  }

  /// <summary>Pop the top of the stack. Returns the remainder and the NEW top (if any).</summary>
  public static (OverlayFrame[] Stack, OverlayFrame? NewTop) Pop(IReadOnlyList<OverlayFrame> current)
  {
    if (current.Count == 0) return (Array.Empty<OverlayFrame>(), null);
    var remainder = current.Skip(1).ToArray();
    var newTop = remainder.Length > 0 ? remainder[0] : null;
    return (remainder, newTop);
  }
}
```

- [ ] **Step 5: Run, see pass**

```bash
dotnet test src/toimi.tools.ruutu.Tests/ --filter OverlayStackTests
```
Expected: 7 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.ruutu/Rendering/OverlayStack.cs src/toimi.tools.ruutu/Rendering/OverlayFrame.cs src/toimi.tools.ruutu.Tests/Rendering/OverlayStackTests.cs
git commit -m "feat(ruutu): add OverlayStack with LIFO semantics and oldest-eviction"
```

---

## Phase 4: Seeded templates

### Task 13: TemplateSeeder bootstrap + splash + clock

**Files:**
- Create: `src/toimi.tools.ruutu/Data/TemplateSeeder.cs`
- Create: `src/toimi.tools.ruutu/Data/SeedTemplates.cs` (the static list)
- Modify: `src/toimi.tools.ruutu/Program.cs` (run seeder on startup)

- [ ] **Step 1: TemplateSeeder**

Write `src/toimi.tools.ruutu/Data/TemplateSeeder.cs`:

```csharp
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Data;

public class TemplateSeeder(TemplateRepository repo, ILogger<TemplateSeeder> logger)
{
  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var t in SeedTemplates.All)
    {
      await repo.UpsertSeededAsync(t.Name, t.Description, t.SchemaJson, t.ModernHtml, t.LegacyHtml, ct);
      logger.LogInformation("Seeded template '{Name}'", t.Name);
    }
  }
}
```

- [ ] **Step 2: SeedTemplates with splash + clock**

Write `src/toimi.tools.ruutu/Data/SeedTemplates.cs`:

```csharp
namespace toimi.tools.ruutu.Data;

public record SeedTemplate(string Name, string Description, string SchemaJson, string ModernHtml, string LegacyHtml);

public static class SeedTemplates
{
  public static readonly SeedTemplate[] All =
  [
    new(
      Name: "splash",
      Description: "Default idle scene. Shows the Toimi splash and the display identifier — useful for confirming the right URL was opened.",
      SchemaJson: """
        {
          "type": "object",
          "properties": { "message": { "type": "string" } },
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;align-items:center;justify-content:center;min-height:100vh;background:#f5f3ef;font-family:-apple-system,Segoe UI,system-ui,sans-serif">
          <div style="text-align:center">
            <div style="font-size:48px;font-weight:300;color:#222">Toimi</div>
            <div style="font-size:14px;color:#888;margin-top:12px">{{ message | default: "" }}</div>
          </div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" style="background:#f5f3ef;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr>
            <td align="center" valign="middle">
              <div style="font-size:48px;color:#222">Toimi</div>
              <div style="font-size:14px;color:#888;margin-top:12px">{{ message | default: "" }}</div>
            </td>
          </tr>
        </table>
        """
    ),
    new(
      Name: "clock",
      Description: "Large current time + date. Ticks client-side from Date.now(). Optional 24h/12h format. Useful as a single-tile glanceable element.",
      SchemaJson: """
        {
          "type": "object",
          "properties": {
            "timezone": { "type": "string" },
            "format": { "type": "string", "enum": ["24h", "12h"] }
          },
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div data-clock="{{ format | default: "24h" }}" data-tz="{{ timezone | default: "" }}"
             style="display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;background:#fff;font-family:-apple-system,system-ui,sans-serif">
          <div data-clock-time style="font-size:96px;font-weight:200;color:#111">--:--</div>
          <div data-clock-date style="font-size:18px;color:#666;margin-top:8px"></div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr>
            <td align="center" valign="middle" data-clock="{{ format | default: "24h" }}" data-tz="{{ timezone | default: "" }}">
              <div data-clock-time style="font-size:96px;color:#111">--:--</div>
              <div data-clock-date style="font-size:18px;color:#666;margin-top:8px"></div>
            </td>
          </tr>
        </table>
        """
    )
  ];
}
```

Note: The shell page's JS will look for `[data-clock]` and tick `[data-clock-time]` / `[data-clock-date]` once per second. This is handled by the shell, not by the template (templates have no JS).

- [ ] **Step 3: Wire seeder into Program.cs**

In `src/toimi.tools.ruutu/Program.cs`, before the `AddMcpServer` call, add:

```csharp
builder.Services.AddScoped<TemplateSeeder>();
```

And after the migration block, add:

```csharp
using (var seedScope = app.Services.CreateScope())
{
  var seeder = seedScope.ServiceProvider.GetRequiredService<TemplateSeeder>();
  await seeder.SeedAsync();
}
```

Add `using toimi.tools.ruutu.Data;` at the top.

- [ ] **Step 4: Smoke-test the seeding**

Run: `cd src/toimi.tools.ruutu && ASPNETCORE_ENVIRONMENT=Development dotnet run`

In a second terminal: `psql -h localhost -U postgres -d ruutu -c "SELECT name, is_seeded FROM templates ORDER BY name;"`

Expected:
```
  name  | is_seeded
--------+-----------
 clock  | t
 splash | t
```

Stop the dotnet process.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Data/TemplateSeeder.cs src/toimi.tools.ruutu/Data/SeedTemplates.cs src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): seed splash and clock templates on startup"
```

---

### Task 14: Seed remaining leaf templates

Append six more entries to `SeedTemplates.All`: `message`, `notification`, `todo_list`, `weather`, `calendar_day`, `reminders`.

**Files:**
- Modify: `src/toimi.tools.ruutu/Data/SeedTemplates.cs`

- [ ] **Step 1: Append entries to the All array**

In `src/toimi.tools.ruutu/Data/SeedTemplates.cs`, before the closing `]`, add (preserving the trailing comma after the `clock` entry):

```csharp
new(
  Name: "message",
  Description: "Big text card with optional title. Use for short standalone messages like 'Welcome home' or 'Leave for school in 5 min'.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "title": { "type": "string" },
        "body":  { "type": "string" }
      },
      "required": ["body"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="display:flex;align-items:center;justify-content:center;min-height:100vh;background:#fafaf7;padding:40px;font-family:-apple-system,system-ui,sans-serif">
      <div style="max-width:600px;text-align:center">
        {{ if title }}<div style="font-size:14px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ title }}</div>{{ end }}
        <div style="font-size:36px;color:#222;margin-top:18px;line-height:1.3">{{ body }}</div>
      </div>
    </div>
    """,
  LegacyHtml: """
    <table width="100%" height="100%" style="background:#fafaf7;font-family:-apple-system,Helvetica,Arial,sans-serif">
      <tr>
        <td align="center" valign="middle" style="padding:40px">
          {{ if title }}<div style="font-size:14px;color:#888;text-transform:uppercase">{{ title }}</div>{{ end }}
          <div style="font-size:36px;color:#222;margin-top:18px">{{ body }}</div>
        </td>
      </tr>
    </table>
    """
),
new(
  Name: "notification",
  Description: "Notification card. Most commonly used as an overlay. Tap anywhere dismisses. Severity styles the accent color.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "title":    { "type": "string" },
        "body":     { "type": "string" },
        "icon":     { "type": "string" },
        "severity": { "type": "string", "enum": ["info", "warn", "alert"] }
      },
      "required": ["title", "body"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div data-tap="dismiss" data-target="overlay"
         style="background:#222;color:#fff;padding:20px 24px;border-radius:10px;min-width:280px;max-width:400px;
                box-shadow:0 8px 24px rgba(0,0,0,0.3);margin:24px auto;font-family:-apple-system,system-ui,sans-serif">
      <div style="font-size:11px;letter-spacing:2px;color:#aaa;text-transform:uppercase">{{ severity | default: "info" }}</div>
      <div style="font-size:18px;font-weight:500;margin-top:6px">{{ title }}</div>
      <div style="font-size:14px;color:#ccc;margin-top:8px">{{ body }}</div>
      <div style="font-size:11px;color:#888;margin-top:12px">tap to dismiss</div>
    </div>
    """,
  LegacyHtml: """
    <table data-tap="dismiss" data-target="overlay"
           cellpadding="20" style="background:#222;color:#fff;margin:24px auto;border:0;font-family:-apple-system,Helvetica,Arial,sans-serif;width:300px">
      <tr><td>
        <div style="font-size:11px;color:#aaa;text-transform:uppercase">{{ severity | default: "info" }}</div>
        <div style="font-size:18px;margin-top:6px">{{ title }}</div>
        <div style="font-size:14px;color:#ccc;margin-top:8px">{{ body }}</div>
        <div style="font-size:11px;color:#888;margin-top:12px">tap to dismiss</div>
      </td></tr>
    </table>
    """
),
new(
  Name: "todo_list",
  Description: "Title plus a checkbox list. Tap a row to record a check event with target=step.id. Use for in-progress routines (e.g. evening routine).",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "title": { "type": "string" },
        "steps": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id":    { "type": "string" },
              "label": { "type": "string" },
              "done":  { "type": "boolean" }
            },
            "required": ["id", "label"]
          }
        }
      },
      "required": ["title", "steps"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="background:#f5f3ef;min-height:100vh;padding:24px;font-family:-apple-system,system-ui,sans-serif">
      <div style="max-width:520px;margin:0 auto;background:#fff;border:1px solid #e0d8cc;padding:20px;border-radius:8px">
        <div style="font-size:20px;font-weight:600;color:#222">{{ title }}</div>
        <div style="margin-top:14px">
          {{ for step in steps }}
            <div data-tap="check" data-target="{{ step.id }}" data-value="{{ if step.done }}false{{ else }}true{{ end }}"
                 style="display:flex;align-items:center;padding:10px 0;border-bottom:1px solid #eee">
              <div style="width:24px;font-size:18px">{{ if step.done }}&#9745;{{ else }}&#9744;{{ end }}</div>
              <div style="flex:1;font-size:15px;{{ if step.done }}text-decoration:line-through;color:#999{{ else }}color:#333{{ end }}">{{ step.label }}</div>
            </div>
          {{ end }}
        </div>
      </div>
    </div>
    """,
  LegacyHtml: """
    <table width="100%" style="background:#f5f3ef;font-family:-apple-system,Helvetica,Arial,sans-serif">
      <tr><td align="center" style="padding:24px">
        <table width="520" cellpadding="16" style="background:#fff;border:1px solid #e0d8cc">
          <tr><td>
            <div style="font-size:20px;color:#222">{{ title }}</div>
            <table width="100%" cellpadding="6" style="margin-top:14px">
              {{ for step in steps }}
              <tr data-tap="check" data-target="{{ step.id }}" data-value="{{ if step.done }}false{{ else }}true{{ end }}">
                <td width="28" style="font-size:18px">{{ if step.done }}&#9745;{{ else }}&#9744;{{ end }}</td>
                <td style="font-size:15px;{{ if step.done }}text-decoration:line-through;color:#999{{ else }}color:#333{{ end }}">{{ step.label }}</td>
              </tr>
              {{ end }}
            </table>
          </td></tr>
        </table>
      </td></tr>
    </table>
    """
),
new(
  Name: "weather",
  Description: "Current temperature plus brief outlook. AI populates from koti (Home Assistant weather entity).",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "location": { "type": "string" },
        "current":  {
          "type": "object",
          "properties": {
            "temp":       { "type": "number" },
            "condition":  { "type": "string" },
            "feels_like": { "type": "number" }
          },
          "required": ["temp", "condition"]
        },
        "today": {
          "type": "object",
          "properties": {
            "high":  { "type": "number" },
            "low":   { "type": "number" },
            "notes": { "type": "string" }
          }
        }
      },
      "required": ["location", "current"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
      <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ location }}</div>
      <div style="font-size:64px;font-weight:200;color:#222;margin-top:6px;line-height:1">{{ current.temp }}&deg;</div>
      <div style="font-size:14px;color:#666;margin-top:4px">{{ current.condition }}{{ if current.feels_like }} &middot; feels {{ current.feels_like }}&deg;{{ end }}</div>
      {{ if today }}
        <div style="font-size:12px;color:#888;margin-top:14px">
          {{ if today.low }}&darr; {{ today.low }}&deg;  {{ end }}{{ if today.high }}&uarr; {{ today.high }}&deg;{{ end }}
          {{ if today.notes }} &middot; {{ today.notes }}{{ end }}
        </div>
      {{ end }}
    </div>
    """,
  LegacyHtml: """
    <table cellpadding="20" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
      <tr><td>
        <div style="font-size:11px;color:#888;text-transform:uppercase">{{ location }}</div>
        <div style="font-size:64px;color:#222;margin-top:6px">{{ current.temp }}&deg;</div>
        <div style="font-size:14px;color:#666">{{ current.condition }}{{ if current.feels_like }} &middot; feels {{ current.feels_like }}&deg;{{ end }}</div>
        {{ if today }}
          <div style="font-size:12px;color:#888;margin-top:14px">
            {{ if today.low }}&darr; {{ today.low }}&deg;  {{ end }}{{ if today.high }}&uarr; {{ today.high }}&deg;{{ end }}
            {{ if today.notes }} &middot; {{ today.notes }}{{ end }}
          </div>
        {{ end }}
      </td></tr>
    </table>
    """
),
new(
  Name: "calendar_day",
  Description: "Today's events as a vertical list with times. AI populates from Google Calendar.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "date":   { "type": "string" },
        "events": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "time":  { "type": "string" },
              "title": { "type": "string" }
            },
            "required": ["time", "title"]
          }
        }
      },
      "required": ["date", "events"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
      <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ date }}</div>
      <div style="margin-top:14px">
        {{ for e in events }}
          <div style="padding:8px 0;border-bottom:1px solid #eee;font-size:14px;color:#333">
            <strong style="color:#222">{{ e.time }}</strong>&nbsp;&nbsp;{{ e.title }}
          </div>
        {{ end }}
        {{ if (events | array.size) == 0 }}
          <div style="color:#888;font-size:13px">No events today.</div>
        {{ end }}
      </div>
    </div>
    """,
  LegacyHtml: """
    <table cellpadding="16" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
      <tr><td>
        <div style="font-size:11px;color:#888;text-transform:uppercase">{{ date }}</div>
        <table width="100%" cellpadding="6" style="margin-top:14px">
          {{ for e in events }}
            <tr><td style="border-bottom:1px solid #eee;font-size:14px;color:#333"><strong>{{ e.time }}</strong>&nbsp;&nbsp;{{ e.title }}</td></tr>
          {{ end }}
          {{ if (events | array.size) == 0 }}
            <tr><td style="color:#888;font-size:13px">No events today.</td></tr>
          {{ end }}
        </table>
      </td></tr>
    </table>
    """
),
new(
  Name: "reminders",
  Description: "Upcoming reminders, time-ordered. AI populates from muistutin.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "due_at": { "type": "string" },
              "title":  { "type": "string" }
            },
            "required": ["due_at", "title"]
          }
        }
      },
      "required": ["items"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
      <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">Reminders</div>
      <div style="margin-top:14px">
        {{ for it in items }}
          <div style="padding:8px 0;border-bottom:1px solid #eee;font-size:14px;color:#333">
            <span style="color:#888;font-size:12px;display:inline-block;width:120px">{{ it.due_at }}</span>{{ it.title }}
          </div>
        {{ end }}
        {{ if (items | array.size) == 0 }}
          <div style="color:#888;font-size:13px">No upcoming reminders.</div>
        {{ end }}
      </div>
    </div>
    """,
  LegacyHtml: """
    <table cellpadding="16" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
      <tr><td>
        <div style="font-size:11px;color:#888;text-transform:uppercase">Reminders</div>
        <table width="100%" cellpadding="6" style="margin-top:14px">
          {{ for it in items }}
            <tr>
              <td width="130" style="color:#888;font-size:12px">{{ it.due_at }}</td>
              <td style="font-size:14px;color:#333">{{ it.title }}</td>
            </tr>
          {{ end }}
          {{ if (items | array.size) == 0 }}
            <tr><td colspan="2" style="color:#888;font-size:13px">No upcoming reminders.</td></tr>
          {{ end }}
        </table>
      </td></tr>
    </table>
    """
)
```

- [ ] **Step 2: Verify build + seeding**

Run: `dotnet build src/toimi.tools.ruutu/`
Run app, then: `psql -h localhost -U postgres -d ruutu -c "SELECT name FROM templates ORDER BY name;"`
Expected: 8 leaves listed.

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Data/SeedTemplates.cs
git commit -m "feat(ruutu): seed message, notification, todo_list, weather, calendar_day, reminders templates"
```

---

### Task 15: Seed layout templates

**Files:**
- Modify: `src/toimi.tools.ruutu/Data/SeedTemplates.cs`

- [ ] **Step 1: Append three layout entries to the All array**

Append (before closing `]`):

```csharp
new(
  Name: "split_horizontal",
  Description: "Two tiles side by side. Sub-templates declared as { template, data } in 'left' and 'right'. Renders each at the display's capability tier.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "left":  { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] },
        "right": { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] }
      },
      "required": ["left","right"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="display:flex;gap:12px;padding:12px;min-height:100vh;background:#f5f3ef;box-sizing:border-box">
      <div style="flex:1;min-width:0">{{ left_html }}</div>
      <div style="flex:1;min-width:0">{{ right_html }}</div>
    </div>
    """,
  LegacyHtml: """
    <table width="100%" height="100%" cellpadding="6" cellspacing="0" style="background:#f5f3ef">
      <tr>
        <td width="50%" valign="top">{{ left_html }}</td>
        <td width="50%" valign="top">{{ right_html }}</td>
      </tr>
    </table>
    """
),
new(
  Name: "split_vertical",
  Description: "Two tiles stacked top over bottom. Sub-templates in 'top' and 'bottom'.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "top":    { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] },
        "bottom": { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] }
      },
      "required": ["top","bottom"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="display:flex;flex-direction:column;gap:12px;padding:12px;min-height:100vh;background:#f5f3ef;box-sizing:border-box">
      <div style="flex:1;min-height:0">{{ top_html }}</div>
      <div style="flex:1;min-height:0">{{ bottom_html }}</div>
    </div>
    """,
  LegacyHtml: """
    <table width="100%" height="100%" cellpadding="6" cellspacing="0" style="background:#f5f3ef">
      <tr><td valign="top">{{ top_html }}</td></tr>
      <tr><td valign="top">{{ bottom_html }}</td></tr>
    </table>
    """
),
new(
  Name: "stack",
  Description: "N tiles stacked vertically with optional gap. 'items' is an array of { template, data }.",
  SchemaJson: """
    {
      "type": "object",
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": { "template": { "type":"string" }, "data": { "type":"object" } },
            "required": ["template","data"]
          }
        },
        "gap": { "type": "integer", "minimum": 0 }
      },
      "required": ["items"],
      "additionalProperties": false
    }
    """,
  ModernHtml: """
    <div style="display:flex;flex-direction:column;gap:{{ gap | default: 12 }}px;padding:12px;background:#f5f3ef;min-height:100vh;box-sizing:border-box">
      {{ for it in items_html }}<div>{{ it }}</div>{{ end }}
    </div>
    """,
  LegacyHtml: """
    <table width="100%" cellpadding="{{ (gap | default: 12) / 2 }}" cellspacing="0" style="background:#f5f3ef">
      {{ for it in items_html }}<tr><td>{{ it }}</td></tr>{{ end }}
    </table>
    """
)
```

- [ ] **Step 2: Verify seeded templates**

Run the app, then:
```bash
psql -h localhost -U postgres -d ruutu -c "SELECT name FROM templates WHERE is_seeded ORDER BY name;"
```
Expected: 11 rows: calendar_day, clock, message, notification, reminders, splash, split_horizontal, split_vertical, stack, todo_list, weather.

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Data/SeedTemplates.cs
git commit -m "feat(ruutu): seed split_horizontal, split_vertical, stack layout templates"
```

---

## Phase 5: Display shell + REST/SSE

### Task 16: The ES5 display shell (wwwroot/shell.html + shell.css)

This is the page served at `/ruutu/<identifier>`. It must run on iOS Safari 9. No build step, no Promises, no fetch — XHR, plain functions, var.

**Files:**
- Create: `src/toimi.tools.ruutu/wwwroot/shell.html`
- Create: `src/toimi.tools.ruutu/wwwroot/shell.css`
- Modify: `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj` (ensure static files copy)

- [ ] **Step 1: shell.css**

Write `src/toimi.tools.ruutu/wwwroot/shell.css`:

```css
/* Minimal reset + utilities. Legacy-tier safe. */
* { margin: 0; padding: 0; box-sizing: border-box; }
html, body { width: 100%; height: 100%; background: #f5f3ef; font-family: -apple-system, Helvetica, Arial, sans-serif; color: #222; }
#scene, #overlay-wrap { position: absolute; top: 0; left: 0; width: 100%; height: 100%; }
#overlay-wrap { background: rgba(0,0,0,0.45); z-index: 10; display: none; }
#overlay-wrap.show { display: block; }
#overlay { width: 100%; height: 100%; }
#disconnected { position: fixed; top: 12px; right: 12px; background: rgba(200,60,60,0.85); color: #fff; padding: 6px 10px; font-size: 11px; border-radius: 4px; z-index: 100; display: none; }
#disconnected.show { display: block; }
[data-tap] { cursor: pointer; }
```

- [ ] **Step 2: shell.html**

Write `src/toimi.tools.ruutu/wwwroot/shell.html`:

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
  <title>Toimi - __IDENTIFIER__</title>
  <link rel="stylesheet" href="/ruutu/static/shell.css">
</head>
<body>
  <div id="scene"></div>
  <div id="overlay-wrap"><div id="overlay"></div></div>
  <div id="disconnected">disconnected</div>

  <script>
  (function(){
    var ID = "__IDENTIFIER__";
    var API = "/ruutu/api/displays/" + encodeURIComponent(ID);
    var scene = document.getElementById("scene");
    var overlayWrap = document.getElementById("overlay-wrap");
    var overlay = document.getElementById("overlay");
    var disconnected = document.getElementById("disconnected");
    var es = null;

    function testStyle(prop, val) {
      var el = document.createElement("div");
      try { el.style[prop] = val; return el.style[prop] === val; } catch (e) { return false; }
    }

    function detectCaps() {
      return {
        flexbox: testStyle("display","flex"),
        cssGrid: !!(window.CSS && window.CSS.supports && window.CSS.supports("display","grid")),
        fetch:   typeof window.fetch === "function",
        promise: typeof window.Promise === "function",
        viewport_width: window.innerWidth || document.documentElement.clientWidth,
        viewport_height: window.innerHeight || document.documentElement.clientHeight,
        user_agent: navigator.userAgent
      };
    }

    function xhrPost(url, payload, onSuccess) {
      var xhr = new XMLHttpRequest();
      xhr.open("POST", url, true);
      xhr.setRequestHeader("Content-Type", "application/json");
      xhr.onreadystatechange = function() {
        if (xhr.readyState === 4 && onSuccess) onSuccess(xhr.status, xhr.responseText);
      };
      xhr.send(JSON.stringify(payload));
    }

    function postEvent(type, target, value) {
      if (!type) return;
      xhrPost(API + "/events", { type: type, target: target || null, value: value || null });
    }

    function connectStream() {
      if (typeof EventSource === "undefined") {
        disconnected.innerHTML = "browser does not support live updates";
        disconnected.className = "show";
        return;
      }
      try { es = new EventSource(API + "/stream"); } catch (e) { return; }

      es.addEventListener("scene", function(ev) {
        var d = JSON.parse(ev.data);
        scene.innerHTML = d.html;
        startClocksIn(scene);
      });
      es.addEventListener("overlay", function(ev) {
        var d = JSON.parse(ev.data);
        overlay.innerHTML = d.html;
        overlayWrap.className = "show";
      });
      es.addEventListener("overlay_clear", function() {
        overlay.innerHTML = "";
        overlayWrap.className = "";
      });
      es.addEventListener("clear", function() {
        overlay.innerHTML = "";
        overlayWrap.className = "";
      });
      es.addEventListener("heartbeat", function() { disconnected.className = ""; });
      es.onopen = function() { disconnected.className = ""; };
      es.onerror = function() { disconnected.className = "show"; };
    }

    function applyOptimisticUpdate(el) {
      var tap = el.getAttribute("data-tap");
      if (tap === "dismiss") {
        overlay.innerHTML = "";
        overlayWrap.className = "";
      } else if (tap === "check") {
        el.style.opacity = "0.55";
      }
    }

    document.addEventListener("click", function(e) {
      var el = e.target;
      while (el && el !== document.body && !el.getAttribute("data-tap")) el = el.parentNode;
      if (!el || el === document.body) return;
      postEvent(el.getAttribute("data-tap"), el.getAttribute("data-target"), el.getAttribute("data-value"));
      applyOptimisticUpdate(el);
    });

    /* Client-side clock tick. Templates with [data-clock] get updated once per second. */
    function startClocksIn(root) {
      var elements = root.querySelectorAll ? root.querySelectorAll("[data-clock]") : [];
      for (var i = 0; i < elements.length; i++) {
        tickClock(elements[i]);
      }
    }
    function tickClock(host) {
      var format = host.getAttribute("data-clock") || "24h";
      function render() {
        var now = new Date();
        var hh = now.getHours();
        var mm = now.getMinutes();
        var ampm = "";
        if (format === "12h") {
          ampm = hh >= 12 ? " PM" : " AM";
          hh = hh % 12; if (hh === 0) hh = 12;
        }
        var t = (hh < 10 ? "0" : "") + hh + ":" + (mm < 10 ? "0" : "") + mm + ampm;
        var d = now.toDateString();
        var te = host.querySelector("[data-clock-time]");
        var de = host.querySelector("[data-clock-date]");
        if (te) te.innerHTML = t;
        if (de) de.innerHTML = d;
      }
      render();
      setInterval(render, 1000);
    }

    /* Bootstrap: post capabilities, then open stream. */
    xhrPost(API + "/capabilities", detectCaps(), function(status) {
      if (status >= 200 && status < 300) connectStream();
      else disconnected.innerHTML = "setup failed";
    });
  })();
  </script>
</body>
</html>
```

- [ ] **Step 3: Ensure static files copy to publish output**

The Web SDK serves `wwwroot/` by default but only via `app.UseStaticFiles()`. Add to `Program.cs` (after `var app = builder.Build();`):

```csharp
app.UseStaticFiles(new StaticFileOptions
{
  RequestPath = "/ruutu/static"
});
```

This serves `wwwroot/shell.css` at `/ruutu/static/shell.css` (matching the `<link>` href).

- [ ] **Step 4: Commit**

```bash
git add src/toimi.tools.ruutu/wwwroot/ src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add ES5 display shell (shell.html, shell.css) and static-file mount"
```

---

### Task 17: DisplayApiController — GET /ruutu/<identifier>

Serves the shell with the identifier interpolated.

**Files:**
- Create: `src/toimi.tools.ruutu/Transport/DisplayApiController.cs`
- Modify: `src/toimi.tools.ruutu/Program.cs`

- [ ] **Step 1: DisplayApiController skeleton**

Write `src/toimi.tools.ruutu/Transport/DisplayApiController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Transport;

[ApiController]
[Route("ruutu")]
public class DisplayApiController(DisplayRepository displays, IWebHostEnvironment env) : ControllerBase
{
  [HttpGet("{identifier}")]
  public async Task<IActionResult> GetShell(string identifier, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null)
      return Content(NotConfiguredPage(identifier), "text/html");

    var shellPath = Path.Combine(env.WebRootPath, "shell.html");
    var html = await System.IO.File.ReadAllTextAsync(shellPath, ct);
    html = html.Replace("__IDENTIFIER__", identifier);
    return Content(html, "text/html");
  }

  private static string NotConfiguredPage(string identifier) =>
    $$"""
    <!DOCTYPE html><html><head><meta charset="utf-8"><title>not configured</title>
    <style>body{font-family:-apple-system,system-ui,sans-serif;background:#f5f3ef;padding:40px;text-align:center;color:#444}</style>
    </head><body>
      <h1>Display '{{identifier}}' is not configured.</h1>
      <p>Ask Toimi to register this display, then refresh this page.</p>
    </body></html>
    """;
}
```

- [ ] **Step 2: Register controllers in Program.cs**

In `src/toimi.tools.ruutu/Program.cs`, before the `AddMcpServer` call:

```csharp
builder.Services.AddControllers();
```

After `var app = builder.Build();`:

```csharp
app.MapControllers();
```

- [ ] **Step 3: Smoke test**

Run app. In a separate terminal:

```bash
curl -i http://localhost:8081/ruutu/kitchen
```

Expected: 200 OK with the "not configured" HTML (kitchen not registered yet).

Then register a row manually:
```bash
psql -h localhost -U postgres -d ruutu -c \
  "INSERT INTO displays (identifier, overlay_stack, created_at) VALUES ('kitchen', '[]', now());"
curl -i http://localhost:8081/ruutu/kitchen
```

Expected: 200 OK with the shell HTML; the `__IDENTIFIER__` placeholder should be replaced with "kitchen". Stop dotnet.

- [ ] **Step 4: Commit**

```bash
git add src/toimi.tools.ruutu/Transport/DisplayApiController.cs src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): serve display shell at GET /ruutu/<identifier>"
```

---

### Task 18: POST /ruutu/api/displays/<id>/capabilities

**Files:**
- Modify: `src/toimi.tools.ruutu/Transport/DisplayApiController.cs`

- [ ] **Step 1: Add capability DTO + endpoint**

Append to `DisplayApiController.cs` (inside the class):

```csharp
public record CapabilitiesRequest(
  bool Flexbox, bool CssGrid, bool Fetch, bool Promise,
  int Viewport_Width, int Viewport_Height, string User_Agent);

[HttpPost("api/displays/{identifier}/capabilities")]
public async Task<IActionResult> PostCapabilities(
  string identifier, [FromBody] CapabilitiesRequest req, CancellationToken ct)
{
  var display = await displays.GetAsync(identifier, ct);
  if (display is null) return NotFound();

  var payload = new toimi.tools.ruutu.Rendering.CapabilityPayload(
    req.Flexbox, req.CssGrid, req.Fetch, req.Promise,
    req.Viewport_Width, req.Viewport_Height, req.User_Agent ?? "");

  // Validate payload basics; fall back to legacy on garbage.
  string tier;
  string orientation;
  try
  {
    tier = toimi.tools.ruutu.Rendering.CapabilityClassifier.Classify(payload);
    orientation = toimi.tools.ruutu.Rendering.CapabilityClassifier.DeriveOrientation(
      payload.ViewportWidth, payload.ViewportHeight);
  }
  catch
  {
    tier = "legacy";
    orientation = "landscape";
  }

  await displays.RecordCapabilitiesAsync(
    identifier, tier, payload.UserAgent,
    payload.ViewportWidth, payload.ViewportHeight, orientation, ct);
  return Ok();
}
```

Note: the JSON property naming relies on ASP.NET Core's default camel-case binding mapping `viewport_width` → `Viewport_Width`. If snake_case JSON binding isn't working out of the box, add `[JsonPropertyName("viewport_width")]` decorators on the record fields (using `System.Text.Json.Serialization`). Simpler approach: instruct the shell.js to send `viewportWidth`/`viewportHeight`/`userAgent` instead. **Apply this fix here:** change shell.html's `detectCaps()` to emit camelCase keys (`viewportWidth`, `viewportHeight`, `userAgent`) and rename the DTO fields to `ViewportWidth`, `ViewportHeight`, `UserAgent`. Adjust the DTO above accordingly. (The spec uses snake_case in examples; this is a server-internal binding choice.)

- [ ] **Step 2: Apply the camelCase fix consistently**

In `src/toimi.tools.ruutu/wwwroot/shell.html` `detectCaps()`, rename the returned keys:

```js
return {
  flexbox: testStyle("display","flex"),
  cssGrid: !!(window.CSS && window.CSS.supports && window.CSS.supports("display","grid")),
  fetch:   typeof window.fetch === "function",
  promise: typeof window.Promise === "function",
  viewportWidth: window.innerWidth || document.documentElement.clientWidth,
  viewportHeight: window.innerHeight || document.documentElement.clientHeight,
  userAgent: navigator.userAgent
};
```

And in the controller:

```csharp
public record CapabilitiesRequest(
  bool Flexbox, bool CssGrid, bool Fetch, bool Promise,
  int ViewportWidth, int ViewportHeight, string UserAgent);
```

(Reuse `ViewportWidth`/`ViewportHeight`/`UserAgent` throughout the endpoint body.)

- [ ] **Step 3: Smoke test**

Run app. Register a kitchen display row if not already (see Task 17 Step 3). Then:

```bash
curl -s -X POST http://localhost:8081/ruutu/api/displays/kitchen/capabilities \
  -H "Content-Type: application/json" \
  -d '{"flexbox":true,"cssGrid":true,"fetch":true,"promise":true,"viewportWidth":1024,"viewportHeight":768,"userAgent":"Test"}'
psql -h localhost -U postgres -d ruutu -c \
  "SELECT tier, viewport_width, orientation FROM displays WHERE identifier='kitchen';"
```

Expected: tier=modern, viewport_width=1024, orientation=landscape.

- [ ] **Step 4: Commit**

```bash
git add src/toimi.tools.ruutu/Transport/DisplayApiController.cs src/toimi.tools.ruutu/wwwroot/shell.html
git commit -m "feat(ruutu): record display capabilities + classify tier on first connect"
```

---

### Task 19: SseHub + GET /ruutu/api/displays/<id>/stream

Per-display SSE channel held in memory. ContentPushService writes to it; the stream endpoint reads from it and writes SSE frames.

**Files:**
- Create: `src/toimi.tools.ruutu/Transport/SseEvent.cs`
- Create: `src/toimi.tools.ruutu/Transport/SseHub.cs`
- Modify: `src/toimi.tools.ruutu/Transport/DisplayApiController.cs`
- Modify: `src/toimi.tools.ruutu/Program.cs`

- [ ] **Step 1: SseEvent record**

Write `src/toimi.tools.ruutu/Transport/SseEvent.cs`:

```csharp
namespace toimi.tools.ruutu.Transport;

public record SseEvent(string EventType, string JsonPayload);
```

- [ ] **Step 2: SseHub**

Write `src/toimi.tools.ruutu/Transport/SseHub.cs`:

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace toimi.tools.ruutu.Transport;

public class SseHub
{
  private readonly ConcurrentDictionary<string, Channel<SseEvent>> _channels = new();

  public Channel<SseEvent> Subscribe(string identifier)
  {
    var newChan = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(64)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false
    });
    return _channels.AddOrUpdate(identifier, newChan, (_, existing) =>
    {
      existing.Writer.TryComplete();
      return newChan;
    });
  }

  public void Unsubscribe(string identifier, Channel<SseEvent> channel)
  {
    if (_channels.TryGetValue(identifier, out var current) && current == channel)
    {
      _channels.TryRemove(identifier, out _);
      channel.Writer.TryComplete();
    }
  }

  public async Task<bool> PublishAsync(string identifier, SseEvent ev, CancellationToken ct = default)
  {
    if (!_channels.TryGetValue(identifier, out var ch)) return false;
    await ch.Writer.WriteAsync(ev, ct);
    return true;
  }

  public bool HasSubscriber(string identifier) => _channels.ContainsKey(identifier);
}
```

- [ ] **Step 3: GET /stream endpoint**

Append to `DisplayApiController.cs`:

```csharp
[HttpGet("api/displays/{identifier}/stream")]
public async Task StreamAsync(
  string identifier,
  [FromServices] SseHub hub,
  [FromServices] DisplayRepository displaysRepo,
  [FromServices] toimi.tools.ruutu.Transport.ContentPushService pusher,
  CancellationToken ct)
{
  var display = await displaysRepo.GetAsync(identifier, ct);
  if (display is null) { Response.StatusCode = 404; return; }

  Response.Headers["Content-Type"] = "text/event-stream";
  Response.Headers["Cache-Control"] = "no-cache";
  Response.Headers["X-Accel-Buffering"] = "no";

  var channel = hub.Subscribe(identifier);

  // Replay current scene + top overlay (if any) on (re)connect.
  try
  {
    await pusher.ReplayCurrentStateAsync(identifier, ct);
  }
  catch (Exception ex)
  {
    await WriteSseAsync("error", $"{{\"message\":\"{ex.Message}\"}}", ct);
  }

  // Heartbeat task
  var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
  var heartbeatTask = Task.Run(async () =>
  {
    while (!heartbeatCts.IsCancellationRequested)
    {
      await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
      await hub.PublishAsync(identifier, new SseEvent("heartbeat", "{}"), heartbeatCts.Token);
      await displaysRepo.UpdateLastSeenAsync(identifier, heartbeatCts.Token);
    }
  }, heartbeatCts.Token);

  try
  {
    await foreach (var ev in channel.Reader.ReadAllAsync(ct))
    {
      await WriteSseAsync(ev.EventType, ev.JsonPayload, ct);
    }
  }
  catch (OperationCanceledException) { }
  finally
  {
    heartbeatCts.Cancel();
    hub.Unsubscribe(identifier, channel);
  }
}

private async Task WriteSseAsync(string type, string json, CancellationToken ct)
{
  await Response.WriteAsync($"event: {type}\n", ct);
  await Response.WriteAsync($"data: {json}\n\n", ct);
  await Response.Body.FlushAsync(ct);
}
```

- [ ] **Step 4: Register SseHub singleton in Program.cs**

In `src/toimi.tools.ruutu/Program.cs`, near the other DI registrations:

```csharp
builder.Services.AddSingleton<SseHub>();
builder.Services.AddScoped<ContentPushService>();
```

(`ContentPushService` is defined in Task 21; this DI registration stays valid because the controller will only resolve it once Task 21 lands. To keep this task green before Task 21, you can temporarily comment the `AddScoped<ContentPushService>()` line — re-enable in Task 21.)

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Transport/SseEvent.cs src/toimi.tools.ruutu/Transport/SseHub.cs src/toimi.tools.ruutu/Transport/DisplayApiController.cs src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add SseHub and GET /stream SSE endpoint with heartbeat"
```

---

### Task 20: POST /ruutu/api/displays/<id>/events + dismiss flow

**Files:**
- Modify: `src/toimi.tools.ruutu/Transport/DisplayApiController.cs`

- [ ] **Step 1: Add events endpoint**

Append to `DisplayApiController.cs`:

```csharp
public record EventRequest(string Type, string? Target, object? Value);

[HttpPost("api/displays/{identifier}/events")]
public async Task<IActionResult> PostEvent(
  string identifier,
  [FromBody] EventRequest req,
  [FromServices] DisplayEventRepository events,
  [FromServices] toimi.tools.ruutu.Transport.ContentPushService pusher,
  CancellationToken ct)
{
  var display = await displays.GetAsync(identifier, ct);
  if (display is null) return NotFound();

  await displays.UpdateLastSeenAsync(identifier, ct);

  var valueJson = req.Value is null ? null
    : System.Text.Json.JsonSerializer.Serialize(req.Value);

  await events.AppendAsync(display.Id, req.Type, req.Target, valueJson, ct);

  if (req.Type == "dismiss" && req.Target == "overlay")
  {
    await pusher.DismissTopOverlayAsync(identifier, ct);
  }

  return Ok();
}
```

(The `ContentPushService.DismissTopOverlayAsync` method is defined in Task 21.)

- [ ] **Step 2: Smoke test (deferred)**

Tap-back smoke is best run end-to-end after Task 21 + Task 22. For now ensure build succeeds:

```bash
dotnet build src/toimi.tools.ruutu/
```

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Transport/DisplayApiController.cs
git commit -m "feat(ruutu): accept tap-back events; dismiss-overlay pops stack and surfaces next"
```

---

### Task 21: ContentPushService — render, push, persist

This is the layer MCP tools call. It wraps repositories + renderer + hub so tools stay thin. It also handles replay on (re)connect and the dismiss-pop-replace flow.

**Files:**
- Create: `src/toimi.tools.ruutu/Transport/ContentPushService.cs`
- Create: `src/toimi.tools.ruutu/Rendering/DbTemplateSource.cs`

- [ ] **Step 1: DbTemplateSource — bridges TemplateRepository to IRenderTemplateSource**

Write `src/toimi.tools.ruutu/Rendering/DbTemplateSource.cs`:

```csharp
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Rendering;

public class DbTemplateSource(TemplateRepository templates) : IRenderTemplateSource
{
  public bool TryGet(string name, out string modernHtml, out string legacyHtml)
  {
    // Synchronous bridge: repository methods are async, but ScribanRenderer
    // is synchronous. Block here intentionally — calls happen at push time,
    // not in a hot loop, and the row is small.
    var t = templates.GetAsync(name).GetAwaiter().GetResult();
    if (t is null) { modernHtml = legacyHtml = ""; return false; }
    modernHtml = t.ModernHtml ?? "";
    legacyHtml = t.LegacyHtml ?? "";
    return true;
  }
}
```

(If blocking is awkward, a memoized in-memory map of templates loaded on startup is a future optimization.)

- [ ] **Step 2: ContentPushService**

Write `src/toimi.tools.ruutu/Transport/ContentPushService.cs`:

```csharp
using System.Text.Json;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Transport;

public class ContentPushService(
  DisplayRepository displays,
  TemplateRepository templates,
  DisplayEventRepository events,
  Data.RuutuDbContext db,
  SseHub hub,
  ILogger<ContentPushService> logger)
{
  private readonly DbTemplateSource _source = new(templates);

  public async Task ShowSceneAsync(string identifier, string template, JsonElement data, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct)
      ?? throw new InvalidOperationException($"Display '{identifier}' not registered");

    var tier = display.Tier ?? "legacy";
    var html = ScribanRenderer.Render(template, data, tier, _source);

    display.CurrentTemplate = template;
    display.CurrentData = data.GetRawText();
    display.CurrentPushedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier,
      new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
  }

  public async Task ShowOverlayAsync(string identifier, string template, JsonElement data, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct)
      ?? throw new InvalidOperationException($"Display '{identifier}' not registered");

    var tier = display.Tier ?? "legacy";
    var html = ScribanRenderer.Render(template, data, tier, _source);

    var stack = OverlayStack.Parse(display.OverlayStack);
    var (next, evicted) = OverlayStack.Push(stack,
      new OverlayFrame(template, data.GetRawText(), DateTimeOffset.UtcNow));
    display.OverlayStack = OverlayStack.Serialize(next);

    if (evicted is not null)
    {
      var droppedPayload = JsonSerializer.Serialize(new
      {
        evicted.Template,
        data = JsonDocument.Parse(evicted.DataJson).RootElement
      });
      await events.AppendAsync(display.Id, "overlay_dropped", null, droppedPayload, ct);
    }
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier,
      new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
  }

  public async Task DismissTopOverlayAsync(string identifier, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null) return;

    var stack = OverlayStack.Parse(display.OverlayStack);
    var (next, newTop) = OverlayStack.Pop(stack);
    display.OverlayStack = OverlayStack.Serialize(next);
    await db.SaveChangesAsync(ct);

    if (newTop is null)
    {
      await hub.PublishAsync(identifier, new SseEvent("overlay_clear", "{}"), ct);
    }
    else
    {
      var tier = display.Tier ?? "legacy";
      try
      {
        var html = ScribanRenderer.Render(newTop.Template,
          JsonDocument.Parse(newTop.DataJson).RootElement, tier, _source);
        await hub.PublishAsync(identifier,
          new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to render replacement overlay for '{Identifier}'", identifier);
        await hub.PublishAsync(identifier, new SseEvent("overlay_clear", "{}"), ct);
      }
    }
  }

  public async Task ClearAsync(string identifier, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null) return;

    display.CurrentTemplate = display.IdleTemplate;
    display.CurrentData = display.IdleData;
    display.CurrentPushedAt = DateTimeOffset.UtcNow;
    display.OverlayStack = "[]";
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier, new SseEvent("clear", "{}"), ct);
    await ReplayCurrentStateAsync(identifier, ct);
  }

  /// <summary>Sends the current scene + top overlay (if any) over SSE. Used on (re)connect.</summary>
  public async Task ReplayCurrentStateAsync(string identifier, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null) return;

    var tier = display.Tier ?? "legacy";
    var (template, dataJson) = (display.CurrentTemplate, display.CurrentData);
    if (template is null) (template, dataJson) = (display.IdleTemplate, display.IdleData);

    if (template is not null)
    {
      try
      {
        var data = dataJson is null ? JsonDocument.Parse("{}").RootElement
          : JsonDocument.Parse(dataJson).RootElement;
        var html = ScribanRenderer.Render(template, data, tier, _source);
        await hub.PublishAsync(identifier,
          new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to replay scene for '{Identifier}'", identifier);
      }
    }
    else
    {
      var splashData = JsonDocument.Parse($$"""{ "message": "{{identifier}}" }""").RootElement;
      var html = ScribanRenderer.Render("splash", splashData, tier, _source);
      await hub.PublishAsync(identifier,
        new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
    }

    var stack = OverlayStack.Parse(display.OverlayStack);
    if (stack.Length > 0)
    {
      try
      {
        var top = stack[0];
        var html = ScribanRenderer.Render(top.Template,
          JsonDocument.Parse(top.DataJson).RootElement, tier, _source);
        await hub.PublishAsync(identifier,
          new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to replay overlay for '{Identifier}'", identifier);
      }
    }
  }
}
```

- [ ] **Step 3: Re-enable DI registration**

In `src/toimi.tools.ruutu/Program.cs`, uncomment / ensure `builder.Services.AddScoped<ContentPushService>();` is present.

- [ ] **Step 4: Smoke test the full display flow**

Start the app. In a browser tab, open `http://localhost:8081/ruutu/kitchen`. The shell should load and show the default splash (via `ReplayCurrentStateAsync`).

Now push a scene manually via psql + raw HTTP — without MCP tools yet:
```bash
# Set the kitchen display to show a message via direct SQL + curl trick
# (We don't have MCP tools yet — Task 22 introduces them. Skip this step
#  if you just want to confirm the shell loads.)
```

Confirm the browser tab shows the splash with "kitchen" as the message. Stop the app.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Transport/ContentPushService.cs src/toimi.tools.ruutu/Rendering/DbTemplateSource.cs src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add ContentPushService (render+push+persist, dismiss-pop, replay-on-connect)"
```

---

## Phase 6: MCP tool surface

Each tool class follows the muistutin pattern (`ListRemindersTool.cs`): `[McpServerToolType]` on the class, `[McpServerTool]` + `[Description]` on each method, constructor-injected dependencies, returns a serialized JSON string or a plain string.

### Task 22: DisplayManagementTools

**Files:**
- Create: `src/toimi.tools.ruutu/Tools/DisplayManagementTools.cs`

- [ ] **Step 1: Implement the tools**

Write `src/toimi.tools.ruutu/Tools/DisplayManagementTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayManagementTools(DisplayRepository displays)
{
  [McpServerTool, Description("Register a display so it can connect. The identifier becomes part of the URL: http://<host>/ruutu/<identifier>. Optionally lock the capability tier to override auto-detection. Idempotent: re-registering returns the existing display.")]
  public async Task<string> DisplayRegister(
    [Description("URL-safe slug naming the display (e.g. 'kitchen', 'bedroom').")] string identifier,
    [Description("Optional tier override: 'modern' or 'legacy'. Omit to auto-detect.")] string? capabilityTierOverride = null)
  {
    if (capabilityTierOverride is not null and not "modern" and not "legacy")
      return "Error: capabilityTierOverride must be 'modern', 'legacy', or null.";

    var d = await displays.RegisterAsync(identifier, capabilityTierOverride);
    return JsonSerializer.Serialize(new { d.Identifier, d.Tier, d.TierOverride, url = $"/ruutu/{d.Identifier}" });
  }

  [McpServerTool, Description("Unregister a display. Removes the display record and any associated events. Pages opened on this display will fall back to a 'not configured' page.")]
  public async Task<string> DisplayUnregister(
    [Description("The display identifier to remove.")] string identifier)
  {
    var ok = await displays.UnregisterAsync(identifier);
    return ok ? "ok" : $"Display '{identifier}' not found.";
  }

  [McpServerTool, Description("List all registered displays with their current status. Online means the display sent a heartbeat or tap in the last 30 seconds.")]
  public async Task<string> DisplayList()
  {
    var list = await displays.ListAsync();
    var now = DateTimeOffset.UtcNow;
    var view = list.Select(d => new {
      d.Identifier,
      d.Tier,
      status = (d.LastSeenAt.HasValue && (now - d.LastSeenAt.Value) < TimeSpan.FromSeconds(30)) ? "online" : "offline",
      last_seen_at = d.LastSeenAt?.ToString("o"),
      current_template = d.CurrentTemplate,
      viewport_width = d.ViewportWidth,
      viewport_height = d.ViewportHeight,
      orientation = d.Orientation
    });
    return JsonSerializer.Serialize(view);
  }

  [McpServerTool, Description("Manually set the capability tier for a display, overriding auto-detection. Use when a display is mis-classified (e.g. a modern iPad shows up as legacy due to a privacy proxy stripping user-agent info).")]
  public async Task<string> DisplaySetTier(
    [Description("The display identifier.")] string identifier,
    [Description("Tier to apply: 'modern' or 'legacy'.")] string tier)
  {
    if (tier is not "modern" and not "legacy") return "Error: tier must be 'modern' or 'legacy'.";
    var ok = await displays.SetTierAsync(identifier, tier);
    return ok ? "ok" : $"Display '{identifier}' not found.";
  }
}
```

- [ ] **Step 2: Smoke-test by talking to the MCP endpoint manually**

Run the app. Then list tools:

```bash
curl -s http://localhost:8081/sse -N | head -20
```

Expected: an SSE-style listing that includes `display_register`, `display_list`, etc. (You may need an actual MCP client to validate properly; this raw curl just verifies the endpoint is alive.)

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Tools/DisplayManagementTools.cs
git commit -m "feat(ruutu): add DisplayManagement MCP tools (register/unregister/list/set_tier)"
```

---

### Task 23: DisplayContentTools

**Files:**
- Create: `src/toimi.tools.ruutu/Tools/DisplayContentTools.cs`

- [ ] **Step 1: Implement**

Write `src/toimi.tools.ruutu/Tools/DisplayContentTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Rendering;
using toimi.tools.ruutu.Transport;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayContentTools(ContentPushService pusher, ILogger<DisplayContentTools> logger)
{
  [McpServerTool, Description("Render a template with the given data and push it as the display's current scene. Replaces whatever was being shown. Use list_templates first to see what's available; create_template if you need a new shape.")]
  public async Task<string> DisplayShow(
    [Description("The display identifier.")] string identifier,
    [Description("Template name from display_list_templates.")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson)
  {
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowSceneAsync(identifier, template, data);
      return "ok";
    }
    catch (JsonException ex)
    {
      return $"Error: dataJson is not valid JSON: {ex.Message}";
    }
    catch (RenderException ex)
    {
      return $"Error rendering '{template}': {ex.Message}";
    }
    catch (InvalidOperationException ex)
    {
      return $"Error: {ex.Message}";
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "display_show failed");
      return $"Error: {ex.Message}";
    }
  }

  [McpServerTool, Description("Push a template as a temporary overlay on top of the current scene. Stays until the user taps it (no auto-clear). Newest overlay appears on top; tapping dismisses and reveals the next. Most commonly used with the 'notification' template.")]
  public async Task<string> DisplayOverlay(
    [Description("The display identifier.")] string identifier,
    [Description("Template name (any template works as an overlay).")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson)
  {
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowOverlayAsync(identifier, template, data);
      return "ok";
    }
    catch (JsonException ex) { return $"Error: dataJson is not valid JSON: {ex.Message}"; }
    catch (RenderException ex) { return $"Error rendering '{template}': {ex.Message}"; }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Reset the display: clear all overlays and return to the configured idle scene (or the Toimi splash if no idle is configured).")]
  public async Task<string> DisplayClear(
    [Description("The display identifier.")] string identifier)
  {
    try
    {
      await pusher.ClearAsync(identifier);
      return "ok";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
  }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/toimi.tools.ruutu/Tools/DisplayContentTools.cs
git commit -m "feat(ruutu): add DisplayContent MCP tools (show/overlay/clear)"
```

---

### Task 24: TemplateTools

**Files:**
- Create: `src/toimi.tools.ruutu/Tools/TemplateTools.cs`

- [ ] **Step 1: Implement**

Write `src/toimi.tools.ruutu/Tools/TemplateTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class TemplateTools(TemplateRepository templates, DbTemplateSource source)
{
  [McpServerTool, Description("List all available templates with their schemas. Read this at session start to know what shapes you can push to a display without writing HTML.")]
  public async Task<string> DisplayListTemplates()
  {
    var list = await templates.ListAsync();
    var view = list.Select(t => new {
      t.Name,
      t.Description,
      schema = JsonDocument.Parse(t.SchemaJson).RootElement,
      has_modern = !string.IsNullOrEmpty(t.ModernHtml),
      has_legacy = !string.IsNullOrEmpty(t.LegacyHtml),
      t.IsSeeded
    });
    return JsonSerializer.Serialize(view);
  }

  [McpServerTool, Description("Fetch the full definition of a single template including both modern_html and legacy_html variants. Useful when modifying an existing template.")]
  public async Task<string> DisplayGetTemplate(
    [Description("Template name.")] string name)
  {
    var t = await templates.GetAsync(name);
    if (t is null) return $"Template '{name}' not found.";
    return JsonSerializer.Serialize(new
    {
      t.Name, t.Description,
      schema = JsonDocument.Parse(t.SchemaJson).RootElement,
      modern_html = t.ModernHtml,
      legacy_html = t.LegacyHtml,
      t.IsSeeded
    });
  }

  [McpServerTool, Description($"""
    Create a new template. Both modern_html and legacy_html variants are required and are LINTED before saving.
    Templates are declarative HTML — no <script> tags. Use data-tap/data-target/data-value attributes for interactivity.
    Variables come from the data object via Scriban syntax: {{ "{{ name }}" }}, {{ "{{ for x in items }}…{{ end }}" }}.
    For composite layouts: any data field shaped {{ "{ template, data }" }} is auto-rendered and the result is exposed as {{ "{fieldname}_html" }} variable to the parent template.

    MODERN tier: Safari 14+/Chrome 90+ (≈2020+). Flexbox, grid, gap, vw/vh/rem/clamp/min/max, CSS variables, modern color syntax, WebP images, transitions, transforms allowed.
    LEGACY tier: iOS Safari 9-12 (iPad 2/3/4/Air 1). NO flexbox/grid (use tables/floats). NO var(--*). NO WebP. NO @import/@font-face. NO clamp/min/max CSS functions. NO :has()/:is()/:where(). Use system font stack only. Tune layouts for ~1024×768 either orientation.
    """)]
  public async Task<string> DisplayCreateTemplate(
    [Description("Template name (unique).")] string name,
    [Description("Short human-readable description of what the template shows. Other AI sessions read this when picking which template to use.")] string description,
    [Description("JSON Schema for the data parameter as a JSON string.")] string schemaJson,
    [Description("Scriban template for modern-tier displays.")] string modernHtml,
    [Description("Scriban template for legacy-tier displays.")] string legacyHtml)
  {
    var modernLint = TierLinter.Lint("modern", modernHtml);
    var legacyLint = TierLinter.Lint("legacy", legacyHtml);
    if (!modernLint.Valid || !legacyLint.Valid)
    {
      return JsonSerializer.Serialize(new
      {
        valid = false,
        modern_issues = modernLint.Issues,
        legacy_issues = legacyLint.Issues
      });
    }

    try { JsonDocument.Parse(schemaJson); }
    catch (JsonException ex) { return $"Error: schemaJson is not valid JSON: {ex.Message}"; }

    try
    {
      await templates.UpsertAiAsync(name, description, schemaJson, modernHtml, legacyHtml);
      return "ok";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Update an existing template. Cannot modify seeded templates. modernHtml and legacyHtml are optional — pass null to keep current value. Linted before save.")]
  public async Task<string> DisplayUpdateTemplate(
    [Description("Template name.")] string name,
    [Description("New description, or null to keep current.")] string? description = null,
    [Description("New schema JSON, or null to keep current.")] string? schemaJson = null,
    [Description("New modern_html, or null to keep current.")] string? modernHtml = null,
    [Description("New legacy_html, or null to keep current.")] string? legacyHtml = null)
  {
    var existing = await templates.GetAsync(name);
    if (existing is null) return $"Template '{name}' not found.";

    if (modernHtml is not null)
    {
      var r = TierLinter.Lint("modern", modernHtml);
      if (!r.Valid) return JsonSerializer.Serialize(new { valid = false, modern_issues = r.Issues });
    }
    if (legacyHtml is not null)
    {
      var r = TierLinter.Lint("legacy", legacyHtml);
      if (!r.Valid) return JsonSerializer.Serialize(new { valid = false, legacy_issues = r.Issues });
    }

    try
    {
      await templates.UpsertAiAsync(
        name,
        description ?? existing.Description,
        schemaJson ?? existing.SchemaJson,
        modernHtml ?? existing.ModernHtml,
        legacyHtml ?? existing.LegacyHtml);
      return "ok";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Delete a non-seeded template. Seeded templates cannot be deleted.")]
  public async Task<string> DisplayDeleteTemplate(
    [Description("Template name.")] string name)
  {
    try
    {
      var ok = await templates.DeleteAsync(name);
      return ok ? "ok" : $"Template '{name}' not found.";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Render a template+data combination without pushing it to a display. Returns the HTML string. Use to sanity-check a new template's output before saving it.")]
  public async Task<string> DisplayPreview(
    [Description("Template name.")] string template,
    [Description("Data JSON.")] string dataJson,
    [Description("Tier: 'modern' or 'legacy'.")] string tier)
  {
    if (tier is not "modern" and not "legacy") return "Error: tier must be 'modern' or 'legacy'.";
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      return ScribanRenderer.Render(template, data, tier, source);
    }
    catch (JsonException ex) { return $"Error: dataJson is not valid JSON: {ex.Message}"; }
    catch (RenderException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Return the full author brief for a capability tier: the rules and constraints to follow when authoring templates for it. Use if you need a refresher beyond the inline create-template description.")]
  public string DisplayGetTierBrief(
    [Description("Tier: 'modern' or 'legacy'.")] string tier)
  {
    return tier switch
    {
      "modern" => TierBriefs.MODERN,
      "legacy" => TierBriefs.LEGACY,
      _ => "Error: tier must be 'modern' or 'legacy'."
    };
  }
}
```

- [ ] **Step 2: Register DbTemplateSource for DI**

In `src/toimi.tools.ruutu/Program.cs`, add:

```csharp
builder.Services.AddScoped<toimi.tools.ruutu.Rendering.DbTemplateSource>();
```

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Tools/TemplateTools.cs src/toimi.tools.ruutu/Program.cs
git commit -m "feat(ruutu): add Template MCP tools (list/get/create/update/delete/preview/get_tier_brief)"
```

---

### Task 25: DisplayEventsTools

**Files:**
- Create: `src/toimi.tools.ruutu/Tools/DisplayEventsTools.cs`

- [ ] **Step 1: Implement**

Write `src/toimi.tools.ruutu/Tools/DisplayEventsTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayEventsTools(DisplayRepository displays, DisplayEventRepository events)
{
  [McpServerTool, Description("Return recent tap-back events from a display. Use when reacting to user interaction (e.g. user tapped a step in an in-progress routine). Events are append-only; the same event will be returned again if you don't advance the 'since' cursor.")]
  public async Task<string> DisplayGetEvents(
    [Description("The display identifier.")] string identifier,
    [Description("Optional ISO 8601 timestamp; only events strictly after this are returned. Pass the timestamp of the last event you previously processed.")] string? sinceUtc = null)
  {
    var d = await displays.GetAsync(identifier);
    if (d is null) return $"Display '{identifier}' not found.";

    DateTimeOffset? since = null;
    if (sinceUtc is not null)
    {
      if (!DateTimeOffset.TryParse(sinceUtc, out var parsed))
        return "Error: sinceUtc must be ISO 8601.";
      since = parsed;
    }

    var rows = await events.GetSinceAsync(d.Id, since);
    var view = rows.Select(e => new
    {
      type = e.EventType,
      target = e.Target,
      value = e.Value is null ? (object?)null : JsonDocument.Parse(e.Value).RootElement,
      timestamp = e.CreatedAt.ToString("o")
    });
    return JsonSerializer.Serialize(view);
  }
}
```

- [ ] **Step 2: Verify MCP tool discovery**

Run the app. The startup logs should show all 4 tool classes' methods being registered (via `WithToolsFromAssembly`). No code changes — `[McpServerToolType]` classes are auto-discovered.

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.ruutu/Tools/DisplayEventsTools.cs
git commit -m "feat(ruutu): add DisplayEvents MCP tool (get_events with since cursor)"
```

---

## Phase 7: Integration & smoke test

### Task 26: Wire ruutu into toimi.web

**Files:**
- Modify: `src/toimi.web/appsettings.json`

- [ ] **Step 1: Add ruutu McpServers entry**

In `src/toimi.web/appsettings.json`, add to the `McpServers` array (alphabetic position after `muistutin`, before `taidot`):

```json
{
  "Name": "ruutu",
  "Transport": "Http",
  "Url": "http://toimi-tools-ruutu.apps.svc.cluster.local/sse"
}
```

- [ ] **Step 2: Smoke test discovery (optional, requires cluster deploy)**

After deploying both pods, toimi.web's startup logs should show it connecting to ruutu's `/sse` and listing the ruutu tools alongside the other tool servers' tools.

- [ ] **Step 3: Commit**

```bash
git add src/toimi.web/appsettings.json
git commit -m "feat(ruutu): register ruutu as MCP server in toimi.web appsettings"
```

---

### Task 27: Seed `use-displays` skill in taidot

**Files:**
- Modify: `src/toimi.tools.taidot/Skills/SkillSeeder.cs`

- [ ] **Step 1: Append the entry to StandardSkills**

In `src/toimi.tools.taidot/Skills/SkillSeeder.cs`, append to the `StandardSkills` array:

```csharp
(
  "use-displays",
  "Push content to user-owned web displays (e.g. wall-mounted iPads, kitchen tablets) registered with ruutu.",
  """
  Displays are physical screens (often an old iPad or wall-mounted tablet) you can push content to.
  Workflow:
  1. List displays with DisplayList. Each has an identifier (e.g. 'kitchen'), a tier (modern/legacy, auto-detected), and an online/offline status.
  2. List available content shapes with DisplayListTemplates. Each template has a name, description, and JSON Schema for its data.
  3. Push the current scene with DisplayShow(identifier, template, dataJson). The data must match the template's schema.
  4. Push transient cards with DisplayOverlay(identifier, template, dataJson). Overlays stack LIFO; the user must tap to dismiss. The 'notification' template is the common choice.
  5. Reset to idle with DisplayClear(identifier).
  Composite scenes: layout templates split_horizontal, split_vertical, and stack accept nested {template, data} blocks; the renderer composes them automatically.
  Authoring new templates: DisplayCreateTemplate requires both modern_html AND legacy_html variants. Legacy tier targets iOS Safari 9 (no flex/grid, no CSS variables, no WebP, no @import/@font-face — use tables, floats, system fonts). Modern tier is permissive. The server lints both before saving; iterate until the linter passes.
  Tap-back: when a user taps a checkbox or dismisses an overlay, a tap event is recorded. Use DisplayGetEvents(identifier, sinceUtc) to pull them when relevant (e.g. during an in-progress routine to track progress). v1 does NOT auto-trigger sessions on taps — you must query.
  """,
  ["displays", "ruutu", "ui", "iPad", "templates"]
),
```

- [ ] **Step 2: Commit**

```bash
git add src/toimi.tools.taidot/Skills/SkillSeeder.cs
git commit -m "feat(ruutu): seed 'use-displays' skill in taidot teaching the AI how to drive ruutu"
```

---

### Task 28: End-to-end smoke test

This validates the full chain in a local kind cluster. The exact deploy steps depend on the engineer's environment; the assertions below are what to verify.

**Files:** none modified.

- [ ] **Step 1: Build and load images into the local kind cluster**

```bash
bash scripts/deploy.sh dev ruutu
```

Wait for the deployment to roll out:
```bash
kubectl rollout status deployment/toimi-tools-ruutu -n apps --timeout=120s
```
Expected: `successfully rolled out`.

- [ ] **Step 2: Confirm the templates seeded**

```bash
kubectl exec -n apps deploy/toimi-tools-ruutu -- \
  sh -c "psql \$ConnectionStrings__Ruutu -c 'SELECT name, is_seeded FROM templates ORDER BY name;'"
```

Expected: 11 seeded rows.

- [ ] **Step 3: Deploy toimi.web with the updated appsettings (so it picks up ruutu)**

```bash
bash scripts/deploy.sh dev toimi.web
```

In the toimi.web logs, look for a successful MCP connection to `http://toimi-tools-ruutu.apps.svc.cluster.local/sse` and the ruutu tools listed in tool discovery.

- [ ] **Step 4: Register a display via the chat UI**

Open the Toimi web UI in a browser. Tell Toimi: "Register a display called 'kitchen'".

Expected: AI invokes `display_register("kitchen")`. Then ask "List my displays" — should see kitchen.

- [ ] **Step 5: Open the display URL on a device (or a second browser tab)**

Visit `http://${TOIMI_HOST}/ruutu/kitchen` in a browser.

Expected: shell loads, splash appears with "kitchen" as the message. Browser dev tools should show:
- Successful POST to `/ruutu/api/displays/kitchen/capabilities` (200)
- Open SSE connection to `/ruutu/api/displays/kitchen/stream`
- One `scene` event delivered

- [ ] **Step 6: Push a clock scene from chat**

In chat: "Show a clock on the kitchen display."

Expected: AI calls `display_show("kitchen", "clock", "{}")`. The browser tab swaps from splash to the ticking clock.

- [ ] **Step 7: Push an overlay from chat**

In chat: "Send a notification to the kitchen that the laundry is done."

Expected: AI calls `display_overlay("kitchen", "notification", "{ \"title\": \"Laundry done\", \"body\": \"Take it out\", \"severity\": \"info\" }")`. The browser tab shows the notification overlaid on the clock. Tap the overlay → it dismisses; the clock is still ticking underneath.

- [ ] **Step 8: Verify tap event was recorded**

In chat: "What events has the kitchen display gotten lately?"

Expected: AI calls `display_get_events("kitchen")` and reports the dismiss event.

- [ ] **Step 9: Take notes on anything that broke**

Log issues encountered in a follow-up Task or PR comment. Common things to look out for:
- Snake_case binding in the capabilities POST (the spec used snake_case examples but the implementation switched to camelCase — Task 18 step 2)
- Static file routing for `/ruutu/static/shell.css`
- SSE buffering at any proxy in between
- Scriban templates referencing data fields the schema doesn't require (gracefully missing vs hard error)

- [ ] **Step 10: Final commit (if any cleanup needed)**

If you found and fixed issues during the smoke test, commit them now with a `fix(ruutu):` prefix. Otherwise, no final commit needed.

---

## Self-review notes

The plan covers:

| spec section | task(s) |
|---|---|
| Service surface — Display management | 22 |
| Service surface — Content | 21, 23 |
| Service surface — Templates | 24 |
| Service surface — Events | 20, 25 |
| Data model — displays | 5, 7 |
| Data model — templates | 5, 7 |
| Data model — display_events | 5, 7 |
| Templating engine: Scriban | 1, 11 |
| Display page — shell + capability detection | 16, 18 |
| Display page — SSE event types | 19, 21 |
| Dismiss flow | 20, 21 |
| Optimistic UI for taps | 16 |
| Idle behavior | 21 (ReplayCurrentStateAsync) |
| Capability tier definitions + briefs | 9, 10 |
| Linter | 10 |
| Seeded v1 templates (8 leaves + 3 layouts) | 13, 14, 15 |
| Overlay stack semantics (LIFO, cap 10, evict oldest) | 12, 21 |
| Failure modes — render error / unknown template / lint failure | 11, 21, 23, 24 |
| URL routing — pod owns /ruutu prefix | 17, 18, 19, 20 |
| /sse vs /ruutu/api/...stream | 17, 19 (separate endpoints, separately documented) |
| K8s deployment + ingress | 4 |
| Database creation | 3 |
| Web wiring | 26 |
| Skill seeding | 27 |
| End-to-end | 28 |

**Out of scope items from the spec** (deliberately not in plan):
- Live AI reaction to taps
- Slot-based partial scene updates
- Per-display auth tokens
- TLS strategy for non-LAN
- Template versioning
- Event retention sweep
- Free-form `display_set_layout`
- `question` template
- Multi-display broadcast
- Multiple tiers beyond modern/legacy

These match the spec's "Out of scope (phase 2 and beyond)" section.

---

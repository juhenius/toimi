# Tietue Phase 6 — Cutover (Retire muistio/taidot/muistutin/ajastin) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **⚠️ DESTRUCTIVE.** This plan DELETES four working tool servers (`muistio`, `taidot`, `muistutin`, `ajastin`) and their tests, Dockerfiles, k8s bases, and databases. It is the final consolidation step: `tietue` now functionally covers all four (Phases 2–4 seeded `memory`/`skill`/`reminder`/`schedule`). Toimi is pre-production, so there is no production data to migrate. Do not run this until you're satisfied `tietue` is the intended replacement. Authoring/reviewing this plan is safe; executing it removes the servers.

**Goal:** Realize the design-study consolidation — **6 stateful pods → `tietue` + `koti` + `verkko` (+ `ruutu`)** — by removing the four retired tool servers and rewiring every reference (solution, survivor Dockerfiles, k8s bases, DB creation, web + tietue agent `McpServers` lists, admin `Tools` list). The result builds, all surviving tests pass, the k8s base renders, and **no reference to the deleted servers remains anywhere**.

**Architecture:** Pure removal + rewiring — verified earlier that **no surviving project has a `ProjectReference` or code dependency** on the four servers (they're independent pods reached only via configured MCP URLs). So the cutover touches: `toimi.sln` (8 project entries), the survivor Dockerfiles' over-broad `COPY *.csproj` layer-cache lines, `k8s/base/kustomization.yaml` + the four `k8s/base/tools-<x>/` dirs, `scripts/dev-setup.sh` + `infrastructure/base/helm/postgresql-values.yaml` (DB creation), and the `McpServers`/`Admin:Tools` config in `src/toimi.web/appsettings.json` + `src/toimi.tools.tietue/appsettings.json`. The agent/skill injection already calls both `list_skills` and `list_types` and guards on null, so dropping the `taidot` `list_skills` provider degrades gracefully (type-catalog injection remains).

**Tech Stack:** .NET 10 solution surgery (`dotnet sln remove`, `git rm`), Kustomize, shell/YAML/JSON edits. Run dotnet inside the cached .NET 10 SDK Docker image. The repo enforces `dotnet format` (IDE0005/IDE0022/IDE0046/whitespace) as errors on the *surviving* C# projects — but this plan changes no C# source, so a build + the existing tietue lint-clean state should hold; still run `dotnet format --verify-no-changes` on tietue if you touch any of its files.

**Scope boundary (Phase 6 — final phase):**
- IN: delete the four servers + their test projects (dirs, Dockerfiles, sln entries); remove their k8s bases + kustomization entries; stop creating their databases (dev + server); trim the web `McpServers` + `Admin:Tools` and the tietue agent `McpServers` to the survivors (koti, ruutu, verkko, tietue); verify the solution builds, surviving tests pass, kustomize renders, and zero dangling references remain.
- OUT / DEFERRED (noted inline): **dropping the existing `muistio`/`muistutin`/`ajastin` Postgres databases** on a running cluster (a manual, irreversible ops step — this plan only stops *creating* them; it does not script `DROP DATABASE`); **migrating existing data** from the old servers into tietue (pre-prod: none to migrate); **standard-skill re-seeding** — `taidot`'s `SkillSeeder` (14 standard skills teaching the old verbs) is deleted with it; the tietue **type-catalog injection** (Phase 2) already teaches the AI the available types, and skills remain searchable as tietue `skill` entities, but **auto-injecting a skill summary into the system prompt** (the old `list_skills` path) is no longer wired — re-seeding standard skills as tietue `skill` entities and surfacing them in the prompt is a follow-up enhancement, not part of the structural cutover.

**Assumes Phases 1–5 are merged** (tietue is the full generic engine with semantic index, triggers/scheduler/handlers, message/agent handler, script sandbox, and seeded `memory`/`skill`/`reminder`/`schedule` types).

---

## Files removed / modified

**Removed (whole directories, via `git rm -r`):**
- `src/toimi.tools.muistio/`, `src/toimi.tools.muistio.Tests/`
- `src/toimi.tools.taidot/`, `src/toimi.tools.taidot.Tests/`
- `src/toimi.tools.muistutin/`, `src/toimi.tools.muistutin.Tests/`
- `src/toimi.tools.ajastin/`, `src/toimi.tools.ajastin.Tests/`
- `k8s/base/tools-muistio/`, `k8s/base/tools-taidot/`, `k8s/base/tools-muistutin/`, `k8s/base/tools-ajastin/`

**Modified:**
- `toimi.sln` — remove the 8 project entries
- `src/toimi.tools.koti/Dockerfile`, `src/toimi.tools.ruutu/Dockerfile`, `src/toimi.tools.verkko/Dockerfile`, `src/toimi.tools.tietue/Dockerfile`, `src/toimi.web/Dockerfile` — drop the `COPY ...csproj` lines for the four retired projects
- `k8s/base/kustomization.yaml` — remove the four `- tools-<x>` resources
- `scripts/dev-setup.sh` — drop `muistio muistutin ajastin` from the DB loop
- `infrastructure/base/helm/postgresql-values.yaml` — drop the three `CREATE DATABASE` lines
- `src/toimi.web/appsettings.json` — trim `McpServers` + `Admin:Tools`
- `src/toimi.tools.tietue/appsettings.json` — trim `Toimi:McpServers`

---

## Task 1: Remove the four servers + test projects from the solution and delete their directories

**Files:** `toimi.sln`; delete 8 directories.

- [ ] **Step 1: remove the project entries from the solution** (cleaner than hand-editing the `.sln` GUID blocks). Run inside the Docker SDK image:
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  for p in muistio muistio.Tests taidot taidot.Tests muistutin muistutin.Tests ajastin ajastin.Tests; do
    dotnet sln toimi.sln remove src/toimi.tools.$p/toimi.tools.$p.csproj;
  done'
```
Expected: 8 "Project ... removed" lines.

- [ ] **Step 2: delete the directories (tracked).**
```bash
git rm -r src/toimi.tools.muistio src/toimi.tools.muistio.Tests \
          src/toimi.tools.taidot src/toimi.tools.taidot.Tests \
          src/toimi.tools.muistutin src/toimi.tools.muistutin.Tests \
          src/toimi.tools.ajastin src/toimi.tools.ajastin.Tests
```

- [ ] **Step 3: confirm the solution no longer references them.**
```bash
grep -nE "toimi\.tools\.(muistio|taidot|muistutin|ajastin)" toimi.sln || echo "SLN_CLEAN"
```
Expected: `SLN_CLEAN`.

- [ ] **Step 4: confirm the solution still restores/builds its remaining projects** (the deleted projects are gone; the survivors don't reference them):
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build toimi.sln
```
Expected: `Build succeeded.` If a build error mentions a deleted project, a survivor has a stray reference — find and remove it (none expected; verified no `ProjectReference`s exist).
> Note: `toimi.web` builds its React client; if the solution build fails ONLY inside `toimi.web`'s client-asset step (npm) in this environment, build the individual surviving server projects instead (`dotnet build src/toimi.tools.tietue/...`, `koti`, `ruutu`, `verkko`, `toimi.core`) and note that web was skipped for environment reasons.

- [ ] **Step 5: commit.**
```bash
git add -A
git commit -m "chore(cutover): remove muistio/taidot/muistutin/ajastin projects from solution"
```

---

## Task 2: Drop the retired csproj from survivor Dockerfiles

The survivor Dockerfiles (`koti`, `ruutu`, `verkko`, `tietue`, `web`) each `COPY src/toimi.tools.<x>/<x>.csproj ...` for ALL projects (a layer-cache convention). With the four projects deleted, those `COPY` lines reference non-existent files and will break `docker build`.

**Files:** `src/toimi.tools.koti/Dockerfile`, `src/toimi.tools.ruutu/Dockerfile`, `src/toimi.tools.verkko/Dockerfile`, `src/toimi.tools.tietue/Dockerfile`, `src/toimi.web/Dockerfile`.

- [ ] **Step 1: in EACH of the five Dockerfiles, delete the four lines** that copy the retired csproj:
```
COPY src/toimi.tools.muistio/toimi.tools.muistio.csproj src/toimi.tools.muistio/
COPY src/toimi.tools.muistutin/toimi.tools.muistutin.csproj src/toimi.tools.muistutin/
COPY src/toimi.tools.taidot/toimi.tools.taidot.csproj src/toimi.tools.taidot/
COPY src/toimi.tools.ajastin/toimi.tools.ajastin.csproj src/toimi.tools.ajastin/
```
(Some Dockerfiles may not have all four — remove whichever of these four lines are present. Leave the `toimi.sln`, `toimi.core`, `toimi.notifications`, and survivor `COPY` lines intact.)

- [ ] **Step 2: confirm no Dockerfile references the retired projects.**
```bash
grep -rnE "toimi\.tools\.(muistio|taidot|muistutin|ajastin)" src/*/Dockerfile || echo "DOCKERFILES_CLEAN"
```
Expected: `DOCKERFILES_CLEAN`.

- [ ] **Step 3: (optional) confirm one survivor image still builds** — if Docker-in-Docker is available, `docker build -f src/toimi.tools.tietue/Dockerfile -t tietue-cutover-check .` from the repo root; else rely on the `grep` + the structure (the remaining `COPY` lines all point at existing files).

- [ ] **Step 4: commit.**
```bash
git add -A
git commit -m "chore(cutover): drop retired csproj COPY lines from survivor Dockerfiles"
```

---

## Task 3: Remove the k8s bases

**Files:** delete `k8s/base/tools-muistio/`, `tools-taidot/`, `tools-muistutin/`, `tools-ajastin/`; modify `k8s/base/kustomization.yaml`.

- [ ] **Step 1: delete the four base dirs.**
```bash
git rm -r k8s/base/tools-muistio k8s/base/tools-taidot k8s/base/tools-muistutin k8s/base/tools-ajastin
```

- [ ] **Step 2: remove the four resources from `k8s/base/kustomization.yaml`.** Delete these lines:
```yaml
  - tools-muistio
  - tools-muistutin
  - tools-taidot
  - tools-ajastin
```
The `resources:` list should end up as: `web`, `tools-koti`, `tools-ruutu`, `tools-verkko`, `tools-tietue`.

- [ ] **Step 3: confirm the base renders with only the survivors.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc "command -v kubectl >/dev/null && kubectl kustomize k8s/base >/dev/null && echo KUSTOMIZE_OK || echo NO_KUBECTL"
```
If `NO_KUBECTL`, instead confirm `k8s/base/kustomization.yaml` lists exactly the five surviving resources and that each referenced dir exists: `for d in web tools-koti tools-ruutu tools-verkko tools-tietue; do test -d k8s/base/$d && echo "ok $d" || echo "MISSING $d"; done`.

- [ ] **Step 4: confirm no k8s reference to the retired servers remains.**
```bash
grep -rnE "tools-(muistio|taidot|muistutin|ajastin)|toimi-tools-(muistio|taidot|muistutin|ajastin)" k8s/ || echo "K8S_CLEAN"
```
Expected: `K8S_CLEAN`.

- [ ] **Step 5: commit.**
```bash
git add -A
git commit -m "chore(cutover): remove muistio/taidot/muistutin/ajastin k8s bases"
```

---

## Task 4: Stop creating the retired databases

**Files:** `scripts/dev-setup.sh`, `infrastructure/base/helm/postgresql-values.yaml`.

- [ ] **Step 1: dev DB loop.** In `scripts/dev-setup.sh` (~line 131), change:
```bash
for DB_NAME in muistio muistutin ajastin toimi ruutu tietue; do
```
to:
```bash
for DB_NAME in toimi ruutu tietue; do
```
(`taidot` was never in this loop — it's Qdrant-only. Keep `toimi`, `ruutu`, `tietue`.)

- [ ] **Step 2: server DB creation.** In `infrastructure/base/helm/postgresql-values.yaml`, delete the three lines:
```sql
    CREATE DATABASE muistio;
    CREATE DATABASE muistutin;
    CREATE DATABASE ajastin;
```
(Leave `CREATE DATABASE tietue;` and any others — `toimi`/`ruutu` — intact.)

- [ ] **Step 3: confirm no DB-creation reference remains.**
```bash
grep -nE "muistio|muistutin|ajastin|taidot" scripts/dev-setup.sh infrastructure/base/helm/postgresql-values.yaml || echo "DB_CLEAN"
```
Expected: `DB_CLEAN`.
> NOTE (manual ops, not scripted here): on any cluster where the old databases already exist, drop them manually after cutover (`DROP DATABASE muistio;` etc.). This plan deliberately does NOT script destructive `DROP DATABASE` — there's no pre-prod data, and an accidental drop is irreversible.

- [ ] **Step 4: lint the shell + yaml** (the repo's `scripts/lint.sh` runs shellcheck + yamllint; run it if available, else visually confirm the edits are well-formed). Commit:
```bash
git add -A
git commit -m "chore(cutover): stop creating muistio/muistutin/ajastin databases"
```

---

## Task 5: Trim the MCP server + admin tool lists

**Files:** `src/toimi.web/appsettings.json`, `src/toimi.tools.tietue/appsettings.json`.

- [ ] **Step 1: web `appsettings.json`.** Remove the four retired entries from `Toimi:McpServers` (delete the `muistio`, `muistutin`, `taidot`, `ajastin` objects — keep `koti`, `ruutu`, `verkko`, `tietue`), and trim `Toimi:Admin:Tools` from:
```json
      "Tools": ["muistio", "muistutin", "ajastin", "taidot", "tietue"]
```
to:
```json
      "Tools": ["tietue"]
```
(`tietue` is the surviving server with an `/admin` surface; the four retired ones are gone, and koti/ruutu/verkko were never in the admin list.) Ensure the JSON remains valid (no trailing comma where the last array element was removed).

- [ ] **Step 2: tietue `appsettings.json`.** In the `Toimi:McpServers` array (used by the agent runner), remove the `muistio`, `muistutin`, and `taidot` entries (keep `koti`, `verkko`, `ruutu`, `tietue`). Ensure valid JSON.

- [ ] **Step 3: validate both JSON files + confirm no retired reference remains.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc "
  for f in src/toimi.web/appsettings.json src/toimi.tools.tietue/appsettings.json; do
    node -e \"JSON.parse(require('fs').readFileSync('\$f','utf8')); console.log('JSON_OK \$f')\" 2>/dev/null || dotnet run --project - <<< '' 2>/dev/null || echo 'NO_JSON_VALIDATOR \$f';
  done"
grep -rnE "muistio|muistutin|ajastin|taidot" src/toimi.web/appsettings.json src/toimi.tools.tietue/appsettings.json || echo "APPSETTINGS_CLEAN"
```
Expected: both files parse, and `APPSETTINGS_CLEAN`. (If no JSON validator is in the image, parse with a tiny C# one-liner or visually confirm; the `grep` clean result is the key check.)

- [ ] **Step 4: commit.**
```bash
git add -A
git commit -m "chore(cutover): trim McpServers and admin tools to the surviving servers"
```

---

## Task 6: Full verification — build, tests, and zero dangling references

**Files:** none (verification only)

- [ ] **Step 1: solution builds (survivors only).**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build toimi.sln 2>&1 | grep -E "Build succeeded|error" | head
```
Expected: `Build succeeded.` (If `toimi.web`'s npm client step fails for environment reasons, build each surviving server project individually and note web was skipped.)

- [ ] **Step 2: all surviving tests pass.**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  for t in ruutu web; do
    test -d src/toimi.tools.$t.Tests && dotnet test src/toimi.tools.$t.Tests/*.csproj 2>&1 | grep -E "Passed!|Failed!";
    test -d src/toimi.$t.Tests && dotnet test src/toimi.$t.Tests/*.csproj 2>&1 | grep -E "Passed!|Failed!";
  done'
```
Expected: tietue 108/108; any surviving `ruutu.Tests`/`web.Tests` still pass. (No test project should reference a deleted server — verified.)

- [ ] **Step 3: REPO-WIDE dangling-reference sweep (the key safety net).**
```bash
grep -rnE "toimi\.tools\.(muistio|taidot|muistutin|ajastin)|toimi-tools-(muistio|taidot|muistutin|ajastin)|tools-(muistio|taidot|muistutin|ajastin)" \
  --include='*.cs' --include='*.csproj' --include='*.sln' --include='*.json' --include='*.yaml' --include='*.yml' --include='Dockerfile' --include='*.sh' \
  . | grep -vE '/(bin|obj|node_modules)/' | grep -vE '^docs/' || echo "NO_DANGLING_REFERENCES"
```
Expected: `NO_DANGLING_REFERENCES` (docs/ — the design specs/plans — legitimately mention the retired servers; they're excluded). If anything else shows up, fix it.

- [ ] **Step 4: confirm the final pod set.** The surviving deployable pods are `tietue`, `koti`, `ruutu`, `verkko`, plus `toimi.web`:
```bash
ls -d src/toimi.tools.* | grep -v Tests
```
Expected: `koti`, `ruutu`, `tietue`, `verkko` only. The "6 stateful pods → tietue + koti + verkko (+ ruutu)" consolidation is realized.

- [ ] **Step 5: final commit if anything changed.**
```bash
git add -A && git commit -m "chore(cutover): tietue cutover complete" --allow-empty
```

---

## Phase 6 Done — the consolidation is complete

The four legacy stateful servers are gone. `tietue` is the single generic entity engine — typed entities with JSON-Schema validation, semantic search, triggers + a scheduler firing native/script/agent handlers, copy-down default triggers, and seeded `memory`/`skill`/`reminder`/`schedule` types — alongside the stateless `koti` (Home Assistant) and `verkko` (web/ntfy), plus `ruutu` and the `toimi.web` transport. The design study's north star (one product, one store, behaviors as composable handlers, runtime user-defined types) is realized.

**Manual ops follow-ups (NOT scripted here):**
- Drop the orphaned `muistio`/`muistutin`/`ajastin` databases on any live cluster (`DROP DATABASE ...`).
- Delete the old servers' Qdrant collections (`memories`, `skills`) if they should be reclaimed — tietue uses its own per-type collections.
- Remove the four servers' images from the registry.

**Deferred feature-continuity follow-up:**
- Re-seed the standard skills (formerly in `taidot`'s `SkillSeeder`) as tietue `skill` entities, and wire tietue skill-listing into the system-prompt injection (the old `list_skills` auto-injection is no longer present; the type-catalog injection remains and skills stay searchable via tietue `search`).

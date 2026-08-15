# Model tiers and delegation — fast/smart models with fresh-context subtasks

**Date:** 2026-08-15
**Status:** Designed (grilling session 2026-08-14/15); see the Models section
of `CONTEXT.md` for the vocabulary.

## 1. Motivation

Every LLM call in toimi — chat turns, scheduled agent runs, script
`extract()`, compaction — runs on the single `Toimi:OpenAI:Model`, a
flagship model. Most turns are cheap chit-chat ("thanks", "remind me
tomorrow") that a far cheaper model handles fine; paying flagship rates for
them is the dominant waste. The goal is **cost** (latency is a welcome side
effect): run everything on a cheap model by default, and let the agent
delegate genuinely hard work to a capable one.

A second, initially incidental win drove the mechanism choice: today a
fetched web page sits in the conversation transcript until compaction
eventually summarizes it away near the ~100k-token budget. Delegation into a
fresh context keeps bulk out of the main history entirely — only the
extracted answer comes back.

## 2. Decision summary

| Decision | Choice |
|---|---|
| Tiers | **fast model** (required) + **smart model** (optional); `OPENAI_MODEL_FAST` / `OPENAI_MODEL_SMART` in `toimi.env` |
| Old config | `OPENAI_MODEL` removed; `render-config.sh` fails loudly if it's still present |
| Smart unset | `smart` resolves to fast; the `delegate` tool description (composed at runtime) says so |
| Mechanism | **Delegation** into a fresh-context **subtask**, not handoff — the parent's history is never replayed on the smart model |
| Tool | One core built-in: `delegate(task, model: fast\|smart)`, default `fast`; not an MCP server tool |
| Subtask powers | Full agent run: standard bootstrap (system prompt + catalog injection), full MCP tool set |
| Nesting | Depth 2 — a subtask may delegate once more; a subtask's subtask may not |
| Defaults per call site | Chat turns and scheduled runs start fast with delegation; `extract()` always fast, no delegation; compaction always fast |
| Pinning | Optional `model: fast\|smart` on the `message` handler config, the seeded `schedule` type (copy-down), and `activate` |
| Limits | Subtasks reuse `AgentRunTimeoutSeconds` (300s); result truncated with a marker at ~8k tokens' worth |
| Persistence | Subtasks persist as their own conversation records, marked and linked to the parent |
| Accounting | Per-message concrete model attribution; per-tier price pairs (fast/smart × input/output) replace the flat pair |
| Untouched | Embeddings (`OpenAI:EmbeddingModel`, separate client), suoritin, all non-LLM pods |

## 3. Design

### Tiers and configuration

Two named tiers, defined in `CONTEXT.md`:

- **Fast model** — the default every agent turn runs on; chosen for cost;
  always configured.
- **Smart model** — the more capable model for harder work; optional. When
  unconfigured, the fast model stands in wherever the smart model is asked
  for, so delegation-for-isolation keeps working with a single model and
  configuring a smart model later changes nothing structurally.

`toimi.env` drops `OPENAI_MODEL` for an explicit pair:

```
OPENAI_MODEL_FAST=...            # required
OPENAI_MODEL_SMART=...           # optional
OPENAI_PRICE_FAST_INPUT_PER_1M=...
OPENAI_PRICE_FAST_OUTPUT_PER_1M=...
OPENAI_PRICE_SMART_INPUT_PER_1M=...
OPENAI_PRICE_SMART_OUTPUT_PER_1M=...
```

`render-config.sh` **fails loudly** if `OPENAI_MODEL` is still set, so an
un-migrated `toimi.env` can't silently misconfigure a deploy. The vars flow
through the existing pipeline (config.env → envsubst allowlist → the web and
tietue deployments) into `Toimi:OpenAI:FastModel` / `Toimi:OpenAI:SmartModel`
and the price options. `ILlmClientProvider.Create()` grows a tier parameter
(today it takes none and the model is fixed per pod at startup —
`OpenAiLlmClientProvider.cs`).

Prices are keyed **by tier**, not by model name: four numbers in the same
file two lines from the model names, which is as much staleness protection
as a price table would buy in a single-user system. The DB records the
concrete model name either way (see Accounting), so history stays honest
across model swaps.

### Delegation

Every `ToimiAgent` session offers one built-in tool alongside the MCP tools:

```
delegate(task: string, model: "fast" | "smart" = "fast") → result text
```

It lives in `toimi.core` (option over a tietue MCP tool: both hosts already
run `ToimiAgent`, the subtask inherits the host's MCP client set naturally,
tietue's surface stays about entities, and chat delegation doesn't depend on
tietue being up).

Calling it runs a **subtask**: a fresh `ToimiAgent` session on the requested
tier with

- the standard agent bootstrap — system prompt + catalog injection, same as
  a scheduled run (a subtask that can't see the type catalog can't use
  tietue competently);
- the delegator's brief as the incoming message — **never the parent's
  history**;
- the full MCP tool set.

The subtask's final text returns as the tool result. The tool description
teaches three habits — fast→smart (**escalation**: the task is beyond you),
smart→fast (**cheap chores**), same→same (**context isolation**: the raw
material would bloat your context; e.g. a bulky page fetch whose extract is
all that returns) — and instructs the delegator to write self-contained
briefs (the subtask sees nothing else) and to relay results verbatim where
appropriate (mitigates the weak-model-paraphrases-strong-answer failure
mode).

Handoff (replaying the conversation on the smart model) was rejected because
the whole history is sent with each completion request — a long chat would
bill flagship rates for mostly-irrelevant tokens. A pre-turn router was
rejected for paying a routing cost on every turn and misjudging without
conversation context.

**Nesting is capped at depth 2**: the mid-escalation offload is the
motivating case — a smart-model subtask doing real work needs a bulky fetch,
and the smart model's context is the most expensive one to pollute, so it
delegates the fetch to a fast subtask. A subtask's subtask cannot delegate
further; the depth cap bounds runaway cost.

When the smart model is unconfigured, `model: "smart"` resolves to fast (one
code path; the agent's "this needs more capability" decision stays recorded
and starts paying off the moment a smart model is configured). The runtime-
composed tool description then notes that smart currently resolves to the
same model — delegate for isolation, not capability — so the fast model
doesn't burn tokens on pointless escalation.

### Call-site defaults

| Site | Tier | Delegation? |
|---|---|---|
| Chat turn (toimi.web) | fast | yes |
| Scheduled agent run (`message` handler, `activate`) | fast unless pinned | yes |
| Subtask | as requested | yes if depth < 2 |
| Script `extract()` | always fast | no — it is deliberately the cost-ladder rung *below* an agent |
| Compaction/summarization | always fast | no |

Compaction today reuses the turn's chat client (`ConversationContext` is
handed the same `IChatClient`); "always fast" means the host passes a
separate fast-tier client for summarization — a real seam change, noted
deliberately.

### Pinning scheduled runs

A recurring hard task shouldn't burn a wasted fast attempt every run, and
the schedule author (you, or the agent self-scheduling via `set_trigger`)
knows in advance when a task is heavyweight. Both directions are allowed
(pin smart *or* pin fast):

- the `message` handler config gains optional `model: "fast" | "smart"`
  (default fast), vetted by its `ValidateConfig`;
- the seeded `schedule` type gets a matching optional `model` field that
  `TriggerProvisioner` copies down at create — the usual copy-down caveat
  applies: editing the field later doesn't reprovision; use `update_trigger`;
- `activate` gains the same optional parameter.

### Limits

Subtasks reuse `AgentRunTimeoutSeconds` (300s) — one knob, and a smart
subtask doing real work legitimately needs minutes. A chat turn blocks on
the delegate call while the subtask runs; the existing tool-call indicator
makes it visible.

The result is truncated with a marker at a generous cap (~8k tokens' worth):
the design exists to keep bulk out of the parent context, so the seam
enforces it rather than trusting the subtask not to return its whole haul.

### Persistence and accounting

- Subtasks persist as their own conversation records, marked as subtasks and
  linked to the parent conversation — "why did the assistant say this?"
  sometimes requires seeing inside a delegation, and it gives the admin view
  a place to show delegation frequency (the feature's own effectiveness
  metric). Persistence is host-provided (`ISubtaskStore`): the web host
  records subtask conversations; tietue has no connection to the `toimi`
  conversation DB, so subtasks spawned by scheduled runs keep the existing
  occurrence-event trail as their record (delegation itself works there
  unchanged).
- Every message records the **concrete model** that served it (new column;
  the model name travels through `TurnCompleted`).
- The admin usage view prices tokens by attributed model via the per-tier
  price pairs, replacing today's single flat `TokenPriceInputPer1M`/
  `TokenPriceOutputPer1M` pair — without attribution, a usage view can never
  show what tiering saved.
- Subtask tokens land on the model that actually consumed them, not on the
  parent.

## 4. Out of scope

- Embeddings — already a separate client and config key
  (`OpenAI:EmbeddingModel`); untouched.
- suoritin and all non-LLM pods.
- Handoff/stickiness semantics — moot under delegation.
- Nested delegation beyond depth 2.

## 5. Migration

The env rename is a breaking `toimi.env` edit on both machines (dev +
server): replace `OPENAI_MODEL` with `OPENAI_MODEL_FAST` (+ optionally
`OPENAI_MODEL_SMART` and the price pairs) **before** deploying, or web and
tietue fail their required-config check at boot. `render-config.sh`'s loud
failure on the old name is the guard.

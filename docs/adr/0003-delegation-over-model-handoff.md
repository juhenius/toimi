# Model tiering escalates by delegation into fresh-context subtasks, not handoff

Toimi runs two model tiers — a required **fast model** every agent turn
starts on and an optional **smart model** for harder work (cost is the
goal; ambiguity resolves fast). We decided the escalation mechanism is
**delegation**: the agent calls one core built-in tool,
`delegate(task, model: fast|smart)`, which runs a **subtask** — a fresh
`ToimiAgent` session on the requested tier that sees only the delegator's
self-contained brief (never the parent's history), has the standard
bootstrap and full MCP tool set, and returns its final text as the tool
result. Because every completion request resends the whole transcript,
replaying a long chat on the smart model would bill flagship rates for
mostly-irrelevant tokens; the brief is the only thing that crosses the
tier boundary. The same mechanism deliberately serves three habits —
fast→smart (escalation), smart→fast (cheap chores), and same→same
(context isolation: bulky raw material stays in the subtask, only the
extract returns). See `docs/specs/2026-08-15-model-tiers-delegation.md`
for the full design.

## Considered options

- **Handoff** (the fast model calls an `escalate` tool ending its turn;
  the transcript replays on the smart model, which writes the reply) —
  rejected: pays smart-model rates for the entire accumulated history on
  every escalated turn, and provides no context isolation or downward
  delegation. It was the initially recommended option; the token math of
  transcript-resend overturned it.
- **Pre-turn router** (a classifier routes each incoming message to a
  tier before the turn starts) — rejected: pays a routing cost on every
  turn and misjudges without conversation context; nobody is better placed
  to know a task is too hard than the model currently attempting it.
- **Delegation within the shared context** (smart model answers as a tool
  call but reads the same transcript) — rejected: same token bill as
  handoff, none of the isolation.

## Consequences

- **The weak model fronts the strong one.** The fast model relays the
  subtask's result and could paraphrase it badly; mitigated by tool-
  description instruction to relay verbatim where appropriate. Accepted as
  the price of never replaying history at smart rates.
- **Briefs are the contract.** A subtask sees nothing but its brief, so an
  under-specified brief produces a useless subtask; the tool description
  carries the "write self-contained briefs" instruction, and the depth-2
  cap (a subtask may delegate once; its subtask may not) bounds the cost
  of a bad chain.
- **Subtasks are first-class conversations.** Debugging "why did it say
  this?" and honest per-model cost accounting both require seeing inside a
  delegation, so subtask transcripts persist, linked to the parent, with
  per-message model attribution.
- **When no smart model is configured, `smart` resolves to fast** — the
  tool surface never changes shape; the runtime-composed description
  steers the agent toward isolation-only delegation until a smart model
  appears.

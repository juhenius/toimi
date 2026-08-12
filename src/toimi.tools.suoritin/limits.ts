// Shared result-size caps. worker.ts applies them best-effort inside the
// sandbox, but the script shares the worker global and can call
// self.postMessage directly — so executor.ts re-applies the same caps
// host-side on every received message.
// Counterpart: tietue re-clamps identical numbers on receipt — keep MAX_LOGS /
// MAX_LOG_CHARS equal to MaxLogEntries / MaxLogChars in
// src/toimi.tools.tietue/Scripts/SuoritinClient.cs (SuoritinIntegrationTests
// pins the log-entry agreement across the seam).
export const MAX_LOGS = 100;
export const MAX_LOG_CHARS = 2000;
export const MAX_ERROR_CHARS = 2000;

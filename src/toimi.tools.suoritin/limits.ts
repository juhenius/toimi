// Shared result-size caps. worker.ts applies them best-effort inside the
// sandbox, but the script shares the worker global and can call
// self.postMessage directly — so executor.ts re-applies the same caps
// host-side on every received message.
export const MAX_LOGS = 200;
export const MAX_LOG_CHARS = 2000;
export const MAX_ERROR_CHARS = 2000;

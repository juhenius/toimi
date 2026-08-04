# Build context = REPO ROOT per repo convention (only this dir is copied).
# Build: docker build -f src/toimi.tools.suoritin/Dockerfile -t <registry>/toimi-tools-suoritin:latest .
FROM denoland/deno:alpine-2.9.4
WORKDIR /app
# Copy the whole project dir: deno.json must be present at runtime (it carries
# "unstable": ["worker-options"], which gates Worker.deno.permissions).
COPY src/toimi.tools.suoritin/ .
# Fail-fast type-check at build time (worker.ts is loaded via a runtime URL
# string, outside main.ts's graph, so it must be listed explicitly). The check
# cache is discarded at runtime: DENO_DIR is set after this and /tmp is an
# emptyDir mount.
RUN deno check --frozen main.ts worker.ts
# Non-root, matching the other pods' convention (UID 1654).
RUN addgroup -S app && adduser -S -u 1654 -G app app
USER 1654
# readOnlyRootFilesystem: Deno's cache/tmp writes land on the /tmp emptyDir.
ENV DENO_DIR=/tmp/deno-cache
EXPOSE 8080
# --deny-import: defense-in-depth against remote CDN imports — worker-level
# permissions do not govern remote module imports; the host process's do.
ENTRYPOINT ["deno", "run", "--allow-net", "--allow-read=/app", "--deny-import", "main.ts"]

# syntax=docker/dockerfile:1
# game-service as a container image. Built from this directory:
#
#   docker build workloads/game-service
#
# The service runs from TypeScript through tsx, as `npm start` does, and node-pg-migrate loads the
# .ts migrations the same way, so the image keeps the development dependencies: tsx is one of them.
# `npm ci` needs every `file:` tarball the lock file names, including the two older engine releases
# the upgrade tests install; they are copied for the install and are never loaded at run time.
#
# The image carries no configuration of a deployment: storage, handoff and telemetry come from the
# environment (README.md, "Configuration"), and the handoff verification key from
# GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64 (docker-entrypoint.sh). The schema is migrated on
# start, as the service always does.

FROM node:24-bookworm-slim AS dependencies
WORKDIR /app
COPY package.json package-lock.json ./
COPY vendor/ vendor/
COPY tests/fixtures/pre-upgrade/vendor/ tests/fixtures/pre-upgrade/vendor/
COPY tests/fixtures/previous-release/vendor/ tests/fixtures/previous-release/vendor/
RUN npm ci --no-audit --no-fund

FROM node:24-bookworm-slim AS runtime
ENV GAME_SERVICE_HOST=0.0.0.0 \
    GAME_SERVICE_PORT=8080
WORKDIR /app
COPY --from=dependencies /app/node_modules/ node_modules/
COPY package.json tsconfig.json ./
COPY src/ src/
COPY migrations/ migrations/
COPY --chmod=0755 docker-entrypoint.sh /usr/local/bin/game-service-entrypoint
USER node
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/game-service-entrypoint"]

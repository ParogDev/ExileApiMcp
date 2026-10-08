# The ext-apps reference host (examples/basic-host) for testing MCP Apps against ExileApiMcp --http.
# See ../basic-host.ps1. Ports: 8080 host page, 8081 sandbox (separate origin), 3001 MCP proxy.
FROM node:24.21.0-alpine

ENV npm_config_ignore_scripts=true \
    npm_config_update_notifier=false \
    npm_config_fund=false \
    npm_config_audit=false

# ext-apps v2.0.1. The example's own package.json pulls the monorepo workspace; we replace it with
# ours (exact registry versions + lockfile) and keep only its source.
ARG EXT_APPS_SHA=a523ab30ee6ec27034aab38a30a51eaa921d5f6d
RUN apk add --no-cache git \
 && git clone -q --filter=blob:none https://github.com/modelcontextprotocol/ext-apps /tmp/ext-apps \
 && git -C /tmp/ext-apps checkout -q "$EXT_APPS_SHA" \
 && mkdir /host && cp -r /tmp/ext-apps/examples/basic-host/. /host/ \
 && rm -rf /tmp/ext-apps /host/package.json \
 && apk del git

WORKDIR /host
COPY package.json package-lock.json ./
RUN npm ci --no-progress \
 && INPUT=index.html npx vite build \
 && INPUT=sandbox.html npx vite build
COPY proxy.mjs ./

# basic-host connects (from the browser) to the proxy below. Set here, not with docker run -e:
# Windows PowerShell 5.1 strips the JSON quotes from native-command arguments.
ENV SERVERS='["http://localhost:3001/mcp"]'

# serve.ts only imports types besides express/cors, so Node's built-in type stripping runs it.
CMD ["sh", "-c", "node proxy.mjs & exec node serve.ts"]

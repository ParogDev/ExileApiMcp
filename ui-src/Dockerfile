# Build image for the MCP App UIs: keeps Node/npm off Windows. See build.ps1 and README.md.
#
# Mounts at run time:
#   /src               - this ui-src folder (read-write: dist/ and package-lock.json land here)
#   /src/node_modules  - named volume (exileapi-mcp-ui-node-modules), so node_modules never
#                        touches the Windows checkout
FROM node:24.21.0-alpine

# Supply-chain hygiene: no install scripts (nothing here needs them; native bits such as
# rolldown and lightningcss come as optional platform packages), no telemetry-ish noise.
ENV npm_config_ignore_scripts=true \
    npm_config_update_notifier=false \
    npm_config_fund=false \
    npm_config_audit=false

WORKDIR /src

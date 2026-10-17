#!/usr/bin/env bash
# Source this from the existing checkout; cloud tasks are already isolated.
export PATH="/workspace/.tools/dotnet:$PATH"
export DOTNET_CLI_HOME=/workspace/.tools/dotnet-home
export NUGET_PACKAGES=/workspace/.tools/nuget
export npm_config_cache=/workspace/.tools/npm
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1
export XDG_CACHE_HOME="${XDG_CACHE_HOME:-/workspace/.tools/xdg-cache}"
export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-/workspace/.tools/xdg-config}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-/workspace/.tools/xdg-data}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/workspace/.tools/runtime}"
if [[ ! -d "$XDG_RUNTIME_DIR" ]]; then mkdir -m 700 -p "$XDG_RUNTIME_DIR"; fi
if [[ -x /usr/bin/chromium ]]; then export CHROMIUM_PATH="${CHROMIUM_PATH:-/usr/bin/chromium}"; fi
if [[ -x /workspace/.tools/pwsh/pwsh ]]; then export PATH="/workspace/.tools/pwsh:$PATH"; fi

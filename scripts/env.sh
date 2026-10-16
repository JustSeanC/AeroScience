#!/usr/bin/env bash
# Source this from the existing checkout; cloud tasks are already isolated.
export PATH="/workspace/.tools/dotnet:$PATH"
export DOTNET_CLI_HOME=/workspace/.tools/dotnet-home
export NUGET_PACKAGES=/workspace/.tools/nuget
export npm_config_cache=/workspace/.tools/npm
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1

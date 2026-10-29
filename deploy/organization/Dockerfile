# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /source
ARG SOURCE_REVISION
# One build layer avoids VFS copies of the entire SDK for every source COPY. Context remains allowlisted.
RUN --mount=type=bind,source=.,target=/input,readonly \
    --mount=type=secret,id=proxy_ca \
    --mount=type=cache,target=/root/.nuget/packages \
    set -eu; \
    cp /input/global.json /input/Directory.Build.props /input/LICENSE /input/THIRD-PARTY-NOTICES.md ./; \
    mkdir -p src/AeroScience.Engine; \
    cp -a /input/src/AeroScience.Shared /input/src/AeroScience.Missions /input/src/AeroScience.Organization src/; \
    cp -a /input/src/AeroScience.Engine/wwwroot src/AeroScience.Engine/; \
    cp -a /input/licenses ./; \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    test "${#SOURCE_REVISION}" = 40; case "$SOURCE_REVISION" in *[!0-9a-f]*) exit 1;; esac; \
    dotnet restore src/AeroScience.Organization --locked-mode; \
    dotnet publish src/AeroScience.Organization -c Release --no-restore -o /published \
    -p:SourceRevisionId="$SOURCE_REVISION" -p:ContinuousIntegrationBuild=true -p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df
ARG SOURCE_REVISION
LABEL org.opencontainers.image.title="AeroScience Organization Server" \
      org.opencontainers.image.version="0.7.0-development" \
      org.opencontainers.image.revision="$SOURCE_REVISION" \
      org.opencontainers.image.source="https://github.com/JustSeanC/AeroScience"
WORKDIR /app
COPY --from=build --chown=1654:1654 /published/ ./
COPY --from=build --chown=1654:1654 /source/licenses/ ./licenses/
COPY --from=build --chown=1654:1654 /source/LICENSE /source/THIRD-PARTY-NOTICES.md ./
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8080
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 CMD ["dotnet", "AeroScience.Organization.dll", "--health-check"]
ENTRYPOINT ["dotnet", "AeroScience.Organization.dll"]

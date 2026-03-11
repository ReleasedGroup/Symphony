FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/Symphony.Host/Symphony.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG CODEX_NPM_VERSION=0.114.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends bash ca-certificates curl git nodejs npm openssh-client \
    && npm install -g @openai/codex@${CODEX_NPM_VERSION} \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --create-home --home-dir /home/symphony --shell /bin/bash symphony \
    && mkdir -p /config /var/lib/symphony/data /var/lib/symphony/workspaces \
    && chown -R symphony:symphony /config /var/lib/symphony /home/symphony

WORKDIR /app
COPY --from=build /app/publish ./
COPY deploy/container/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_EnableDiagnostics=0
ENV HOME=/home/symphony
ENV Persistence__ConnectionString Data Source=/var/lib/symphony/data/symphony.db;Cache=Shared;Mode=ReadWriteCreate
ENV SYMPHONY_WORKFLOW_PATH=/config/WORKFLOW.md

USER symphony

VOLUME ["/config", "/var/lib/symphony", "/home/symphony/.codex"]

EXPOSE 8080

ENTRYPOINT ["/entrypoint.sh"]

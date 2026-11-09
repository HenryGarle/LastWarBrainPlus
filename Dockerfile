# LastWar.Server with the web UI inside it: one image, one process.
#   docker build -t lastwar-server .        (from the LastWar folder)
# Runs as a non-root user on :8080. Everything it keeps lives in /srv/lastwar/data (mount it) and
# Postgres (connection string in the "pg" environment variable).

# 1. The web UI (Vite writes it into the server's wwwroot).
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. The server, published with that wwwroot.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=web /src/src/LastWar.Server/wwwroot src/LastWar.Server/wwwroot
RUN dotnet publish src/LastWar.Server/LastWar.Server.csproj -c Release -o /app

# 3. Runtime only.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    root=/srv/lastwar \
    proxy=true
# Npgsql probes Kerberos (GSSAPI) when it connects; without the library .NET stops the process.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /srv/lastwar/data && chown -R app:app /srv/lastwar
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "LastWar.Server.dll"]

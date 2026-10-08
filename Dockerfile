# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so that the package layer is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Forms.Core/Forms.Core.csproj src/Forms.Core/
COPY src/Forms.Api/Forms.Api.csproj src/Forms.Api/
RUN dotnet restore src/Forms.Api/Forms.Api.csproj

COPY src/ src/
RUN dotnet publish src/Forms.Api/Forms.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# ---- runtime ----
# Chiseled Ubuntu: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Logging__Console__FormatterName=json
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "Forms.Api.dll"]

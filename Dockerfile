FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore dependencies first (layer caching)
COPY TradeRadar.csproj .
RUN dotnet restore

# Copy source and config
COPY Program.cs .
COPY appsettings.json .
COPY appsettings.Development.json .

# Publish
RUN dotnet publish -c Release -o /app

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# PORT is injected by Railway at runtime — app reads it in Program.cs
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradeRadar.dll"]

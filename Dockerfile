FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies first (layer caching)
COPY TradeRadar.csproj .
RUN dotnet restore

# Copy source and config files
COPY Program.cs .
COPY appsettings.json .
COPY appsettings.Development.json .

# Publish release build
RUN dotnet publish -c Release -o /app

# ── Runtime image (smaller) ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Railway injects PORT at runtime — ASP.NET reads it via ASPNETCORE_URLS
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TradeRadar.dll"]

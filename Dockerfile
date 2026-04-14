FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LogWatcher.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create a volume mount point for the SQLite DB so it persists across restarts
VOLUME ["/app/data"]
ENV DOTNET_ENVIRONMENT=Production

COPY --from=build /app .

# Override DB path to use the volume
ENV AppSettings__Watcher__DbPath=/app/data/logwatcher.db

ENTRYPOINT ["dotnet", "LogWatcher.dll"]

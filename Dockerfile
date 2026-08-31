FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV DOTNET_GCServer=0
ENV DOTNET_EnableDiagnostics=0
ENV NOVASPARX_CACHE_DIR=/tmp/novasparx-cache
ENV NOVASPARX_MAX_VERTICES=320000
ENV NOVASPARX_MAX_INDICES=960000
ENV NOVASPARX_PARSE_CONCURRENCY=1
ENV NOVASPARX_ARCHIVE_REGISTER_CONCURRENCY=3
ENV NOVASPARX_ONDEMAND_TIMEOUT_SECONDS=80

EXPOSE 10000
ENTRYPOINT ["dotnet","NovaSparx.Backend.dll"]

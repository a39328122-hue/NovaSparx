FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore first so Docker can reuse the NuGet layer when only source files change.
COPY NovaSparx.Backend.csproj ./
RUN dotnet restore ./NovaSparx.Backend.csproj

COPY . .

RUN dotnet publish ./NovaSparx.Backend.csproj \
    -c Release \
    -o /out \
    --no-restore \
    -p:UseAppHost=true


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /out/ /app/
COPY start.sh /app/start.sh

RUN chmod +x /app/start.sh \
    && mkdir -p /tmp/novasparx-cache

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_GCServer=0
ENV DOTNET_EnableDiagnostics=0

ENV NOVASPARX_CACHE_DIR=/tmp/novasparx-cache
ENV NOVASPARX_MAX_VERTICES=320000
ENV NOVASPARX_MAX_INDICES=960000
ENV NOVASPARX_PARSE_CONCURRENCY=1
ENV NOVASPARX_TEXTURE_CONCURRENCY=1
ENV NOVASPARX_TEXTURE_MAX_SIZE=2048
ENV NOVASPARX_TEXTURE_MAX_BYTES=12582912
ENV NOVASPARX_ARCHIVE_REGISTER_CONCURRENCY=3
ENV NOVASPARX_ONDEMAND_TIMEOUT_SECONDS=80
ENV NOVASPARX_SKIP_REFERENCED_TEXTURES=false

EXPOSE 10000

ENTRYPOINT ["/app/start.sh"]

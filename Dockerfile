FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and src for restore caching (picks up new projects automatically)
COPY Connapse.slnx .
COPY src/ src/
RUN dotnet restore src/Connapse.Web/Connapse.Web.csproj

# Copy remaining files and publish
COPY . .
RUN dotnet publish src/Connapse.Web/Connapse.Web.csproj -c Release -o /app/publish --no-restore

# The publish is portable, so it carries native libraries for every platform its packages ship
# (ONNX Runtime, SkiaSharp, PDFium, libgit2): Windows, macOS, Android and iOS among them, ~700 MB.
# Only this image's own Linux architecture is ever loaded. TARGETARCH is set by BuildKit, per
# platform in a multi-platform build.
ARG TARGETARCH
RUN case "${TARGETARCH:-amd64}" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
    && find /app/publish/runtimes -mindepth 1 -maxdepth 1 -type d ! -name "$rid" -exec rm -rf {} +

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# MCP Registry ownership verification
LABEL io.modelcontextprotocol.server.name="io.github.Destrayon/connapse"

ENV ASPNETCORE_ENVIRONMENT=Production

# Install missing native lib needed by Npgsql's GSSAPI/Kerberos probe
# and create writable data directory owned by the non-root user
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/appdata \
    && chown app:app /app/appdata

# Run as non-root user for security
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "Connapse.Web.dll"]

# The same SDK global.json pins for CI's analysis (#786), so the shipped build is the one CI checked.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

# Copy solution and src for restore caching (picks up new projects automatically). The SDK pin and
# Directory.Build.props come first: the props add package references every project restores.
COPY global.json Directory.Build.props Connapse.slnx ./
COPY src/ src/
RUN dotnet restore src/Connapse.Web/Connapse.Web.csproj

# Copy remaining files and publish
COPY . .
# The version the server reports (/api/v1/info, MCP server info). MinVer normally reads it from git tags, but
# .git/ is not in the build context, so without this every image says 0.0.0-alpha.0 (CON-316). The release
# workflow passes the tag without its "v" (v0.4.1-alpha.2 -> 0.4.1-alpha.2). A local build may leave it empty.
ARG VERSION=""
# The OpenAPI document is committed at docs/api/openapi.json (#857); the image doesn't need it.
RUN dotnet publish src/Connapse.Web/Connapse.Web.csproj -c Release -o /app/publish --no-restore -p:OpenApiGenerateDocuments=false \
    ${VERSION:+-p:MinVerVersionOverride=$VERSION}
# A release build that doesn't carry its tag's version must not ship. deps.json records the version the
# assembly was stamped with, the same one the server reports.
RUN if [ -n "$VERSION" ] && ! grep -q "\"Connapse.Web/$VERSION\"" /app/publish/Connapse.Web.deps.json; then \
      echo "Built version is not $VERSION:" >&2; grep -o '"Connapse.Web/[^"]*"' /app/publish/Connapse.Web.deps.json >&2; exit 1; \
    fi

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

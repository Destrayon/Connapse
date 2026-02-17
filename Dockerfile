FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Connapse.slnx .
COPY src/Connapse.Core/Connapse.Core.csproj src/Connapse.Core/
COPY src/Connapse.Storage/Connapse.Storage.csproj src/Connapse.Storage/
COPY src/Connapse.Ingestion/Connapse.Ingestion.csproj src/Connapse.Ingestion/
COPY src/Connapse.Search/Connapse.Search.csproj src/Connapse.Search/
COPY src/Connapse.Agents/Connapse.Agents.csproj src/Connapse.Agents/
COPY src/Connapse.CLI/Connapse.CLI.csproj src/Connapse.CLI/
COPY src/Connapse.Web/Connapse.Web.csproj src/Connapse.Web/
RUN dotnet restore src/Connapse.Web/Connapse.Web.csproj

# Copy everything and publish
COPY . .
RUN dotnet publish src/Connapse.Web/Connapse.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Connapse.Web.dll"]

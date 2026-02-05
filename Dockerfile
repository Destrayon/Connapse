FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY AIKnowledgePlatform.slnx .
COPY src/AIKnowledge.Core/AIKnowledge.Core.csproj src/AIKnowledge.Core/
COPY src/AIKnowledge.Storage/AIKnowledge.Storage.csproj src/AIKnowledge.Storage/
COPY src/AIKnowledge.Ingestion/AIKnowledge.Ingestion.csproj src/AIKnowledge.Ingestion/
COPY src/AIKnowledge.Search/AIKnowledge.Search.csproj src/AIKnowledge.Search/
COPY src/AIKnowledge.Agents/AIKnowledge.Agents.csproj src/AIKnowledge.Agents/
COPY src/AIKnowledge.CLI/AIKnowledge.CLI.csproj src/AIKnowledge.CLI/
COPY src/AIKnowledge.Web/AIKnowledge.Web.csproj src/AIKnowledge.Web/
RUN dotnet restore src/AIKnowledge.Web/AIKnowledge.Web.csproj

# Copy everything and publish
COPY . .
RUN dotnet publish src/AIKnowledge.Web/AIKnowledge.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "AIKnowledge.Web.dll"]

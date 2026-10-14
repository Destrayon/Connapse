FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Connapse.CustomSource.Example.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore -p:MinVerSkip=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Connapse.CustomSource.Example.dll"]

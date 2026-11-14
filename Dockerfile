FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY Ecommerce.sln ./
COPY src/BuildingBlocks/BuildingBlocks.csproj src/BuildingBlocks/
COPY src/Ecommerce.Api/Ecommerce.Api.csproj src/Ecommerce.Api/
COPY tests/Ecommerce.UnitTests/Ecommerce.UnitTests.csproj tests/Ecommerce.UnitTests/
COPY tests/Ecommerce.IntegrationTests/Ecommerce.IntegrationTests.csproj tests/Ecommerce.IntegrationTests/
RUN dotnet restore Ecommerce.sln

COPY . .
RUN dotnet publish src/Ecommerce.Api/Ecommerce.Api.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ecommerce.Api.dll"]

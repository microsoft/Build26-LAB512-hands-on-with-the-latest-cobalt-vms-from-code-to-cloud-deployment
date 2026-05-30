FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/Identity.API/Identity.API.csproj src/Identity.API/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/

RUN dotnet restore src/Identity.API/Identity.API.csproj -a $TARGETARCH

COPY src/Identity.API/ src/Identity.API/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/
COPY src/Shared/ src/Shared/

WORKDIR /src/src/Identity.API
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Identity.API.dll"]

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/WebhookClient/WebhookClient.csproj src/WebhookClient/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/

RUN dotnet restore src/WebhookClient/WebhookClient.csproj -a $TARGETARCH

COPY src/WebhookClient/ src/WebhookClient/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/

WORKDIR /src/src/WebhookClient
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

# Blazor framework JS files are architecture-neutral but excluded from cross-arch publish.
RUN dotnet publish -c Release -o /tmp/blazor-assets -p:OpenApiGenerateDocumentsOnBuild=false \
    && cp -r /tmp/blazor-assets/wwwroot/_framework /app/publish/wwwroot/_framework

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WebhookClient.dll"]

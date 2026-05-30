FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/Webhooks.API/Webhooks.API.csproj src/Webhooks.API/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/EventBusRabbitMQ.csproj src/EventBusRabbitMQ/
COPY src/EventBus/EventBus.csproj src/EventBus/
COPY src/IntegrationEventLogEF/IntegrationEventLogEF.csproj src/IntegrationEventLogEF/

RUN dotnet restore src/Webhooks.API/Webhooks.API.csproj -a $TARGETARCH

COPY src/Webhooks.API/ src/Webhooks.API/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/ src/EventBusRabbitMQ/
COPY src/EventBus/ src/EventBus/
COPY src/IntegrationEventLogEF/ src/IntegrationEventLogEF/
COPY src/Shared/ src/Shared/

WORKDIR /src/src/Webhooks.API
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Webhooks.API.dll"]

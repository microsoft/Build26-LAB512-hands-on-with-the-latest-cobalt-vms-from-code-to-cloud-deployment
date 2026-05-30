FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*
ENV PROTOBUF_PROTOC=/usr/bin/protoc

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/WebApp/WebApp.csproj src/WebApp/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/EventBusRabbitMQ.csproj src/EventBusRabbitMQ/
COPY src/EventBus/EventBus.csproj src/EventBus/
COPY src/WebAppComponents/WebAppComponents.csproj src/WebAppComponents/

RUN dotnet restore src/WebApp/WebApp.csproj -a $TARGETARCH

COPY src/WebApp/ src/WebApp/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/ src/EventBusRabbitMQ/
COPY src/EventBus/ src/EventBus/
COPY src/WebAppComponents/ src/WebAppComponents/
COPY src/Basket.API/Proto/ src/Basket.API/Proto/
COPY src/Shared/ src/Shared/

WORKDIR /src/src/WebApp
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

# Blazor framework JS files are architecture-neutral but excluded from cross-arch publish.
# Generate them with a platform-neutral publish and merge.
RUN dotnet publish -c Release -o /tmp/blazor-assets -p:OpenApiGenerateDocumentsOnBuild=false \
    && cp -r /tmp/blazor-assets/wwwroot/_framework /app/publish/wwwroot/_framework

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WebApp.dll"]

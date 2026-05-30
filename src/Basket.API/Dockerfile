FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*
ENV PROTOBUF_PROTOC=/usr/bin/protoc

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/Basket.API/Basket.API.csproj src/Basket.API/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/EventBusRabbitMQ.csproj src/EventBusRabbitMQ/
COPY src/EventBus/EventBus.csproj src/EventBus/

RUN dotnet restore src/Basket.API/Basket.API.csproj -a $TARGETARCH

COPY src/Basket.API/ src/Basket.API/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/
COPY src/EventBusRabbitMQ/ src/EventBusRabbitMQ/
COPY src/EventBus/ src/EventBus/
COPY src/Shared/ src/Shared/

WORKDIR /src/src/Basket.API
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Basket.API.dll"]

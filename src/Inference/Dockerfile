ARG TARGETARCH
# Registry holding the pre-built models image (built first with Dockerfile.models).
# Required - pass --build-arg MODELS_IMAGE=<registry>/eshop/inference-models:<tag>
ARG MODELS_IMAGE
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY nuget.config ./
COPY src/Inference/Inference.csproj src/Inference/

RUN dotnet restore src/Inference/Inference.csproj -a $TARGETARCH

COPY src/Inference/ src/Inference/

WORKDIR /src/src/Inference
RUN dotnet publish -c Release -o /app/publish --no-restore -a $TARGETARCH -p:OpenApiGenerateDocumentsOnBuild=false

# Per-arch native overlay: linux-arm64 needs a custom-built libonnxruntime-genai.so
# (NuGet 0.13.2's package didn't ship one for ARM at the time). linux-amd64 uses
# the NuGet-supplied libonnxruntime-genai.so unchanged. Use a per-platform
# intermediate stage so the final stage's COPY is unconditional but only adds
# files on arm64.
FROM --platform=$BUILDPLATFORM busybox AS native-amd64
RUN mkdir -p /native

FROM --platform=$BUILDPLATFORM busybox AS native-arm64
RUN mkdir -p /native
COPY src/Inference/native/linux-arm64/libonnxruntime-genai.so /native/

FROM native-${TARGETARCH} AS native

# Pre-built models image from ACR (built once with Dockerfile.models)
FROM ${MODELS_IMAGE} AS models

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .
COPY --from=models /models /models
COPY --from=native /native/ /app/

ENTRYPOINT ["dotnet", "Inference.dll"]

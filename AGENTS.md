# AI Agent Guidelines

This repository contains **Microsoft Build 2026 LAB512** - a hands-on lab where attendees build, deploy, and run the .NET eShop application on Azure Cobalt 200 Arm64 VMs. The lab covers multi-arch container builds, AKS deployment, and on-CPU AI inference with ONNX Runtime.

## Repository structure

| Path | Purpose |
|:-----|:--------|
| `src/` | .NET eShop application source code and Dockerfiles |
| `deploy/k8s/base/` | Shared Kubernetes manifests (deployments, services, infra) |
| `deploy/k8s/overlays/local/` | Kustomize overlay for Docker Desktop Kubernetes |
| `deploy/k8s/overlays/aks/` | Kustomize overlay for AKS on Cobalt 200 |
| `docs/` | Self-paced lab guides (setup, parts 1-3, troubleshooting) |
| `models/` | Gitignored - ONNX model weights downloaded by `prepare-models.cmd` |
| `img/` | Screenshots and QR codes for documentation |

## Build and run

```cmd
REM Download ONNX models (required before building inference images)
prepare-models.cmd

REM Build all service images as multi-arch and push to ACR
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/<service>:latest -f src\<Service>\Dockerfile . --push

REM Deploy to local Docker Desktop Kubernetes
kubectl apply -k deploy\k8s\overlays\local

REM Deploy to AKS on Cobalt 200
kubectl apply -k deploy\k8s\overlays\aks
```

## Rules for AI agents

### Commands and syntax
- All lab commands use **cmd.exe** syntax, not PowerShell. Use `set VAR=value`, `%VAR%`, and `^` for line continuation.
- When editing docs in `docs/`, preserve cmd.exe syntax in all code blocks.

### Kubernetes manifests
- The `deploy/k8s/` tree uses Kustomize with a shared base and two overlays (local, aks). Changes to the base affect both environments.
- Overlay files patch only what differs between environments (ports, hostnames, identity URLs). Do not duplicate base content into overlays.
- The `inference` deployment is optional and activated separately in Part 3 of the lab.

### Container images
- All application images must build as multi-arch (`linux/amd64` + `linux/arm64`) except during the Part 1 x64-only webapp step (which is intentional for the multi-arch reveal in Part 2).
- Dockerfiles use `FROM --platform=$BUILDPLATFORM` for the SDK stage and `dotnet publish -a $TARGETARCH` to cross-compile without QEMU.
- Infrastructure images (Redis, RabbitMQ, Postgres, busybox) are imported from Docker Hub, not built from source.

### Models and large files
- The `models/` directory is gitignored. Never commit `.onnx` or `.onnx.data` files.
- Model weights are downloaded at build time via `prepare-models.cmd`, which calls `dotnet msbuild src\Inference\Inference.csproj -t:DownloadModels`.
- The `inference-models` image must be built before the `inference` image (it supplies the model files via a multi-stage copy).

### Documentation
- Self-paced guides in `docs/` are the attendee-facing content. Keep them clear and step-by-step.
- Do not add instructor-only or Skillable-specific content to this repository.
- Use "Arm64" (not "ARM64") and "Windows Arm64" (two words, no hyphen).

## Security

- Never commit API keys, tokens, credentials, or connection strings.
- Do not modify license files (`LICENSE`, `LICENSE-DOCS`), `CODE_OF_CONDUCT.md`, or `SECURITY.md`.
- Use environment variables (`%ACR%`, `%RG%`, `%AKS%`, `%LOC%`) for all user-specific values.

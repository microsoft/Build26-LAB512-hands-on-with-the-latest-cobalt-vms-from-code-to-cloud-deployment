# Part 1 - Local build and deploy

**Time:** ~20 minutes

In this part you will build the eShop webapp container image, deploy the full application to Docker Desktop Kubernetes using Kustomize, and verify it works end-to-end on your local x64 machine.

> **Prerequisite:** Complete [Machine setup](setup.md) before starting this part.

## The application: .NET eShop

This lab uses [**.NET eShop**](https://github.com/dotnet/eshop) - the official .NET reference application for modern cloud-native development.

<img src="../img/eshop_homepage.png" alt="eShop homepage screenshot" width="600"/>

.NET eShop implements an e-commerce website using a services-based architecture built on [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/). This lab skips Aspire's AppHost and deploys the services directly to Kubernetes, closer to how they'd run in production. eShop is a polyglot, event-driven, microservice-based ecommerce stack comprising 10 services and three open-source backends (RabbitMQ, Postgres, Redis), all running unmodified on Azure Cobalt 200 Arm64.

<img src="../img/eshop_architecture.png" alt="eShop architecture diagram" width="600"/>

### Services in the deployment

- **WebApp** - Blazor storefront for browsing and ordering.
- **Identity API** - IdentityServer-based OIDC issuer.
- **Catalog API** - product catalog.
- **Basket API** - shopping cart, backed by Redis.
- **Ordering API + Order Processor + Payment Processor** - order pipeline, backed by Postgres and RabbitMQ.
- **Webhooks API + Webhooks Client** - outbound notifications.
- **Inference** - local ONNX-based chat model serving the WebApp's built-in chatbot (wired up in [Part 3](part3-ai-inference-on-cobalt.md)).
- **Postgres, Redis, RabbitMQ** - backing services.

In this lab you will build all 10 application service images from source as multi-arch container images and deploy them to Kubernetes.

### Kustomize: one manifest tree, many targets

[**Kustomize**](https://kustomize.io/) is Kubernetes' native manifest-customization tool (built into `kubectl -k`). It lets you write shared YAML once in a **base** and apply per-environment **overlays** that patch only the bits that differ - no templating language, just strategic-merge and JSON-merge patches.

| Location | Purpose |
|:---------|:--------|
| `deploy\k8s\base` | Shared namespace, services, deployments, and infra |
| `deploy\k8s\overlays\local` | Docker Desktop-specific ports and OIDC settings |
| `deploy\k8s\overlays\aks` | AKS LoadBalancers, public DNS labels, and AKS OIDC settings |

Differences between overlays are small: ports, hostnames, and identity URLs. Pod specs, image references, and service wiring are identical in the base.

## Before you begin

Open a **Command Prompt** (cmd.exe) and change to the lab directory:

```cmd
cd /d C:\lab512
```

### _**All commands in this guide assume you are running from the C:\Lab512 directory.**_

## Step 1: Set your lab environment variables

All lab commands key off a single attendee prefix. Set it now along with the derived variables:

```cmd
set ATTENDEE=lab51212345
set RG=%ATTENDEE%-rg
set ACR=%ATTENDEE%acr
set AKS=%ATTENDEE%-aks
set LAB512_DNS_LABEL=%ATTENDEE%
set LOC=westus3
```

`%ATTENDEE%` must be lowercase alphanumeric and globally unique (it becomes part of your ACR name). Replace `12345` with your own suffix - initials, a random number, whatever makes it unique.

## Step 2: Sign in to Azure and create your personal ACR

You need a container registry to store your images. Azure Container Registry (ACR) is the private registry both the local Docker Desktop cluster and (later) the AKS cluster will pull from.

```cmd
az login

az group create -n %RG% -l %LOC%
az acr create -n %ACR% -g %RG% --sku Basic --location %LOC% --admin-enabled
```

Before running `az acr login`, confirm the Docker engine is running:

```cmd
docker version --format "Client: {{.Client.Version}}  Server: {{.Server.Version}}"
```

You should see a single line showing both Client and Server versions. If you see a connection error, wait for Docker Desktop to finish starting, then re-run.

Once confirmed, log in to your ACR:

```cmd
az acr login -n %ACR%
```

## Step 3: Build the supporting service images

You will build 9 supporting service images as multi-arch manifests and push them to your ACR. These need to be multi-arch now because you will deploy them to both x64 (local) and Arm64 (AKS) later. This takes **15-25 minutes** on a first run (subsequent builds are cached).

```cmd
cd /d C:\lab512

docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/identity-api:latest -f src\Identity.API\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/catalog-api:latest -f src\Catalog.API\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/basket-api:latest -f src\Basket.API\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/ordering-api:latest -f src\Ordering.API\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/order-processor:latest -f src\OrderProcessor\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/payment-processor:latest -f src\PaymentProcessor\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/webhooks-api:latest -f src\Webhooks.API\Dockerfile . --push
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/webhooksclient:latest -f src\WebhookClient\Dockerfile . --push
```

### Build the inference images

First, download the ONNX models to the local `models\` folder. This is a one-time download (~5 GB):

```cmd
prepare-models.cmd
```

Then build and push the inference images. The `inference-models` image must be built **before** `inference` because the inference Dockerfile copies model files from it:

```cmd
docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/inference-models:latest -f src\Inference\Dockerfile.models . --push

docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/inference:latest -f src\Inference\Dockerfile . --push
```

> The inference images are large (~5 GB) because the ONNX model files are baked in. This build step will take longer than the others.

### Import infrastructure images from Docker Hub

The deployment uses Redis, RabbitMQ, PostgreSQL, and busybox (for init containers). Import them into your ACR so Kubernetes pulls from a single registry:

```cmd
az acr import --name %ACR% --source docker.io/library/redis:8.6 --image eshop/redis:8.6 --force
az acr import --name %ACR% --source docker.io/library/rabbitmq:4.2 --image eshop/rabbitmq:4.2 --force
az acr import --name %ACR% --source docker.io/pgvector/pgvector:pg17 --image eshop/pgvector:pg17 --force
az acr import --name %ACR% --source docker.io/library/busybox:1.37 --image eshop/busybox:1.37 --force
```

## Step 4: Build the webapp image

Now build **webapp** - the service you interact with directly. For now, build it as a standard x64 image for local deployment:

```cmd
docker build -t %ACR%.azurecr.io/eshop/webapp:latest -f src\WebApp\Dockerfile .
docker push %ACR%.azurecr.io/eshop/webapp:latest
```

The first build typically takes **3-5 minutes**.

### Verify: Confirm all images are in your ACR

```cmd
az acr repository list --name %ACR% --output table
```

You should see **15 repositories**: 10 application services (`webapp`, `identity-api`, `catalog-api`, `basket-api`, `ordering-api`, `order-processor`, `payment-processor`, `webhooks-api`, `webhooksclient`, `inference`) plus `inference-models` plus 4 infrastructure images (`redis`, `rabbitmq`, `pgvector`, `busybox`).

## Step 5: Point the local overlay to your ACR

The base manifests reference images as `eshop/<service>` with no registry prefix. The `kustomize edit set image` command rewrites every image reference in the overlay to point at your personal ACR:

```cmd
cd /d C:\lab512\deploy\k8s\overlays\local

for %s in (identity-api catalog-api webapp basket-api ordering-api order-processor payment-processor webhooks-api webhooksclient inference) do kustomize edit set image eshop/%s=%ACR%.azurecr.io/eshop/%s:latest

:: Also point infrastructure and init container images at your ACR
kustomize edit set image eshop/redis:8.6=%ACR%.azurecr.io/eshop/redis:8.6 eshop/rabbitmq:4.2=%ACR%.azurecr.io/eshop/rabbitmq:4.2 eshop/pgvector:pg17=%ACR%.azurecr.io/eshop/pgvector:pg17 eshop/busybox:1.37=%ACR%.azurecr.io/eshop/busybox:1.37

cd /d C:\lab512
```

## Step 6: Create namespace and pull secret

Docker Desktop's Kubernetes doesn't automatically pick up your `az acr login` credentials. Create the namespace and an image pull secret before deploying:

```cmd
kubectl config use-context docker-desktop

:: Create the eshop namespace
kubectl create namespace eshop

:: Create a Kubernetes pull secret using ACR admin credentials
for /f %p in ('az acr credential show -n %ACR% --query "passwords[0].value" -o tsv') do kubectl create secret docker-registry acr-pull-secret --docker-server=%ACR%.azurecr.io --docker-username=%ACR% --docker-password=%p -n eshop --dry-run=client -o yaml | kubectl apply -f -
```

## Step 7: Pre-pull images locally

Pre-pulling caches the images locally so deployments start almost instantly:

```cmd
:: Pre-pull all eShop images from your ACR
for %s in (identity-api catalog-api webapp basket-api ordering-api order-processor payment-processor webhooks-api webhooksclient inference) do docker pull %ACR%.azurecr.io/eshop/%s:latest

:: Pre-pull infrastructure images
for %s in (redis:8.6 rabbitmq:4.2 pgvector:pg17 busybox:1.37) do docker pull %ACR%.azurecr.io/eshop/%s
```

**Verify:** Run `docker images --format "table {{.Repository}}:{{.Tag}}\t{{.Size}}" | findstr eshop`. You should see all 15 images (10 application services + inference + 4 infrastructure).

## Step 8: Deploy the application

```cmd
:: Apply the local overlay - Kustomize merges base + local patches into a single manifest stream
kubectl apply -k deploy\k8s\overlays\local

:: Wait for all Deployments to report Available
kubectl -n eshop wait --for=condition=available deployment --all --timeout=300s

:: Wait for all Pods to be Ready
kubectl -n eshop wait --for=condition=ready pod --all --timeout=300s

:: Start the inference service (deployed with 0 replicas initially to let core services start first)
kubectl scale deployment inference -n eshop --replicas=1
```

> **Windows Firewall prompt:** You may see a dialog asking whether to allow network access. Select **Allow**.

The inference image is ~3.4 GB and may take a few minutes to pull. The core eShop services are usable while it downloads.

**Verify:** Run `kubectl -n eshop get pods`. All pods should show **Running** status.

## Step 9: Test the app

Open your browser:

1. Go to **http://localhost:8080** and confirm the storefront loads.
2. Select **Login**.
3. Sign in with:
   - **Email:** `alice@alice.com`
   - **Password:** use the password shown on the screen.
4. Browse the catalog and add an item to the basket.

You should be able to browse products and add items to the cart. The login flow should complete and return you to the storefront.

## What you just did

You now have a working .NET microservices deployment on your local Docker Desktop Kubernetes cluster. The application is running on your x64 machine - in the next part, you'll take the exact same app to AKS on Azure Cobalt 200 Arm64 nodes with no code changes.

> **Dev inner loop (for reference):** If you ever need to iterate on a service, the workflow is: edit the source under `C:\lab512\src\<Service>\`, rebuild with `docker build`, push with `docker push`, then `kubectl -n eshop rollout restart deploy/<service>` and refresh the browser.

---

**Next:** [Part 2 - AKS on Cobalt 200](part2-aks-on-cobalt.md)

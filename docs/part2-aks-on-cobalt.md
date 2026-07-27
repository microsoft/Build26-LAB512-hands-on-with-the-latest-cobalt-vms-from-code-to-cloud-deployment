# Part 2 - AKS on Cobalt 200

**Time:** ~20 minutes

In this part you will rebuild the webapp as a multi-arch image, create an AKS cluster running on Azure Cobalt 200 Arm64 nodes, and deploy the same application - no code changes, no special Arm64 branch.

> **Prerequisite:** Complete [Part 1 - Local build and deploy](part1-local-build-and-deploy.md) before starting this part.

## Multi-arch container images

Your local deployment ran on **x64**, but the AKS cluster will run on Cobalt 200's **Arm64** nodes. The x64-only `webapp` image you built in Part 1 won't run there - Kubernetes needs an Arm64 variant.

This is where **multi-arch container images** come in. Instead of maintaining separate images per architecture, you build a single image manifest that contains both `linux/amd64` and `linux/arm64` variants. Kubernetes automatically pulls the correct variant for the node it lands on. You are no longer locked to a single deployment architecture - you can have hybrid deployments that span both x64 and Arm64 nodes.

The eShop Dockerfiles are already prepared for cross-architecture publishing:

- `FROM --platform=$BUILDPLATFORM` keeps the SDK stage native to the machine doing the build
- `dotnet publish -a $TARGETARCH` emits runtime assets for the target architecture
- Both build pipelines run as native x64 .NET - **no slow QEMU emulation**

| Without multi-arch | With multi-arch |
|:-------------------|:----------------|
| Separate image names or custom pipelines per CPU architecture | One image name and one logical version |
| More environment-specific branching | Kubernetes pulls the correct manifest automatically |
| Higher friction moving to Arm64 | Same app, different node architecture |

That is the story: **minimal change, maximum portability**.

> The other 9 application services and the inference images were already built as multi-arch in Part 1. Only `webapp` needs to be rebuilt here.

## Step 1: Rebuild webapp as a multi-arch image

Rebuild webapp with both `linux/amd64` and `linux/arm64` variants and push to your ACR:

```cmd
cd /d C:\lab512

docker buildx build --platform linux/amd64,linux/arm64 -t %ACR%.azurecr.io/eshop/webapp:latest -f src\WebApp\Dockerfile . --push
```

**Verify:** Confirm the manifest now contains both architectures:

```cmd
docker buildx imagetools inspect %ACR%.azurecr.io/eshop/webapp:latest --format "{{range .Manifest.Manifests}}{{if ne .Platform.OS \"unknown\"}}{{.Platform.OS}}/{{.Platform.Architecture}} {{end}}{{end}}"
```

You should see `linux/amd64 linux/arm64`.

## Step 2: Create your AKS cluster

Provision a real AKS cluster with Cobalt 200 (Arm64) nodes and attach it to your ACR:

```cmd
az aks create -n %AKS% -g %RG% --node-vm-size Standard_D8pds_v7 --node-count 2 --attach-acr %ACR% --generate-ssh-keys --location %LOC%
```

| Setting | Value |
|:--------|:------|
| VM size | `Standard_D8pds_v7` |
| CPU architecture | Arm64 (Cobalt 200) |
| Node count | 2 |
| CPU / memory per node | 8 vCPU / 32 GiB RAM |

AKS creation usually takes **3-5 minutes**.

Once the cluster is ready, download its credentials and switch kubectl to the new context:

```cmd
az aks get-credentials -n %AKS% -g %RG%
kubectl config use-context %AKS%
```

**Verify:** Confirm the nodes are ready and running on Arm64:

```cmd
kubectl get nodes -o custom-columns=NAME:.metadata.name,STATUS:.status.conditions[-1:].type,ARCH:.status.nodeInfo.architecture,VM-SIZE:.metadata.labels.node\.kubernetes\.io/instance-type
```

You should see two nodes with STATUS **Ready**, ARCH **arm64**, and VM-SIZE **Standard_D8pds_v7**.

## Step 3: Point the AKS overlay to your ACR

Just like the local overlay, the AKS overlay needs its image references rewritten to point at your personal ACR:

```cmd
cd /d C:\lab512\deploy\k8s\overlays\aks

for %s in (identity-api catalog-api webapp basket-api ordering-api order-processor payment-processor webhooks-api webhooksclient inference) do kustomize edit set image eshop/%s=%ACR%.azurecr.io/eshop/%s:latest

:: Also point infrastructure and init container images at your ACR
kustomize edit set image eshop/redis:8.6=%ACR%.azurecr.io/eshop/redis:8.6 eshop/rabbitmq:4.2=%ACR%.azurecr.io/eshop/rabbitmq:4.2 eshop/pgvector:pg17=%ACR%.azurecr.io/eshop/pgvector:pg17 eshop/busybox:1.37=%ACR%.azurecr.io/eshop/busybox:1.37

cd /d C:\lab512
```

## Step 4: Deploy to AKS

AKS pulls images directly from the attached ACR using its managed identity - no pull secret needed (unlike the Docker Desktop cluster).

```cmd
:: Render the AKS overlay, replace the DNS label placeholder, and apply
powershell -NoProfile -Command "kubectl kustomize deploy\k8s\overlays\aks | ForEach-Object { $_ -replace 'LAB512_DNS_LABEL','%LAB512_DNS_LABEL%' } | kubectl apply -f -"

:: Wait for all Deployments to report Available
kubectl -n eshop wait --for=condition=available deployment --all --timeout=300s

:: Wait for all Pods to be Ready
kubectl -n eshop wait --for=condition=ready pod --all --timeout=300s
```

> **Troubleshooting:** If a pod shows `ImagePullBackOff` or `CrashLoopBackOff`, run `kubectl -n eshop describe pod <pod-name>` to see the Events section with the specific failure reason.

**Verify:** Confirm all pods are running and running on Cobalt 200:

```cmd
:: All pods should be Running
kubectl -n eshop get pods

:: Confirm the CPU architecture inside the webapp container
kubectl -n eshop exec deploy/webapp -c webapp -- uname -m

:: Confirm the node's VM size
powershell -NoProfile -Command "$p = kubectl get pod -n eshop -l app.kubernetes.io/name=webapp -o jsonpath='{.items[0].spec.nodeName}'; kubectl get node $p -o jsonpath='{.metadata.labels.node\.kubernetes\.io/instance-type}'"

:: Confirm both services have external IPs
kubectl -n eshop get svc identity-api webapp
```

You should see:

- All pods in namespace `eshop` are **Running**
- `uname -m` returns **`aarch64`**
- The node VM size is **`Standard_D8pds_v7`** (Cobalt 200)
- Both services have external IPs (if either shows `<pending>`, wait a moment and re-run)

## Step 5: Test the app on Cobalt 200

Open your browser:

1. Go to **http://%LAB512_DNS_LABEL%.westus3.cloudapp.azure.com/**
2. Select **Login**.
3. Sign in with:
   - **Email:** `alice@alice.com`
   - **Password:** leave it blank
4. Browse the catalog and add an item to the basket.

## What just happened

The same application image set is now running on Cobalt 200 Arm64 nodes.

- You did **not** create a special Arm64 branch.
- You did **not** rewrite the app for Arm.
- You did **not** change your Kubernetes manifests beyond the environment-specific overlay.
- The only thing you changed was rebuilding webapp as multi-arch with `docker buildx build --platform linux/amd64,linux/arm64` - same Dockerfile, same source code.

Kubernetes simply pulled the correct architecture from the image manifests. That is the value of a multi-arch container strategy.

---

**Next:** [Part 3 - AI inference on Cobalt](part3-ai-inference-on-cobalt.md)

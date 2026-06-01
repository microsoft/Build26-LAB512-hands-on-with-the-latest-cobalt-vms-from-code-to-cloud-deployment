# Troubleshooting

This guide covers the most common issues you may hit during the lab, organized roughly in the order they appear. Each section gives the symptom, the likely cause, and the fix.

## First steps for any issue

Before chasing a specific failure, these three commands tell you most of what you need:

```cmd
:: Pod status overview - first thing to run for any "it's not working" symptom
kubectl -n eshop get pods

:: Pod-level events (the bottom of the output is usually the answer)
kubectl -n eshop describe pod <pod-name>

:: Container logs (use --previous if the container has crashed and restarted)
kubectl -n eshop logs <pod-name> --tail=100
kubectl -n eshop logs <pod-name> --tail=100 --previous
```

Also useful for seeing what image each pod is running and whether it has its pull secret:

```cmd
kubectl -n eshop get pods -o custom-columns=NAME:.metadata.name,IMAGE:.spec.containers[*].image,SECRETS:.spec.imagePullSecrets[*].name
```

---

## Docker Desktop and WSL

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `winget install` reports `No package found matching input criteria` | winget source not refreshed | `winget source update` then retry; for Docker Desktop you can also download the installer from https://docker.com |
| `wsl --status` reports `WSL is not installed` | WSL feature was not enabled (or reboot was skipped) | `winget install -e --id Microsoft.WSL`, then **reboot** |
| `docker desktop start` returns immediately but `docker version` says `error during connect` | Engine is still starting (~20-60s on first boot), or virtualization is disabled | Wait for the tray icon to stop animating; if it never starts, confirm the VM has nested virtualization enabled |
| `docker buildx inspect default` shows only `linux/amd64` | Classic image store is in use instead of containerd | Verify `UseContainerdSnapshotter` is `true` in `%APPDATA%\Docker\settings-store.json`, then `docker desktop restart` |
| `kubectl config use-context docker-desktop` reports the context doesn't exist | Kubernetes not enabled in Docker Desktop | Re-check `KubernetesEnabled` is `true` in `settings-store.json`, then `docker desktop restart` and wait 1-3 minutes |
| `kubectl get nodes` says `The connection to the server ... was refused` | Kubernetes control plane still starting | Wait until the Docker Desktop tray icon's Kubernetes indicator turns green |
| `kubectl get nodes` shows node `desktop-control-plane` instead of `docker-desktop`, and pods take 5+ minutes to start with every image re-pulling from the registry | Docker Desktop is using the newer **kind**-based Kubernetes engine, which runs kubelet inside an isolated container with its own image store — pre-pulled host images are invisible to it | Switch back to the classic engine: set `KubernetesMode` to `"kubeadm"` in `%APPDATA%\Docker\settings-store.json`, then `docker desktop restart`. Or toggle via **Settings → Kubernetes → Choose cluster provisioning method → Kubeadm** |
| Pod stuck in `Init:0/1` (e.g. `webhooksclient` or `webapp`) even after `rabbitmq` is Running and accepting AMQP connections from other pods | The init container's `nc -z rabbitmq 5672` loop started before the rabbitmq Service had endpoints registered and got wedged on a stale DNS or socket state | Delete the stuck pod and let the Deployment recreate it: `kubectl -n eshop delete pod <pod-name>`. The new init container resolves rabbitmq cleanly and exits within seconds |

## Azure CLI and ACR

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `az login` opens browser but never completes | Proxy/VPN blocking auth callback | Use device code flow: `az login --use-device-code` |
| `az acr create` fails with `RegistryNameAlreadyInUse` | ACR names are globally unique | Pick a different `%ATTENDEE%` suffix and re-run |
| `az acr login` fails with `Unable to use Azure CLI credentials directly` | AAD token expired | Run `az login` to refresh, then retry `az acr login` |
| `az acr login` fails with `Error response from daemon` | Docker engine is not running | Start Docker Desktop first |
| `az acr import` fails with `not found` | Source image doesn't exist on Docker Hub, or typo in the image name | Double-check the image name and tag against https://hub.docker.com |
| `az acr import` fails with `denied` / `unauthorized` | Docker Hub rate limit or authentication issue | Wait and retry, or authenticate with Docker Hub: `docker login` |

## Docker build and push

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `failed to push: insufficient_scope: authorization failed` | Docker not logged into your ACR | `az acr login -n %ACR%` |
| `failed to push: manifest unknown` immediately after build | Tag or registry name typo | Check the `-t` argument matches `%ACR%.azurecr.io/eshop/<svc>:latest` exactly |
| `exec format error` during a `RUN` step | Buildx tried to execute a foreign-arch binary in a stage not pinned to `$BUILDPLATFORM` | Pin the stage to `--platform=$BUILDPLATFORM`, or run `docker run --privileged --rm tonistiigi/binfmt --install all` |
| Multi-arch build is extremely slow (10+ minutes per service) | Containerd image store is not enabled, falling back to QEMU emulation | Enable containerd image store in Docker Desktop settings |
| Build dies with `rpc error: code = Unavailable desc = error reading from server: EOF` | buildkit OOM'd or hit a full disk | Restart Docker Desktop, prune (`docker system prune -af` + `docker buildx prune -af`), and retry |

## Pod stuck in ImagePullBackOff or ErrImagePull

This is the most common failure. Start by reading the actual error:

```cmd
kubectl -n eshop describe pod <pod-name>
```

Look at the **Events** section at the bottom. The exact error text determines the fix:

| Event message | Cause | Fix |
|:--------------|:------|:----|
| `pull access denied` / `requires authorization` | Missing `imagePullSecrets` on the pod | Verify the pull secret exists: `kubectl -n eshop get secret acr-pull-secret`. If missing, re-create it (see Part 1, Step 5) |
| `manifest unknown` / `not found` | Image isn't in your ACR, or the tag is wrong | `az acr repository list -n %ACR% -o table` to confirm |
| `no match for platform in manifest` | Image is single-arch but the cluster needs the other arch | Rebuild with `docker buildx build --platform linux/amd64,linux/arm64 ... --push` |
| `unauthorized: authentication required` | Pull secret credentials are stale | Re-create the secret (re-run the `az acr credential show` + `kubectl create secret` block from Part 1) |
| `short read` / `unexpected EOF` | Transient Docker Desktop network flake | `docker desktop restart`, wait ~30 seconds, pods retry automatically |
| `failed to resolve reference` / DNS errors | Docker Desktop networking glitch | `docker desktop restart` |

If pulls still fail after restarting Docker Desktop, try pulling the image directly:

```cmd
docker pull %ACR%.azurecr.io/eshop/<svc>:latest
```

- If `docker pull` succeeds, kick the deployment: `kubectl -n eshop rollout restart deploy/<svc>`
- If `docker pull` also fails with EOF, check Docker Desktop disk size (Settings > Resources > Disk image size, bump to 128 GB if needed)
- If `docker pull` fails with auth, run `az acr login -n %ACR%` and retry

To restart all stuck deployments at once:

```cmd
kubectl -n eshop rollout restart deploy
kubectl -n eshop wait --for=condition=available deployment --all --timeout=600s
```

Clean up stale pods after restarts:

```cmd
kubectl -n eshop delete pods --field-selector=status.phase=Pending
kubectl -n eshop delete pods --field-selector=status.phase!=Running
```

## Pod is Running but not Ready (0/1)

A pod showing `Running 0/1` means the container is alive but the readiness probe is failing.

```cmd
:: Check logs for errors or startup progress
kubectl -n eshop logs <pod-name> --tail=100

:: Test the health endpoint manually
kubectl -n eshop exec <pod-name> -- curl -s -o /dev/null -w "%%{http_code}" http://localhost:8080/health
```

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `inference` pod is 0/1, `/health` returns 503, `/alive` returns 200 | The Phi-4 ONNX model is loading and the canonical-prefix warmup chat is running. On AKS Cobalt 200 this takes 3-5 minutes; on Docker Desktop (x86 CPU-only) the canonical warmup decode can take **13-20 minutes** because token generation runs at ~1 tok/s on CPU. (The local overlay defaults `inference` to 0 replicas — only scale up locally if you're explicitly testing CPU inference.) | Confirm warmup is progressing, not hung. Tail the log filtered to key stages: `kubectl -n eshop logs -f deploy/inference \| findstr /i "warm model ready prefill"`. Expected sequence (minutes between lines on Docker Desktop): `Warming up models...` → `Chat model loaded from /models/phi-4-mini` → `Chat prefill: <ms>ms` → `Chat model warm in <ms>ms` → `Embedding model warm in <ms>ms` → `All models ready`. Confirm the process is burning CPU: `docker stats --no-stream \| findstr inference` should show 300-600% CPU. Once `All models ready` appears, the next readiness probe (10s) flips `/health` to 200. If CPU is <50% AND no new log lines for 5+ minutes, the warmup hung — delete the pod (`kubectl -n eshop delete pod -l app.kubernetes.io/name=inference`) and let it restart |
| Other service pods are 0/1 | A dependency (Postgres, Redis, RabbitMQ) may not be ready yet | Check that infrastructure pods are 1/1 first, then restart the affected deployment |
| `TimeoutRejectedException` on `BasketService.GetBasketAsync` | basket-api started before Redis was fully ready | Restart basket-api: `kubectl -n eshop rollout restart deployment basket-api` |

## Other deployment failures

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `ImagePullBackOff` on AKS only | AKS is not attached to your ACR | `az aks update -n %AKS% -g %RG% --attach-acr %ACR%` |
| `CrashLoopBackOff` | Bad config or a dependency is not ready | `kubectl -n eshop logs deploy/<name> --previous --tail=100` and confirm Postgres/Redis/RabbitMQ are Running |
| Pod stuck in `Pending` with `insufficient cpu/memory` | Resource requests exceed the node | On AKS, scale up the node pool; on Docker Desktop, raise CPU/Memory in Settings > Resources |
| `kubectl apply -k` reports `error: accumulating resources` | Wrong working directory | `cd /d C:\lab512` then re-run |
| `kubectl set env` reports `deployments.apps "X" not found` | Wrong kubectl context | `kubectl config current-context` and switch with `kubectl config use-context <name>` |

## LoadBalancer and networking

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| `EXTERNAL-IP` shows `<pending>` for more than 2 minutes | LoadBalancer provisioning slow | Wait; on AKS this can take up to 3 minutes the first time |
| Browser cannot reach `http://localhost:8080` | Windows Firewall blocked Docker Desktop, or the port is held by another process | Check the firewall prompt was accepted; `netstat -ano \| findstr :8080` to find conflicts |
| AKS DNS hostname does not resolve | DNS propagation delay, or wrong `LAB512_DNS_LABEL` | `nslookup %LAB512_DNS_LABEL%.westus3.cloudapp.azure.com`; re-check the value |

## Sign-in and OIDC

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| Sign-in loops back to login page | Stale cookies from a previous deploy | Open an **InPrivate/Incognito** window and sign in fresh |
| `Correlation failed` error | `identity-api` public endpoint not yet ready, or stale cookies | Wait for both LoadBalancer services to have external IPs; clear cookies and retry |
| Antiforgery token errors | Mixing local and AKS URLs in the same browser session | Use separate browser profiles or clear cookies between local/AKS testing |
| `RpcException: Unauthenticated` when adding items to basket | Auth tokens invalidated after identity-api pod restart | Restart all deployments, clear cookies, then log in again |

## Inference and chat

| Symptom | Likely cause | Fix |
|:--------|:-------------|:----|
| Chat icon never appears in the storefront | `OnnxEnabled`/`InferenceUrl` env vars not set on both `webapp` AND `catalog-api` | Re-run `kubectl set env deployment/webapp deployment/catalog-api -n eshop OnnxEnabled=true InferenceUrl=http://inference:5200` |
| Chat icon stays gray (never turns blue) | Inference pod still warming up (model load + prefix-cache warmup ~30-60s after start) | `kubectl -n eshop logs deploy/inference --tail=20` and look for warmup-complete messages |
| First chat query is slow (~3 seconds) but subsequent ones are fast | Prefix cache hadn't been populated yet | Expected - cold start tax on the first turn, then the prefix cache makes follow-ups near-instant. See [Part 3](part3-ai-inference-on-cobalt.md) |
| Every chat query is slow (~3 seconds) | Prefix cache disabled | `kubectl -n eshop set env deploy/inference INFERENCE_PREFIX_CACHE-` (trailing dash unsets the var, restoring default) |
| Inference noticeably slower on local Docker Desktop than on AKS | Expected - Docker Desktop runs as amd64 WSL2 without KleidiAI's Arm64 int4 kernels | Use AKS for the headline "fast inference" demo; local is for the dev/iteration loop |
| `curl http://localhost:5200/...` hangs | Model still loading (cold start) | Wait 30-60s after pod becomes Ready, then retry |
| `curl http://localhost:5200/...` connection refused | Inference service not exposed or pod not Ready | `kubectl -n eshop get svc inference` and `kubectl -n eshop get pods -l app.kubernetes.io/name=inference` |

---

## Useful kubectl commands

### Inspect resources

```cmd
kubectl -n eshop get pods
kubectl -n eshop get svc
kubectl -n eshop get deploy
kubectl -n eshop describe pod <pod-name>
```

### Get the external IP for webapp

```cmd
kubectl -n eshop get svc webapp -o jsonpath="{.status.loadBalancer.ingress[0].ip}"
```

### Check logs

```cmd
kubectl -n eshop logs deployment/webapp --tail=50
kubectl -n eshop logs deployment/identity-api --tail=50
kubectl -n eshop logs deployment/inference --tail=50
```

### Check architecture inside a running pod

```cmd
kubectl -n eshop exec deploy/webapp -- uname -m
```

### Restart a deployment

```cmd
kubectl -n eshop rollout restart deployment/webapp
kubectl -n eshop rollout status deployment/webapp --timeout=300s
```

### Switching kubectl contexts

```cmd
:: List all contexts
kubectl config get-contexts

:: Switch to Docker Desktop Kubernetes
kubectl config use-context docker-desktop

:: Switch to AKS
kubectl config use-context %AKS%

:: Check the active context
kubectl config current-context
```

---

## Reference notes

- The inference service name is **`inference`**, listening on port **5200**.
- The inference health probe uses **`/health`**; alive probe uses **`/alive`**.
- Image pull secrets are needed on Docker Desktop (no managed identity) but **not** on AKS (uses managed identity via `--attach-acr`).
- `az acr login` tokens are good for ~3 hours - re-run if pushes/pulls start failing mid-session.

---

**Back to:** [README](../README.md)

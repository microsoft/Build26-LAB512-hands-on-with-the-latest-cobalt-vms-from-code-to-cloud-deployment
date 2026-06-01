# Part 3 - AI inference on Cobalt

**Time:** ~15 minutes

In this part you will activate the eShop chatbot, powered by a 3.8B parameter language model running on Cobalt 200 Arm64 nodes inside Kubernetes. No GPU, no cloud-hosted endpoints, no API keys.

> **Prerequisite:** Complete [Part 2 - AKS on Cobalt 200](part2-aks-on-cobalt.md) before starting this part.

## What you're about to light up

The eShop storefront ships with a customer-service chatbot. So far it has been hidden because no AI backend is wired up. In this section you serve the chatbot from **Phi-4-mini-instruct** - a 3.8B parameter model published by Microsoft under the MIT License - running on Cobalt 200 inside your AKS cluster.

The inference stack:

- **ONNX Runtime GenAI** - Microsoft's cross-platform inference engine with streaming generation, KV caching, and chat-template helpers. Runs natively on Arm64 using Arm Neoverse NEON SIMD lanes for INT4 matrix multiplications.
- **Phi-4-mini (INT4 quantized)** - 4-bit weights shrink the model from ~7.6 GB (FP16) to ~4.9 GB, reduce memory bandwidth pressure, and pair perfectly with ONNX Runtime's int4 kernel set.
- **Function calling** - the model can search the product catalog and manage the shopping cart. Three tools are registered: `SearchCatalog`, `AddToCart`, and `GetCartContents`.

### Inference architecture

```
WebApp pod --IChatClient (HTTP, OpenAI-shaped)--> inference pod
                                                    |-- ASP.NET Core Minimal API
                                                    |-- Microsoft.ML.OnnxRuntimeGenAI
                                                    +-- Phi-4-mini-instruct (CPU INT4 ONNX)
                                                        (model files baked into the image)
```

The ~4.9 GB model files live inside the container image - no first-boot download, no PVC, no inference-time access to HuggingFace. The image was built and pushed to your ACR in Part 1.

### How it wires up

The inference pod is already part of the base deployment. You do not deploy a second stack to turn it on. Two environment variables activate the feature for the app tier:

- `OnnxEnabled=true`
- `InferenceUrl=http://inference:5200`

When those values are present, webapp registers a chat client and catalog-api registers an embedding generator for semantic product search. Without them, the chat icon stays hidden.

## Step 1: Light up AI on Cobalt 200

The same app, same containers, same model, same ONNX Runtime - now executing natively on Arm64 Neoverse cores:

```cmd
:: Switch kubectl to the AKS cluster
kubectl config use-context %AKS%

:: Activate inference on AKS
kubectl set env deployment/webapp deployment/catalog-api -n eshop OnnxEnabled=true InferenceUrl=http://inference:5200

:: Wait for rolling restarts to complete
kubectl -n eshop rollout status deployment/webapp --timeout=300s
kubectl -n eshop rollout status deployment/catalog-api --timeout=300s
kubectl -n eshop rollout status deployment/inference --timeout=300s
```

## Step 2: Test the inference endpoint directly

Before testing the chat in the browser, confirm the inference service is responding by hitting its OpenAI-compatible HTTP endpoint directly. Inference isn't exposed publicly on AKS - it's a ClusterIP service - so use `kubectl port-forward` to reach it from your VM.

```cmd
:: In a new terminal, start a port-forward (keep this window open)
kubectl -n eshop port-forward svc/inference 5200:5200
```

Then in your main terminal:

```cmd
curl -sS -X POST http://localhost:5200/v1/chat/completions -H "Content-Type: application/json" -d "{\"model\":\"Phi-4-mini-instruct\",\"messages\":[{\"role\":\"user\",\"content\":\"Say hello in one short sentence.\"}],\"stream\":false}"
```

You should get an OpenAI-shaped JSON response with a greeting generated on Cobalt 200 Arm cores. No API key, no cloud LLM, no token meter - and the exact same container image you'd run on a dev laptop, a CI runner, or any other Kubernetes cluster. Stop the port-forward (Ctrl+C) when done.

## Step 3: Test the chat on Cobalt 200

Open **http://%LAB512_DNS_LABEL%.westus3.cloudapp.azure.com/** and sign in first (`alice@alice.com`, leave password blank) - you'll need an authenticated session for the "add to cart" test below.

Wait for the chat icon in the lower-right corner to turn **blue** (warm-up may take 20-30s on first hit). Then click the icon and type:

**"show me watches"**

Product cards appear with images, names, and prices.

### That was fast

Notice the response landed in well under a second - a 3.8B-parameter model, running on CPU, no GPU, inside a Kubernetes pod. Four things stack to deliver that:

1. **KleidiAI int4 GEMM kernels.** Arm's open-source [KleidiAI](https://gitlab.arm.com/kleidi/kleidiai) library ships micro-kernels tuned for Neoverse cores. ONNX Runtime auto-dispatches to KleidiAI for int4 matrix multiplies when it detects an Arm64 CPU. Cobalt 200's Neoverse N3 cores hit the SVE2+i8mm path. This is the single biggest lever - without it, prefill would be ~3-4x slower.
2. **Phi-4-mini int4 quantization.** 4-bit weights shrink the model from ~7.6 GB to ~4.9 GB, reduce memory bandwidth pressure, and pair perfectly with KleidiAI's int4 kernel set.
3. **Warmup at container start.** The inference container fires a prompt through the model at startup, so model weights are already loaded and caches allocated before your first query.
4. **Container-aware ORT threading.** The lab sets `INFERENCE_INTRA_OP_THREADS=8` and bumps the inference container's CPU limit to 7 vCPUs, giving worker threads room to run during prefill bursts.

The fifth lever - the prefix cache - kicks in on the *next* turn.

## Step 5: Multi-turn conversation

With the chat still open after the "show me watches" response, type:

**"add the first one to my cart"**

The assistant confirms the first watch was added to your cart.

### What happened in that round-trip

1. **Multi-turn context.** The webapp held the full chat history in memory and replayed it to inference. The model resolved "the first one" by reading the previous turn's product results.
2. **Tool routing per turn.** The model picked `AddToCart` for this turn - not `SearchCatalog`. The webapp registers three local tools; the model selects the right one based on user intent.
3. **The prefix cache kicked in.** Turn 2's prompt overlaps ~98% with turn 1's - same system prompt, same tool schemas, most of the chat history. The inference container skipped the prefill phase and only had to decode the new tokens. That's why this turn felt sub-second.

### What you just experienced

- **First "show me watches" query: ~700 ms end-to-end** on Cobalt 200 CPU, no GPU
- **Multi-turn follow-ups stay under ~1 second** even as the conversation grows

> The inference service caches the static system prompt and tool schemas across requests. Without the prefix cache, the same query takes ~3,000 ms (~4x slower). The cache reuses ~307 of ~312 prompt tokens - only your words and the assistant's reply are new each turn.

### Optional: Experience the prefix cache

To see the difference the prefix cache makes, try disabling it, retesting, then re-enabling:

```cmd
:: Turn the cache off - every request re-prefills from scratch
kubectl set env -n eshop deploy/inference INFERENCE_PREFIX_CACHE=0
kubectl -n eshop rollout status deployment/inference --timeout=300s

:: Ask "show me watches" again in the chat UI - should now take ~3 seconds

:: Turn it back on (trailing dash unsets the var, restoring the default)
kubectl set env -n eshop deploy/inference INFERENCE_PREFIX_CACHE-
kubectl -n eshop rollout status deployment/inference --timeout=300s
```

You can also inspect the inference logs:

```cmd
kubectl -n eshop logs deployment/inference --tail=20
```

## What just happened - end to end

You ran a **3.8B parameter model locally on CPU** inside Kubernetes on Cobalt 200. No expensive calls to cloud endpoints. No extra app code changes for Arm64. The app used the inference service, the model called into catalog search, and the UI rendered structured product cards end-to-end.

To scale inference in production: scale up the VM SKU for higher throughput per request, or run more `inference` replicas across more nodes for more concurrent users (each replica adds ~4.9 GB of node RAM).

---

## Wrap-up

Congratulations - you built a real-world .NET 10 microservices app as multi-arch container images on a Windows x64 dev machine, validated it on a local Docker Desktop Kubernetes cluster, deployed it to AKS on Azure Cobalt 200, and finished by serving a real LLM-powered chatbot from a 3.8B model running entirely on Cobalt 200 CPUs.

### Key takeaways

1. **Multi-arch images reduce friction.** Kubernetes picks the correct architecture variant for the node.
2. **Cobalt 200 does not require a special app fork.** The same app and deployment model work on Arm64.
3. **Kustomize overlays keep environment differences small and explicit.**
4. **On-CPU AI inference is practical.** You can serve a useful model inside the cluster on Cobalt 200 CPUs without calling an external AI service.

## Clean up

When you are done, clean up both local and Azure resources:

```cmd
kubectl config use-context %AKS%
kubectl delete namespace eshop --grace-period=0 --force
az aks delete -n %AKS% -g %RG% --yes --no-wait
kubectl config use-context docker-desktop
kubectl delete namespace eshop --grace-period=0 --force
az group delete -n %RG% --yes --no-wait
```

> `az aks delete --no-wait` and `az group delete --no-wait` return quickly, but the cloud-side cleanup continues in the background.

---

**Back to:** [README](../README.md)

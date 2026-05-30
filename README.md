<p align="center">
<img src="img/banner-build-26.png" alt="Microsoft Build 2026" width="1200"/>
</p>

# [Microsoft Build 2026](https://build.microsoft.com)

## 🔥 LAB512: Hands-on with the latest Cobalt VMs - from code to cloud deployment

### Session Description

Roll up your sleeves and get hands-on with the latest Azure Cobalt VMs. Build multi-arch container images with Docker, push them to ACR, and deploy to AKS clusters running on Cobalt VMs. Then go further - serve AI predictions using ONNX Runtime on Arm. Walk out ready to build on Cobalt VMs.

You will take a real .NET microservices application - the [eShop reference app](https://github.com/dotnet/eshop) - and:

1. **Build and run it locally** on Docker Desktop Kubernetes
2. **Deploy the same images to AKS on Azure Cobalt 200** - no special Arm64 branch, no code rewrites
3. **Light up on-CPU AI inference** using ONNX Runtime GenAI and Phi-4-mini, running entirely on Cobalt 200 CPUs - no GPU, no cloud-hosted endpoints

The key takeaway: Azure Cobalt 200 _just works_.

### Azure Cobalt 200

[Azure Cobalt 200](https://aka.ms/Cobalt200-VM-Pr) is Microsoft's second-generation custom Arm64 processor, purpose-built for cloud-native workloads. Built on the latest Arm architecture and TSMC's 3nm process, it delivers up to **50% higher per-core performance** over Cobalt 100 - with improvements across the board in CPU throughput, storage IOPS, and network bandwidth. It is Azure's most power-efficient compute offering.

Cobalt 200 is designed for the workloads that define this era of cloud computing: containerized microservices, distributed data pipelines, web and application servers, databases, and agentic AI runtimes. It ships as a full family of VMs - general purpose (Dpsv7/Dplsv7), memory optimized (Epsv7), high-memory (Mpv4), and dense local storage (Lpv5) - so you can match the VM to the workload you actually run. If your stack is built on .NET, Java, Python, Go, or Node.js, it runs on Cobalt 200 with no code changes. This lab lets you experience that firsthand.

### The app: eShop

This lab uses [.NET eShop](https://github.com/dotnet/eshop) - the official .NET reference application for cloud-native development. It is a full-featured online storefront with product browsing, shopping cart, checkout, and user identity.

<img src="img/eshop_homepage.png" alt="eShop homepage screenshot" width="600"/>

### Lab outline

| Part | What you'll do | Approx. time |
|:-----|:---------------|:-------------|
| [Part 1 - Local build and deploy](docs/part1-local-build-and-deploy.md) | Build the webapp image, deploy to Docker Desktop Kubernetes via Kustomize, verify the app end-to-end | 20 min |
| [Part 2 - AKS on Cobalt 200](docs/part2-aks-on-cobalt.md) | Rebuild as a multi-arch image, create an AKS cluster on Cobalt 200, deploy and test | 20 min |
| [Part 3 - AI inference on Cobalt](docs/part3-ai-inference-on-cobalt.md) | Enable the ONNX Runtime inference service, chat with Phi-4-mini running on Cobalt 200 CPUs | 15 min |

If you run into issues, see the [Troubleshooting guide](docs/troubleshooting.md).

### 🏫 Getting started in a guided session

Open the lab environment provided by your instructor. The VM is pre-configured with all tools and images - follow the on-screen Skillable instructions to begin.

### 🏠 Getting started on your own

See the [Self-paced lab guide](docs/README.md) for prerequisites, machine setup, and step-by-step instructions.

### 🧠 Learning Outcomes

By the end of this lab, you will be able to:

- Build **multi-arch container images** (`linux/amd64` + `linux/arm64`) from a single x64 dev machine using Docker Buildx
- Deploy a microservices application to **AKS on Azure Cobalt 200** with zero code changes
- Run a **3.8B parameter language model on CPU** using ONNX Runtime GenAI on Arm64 - no GPU required
- Use **Kustomize overlays** to target local Kubernetes and AKS from the same manifest tree

### 💬 Keep Learning with Copilot

Try these prompts with GitHub Copilot to explore the topics from this lab. Open Copilot Chat in VS Code (`Ctrl+Alt+I` on Windows/Linux, `Cmd+Shift+I` on Mac), paste a prompt, and see what you learn. Try connecting the [Microsoft Learn MCP Server](#-microsoft-learn-mcp-server) for the latest official documentation.

Use these as a starting point - or write your own!

1. Understand multi-arch builds:

```
Explain how Docker Buildx creates multi-arch container images and how Kubernetes automatically selects the right architecture variant for a node
```

2. Go deeper with Cobalt 200:

```
Using the Microsoft Learn MCP Server, find the latest documentation on Azure Cobalt 200 VM sizes and explain the performance benefits for cloud-native workloads
```

3. Explore ONNX Runtime on Arm:

```
How does ONNX Runtime GenAI use KleidiAI micro-kernels on Arm64 Neoverse cores to accelerate INT4 model inference without a GPU?
```

4. Extend the deployment:

```
Help me add a CI/CD pipeline using GitHub Actions that builds multi-arch images and deploys to AKS on Cobalt 200 automatically on every push
```

5. Try a different model:

```
Help me swap Phi-4-mini for a different ONNX-compatible small language model in the inference service and compare the results
```

### 💻 Technologies Used

1. [Azure Cobalt 200](https://azure.microsoft.com/blog/introducing-azure-cobalt-100-based-virtual-machines/) - Microsoft's Arm64 server processor for cloud-native workloads
1. [.NET eShop](https://github.com/dotnet/eshop) - official .NET reference application for cloud-native development
1. [Docker Desktop](https://www.docker.com/products/docker-desktop/) and [Buildx](https://docs.docker.com/buildx/working-with-buildx/) - multi-arch container image builds
1. [Azure Kubernetes Service (AKS)](https://learn.microsoft.com/azure/aks/) - managed Kubernetes
1. [Kustomize](https://kustomize.io/) - declarative Kubernetes configuration management
1. [ONNX Runtime GenAI](https://onnxruntime.ai/) - local model inference on CPU
1. [Phi-4-mini](https://huggingface.co/microsoft/Phi-4-mini-instruct) - Microsoft's 3.8B parameter small language model

### 📚 Resources and Next Steps

| Resource | Description |
|:---------|:------------|
| [Azure Cobalt 200 documentation](https://learn.microsoft.com/azure/virtual-machines/cobalt-100-overview) | VM sizes, availability, and workload guidance |
| [.NET Aspire documentation](https://learn.microsoft.com/dotnet/aspire/) | The orchestration framework behind eShop |
| [ONNX Runtime documentation](https://onnxruntime.ai/docs/) | Running ML models on CPU, GPU, and NPU |
| [Multi-arch Docker builds](https://docs.docker.com/build/building/multi-platform/) | Building images for multiple CPU architectures |
| [Kustomize documentation](https://kubectl.docs.kubernetes.io/) | Managing Kubernetes manifests with overlays |
| [Build 2026 next steps](https://aka.ms/build26-next-steps) | Continue your learning journey after Build 2026 |

### 🌟 Microsoft Learn MCP Server

[![Install in VS Code](https://img.shields.io/badge/VS_Code-Install_Microsoft_Docs_MCP-0098FF?style=flat-square&logo=visualstudiocode&logoColor=white)](https://vscode.dev/redirect/mcp/install?name=microsoft.docs.mcp&config=%7B%22type%22%3A%22http%22%2C%22url%22%3A%22https%3A%2F%2Flearn.microsoft.com%2Fapi%2Fmcp%22%7D)

The Microsoft Learn MCP Server is a remote MCP Server that enables clients like GitHub Copilot and other AI agents to bring trusted and up-to-date information directly from Microsoft's official documentation. Get started by using the one-click button above for VSCode or access the [mcp.json](.vscode/mcp.json) file included in this repo.

For more information, setup instructions for other dev clients, and to post comments and questions, visit our Learn MCP Server GitHub repo at [https://github.com/MicrosoftDocs/MCP](https://github.com/MicrosoftDocs/MCP). Find other MCP Servers to connect your agent to at [https://mcp.azure.com](https://mcp.azure.com).

*Note: When you use the Learn MCP Server, you agree with [Microsoft Learn](https://learn.microsoft.com/en-us/legal/termsofuse) and [Microsoft API Terms](https://learn.microsoft.com/en-us/legal/microsoft-apis/terms-of-use) of Use.*

## Content Owners

<table>
<tr>
    <td align="center"><a href="https://github.com/jamshedd">
        <img src="https://github.com/jamshedd.png" width="100px;" alt="Jamshed Damkewala"/><br />
        <sub><b>Jamshed Damkewala</b></sub></a><br />
            <a href="https://github.com/jamshedd" title="talk">📢</a>
    </td>
</tr></table>

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.

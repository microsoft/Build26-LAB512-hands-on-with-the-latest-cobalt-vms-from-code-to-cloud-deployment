# Self-paced lab guide

Welcome to **LAB512: Hands-on with the latest Cobalt VMs - from code to cloud deployment**.

This guide is for running the lab on your own machine, outside of a guided Build session. You will build, deploy, and test the eShop application on Azure Cobalt 200 at your own pace.

> **Azure Cobalt 200 - Private Preview**
>
> Azure Cobalt 200-based VMs are currently in **private preview**. Before starting this lab, ensure your Azure subscription has access to Cobalt 200 VM sizes (such as `Standard_D8pds_v7`). If you do not have access, the AKS cluster creation in Part 2 will fail.
>
> Sign up for the preview at **[aka.ms/Cobalt200-VM-Pr](https://aka.ms/Cobalt200-VM-Pr)** and allow time for approval before starting the lab.
>
> <img src="../img/cobalt200-preview-qr.png" alt="QR code - Cobalt 200 VM Preview signup" width="150"/>

## Steps

| Step | Guide | Time |
|:-----|:------|:-----|
| 0 | [Machine setup](setup.md) - install tools, Docker Desktop, Kubernetes, clone the repo | 45 min |
| 1 | [Part 1 - Local build and deploy](part1-local-build-and-deploy.md) - build from source, deploy to Docker Desktop Kubernetes | 20 min |
| 2 | [Part 2 - AKS on Cobalt 200](part2-aks-on-cobalt.md) - multi-arch images, create AKS cluster, deploy | 20 min |
| 3 | [Part 3 - AI inference on Cobalt](part3-ai-inference-on-cobalt.md) - ONNX Runtime + Phi-4-mini on Cobalt 200 CPUs | 15 min |

If you run into issues at any point, see the [Troubleshooting guide](troubleshooting.md).

> ⚠️ This lab creates AKS clusters, ACR registries, and other Azure resources that incur costs on your subscription. Delete the resource group when you are done:
>
> ```cmd
> az group delete -n %RG% --yes --no-wait
> ```

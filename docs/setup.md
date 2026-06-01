# Machine setup

This guide walks you through setting up a Windows x64 development machine for the lab. Complete all steps here before starting [Part 1](part1-local-build-and-deploy.md).

> **Azure Cobalt 200 - Private Preview**
>
> Azure Cobalt 200-based VMs are currently in **private preview**. Before starting this lab, you must ensure your Azure subscription has access to Cobalt 200 VM sizes (such as `Standard_D8pds_v7`). If you do not have access, the AKS cluster creation in Part 2 will fail.
>
> Sign up for the preview at **[aka.ms/Cobalt200-VM-Pr](https://aka.ms/Cobalt200-VM-Pr)** and allow time for approval before starting the lab.
>
> <img src="../img/cobalt200-preview-qr.png" alt="QR code - Cobalt 200 VM Preview signup" width="150"/>

## Prerequisites

You need:

- A **Windows 11 x64** machine with at least 8 cores and 16 GB RAM (32 GB recommended)
- **Nested virtualization support** (required for Docker Desktop + WSL2)
- An **Azure subscription** with **Cobalt 200 preview access** ([create a free account](https://azure.microsoft.com/free/) if you don't have one, then sign up for the preview above)

## Install required tools

Open an elevated **cmd.exe** (Run as Administrator) and install the following. If any tool is already installed, `winget` will report it and move on.

```cmd
winget install -e --id Microsoft.AzureCLI
winget install -e --id Kubernetes.kubectl
winget install -e --id Kubernetes.kustomize
winget install -e --id Git.Git
winget install -e --id Microsoft.DotNet.SDK.10
winget install -e --id Microsoft.WSL
```

> **Reboot required after WSL install.** If WSL was not already present, reboot the machine before continuing.

```cmd
shutdown /r /f /t 0
```

## Enable PowerShell script execution

After the reboot, set the execution policy to allow scripts:

```cmd
pwsh -Command "Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force"
```

## Trust the .NET developer certificate

```cmd
dotnet dev-certs https --trust
```

## Clone the repo

```cmd
git clone https://github.com/microsoft/Build26-LAB512-hands-on-with-the-latest-cobalt-vms-from-code-to-cloud-deployment.git C:\lab512

cd /d C:\lab512
```

## Install Docker Desktop

```cmd
winget install -e --id Docker.DockerDesktop
```

Docker Desktop installs the Hyper-V / Virtual Machine Platform features. A reboot is required before Docker Desktop can start:

```cmd
shutdown /r /f /t 0
```

## Configure Docker Desktop

After the reboot, start Docker Desktop and let it create its default settings file:

```cmd
docker desktop start
```

Wait until the Docker Desktop tray icon stops animating and the settings file exists. You can check with:

```cmd
type "%APPDATA%\Docker\settings-store.json"
```

If the file doesn't exist yet or is nearly empty, wait another 30 seconds and retry. Once you see a full JSON with multiple properties, update the settings to enable Kubernetes, pin the cluster provisioner to **Kubeadm** (the lab is built for the classic kubelet-on-host engine, not the newer kind-based engine), enable the containerd image store (required for multi-arch builds), and auto-start on login:

```cmd
pwsh -Command "$f=\"$env:APPDATA\Docker\settings-store.json\"; $j=Get-Content $f | ConvertFrom-Json; $j | Add-Member -Force KubernetesEnabled $true; $j | Add-Member -Force KubernetesMode 'kubeadm'; $j | Add-Member -Force UseContainerdSnapshotter $true; $j | Add-Member -Force AutoStart $true; $j | ConvertTo-Json -Depth 10 | Set-Content $f"
```

Restart Docker Desktop to apply the new settings:

```cmd
docker desktop restart
```

Wait until the Docker Desktop tray icon stops animating (Kubernetes takes 1-3 minutes on first boot), then verify:

```cmd
docker version --format "Client: {{.Client.Version}}  Server: {{.Server.Version}}"
```

You should see a single line with both Client and Server versions.

## Verify Kubernetes is running

```cmd
kubectl config use-context docker-desktop
kubectl get nodes
```

You should see a **Ready** node (e.g., `docker-desktop`).

## Verify multi-arch build capability

Docker Desktop with the containerd image store lets the default builder produce multi-arch manifest lists directly:

```cmd
docker buildx inspect default | findstr /C:"Platforms:" /C:"Status:"
```

You should see:

- the builder is running
- supported platforms include **`linux/amd64`** and **`linux/arm64`**

> **If you don't see both platforms**, the classic (non-containerd) image store may be in use. Enable containerd in **Docker Desktop Settings > General > Use containerd for pulling and storing images**, then restart Docker Desktop.

## You're ready

Your machine is set up. Continue to [Part 1 - Local build and deploy](part1-local-build-and-deploy.md).

#!/bin/bash
set -e

export DEBIAN_FRONTEND=noninteractive

echo "=== Installing build dependencies ==="
apt-get update
apt-get install -y --no-install-recommends cmake gcc g++ make git curl python3 python3-pip python3-dev ca-certificates
pip3 install requests --break-system-packages

echo "=== Downloading ONNX Runtime 1.25.1 ARM64 binaries ==="
cd /tmp
curl -fSL "https://github.com/microsoft/onnxruntime/releases/download/v1.25.1/onnxruntime-linux-aarch64-1.25.1.tgz" -o ort.tgz
tar xzf ort.tgz

echo "=== Cloning onnxruntime-genai ==="
git clone --depth 1 https://github.com/microsoft/onnxruntime-genai.git
cd onnxruntime-genai

echo "=== Building native library ==="
python3 build.py --config Release --ort_home /tmp/onnxruntime-linux-aarch64-1.25.1 --skip_wheel --skip_tests --skip_examples

echo "=== Copying to output ==="
cp build/Linux/Release/libonnxruntime-genai.so /output/
echo "=== Done ==="
ls -lh /output/libonnxruntime-genai.so

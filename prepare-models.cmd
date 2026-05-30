@echo off
REM Downloads ONNX models to the local models/ folder if not already present.
REM Used by both local Aspire dev (dotnet build) and Docker image builds.

setlocal

set MODELS_DIR=%~dp0models

if exist "%MODELS_DIR%\phi-4-mini\model.onnx.data" (
    if exist "%MODELS_DIR%\all-MiniLM-L6-v2\model.onnx" (
        if exist "%MODELS_DIR%\all-MiniLM-L6-v2\model_qint8_arm64.onnx" (
            echo Models already cached at %MODELS_DIR%
            exit /b 0
        )
    )
)

echo Downloading models to %MODELS_DIR% ...
dotnet msbuild src\Inference\Inference.csproj -t:DownloadModels -p:ModelsDir="%MODELS_DIR%"

if %ERRORLEVEL% neq 0 (
    echo ERROR: Model download failed.
    exit /b 1
)

echo Models ready at %MODELS_DIR%

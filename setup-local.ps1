param([string]$PythonExecutable = "")

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($pythonCommand) { $PythonExecutable = $pythonCommand.Source }
    else {
        $bundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
        if (Test-Path -LiteralPath $bundledPython) { $PythonExecutable = $bundledPython }
    }
}
if (-not (Test-Path -LiteralPath $PythonExecutable)) {
    throw 'Python 3.12 not found. Use -PythonExecutable to specify python.exe.'
}
& $PythonExecutable -c 'import sys; assert sys.version_info[:2] == (3, 12) and sys.maxsize > 2**32'
if ($LASTEXITCODE -ne 0) { throw 'This setup script requires 64-bit Python 3.12.' }

$venvDir = Join-Path $projectRoot '.venv'
$venvPython = Join-Path $venvDir 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPython)) {
    & $PythonExecutable -m venv $venvDir
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create Python virtual environment.' }
}

$wheelDir = Join-Path $projectRoot '.wheels'
New-Item -ItemType Directory -Force -Path $wheelDir | Out-Null
$wheels = @(
    @{ Name='onnxruntime_directml-1.24.4-cp312-cp312-win_amd64.whl'; Url='https://pypi.tuna.tsinghua.edu.cn/packages/88/ea/33814eb0ec96775eda4c1d30b0d86e91d7d2cd0d84c66d3915aef0e06fa3/onnxruntime_directml-1.24.4-cp312-cp312-win_amd64.whl'; MinimumBytes=20000000 },
    @{ Name='numpy-2.5.3-cp312-cp312-win_amd64.whl'; Url='https://pypi.tuna.tsinghua.edu.cn/packages/3c/a1/accf6d4f0c80c5d9ba9735d6b1550e444180599f34dec69ca01360f717ad/numpy-2.5.3-cp312-cp312-win_amd64.whl'; MinimumBytes=10000000 },
    @{ Name='espeakng_loader-0.2.4-py3-none-win_amd64.whl'; Url='https://pypi.tuna.tsinghua.edu.cn/packages/9d/ed/a3d872fbad4f3a3f3db0e8c31768ab14e77cd77306de16b8b20b1e1df7ea/espeakng_loader-0.2.4-py3-none-win_amd64.whl'; MinimumBytes=8000000 }
)
foreach ($wheel in $wheels) {
    $target = Join-Path $wheelDir $wheel.Name
    if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -lt $wheel.MinimumBytes) {
        & curl.exe --fail --location --max-time 180 --output $target $wheel.Url
        if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $target).Length -lt $wheel.MinimumBytes) {
            throw "Incomplete wheel download: $($wheel.Name)"
        }
    }
    & $venvPython -m pip install $target --no-deps
    if ($LASTEXITCODE -ne 0) { throw "Failed to install: $($wheel.Name)" }
}
& $venvPython -m pip install 'phonemizer>=3.4.0' flatbuffers packaging protobuf sympy
if ($LASTEXITCODE -ne 0) { throw 'Failed to install local inference dependencies.' }

$modelsDir = Join-Path $projectRoot 'models'
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
$assets = @(
    @{ Name='en_US-lessac-medium.onnx'; Url='https://hf-mirror.com/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx'; MinimumBytes=60000000 },
    @{ Name='en_US-lessac-medium.onnx.json'; Url='https://hf-mirror.com/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json'; MinimumBytes=4000 }
)
foreach ($asset in $assets) {
    $target = Join-Path $modelsDir $asset.Name
    if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -ge $asset.MinimumBytes) { continue }
    $temporary = "$target.download"
    & curl.exe --fail --location --continue-at - --max-time 900 --output $temporary $asset.Url
    if ($LASTEXITCODE -ne 0) { throw "Model download failed: $($asset.Name)" }
    if ((Get-Item -LiteralPath $temporary).Length -lt $asset.MinimumBytes) {
        throw "Incomplete model download: $($asset.Name)"
    }
    Move-Item -LiteralPath $temporary -Destination $target -Force
}
Write-Host 'Local Piper TTS model is ready.'

# 安装 SAM 3D Body 运行环境（在 backend 目录下执行）
#   1. 创建 .venv 并安装 CUDA 版 PyTorch
#   2. clone 上游仓库到 third_party/sam-3d-body
#   3. 安装 posegraph 及 sam3d 可选依赖
#   4. 下载权重并校验 SHA256
#      -Source modelscope（默认）：魔搭镜像，无需登录，国内速度快
#      -Source hf：Hugging Face 官方 gated 仓库，需先申请访问并 `hf auth login`
param(
    [ValidateSet("vith", "dinov3")][string]$Model = "vith",   # vith（631M）或 dinov3（840M）
    [ValidateSet("modelscope", "hf")][string]$Source = "modelscope",
    [string]$TorchIndex = "https://download.pytorch.org/whl/cu128",
    [switch]$SkipWeights
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$py = Join-Path $root ".venv\Scripts\python.exe"

# Hugging Face 官方仓库的 LFS SHA256（2025-11-19 发布版），任何来源下载后都用它校验
$officialSha256 = @{
    "vith"   = @{ "model.ckpt" = "3b1cb897f4bbd977bf81cbb0b30780a9582681ac642ee112865790ceb4d66056"
                  "assets/mhr_model.pt" = "352e271a6c42729c68554ceaea0c955e866970160c31e35506d782dc0f7377bc" }
    "dinov3" = @{ "model.ckpt" = "b5a2f9d305dd02626b967aa2e86021fba07065df66ce7a7e00ffb9664f150abf"
                  "assets/mhr_model.pt" = "352e271a6c42729c68554ceaea0c955e866970160c31e35506d782dc0f7377bc" }
}

if (-not (Test-Path $py)) {
    python -m venv (Join-Path $root ".venv")
    & $py -m pip install -U pip
}
& $py -c "import torch, sys; sys.exit(0 if torch.cuda.is_available() else 1)" 2>$null
if ($LASTEXITCODE -ne 0) {
    & $py -m pip install torch torchvision --index-url $TorchIndex
}

$repo = Join-Path $root "third_party\sam-3d-body"
if (-not (Test-Path $repo)) {
    git clone --depth 1 https://github.com/facebookresearch/sam-3d-body.git $repo
}

& $py -m pip install -e "$root[sam3d]"

if ($SkipWeights) { return }

$dest = Join-Path $root "checkpoints\sam-3d-body-$Model"
if ($Source -eq "hf") {
    & (Join-Path $root ".venv\Scripts\hf.exe") download "facebook/sam-3d-body-$Model" --local-dir $dest
} else {
    $base = "https://www.modelscope.cn/models/facebook/sam-3d-body-$Model/resolve/master"
    New-Item -ItemType Directory -Force (Join-Path $dest "assets") | Out-Null
    foreach ($f in "model_config.yaml", "LICENSE", "assets/mhr_model.pt", "model.ckpt") {
        curl.exe -L -C - --retry 3 -o (Join-Path $dest $f) "$base/$f"
        if ($LASTEXITCODE -ne 0) { throw "下载失败：$f" }
    }
}

# 权重用 torch.load(weights_only=False) / TorchScript 加载，会执行文件内代码，必须校验来源
foreach ($kv in $officialSha256[$Model].GetEnumerator()) {
    $actual = (Get-FileHash (Join-Path $dest $kv.Key) -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $kv.Value) { throw "SHA256 不匹配：$($kv.Key) 实际 $actual" }
}
Write-Host "权重校验通过：$dest"

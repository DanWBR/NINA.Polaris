<#
.SYNOPSIS
  Orchestrate the polaris-ai model pipeline (data prep, fp32 + QAT training,
  fp16/int16/int8 export, evaluation) for one or more tasks on a chosen GPU.

.DESCRIPTION
  Tasks: bge | denoise | decon | upscale. Each full lane runs:
    train fp32 -> train --qat (int8 from scratch) -> export fp16
    -> calib -> int16 PTQ -> int8 PTQ -> eval.
  Data lives under data/own/ and is gitignored. Run -Prep ONCE (shared by both
  GPUs) before launching the two training lanes.

.EXAMPLE
  ./run_all.ps1 -Prep
  # prepare datasets once (all four tasks)

.EXAMPLE
  ./run_all.ps1 -Gpu 0 -Tasks denoise
  ./run_all.ps1 -Gpu 1 -Tasks bge,decon,upscale
  # two GPUs in parallel, in two PowerShell windows

.EXAMPLE
  ./run_all.ps1 -Gpu 0 -Tasks decon -NoQat -Batch 4
  # skip QAT (PTQ only), override batch size
#>
param(
    [int]$Gpu = -1,
    [string]$Tasks = "",
    [switch]$Prep,
    [switch]$NoQat,
    [int]$Workers = 4,
    [int]$Batch = 0,            # 0 = per-phase defaults (8 fp32 / 6 qat)
    [string]$Models = "models"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:PYTHONIOENCODING = "utf-8"
if ($Gpu -ge 0) {
    # Match CUDA indices to nvidia-smi order so -Gpu N selects the card you expect
    # (default CUDA order is by speed, not PCI bus).
    $env:CUDA_DEVICE_ORDER = "PCI_BUS_ID"
    $env:CUDA_VISIBLE_DEVICES = "$Gpu"
}

# Per-task config. kind=tiles -> DeconDataset (--tiles); kind=pairs ->
# PairedTileDataset (--pairs/--val-pairs). size = exported ONNX spatial size
# (LR size for upscale). scale only set for upscale.
$cfg = @{
    decon   = @{ kind = "tiles"; data = "data/own/decon_tiles"; val = "data/own/decon_tiles_val"; base = 96; blocks = 3; fp = 60;  qat = 20; size = 256 }
    denoise = @{ kind = "pairs"; data = "data/own/denoise_tiles"; val = "data/own/denoise_val";    base = 96; blocks = 3; fp = 80;  qat = 20; size = 256 }
    bge     = @{ kind = "pairs"; data = "data/own/bge_tiles";     val = "data/own/bge_val";        base = 96; blocks = 3; fp = 120; qat = 25; size = 256 }
    upscale = @{ kind = "pairs"; data = "data/own/upscale_tiles"; val = "data/own/upscale_val";    base = 64; blocks = 2; fp = 100; qat = 20; size = 128; scale = 2 }
}

function Invoke-Step([string]$desc, [string[]]$pyArgs) {
    Write-Host "==> $desc" -ForegroundColor Cyan
    & python @pyArgs
    if ($LASTEXITCODE -ne 0) { throw "FAILED: $desc (exit $LASTEXITCODE)" }
}

function Get-DataArgs($c, [bool]$withVal) {
    $a = @()
    if ($c.kind -eq "tiles") {
        $a += @("--tiles", $c.data)
        if ($withVal -and $c.val) { $a += @("--val-tiles", $c.val) }
    } else {
        $a += @("--pairs", $c.data)
        if ($withVal) { $a += @("--val-pairs", $c.val) }
    }
    if ($c.ContainsKey("scale")) { $a += @("--scale", "$($c.scale)") }
    return $a
}

# Resolve the task list.
if ([string]::IsNullOrWhiteSpace($Tasks)) {
    if ($Prep) { $list = @("denoise", "bge", "decon", "upscale") }
    else { throw "Specify -Tasks (e.g. -Tasks bge,decon) or use -Prep." }
} else {
    $list = $Tasks.Split(",") | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ }
}
foreach ($t in $list) { if (-not $cfg.ContainsKey($t)) { throw "Unknown task '$t' (bge|denoise|decon|upscale)." } }

# ---- Prep mode: generate datasets once, then exit ----
if ($Prep) {
    foreach ($t in $list) {
        switch ($t) {
            "decon"   { Invoke-Step "prep decon"   @("data_prep/make_distortions.py", "--previews", "3") }
            "denoise" { Invoke-Step "prep denoise" @("data_prep/make_noise.py", "--per-image", "3") }
            "bge"     { Invoke-Step "prep bge"     @("data_prep/make_gradients.py", "--per-image", "40") }
            "upscale" { Invoke-Step "prep upscale" @("data_prep/make_upscale.py", "--scale", "2", "--hr-dir", "denoised") }
        }
    }
    Write-Host "Prep done. Now launch training lanes, e.g. -Gpu 0 -Tasks denoise." -ForegroundColor Green
    return
}

# Verify the requested GPU is actually usable -- fail loudly instead of silently
# training on CPU (e.g. a 'GPU is lost' card, wrong index, or no driver).
$gpuName = & python -c "import torch; print(torch.cuda.get_device_name(0)) if torch.cuda.is_available() else print('NO_CUDA')"
if ($Gpu -ge 0 -and $gpuName -eq "NO_CUDA") {
    throw "Requested -Gpu $Gpu but CUDA is not available for it (device lost / not present / wrong index). Check 'nvidia-smi'. Aborting instead of training on CPU."
}
Write-Host "Using device: $gpuName" -ForegroundColor Green

$fpBatch = if ($Batch -gt 0) { $Batch } else { 8 }
$qatBatch = if ($Batch -gt 0) { $Batch } else { 6 }
Write-Host ("GPU={0} tasks={1} qat={2}" -f $Gpu, ($list -join ","), (-not $NoQat)) -ForegroundColor Yellow

foreach ($t in $list) {
    $c = $cfg[$t]
    if (-not (Test-Path $c.data)) { throw "Missing $($c.data) -- run './run_all.ps1 -Prep -Tasks $t' first." }

    # fp32
    $tr = @("train_task.py", "--task", $t, "--epochs", "$($c.fp)", "--batch", "$fpBatch",
        "--workers", "$Workers", "--base", "$($c.base)", "--blocks", "$($c.blocks)",
        "--out", "checkpoints/$t") + (Get-DataArgs $c $true)
    Invoke-Step "train $t fp32 $($c.fp)ep" $tr

    $ckpt = "checkpoints/$t/best.pt"

    # QAT (int8 from scratch, fine-tuned from fp32)
    if (-not $NoQat) {
        $qa = @("train_task.py", "--task", $t, "--qat", "--resume", "checkpoints/$t/best.pt",
            "--lr", "5e-5", "--epochs", "$($c.qat)", "--batch", "$qatBatch",
            "--workers", "$Workers", "--base", "$($c.base)", "--blocks", "$($c.blocks)",
            "--out", "checkpoints/${t}_qat") + (Get-DataArgs $c $true)
        Invoke-Step "train $t qat $($c.qat)ep" $qa
        $ckpt = "checkpoints/${t}_qat/best.pt"
    }

    # export fp32 + fp16
    $ex = @("export.py", "--task", $t, "--ckpt", $ckpt, "--base", "$($c.base)",
        "--blocks", "$($c.blocks)", "--size", "$($c.size)", "--out", $Models)
    if ($c.ContainsKey("scale")) { $ex += @("--scale", "$($c.scale)") }
    Invoke-Step "export $t" $ex

    # calibration set
    $cal = @("quantize.py", "calib", "--task", $t, "--out", "$Models/calib_$t")
    if ($c.kind -eq "tiles") { $cal += @("--tiles", $c.data) } else { $cal += @("--pairs", $c.data) }
    Invoke-Step "calib $t" $cal

    # int16 + int8 PTQ
    $fp32 = "$Models/${t}_fp32_$($c.size).onnx"
    Invoke-Step "int16 $t" @("quantize.py", "int16", "--onnx", $fp32, "--calib", "$Models/calib_$t", "--out", "$Models/${t}_int16_$($c.size).onnx")
    Invoke-Step "int8 $t"  @("quantize.py", "int8", "--onnx", $fp32, "--calib", "$Models/calib_$t", "--out", "$Models/${t}_int8_$($c.size).onnx")

    # eval (PSNR/SSIM per precision)
    $ev = @("eval_models.py", "--task", $t, "--models", $Models, "--size", "$($c.size)")
    if ($c.kind -eq "tiles") { $ev += @("--tiles-val", $c.val) } else { $ev += @("--val-pairs", $c.val) }
    Invoke-Step "eval $t" $ev

    Write-Host "DONE: $t -> $Models/${t}_fp16/int16/int8_$($c.size).onnx" -ForegroundColor Green
}

Write-Host "All requested tasks complete." -ForegroundColor Green

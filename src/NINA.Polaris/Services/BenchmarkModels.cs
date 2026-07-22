// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

// Data models / DTOs extracted from BenchmarkService.cs for readability.
// Plain serialisable types owned by BenchmarkService; no behaviour here.

namespace NINA.Polaris.Services;

// ----- DTOs -----

public record BenchmarkRequest(
    bool IncludeCamera = false,
    double CameraExposure = 1.0,
    int? CameraGain = null,
    int CameraFrames = 5,
    // High-fps planetary video probe. VideoRoi > 0 sets a centered square
    // subframe before the video test (small ROI is what lets a camera hit
    // 100+ fps; a full-frame OSC can't). MeasureRecording also runs a real
    // SER write to the image-output volume during the window so the report
    // includes the recording path's sustainable fps + dropped frames.
    int VideoRoi = 0,
    bool MeasureRecording = false);

public record BenchmarkDevice(
    string Kind, string Model, string Os, string Architecture,
    int Cores, string ShortLabel, string? Cpu, string? CpuLabel);

public record StackingResult(
    double DetectMs, double MatchMs, double ResampleMs, double StatsMs,
    double TotalMs, double Fps, double MpxPerSec, int Iterations, int StarCount);

public record EncodeResult(
    double DebayerMs, double JpegMs, double Lz4Ms, double TotalMs,
    double Fps, double MpxPerSec, double Lz4MBps, int Iterations);

public record CpuResult(
    double SingleThreadMflops, double MultiThreadMflops,
    double CoreScaling, double MemBandwidthGBps, int Cores);

// OCL: GPU (OpenCL) vs CPU on the same image kernels. MpxPerSec is megapixels
// processed per second; Speedup = gpu / cpu. OverallSpeedup is the geometric
// mean of the per-op speedups (not arithmetic — see GpuOverallSpeedup), so a
// single large win doesn't mask ops that are slower on the GPU. Ran=false when
// no usable GPU.
public record GpuResult(
    bool Ran, string Device,
    double WarpCpuMpxPerSec, double WarpGpuMpxPerSec, double WarpSpeedup,
    double DebayerCpuMpxPerSec, double DebayerGpuMpxPerSec, double DebayerSpeedup,
    double BlurCpuMpxPerSec, double BlurGpuMpxPerSec, double BlurSpeedup,
    double OverallSpeedup);

// QNN-5: NPU (AI inference) column. Times a real GraXpert Denoise on the board's
// NPU — Qualcomm Hexagon (QAIRT) or Rockchip RKNPU2 — over a fixed synthetic
// frame and reports per-tile cost. Precision is the model's dtype (int16/int8 on
// the QCS6490 HTP, which is integer-only; fp16 on RKNN). Ran=false off an NPU
// host or when no denoise model is bundled (Error carries the reason) — same
// convention as the GPU row.
public record NpuResult(
    bool Ran, string Backend, string Model, string Precision,
    double MsPerTile, double TilesPerSec, int Tiles,
    int Width, int Height, string? Error);

// THERM: thermal + clock trace sampled during the sustained CPU workload, so a
// run's score can be read against whether the board throttled. MaxTempC is the
// hottest thermal zone seen; Clock{Min,Avg,Max}Mhz track the fastest running
// core over the window; RatedMaxMhz is the SoC's advertised ceiling. Throttled =
// the sustained clock fell below that ceiling under load. Cause is a hint at why
// ("thermal" when it was also hot, "power" when it throttled while cool - the
// undervoltage signature). Ran=false off a Linux sysfs host (Error carries why),
// same convention as the GPU/NPU rows.
public record ThermalResult(
    bool Ran,
    double StartTempC, double MaxTempC, double EndTempC,
    int RatedMaxMhz, int ClockMinMhz, int ClockAvgMhz, int ClockMaxMhz,
    bool Throttled, string? Cause, int Samples, string? Error);

public record CameraResult(
    int Frames, double MeanCaptureMs, double Fps,
    int Width, int Height, double MBPerSec, string? Error);

public record CameraVideoResult(
    string Mode, double CaptureFps, double TransmitFps,
    int Width, int Height, double MBPerSec,
    long Frames, int DurationSec, string? Error,
    // Recording path (only populated when MeasureRecording was requested):
    // RecordFps = SER frames actually written/sec, DroppedFrames = frames
    // the writer couldn't keep up with, MeanWriteMs = avg per-frame write.
    double RecordFps = 0, long DroppedFrames = 0, double MeanWriteMs = 0);

public record BenchmarkResult(
    string Timestamp,
    BenchmarkDevice Device,
    int FrameWidth, int FrameHeight, double Megapixels,
    StackingResult Stacking,
    EncodeResult Encode,
    CpuResult Cpu,
    double CompositeScore,
    CameraResult? Camera,
    CameraVideoResult? CameraVideo = null,
    GpuResult? Gpu = null,
    NpuResult? Npu = null,
    ThermalResult? Thermal = null);
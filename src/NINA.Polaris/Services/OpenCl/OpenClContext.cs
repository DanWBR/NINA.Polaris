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

using System.Text;
using Silk.NET.OpenCL;

namespace NINA.Polaris.Services.OpenCl;

/// <summary>
/// Owns the OpenCL device + context + command queue + compiled program for the
/// SBC GPU, and a cache of built kernels by name. Construction probes for a GPU
/// device, builds <c>kernels/image_kernels.cl</c>, and throws on any failure so
/// the caller (<see cref="OpenClGpuCompute"/>) can fall back to the CPU. This is
/// the real authority on whether the GPU path is usable (the cheap
/// <see cref="OpenClRuntime"/> probe only checks the loader is present).
///
/// One context per process; the command queue is serialized behind <see cref="Gate"/>
/// because the live-stack background loop and editor previews can dispatch
/// concurrently.
/// </summary>
public sealed unsafe class OpenClContext : IDisposable {
    private readonly CL _cl;
    private readonly nint _context;
    private readonly nint _queue;
    private readonly nint _program;
    private readonly nint _device;
    private readonly Dictionary<string, nint> _kernels = new();

    /// <summary>Serializes command-queue use across threads.</summary>
    public object Gate { get; } = new();

    /// <summary>Human-readable device name (CL_DEVICE_NAME), for status/logs.</summary>
    public string DeviceName { get; }

    /// <summary>
    /// CL_DEVICE_HOST_UNIFIED_MEMORY: true when host and device share physical
    /// memory (the SBC GPUs we target — Mali/Adreno — where buffer copies are
    /// effectively zero-cost), false for a discrete GPU behind PCIe where every
    /// host&lt;-&gt;device transfer has real cost. Used to decide whether to
    /// offload the light per-op kernels (worth it on unified memory, often a net
    /// loss on a discrete card). On query failure we assume unified (true) so the
    /// SBC path — the primary target — is never penalised by a probe.
    /// </summary>
    public bool HostUnifiedMemory { get; }

    public CL Cl => _cl;
    public nint Context => _context;
    public nint Queue => _queue;

    /// <summary>
    /// Build a context on the first GPU device of the first platform that has
    /// one, compile the kernel program, and cache every kernel. Throws on any
    /// OpenCL error (the caller treats that as "GPU unavailable").
    /// </summary>
    public OpenClContext(string kernelSource) {
        _cl = CL.GetApi();

        // --- pick a platform that exposes a GPU device ---
        uint platformCount = 0;
        Check(_cl.GetPlatformIDs(0, null, &platformCount), "GetPlatformIDs(count)");
        if (platformCount == 0) throw new InvalidOperationException("No OpenCL platforms.");
        var platforms = stackalloc nint[(int)platformCount];
        Check(_cl.GetPlatformIDs(platformCount, platforms, null), "GetPlatformIDs");

        nint device = 0;
        for (int i = 0; i < platformCount; i++) {
            uint devCount = 0;
            int r = _cl.GetDeviceIDs(platforms[i], DeviceType.Gpu, 0, null, &devCount);
            if (r != 0 || devCount == 0) continue;
            var devices = stackalloc nint[(int)devCount];
            if (_cl.GetDeviceIDs(platforms[i], DeviceType.Gpu, devCount, devices, null) != 0) continue;
            device = devices[0];
            break;
        }
        if (device == 0) throw new InvalidOperationException("No OpenCL GPU device.");
        _device = device;
        DeviceName = QueryDeviceName(device);
        HostUnifiedMemory = QueryHostUnifiedMemory(device);

        // --- context + command queue ---
        int err;
        _context = _cl.CreateContext(null, 1, &device, null, null, &err);
        Check(err, "CreateContext");
        _queue = _cl.CreateCommandQueue(_context, device, CommandQueueProperties.None, &err);
        Check(err, "CreateCommandQueue");

        // --- build the program ---
        var srcBytes = Encoding.ASCII.GetBytes(kernelSource);
        fixed (byte* srcPtr = srcBytes) {
            byte* one = srcPtr;
            nuint len = (nuint)srcBytes.Length;
            _program = _cl.CreateProgramWithSource(_context, 1, in one, &len, &err);
        }
        Check(err, "CreateProgramWithSource");
        int build = _cl.BuildProgram(_program, 1, &device, (byte*)null, null, null);
        if (build != 0) {
            var log = BuildLog(device);
            throw new InvalidOperationException($"OpenCL build failed ({build}): {log}");
        }
    }

    /// <summary>Get (and cache) a kernel by its name in the program source.</summary>
    public nint GetKernel(string name) {
        if (_kernels.TryGetValue(name, out var k)) return k;
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        int err;
        fixed (byte* p = bytes) {
            k = _cl.CreateKernel(_program, p, &err);
        }
        Check(err, $"CreateKernel({name})");
        _kernels[name] = k;
        return k;
    }

    private string QueryDeviceName(nint device) {
        try {
            nuint size = 0;
            _cl.GetDeviceInfo(device, DeviceInfo.Name, 0, null, &size);
            if (size == 0) return "OpenCL GPU";
            var buf = new byte[(int)size];
            fixed (byte* p = buf) _cl.GetDeviceInfo(device, DeviceInfo.Name, size, p, null);
            return Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
        } catch { return "OpenCL GPU"; }
    }

    private bool QueryHostUnifiedMemory(nint device) {
        try {
            // cl_bool is a 4-byte uint (CL_TRUE == 1).
            uint val = 0;
            // CL_DEVICE_HOST_UNIFIED_MEMORY is marked deprecated since OpenCL 2.0,
            // but it remains the portable way to tell shared- from discrete-memory
            // and is still reported by the SBC drivers (Mali/Adreno) and desktop
            // ICDs we target; there is no non-deprecated equivalent for this query.
#pragma warning disable CS0618 // Type or member is obsolete
            int err = _cl.GetDeviceInfo(device, DeviceInfo.HostUnifiedMemory,
                (nuint)sizeof(uint), &val, null);
#pragma warning restore CS0618
            if (err != 0) return true; // unknown -> assume unified (don't penalise SBCs)
            return val != 0;
        } catch { return true; }
    }

    private string BuildLog(nint device) {
        try {
            nuint size = 0;
            _cl.GetProgramBuildInfo(_program, device, ProgramBuildInfo.BuildLog, 0, null, &size);
            if (size == 0) return "(no log)";
            var buf = new byte[(int)size];
            fixed (byte* p = buf) _cl.GetProgramBuildInfo(_program, device, ProgramBuildInfo.BuildLog, size, p, null);
            return Encoding.ASCII.GetString(buf).TrimEnd('\0');
        } catch { return "(log unavailable)"; }
    }

    private static void Check(int err, string where) {
        if (err != 0) throw new InvalidOperationException($"OpenCL error {err} at {where}");
    }

    public void Dispose() {
        try {
            foreach (var k in _kernels.Values) _cl.ReleaseKernel(k);
            if (_program != 0) _cl.ReleaseProgram(_program);
            if (_queue != 0) _cl.ReleaseCommandQueue(_queue);
            if (_context != 0) _cl.ReleaseContext(_context);
            _cl.Dispose();
        } catch { /* best effort */ }
    }
}

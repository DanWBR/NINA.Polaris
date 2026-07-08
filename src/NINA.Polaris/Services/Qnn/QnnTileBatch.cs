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

using System.Diagnostics;
using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Services.Qnn;

/// <summary>
/// Runs a whole image's worth of model tiles on the Hexagon HTP in ONE batch.
/// Unlike the Rockchip path (a flat C ABI we P/Invoke per tile), the QAIRT/QNN
/// API is an interface-provider table that's painful to marshal, so we drive
/// the validated <c>qnn-net-run</c> tool over a batched <c>--input_list</c>
/// (one process per IMAGE, not per tile) — exactly the path proven on the Q6A.
///
/// The whole GraXpert tiling/normalization/artifact-correction math is the
/// canonical <see cref="RknnPipelines"/>, which is built around a per-tile
/// <see cref="IRknnTileRunner"/>. To reuse it unchanged we don't rewrite it for
/// batching; instead we run it TWICE with cheap CPU-only runners:
/// <list type="number">
/// <item><see cref="RecordingTileRunner"/> — pass 1 captures the ordered input
/// tensors (returns zeros; that pass's output is discarded).</item>
/// <item><see cref="IQnnTileBatch.RunBatch"/> — the captured tensors run on the
/// NPU in one go.</item>
/// <item><see cref="ReplayingTileRunner"/> — pass 2 feeds the captured outputs
/// back in the same order, producing the real result.</item>
/// </list>
/// The pipeline is deterministic and a tile's input never depends on a prior
/// tile's output, so the call order is identical across the two passes. The
/// double CPU pass is negligible next to the inference.
/// </summary>
public interface IQnnTileBatch : IDisposable {
    int TileSize { get; }
    int Channels { get; }

    /// <summary>Run every tile (each row-major NHWC fp32, length
    /// <c>TileSize*TileSize*Channels</c>) and return one output per input, in the
    /// same order, each a fresh fp32 array of length <c>TileSize*TileSize*3</c>.</summary>
    float[][] RunBatch(IReadOnlyList<float[]> tiles);
}

/// <summary>Pass-1 runner: records each input tensor (a defensive copy) and
/// returns a zero output of the model's shape (discarded). Pure CPU.</summary>
public sealed class RecordingTileRunner : IRknnTileRunner {
    private readonly int _outLen;
    public int TileSize { get; }
    public int Channels { get; }
    public List<float[]> Inputs { get; } = new();

    public RecordingTileRunner(int tileSize, int channels) {
        TileSize = tileSize;
        Channels = channels;
        _outLen = tileSize * tileSize * 3;   // GraXpert models output 3 channels
    }

    public float[] RunTile(float[] nhwcInput) {
        Inputs.Add((float[])nhwcInput.Clone());   // caller reuses the buffer
        return new float[_outLen];
    }

    public void Dispose() { }
}

/// <summary>Pass-2 runner: returns the pre-computed batch outputs in call order.
/// Pure CPU.</summary>
public sealed class ReplayingTileRunner : IRknnTileRunner {
    private readonly float[][] _outputs;
    private int _i;
    public int TileSize { get; }
    public int Channels { get; }

    public ReplayingTileRunner(int tileSize, int channels, float[][] outputs) {
        TileSize = tileSize;
        Channels = channels;
        _outputs = outputs;
    }

    public float[] RunTile(float[] nhwcInput) {
        if (_i >= _outputs.Length)
            throw new QnnException($"replay overrun: pipeline asked for tile {_i} but only " +
                                   $"{_outputs.Length} were batched (pass mismatch)");
        return _outputs[_i++];
    }

    public void Dispose() { }
}

/// <summary>
/// Real batch executor: shells out to <c>qnn-net-run</c> against a pre-built HTP
/// context binary with unsigned-PD enabled. Writes each tile as a raw fp32 file,
/// builds the <c>--input_list</c>, runs once, reads the per-tile outputs back.
///
/// NOTE: the exact <c>qnn-net-run</c> output layout (per-input <c>Result_N/</c>
/// dirs holding the output tensor as a <c>.raw</c>) is device-validated on the
/// Q6A; this is the only piece not exercised by the Windows unit tests (which
/// substitute a fake <see cref="IQnnTileBatch"/>).
/// </summary>
public sealed class QnnNetRunBatch : IQnnTileBatch {
    private readonly string _contextBin;
    private readonly ILogger _logger;
    private readonly int _outLen;

    public int TileSize { get; }
    public int Channels { get; }

    public QnnNetRunBatch(string contextBin, int tileSize, int channels, ILogger logger) {
        _contextBin = contextBin;
        TileSize = tileSize;
        Channels = channels;
        _logger = logger;
        _outLen = tileSize * tileSize * 3;
    }

    public float[][] RunBatch(IReadOnlyList<float[]> tiles) {
        if (tiles.Count == 0) return Array.Empty<float[]>();
        var work = Directory.CreateTempSubdirectory("polaris-qnn-");
        try {
            // Stage inputs + the input_list.
            var listPath = Path.Combine(work.FullName, "inputs.txt");
            using (var lw = new StreamWriter(listPath)) {
                for (int i = 0; i < tiles.Count; i++) {
                    var raw = Path.Combine(work.FullName, $"in_{i}.raw");
                    WriteFloats(raw, tiles[i]);
                    lw.WriteLine(raw);
                }
            }

            // Backend-extensions config enabling unsigned PD on the V68 HTP
            // (the cDSP rejects unsigned images otherwise — see QNN-0 notes).
            var htpCfg = Path.Combine(work.FullName, "htp.json");
            File.WriteAllText(htpCfg, "{ \"devices\": [ { \"dsp_arch\": \"v68\", \"pd_session\": \"unsigned\" } ] }");
            var beCfg = Path.Combine(work.FullName, "backend_ext.json");
            File.WriteAllText(beCfg,
                "{ \"backend_extensions\": { \"shared_library_path\": \"libQnnHtpNetRunExtensions.so\", "
                + "\"config_file_path\": \"" + htpCfg.Replace("\\", "/") + "\" } }");

            var outDir = Path.Combine(work.FullName, "out");
            Directory.CreateDirectory(outDir);

            RunNetRun(listPath, beCfg, outDir);

            // Read outputs back in input order: qnn-net-run writes one
            // Result_<N>/ dir per input, each holding the output tensor .raw.
            var outputs = new float[tiles.Count][];
            for (int i = 0; i < tiles.Count; i++) {
                var resultDir = Path.Combine(outDir, $"Result_{i}");
                var raw = Directory.Exists(resultDir)
                    ? Directory.EnumerateFiles(resultDir, "*.raw").FirstOrDefault()
                    : null;
                if (raw == null)
                    throw new QnnException($"qnn-net-run produced no output for tile {i} (looked in {resultDir})");
                outputs[i] = ReadFloats(raw, _outLen);
            }
            return outputs;
        } finally {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private void RunNetRun(string inputList, string backendExt, string outDir) {
        var psi = new ProcessStartInfo {
            FileName = QnnRuntime.NetRunPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add(QnnRuntime.HtpBackendPath);
        psi.ArgumentList.Add("--retrieve_context"); psi.ArgumentList.Add(_contextBin);
        psi.ArgumentList.Add("--config_file"); psi.ArgumentList.Add(backendExt);
        psi.ArgumentList.Add("--input_list"); psi.ArgumentList.Add(inputList);
        psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
        // The HTP backend + DSP skel are resolved through these paths.
        var libDir = Path.Combine(QnnRuntime.QairtRoot, "lib");
        var existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLd) ? libDir : $"{libDir}:{existingLd}";
        psi.Environment["ADSP_LIBRARY_PATH"] = QnnRuntime.AdspLibraryPath;

        using var p = Process.Start(psi) ?? throw new QnnException("failed to start qnn-net-run");
        string stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new QnnException($"qnn-net-run exited {p.ExitCode}: {Tail(stderr)}");
    }

    private static void WriteFloats(string path, float[] data) {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        foreach (var f in data) bw.Write(f);
    }

    private static float[] ReadFloats(string path, int expected) {
        var bytes = File.ReadAllBytes(path);
        int n = bytes.Length / sizeof(float);
        var outp = new float[Math.Max(n, expected)];
        Buffer.BlockCopy(bytes, 0, outp, 0, n * sizeof(float));
        return outp;
    }

    private static string Tail(string s, int max = 400) =>
        string.IsNullOrEmpty(s) ? "(no stderr)" : (s.Length <= max ? s : s[^max..]);

    public void Dispose() { }
}

// ─── Deconvolution: two-input (image + params) batch ──────────────────────

/// <summary>Batch executor for the GraXpert deconvolution models, which take a
/// SECOND input (the <c>params</c> tensor <c>[sigmaNorm, effStrength]</c>) beyond
/// the image tile. The params are constant for the whole image, so they're
/// written once and broadcast to every tile's <c>--input_list</c> line.</summary>
public interface IQnnDeconBatch : IDisposable {
    int TileSize { get; }

    /// <summary>Run every image tile (row-major <c>[1,1,TileSize,TileSize]</c> fp32)
    /// paired with the shared <paramref name="pars"/> <c>[sigmaNorm, effStrength]</c>,
    /// returning one <c>TileSize*TileSize</c> residual per input in order.</summary>
    float[][] RunBatch(IReadOnlyList<float[]> imageTiles, float[] pars);
}

/// <summary>Pass-1 decon runner: records each image tile + the (constant) params,
/// returns a zero residual (discarded). Pure CPU.</summary>
public sealed class DeconRecordingTileRunner : IRknnDeconTileRunner {
    public int TileSize { get; }
    public List<float[]> Inputs { get; } = new();
    public float[]? Params { get; private set; }

    public DeconRecordingTileRunner(int tileSize) { TileSize = tileSize; }

    public float[] RunTile(float[] chwInput, float[] pars) {
        Inputs.Add((float[])chwInput.Clone());
        Params ??= (float[])pars.Clone();
        return new float[chwInput.Length];
    }

    public void Dispose() { }
}

/// <summary>Pass-2 decon runner: returns the pre-computed batch residuals in call
/// order. Pure CPU.</summary>
public sealed class DeconReplayingTileRunner : IRknnDeconTileRunner {
    private readonly float[][] _outputs;
    private int _i;
    public int TileSize { get; }

    public DeconReplayingTileRunner(int tileSize, float[][] outputs) {
        TileSize = tileSize;
        _outputs = outputs;
    }

    public float[] RunTile(float[] chwInput, float[] pars) {
        if (_i >= _outputs.Length)
            throw new QnnException($"decon replay overrun: asked for tile {_i} but only " +
                                   $"{_outputs.Length} were batched (pass mismatch)");
        return _outputs[_i++];
    }

    public void Dispose() { }
}

/// <summary>Real decon batch executor. Like <see cref="QnnNetRunBatch"/> but every
/// <c>--input_list</c> line carries TWO inputs in graph order — the image tile then
/// the shared params raw — matching the model's <c>[gen_input_image, params]</c>
/// input order. Device-validated on the Q6A only (Windows tests inject a fake).</summary>
public sealed class QnnDeconNetRunBatch : IQnnDeconBatch {
    private readonly string _contextBin;
    private readonly ILogger _logger;
    private readonly int _outLen;
    public int TileSize { get; }

    public QnnDeconNetRunBatch(string contextBin, int tileSize, ILogger logger) {
        _contextBin = contextBin;
        TileSize = tileSize;
        _logger = logger;
        _outLen = tileSize * tileSize;   // decon output is single-channel [1,1,T,T]
    }

    public float[][] RunBatch(IReadOnlyList<float[]> tiles, float[] pars) {
        if (tiles.Count == 0) return Array.Empty<float[]>();
        var work = Directory.CreateTempSubdirectory("polaris-qnn-decon-");
        try {
            // Shared params raw (written once, referenced by every line).
            var parsPath = Path.Combine(work.FullName, "params.raw");
            WriteFloats(parsPath, pars);

            var listPath = Path.Combine(work.FullName, "inputs.txt");
            using (var lw = new StreamWriter(listPath)) {
                for (int i = 0; i < tiles.Count; i++) {
                    var raw = Path.Combine(work.FullName, $"in_{i}.raw");
                    WriteFloats(raw, tiles[i]);
                    // Two inputs per line, in the model's graph-input order:
                    // image first, params second.
                    lw.WriteLine($"{raw} {parsPath}");
                }
            }

            var htpCfg = Path.Combine(work.FullName, "htp.json");
            File.WriteAllText(htpCfg, "{ \"devices\": [ { \"dsp_arch\": \"v68\", \"pd_session\": \"unsigned\" } ] }");
            var beCfg = Path.Combine(work.FullName, "backend_ext.json");
            File.WriteAllText(beCfg,
                "{ \"backend_extensions\": { \"shared_library_path\": \"libQnnHtpNetRunExtensions.so\", "
                + "\"config_file_path\": \"" + htpCfg.Replace("\\", "/") + "\" } }");

            var outDir = Path.Combine(work.FullName, "out");
            Directory.CreateDirectory(outDir);
            RunNetRun(listPath, beCfg, outDir);

            var outputs = new float[tiles.Count][];
            for (int i = 0; i < tiles.Count; i++) {
                var resultDir = Path.Combine(outDir, $"Result_{i}");
                var raw = Directory.Exists(resultDir)
                    ? Directory.EnumerateFiles(resultDir, "*.raw").FirstOrDefault()
                    : null;
                if (raw == null)
                    throw new QnnException($"qnn-net-run produced no output for decon tile {i} (looked in {resultDir})");
                outputs[i] = ReadFloats(raw, _outLen);
            }
            return outputs;
        } finally {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private void RunNetRun(string inputList, string backendExt, string outDir) {
        var psi = new ProcessStartInfo {
            FileName = QnnRuntime.NetRunPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add(QnnRuntime.HtpBackendPath);
        psi.ArgumentList.Add("--retrieve_context"); psi.ArgumentList.Add(_contextBin);
        psi.ArgumentList.Add("--config_file"); psi.ArgumentList.Add(backendExt);
        psi.ArgumentList.Add("--input_list"); psi.ArgumentList.Add(inputList);
        psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
        var libDir = Path.Combine(QnnRuntime.QairtRoot, "lib");
        var existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLd) ? libDir : $"{libDir}:{existingLd}";
        psi.Environment["ADSP_LIBRARY_PATH"] = QnnRuntime.AdspLibraryPath;

        using var p = Process.Start(psi) ?? throw new QnnException("failed to start qnn-net-run");
        string stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new QnnException($"qnn-net-run (decon) exited {p.ExitCode}: {Tail(stderr)}");
    }

    private static void WriteFloats(string path, float[] data) {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        foreach (var f in data) bw.Write(f);
    }

    private static float[] ReadFloats(string path, int expected) {
        var bytes = File.ReadAllBytes(path);
        int n = bytes.Length / sizeof(float);
        var outp = new float[Math.Max(n, expected)];
        Buffer.BlockCopy(bytes, 0, outp, 0, n * sizeof(float));
        return outp;
    }

    private static string Tail(string s, int max = 400) =>
        string.IsNullOrEmpty(s) ? "(no stderr)" : (s.Length <= max ? s : s[^max..]);

    public void Dispose() { }
}

// ─── Upscale: single-input, scale×-larger output batch ────────────────────

/// <summary>Batch executor for the Polaris upscale model, which is single-input
/// like BGE/Denoise but whose OUTPUT tile is <c>Scale</c>× larger than the input
/// (<c>[1,TileSize,TileSize,3]</c> → <c>[1,TileSize*Scale,TileSize*Scale,3]</c>).
/// Needs its own batch only because the output length differs from the input.</summary>
public interface IQnnUpscaleBatch : IDisposable {
    int TileSize { get; }
    int Scale { get; }
    float[][] RunBatch(IReadOnlyList<float[]> tiles);
}

/// <summary>Pass-1 upscale runner: records each LR input tile, returns a zero HR
/// output of the model's (scale²·larger) shape (discarded). Pure CPU.</summary>
public sealed class UpscaleRecordingTileRunner : IRknnUpscaleTileRunner {
    private readonly int _outLen;
    public int TileSize { get; }
    public int Scale { get; }
    public List<float[]> Inputs { get; } = new();

    public UpscaleRecordingTileRunner(int tileSize, int scale) {
        TileSize = tileSize;
        Scale = scale;
        _outLen = tileSize * scale * tileSize * scale * 3;
    }

    public float[] RunTile(float[] nhwcInput) {
        Inputs.Add((float[])nhwcInput.Clone());
        return new float[_outLen];
    }

    public void Dispose() { }
}

/// <summary>Pass-2 upscale runner: returns the pre-computed HR outputs in call
/// order. Pure CPU.</summary>
public sealed class UpscaleReplayingTileRunner : IRknnUpscaleTileRunner {
    private readonly float[][] _outputs;
    private int _i;
    public int TileSize { get; }
    public int Scale { get; }

    public UpscaleReplayingTileRunner(int tileSize, int scale, float[][] outputs) {
        TileSize = tileSize;
        Scale = scale;
        _outputs = outputs;
    }

    public float[] RunTile(float[] nhwcInput) {
        if (_i >= _outputs.Length)
            throw new QnnException($"upscale replay overrun: asked for tile {_i} but only " +
                                   $"{_outputs.Length} were batched (pass mismatch)");
        return _outputs[_i++];
    }

    public void Dispose() { }
}

/// <summary>Real upscale batch executor. Same single-input <c>qnn-net-run</c> path
/// as <see cref="QnnNetRunBatch"/>, but each output is <c>(TileSize*Scale)²·3</c>
/// fp32. Device-validated on the Q6A only (Windows tests inject a fake).</summary>
public sealed class QnnUpscaleNetRunBatch : IQnnUpscaleBatch {
    private readonly string _contextBin;
    private readonly ILogger _logger;
    private readonly int _outLen;
    public int TileSize { get; }
    public int Scale { get; }

    public QnnUpscaleNetRunBatch(string contextBin, int tileSize, int scale, ILogger logger) {
        _contextBin = contextBin;
        TileSize = tileSize;
        Scale = scale;
        _logger = logger;
        _outLen = tileSize * scale * tileSize * scale * 3;
    }

    public float[][] RunBatch(IReadOnlyList<float[]> tiles) {
        if (tiles.Count == 0) return Array.Empty<float[]>();
        var work = Directory.CreateTempSubdirectory("polaris-qnn-upscale-");
        try {
            var listPath = Path.Combine(work.FullName, "inputs.txt");
            using (var lw = new StreamWriter(listPath)) {
                for (int i = 0; i < tiles.Count; i++) {
                    var raw = Path.Combine(work.FullName, $"in_{i}.raw");
                    WriteFloats(raw, tiles[i]);
                    lw.WriteLine(raw);
                }
            }

            var htpCfg = Path.Combine(work.FullName, "htp.json");
            File.WriteAllText(htpCfg, "{ \"devices\": [ { \"dsp_arch\": \"v68\", \"pd_session\": \"unsigned\" } ] }");
            var beCfg = Path.Combine(work.FullName, "backend_ext.json");
            File.WriteAllText(beCfg,
                "{ \"backend_extensions\": { \"shared_library_path\": \"libQnnHtpNetRunExtensions.so\", "
                + "\"config_file_path\": \"" + htpCfg.Replace("\\", "/") + "\" } }");

            var outDir = Path.Combine(work.FullName, "out");
            Directory.CreateDirectory(outDir);
            RunNetRun(listPath, beCfg, outDir);

            var outputs = new float[tiles.Count][];
            for (int i = 0; i < tiles.Count; i++) {
                var resultDir = Path.Combine(outDir, $"Result_{i}");
                var raw = Directory.Exists(resultDir)
                    ? Directory.EnumerateFiles(resultDir, "*.raw").FirstOrDefault()
                    : null;
                if (raw == null)
                    throw new QnnException($"qnn-net-run produced no output for upscale tile {i} (looked in {resultDir})");
                outputs[i] = ReadFloats(raw, _outLen);
            }
            return outputs;
        } finally {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private void RunNetRun(string inputList, string backendExt, string outDir) {
        var psi = new ProcessStartInfo {
            FileName = QnnRuntime.NetRunPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add(QnnRuntime.HtpBackendPath);
        psi.ArgumentList.Add("--retrieve_context"); psi.ArgumentList.Add(_contextBin);
        psi.ArgumentList.Add("--config_file"); psi.ArgumentList.Add(backendExt);
        psi.ArgumentList.Add("--input_list"); psi.ArgumentList.Add(inputList);
        psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
        var libDir = Path.Combine(QnnRuntime.QairtRoot, "lib");
        var existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLd) ? libDir : $"{libDir}:{existingLd}";
        psi.Environment["ADSP_LIBRARY_PATH"] = QnnRuntime.AdspLibraryPath;

        using var p = Process.Start(psi) ?? throw new QnnException("failed to start qnn-net-run");
        string stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new QnnException($"qnn-net-run (upscale) exited {p.ExitCode}: {Tail(stderr)}");
    }

    private static void WriteFloats(string path, float[] data) {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        foreach (var f in data) bw.Write(f);
    }

    private static float[] ReadFloats(string path, int expected) {
        var bytes = File.ReadAllBytes(path);
        int n = bytes.Length / sizeof(float);
        var outp = new float[Math.Max(n, expected)];
        Buffer.BlockCopy(bytes, 0, outp, 0, n * sizeof(float));
        return outp;
    }

    private static string Tail(string s, int max = 400) =>
        string.IsNullOrEmpty(s) ? "(no stderr)" : (s.Length <= max ? s : s[^max..]);

    public void Dispose() { }
}

/// <summary>Thrown when the QNN/HTP path fails. Callers catch this and fall back
/// to the GraXpert CLI inference path.</summary>
public sealed class QnnException : Exception {
    public QnnException(string message) : base(message) { }
    public QnnException(string message, Exception inner) : base(message, inner) { }
}

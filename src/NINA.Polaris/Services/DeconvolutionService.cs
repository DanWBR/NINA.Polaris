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

using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services;

/// <summary>
/// Classical, measured-PSF deconvolution on a FITS file (server-side).
///
/// Unlike the AI / GraXpert deconvolution — which runs in the browser with a
/// single guessed FWHM — this measures the frame's actual PSF from its own
/// stars (<see cref="PsfExtractor"/>) and runs Richardson-Lucy with that exact
/// kernel (<see cref="RichardsonLucyDeconvolution"/>), TV-regularized, with a
/// background support mask and a flux-conservation guard. The PSF is measured
/// once from luminance and applied to every channel so colour is preserved.
///
/// Writes a sibling `{stem}_rl.fits` and returns the measured PSF stats so the
/// UI can show the user what shape was reversed.
/// </summary>
public class DeconvolutionService {
    private readonly ILogger<DeconvolutionService> _logger;

    public DeconvolutionService(ILogger<DeconvolutionService> logger) {
        _logger = logger;
    }

    public sealed record DeconResult(
        string OutputPath, int Width, int Height, int Channels,
        double FwhmPx, double Eccentricity, int StarsUsed, int Iterations,
        bool Field = false, int GridCells = 0, int MeasuredCells = 0);

    /// <summary>
    /// Deconvolve <paramref name="sourcePath"/>. <paramref name="strength"/>
    /// (0..1) maps to the RL iteration count; <paramref name="supportMask"/>
    /// limits sharpening to where there is signal. When <paramref name="field"/>
    /// is true the PSF is measured per region on a <paramref name="grid"/>×grid
    /// and each region is deconvolved with its own kernel (corner ≠ centre).
    /// Throws InvalidOperationException when the frame lacks enough clean stars
    /// to measure a PSF (the caller surfaces a clear message).
    /// </summary>
    public DeconResult RichardsonLucy(string sourcePath, double strength = 0.5,
                                      double tvLambda = 0.002, bool supportMask = true,
                                      bool field = false, int grid = 3,
                                      bool noiseAdaptive = false) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) src = FITSReader.Read(fs);

        int w = src.Properties.Width, h = src.Properties.Height;
        int channels = src.Properties.Channels == 3 ? 3 : 1;
        long plane = (long)w * h;

        // Luminance for PSF measurement + the support mask (one consistent
        // estimate shared by every channel).
        var lum = new ushort[plane];
        if (channels == 3) {
            for (long i = 0; i < plane; i++)
                lum[i] = (ushort)((src.Data[i] + src.Data[plane + i] + src.Data[2 * plane + i]) / 3);
        } else {
            Array.Copy(src.Data, lum, plane);
        }

        var ex = new PsfExtractor();
        const string err =
            "Not enough clean stars to measure the PSF (need ≥ 8 unsaturated, " +
            "isolated, round stars). Use AI deconvolution for star-poor frames.";

        // Measure the PSF — a single global one, or a per-region field.
        PsfModel psf;
        PsfField psfField = null;
        if (field) {
            int g = Math.Clamp(grid, 2, 8);
            psfField = ex.ExtractField(lum, w, h, g, g);
            if (psfField == null) throw new InvalidOperationException(err);
            psf = psfField.Global;
        } else {
            psf = ex.Extract(lum, w, h);
            if (psf == null) throw new InvalidOperationException(err);
        }

        int iters = RichardsonLucyDeconvolution.IterationsFromStrength(strength);
        if (iters <= 0) iters = 1;

        // Measured photon-transfer noise map (σ per pixel) from luminance. It
        // drives damped Richardson-Lucy (White 1994) so the iteration doesn't
        // amplify noise or ring around stars — the main cause of the dark halos
        // vanilla RL produces — and optionally the noise-adaptive support mask.
        var (bg, noise) = PsfExtractor.EstimateBackgroundNoise(lum);
        var sigmaMap = NoiseMap.Estimate(lum, w, h, out _);
        const double dampingThreshold = 2.5;  // σ units

        // Support mask from luminance (built once, reused per channel). Even
        // when the signal support mask is off we still build an all-pass mask so
        // the saturation guard can protect clipped-star cores + halos.
        var lumF = new float[plane];
        for (long i = 0; i < plane; i++) lumF[i] = lum[i];
        float[] mask;
        if (supportMask) {
            mask = noiseAdaptive
                ? RichardsonLucyDeconvolution.BuildNoiseAdaptiveSupportMask(lumF, sigmaMap, bg)
                : RichardsonLucyDeconvolution.BuildSupportMask(lumF, w, h, bg, noise);
        } else {
            mask = new float[plane];
            for (long i = 0; i < plane; i++) mask[i] = 1f;
        }

        // Saturation guard: keep the original over genuinely clipped star cores
        // (and a PSF-sized halo) so they don't ring. Only engage when the frame
        // actually reaches near the 16-bit ceiling — i.e. there IS clipping;
        // otherwise every bright (but unclipped) star would be wrongly frozen.
        const double ceiling = 65535.0;
        ushort maxLum = 0;
        for (long i = 0; i < plane; i++) if (lum[i] > maxLum) maxLum = lum[i];
        if (maxLum >= 0.90 * ceiling) {
            double satLevel = 0.97 * ceiling;
            int satDilate = (field ? psfField.Global.Radius : psf.Radius) + 3;
            RichardsonLucyDeconvolution.ApplySaturationGuard(mask, lumF, w, h, satLevel, satDilate);
        }

        // FFT convolution keeps the per-iteration cost independent of the
        // measured PSF stamp size — essential at full resolution / on an SBC.
        var rl = field ? null : new RichardsonLucyDeconvolution {
            Iterations = iters, TvLambda = tvLambda, DampingThreshold = dampingThreshold, UseFft = true
        };
        var fd = field ? new FieldDeconvolution {
            Iterations = iters, TvLambda = tvLambda, DampingThreshold = dampingThreshold, UseFft = true
        } : null;
        var outData = new ushort[src.Data.Length];
        var planeF = new float[plane];
        for (int c = 0; c < channels; c++) {
            long baseIdx = (long)c * plane;
            for (long i = 0; i < plane; i++) planeF[i] = src.Data[baseIdx + i];
            var dec = field
                ? fd.Deconvolve(planeF, w, h, psfField, mask, sigmaMap)
                : rl.Deconvolve(planeF, w, h, psf, mask, sigmaMap);
            for (long i = 0; i < plane; i++) {
                int v = (int)(dec[i] + 0.5f);
                outData[baseIdx + i] = (ushort)(v < 0 ? 0 : (v > 65535 ? 65535 : v));
            }
        }

        var dst = new BaseImageData(outData, src.Properties, src.MetaData);
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_rl.fits");

        int gridCells = field ? psfField.GridX * psfField.GridY : 0;
        int measured = field ? psfField.MeasuredCellCount : 0;

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("DECONALG",
                field ? "RichardsonLucy-fieldPSF" : "RichardsonLucy-measuredPSF"),
            new KeyValuePair<string, string>("DECONITER", iters.ToString()),
            new KeyValuePair<string, string>("DECONFWHM", psf.FwhmPx.ToString("F3")),
            new KeyValuePair<string, string>("DECONECC", psf.Eccentricity.ToString("F3")),
            new KeyValuePair<string, string>("DECONSTAR", psf.StarsUsed.ToString()),
            new KeyValuePair<string, string>("DECONGRID",
                field ? $"{psfField.GridX}x{psfField.GridY}" : "1x1"),
        });

        _logger.LogInformation(
            "RL decon ({Mode}): {Src} ({W}×{H} ch={Ch}) → {Out} | PSF FWHM={Fwhm:F2} " +
            "ecc={Ecc:F2} stars={Stars} iters={Iters} cells={Measured}/{Grid}",
            field ? "field" : "global", sourcePath, w, h, channels, outPath,
            psf.FwhmPx, psf.Eccentricity, psf.StarsUsed, iters, measured, gridCells);

        return new DeconResult(outPath, w, h, channels,
            psf.FwhmPx, psf.Eccentricity, psf.StarsUsed, iters, field, gridCells, measured);
    }
}

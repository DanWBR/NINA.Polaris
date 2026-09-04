using System;
using System.Linq;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// The alignment-point mesh ported from PlanetarySystemStacker: points only
/// where there is light and structure, local shifts recovered to sub-pixel
/// precision by multi-level NCC, ramp weights that fade patches into each
/// other, and a merge that falls back to the reference where the mesh did
/// not reach.
/// </summary>
[TestFixture]
public class AlignmentPointsTests {
    private const int W = 256, H = 256;

    /// <summary>A textured disc (radius 70) on a pedestal: bright blobs at
    /// fixed pseudo-random positions so every box on the disc has structure.</summary>
    private static float[] Texture(double ox = 0, double oy = 0) {
        var f = new float[W * H];
        var rnd = new Random(11);
        var blobs = Enumerable.Range(0, 60).Select(_ => (x: 128 + (rnd.NextDouble() - 0.5) * 120, y: 128 + (rnd.NextDouble() - 0.5) * 120, s: 2.0 + rnd.NextDouble() * 3)).ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) {
                double px = x - ox, py = y - oy;
                double r = double.Hypot(px - 128, py - 128);
                double disc = r < 70 ? 6000 : 0;
                double v = 3000 + disc;
                foreach (var b in blobs) {
                    double d2 = (px - b.x) * (px - b.x) + (py - b.y) * (py - b.y);
                    v += 8000 * Math.Exp(-d2 / (2 * b.s * b.s));
                }
                f[y * W + x] = (float)v;
            }
        return f;
    }

    [Test]
    public void BuildMesh_PutsPointsOnTheStructuredDisc_NotOnTheSky() {
        var refB = PlanetaryFrames.Blur7(Texture(), W, H);
        var mesh = AlignmentPoints.BuildMesh(refB, W, H, new AlignmentPoints.MeshOptions(HalfBox: 16, SearchWidth: 8));
        Assert.That(mesh.Count, Is.GreaterThan(4));
        foreach (var p in mesh)
            Assert.That(double.Hypot(p.Cx - 128, p.Cy - 128), Is.LessThan(70 + 16 + 1), $"AP ({p.Cx},{p.Cy}) is off the disc");
    }

    [Test]
    public void BuildMesh_FlatField_HasNoPoints() {
        var flat = new float[W * H]; Array.Fill(flat, 5000f);
        Assert.That(AlignmentPoints.BuildMesh(flat, W, H, new AlignmentPoints.MeshOptions(HalfBox: 16, SearchWidth: 8)), Is.Empty);
    }

    [Test]
    public void Ncc_SelfMatch_IsOne_AndShiftedIsLower() {
        var a = PlanetaryFrames.Blur7(Texture(), W, H);
        double self = AlignmentPoints.Ncc(a, a, W, H, 128, 128, 128, 128, 16, 1);
        double off = AlignmentPoints.Ncc(a, a, W, H, 128, 128, 131, 126, 16, 1);
        Assert.That(self, Is.EqualTo(1.0).Within(1e-6));
        Assert.That(off, Is.LessThan(self));
    }

    [TestCase(3.0, -2.0)]
    [TestCase(1.6, -0.7)]
    [TestCase(-4.3, 2.2)]
    public void LocalShift_RecoversAKnownSubPixelShift(double sx, double sy) {
        // frame = reference content moved by (sx, sy): the reference box at
        // (Cx, Cy) sits at (Cx + sx, Cy + sy) in the frame, i.e. gdx = -sx.
        var refB = PlanetaryFrames.Blur7(Texture(), W, H);
        var frameB = PlanetaryFrames.Blur7(Texture(sx, sy), W, H);
        var p = new AlignmentPoints.Point { Cx = 120, Cy = 136, HalfBox = 16 };
        // start the search from a deliberately wrong global guess (off by 1 px)
        var shift = AlignmentPoints.LocalShift(refB, frameB, W, H, p, gdx: -sx + 1, gdy: -sy - 1, searchWidth: 14);
        Assert.That(shift, Is.Not.Null);
        Assert.That(shift!.Value.dx, Is.EqualTo(-sx).Within(0.15));
        Assert.That(shift.Value.dy, Is.EqualTo(-sy).Within(0.15));
    }

    [Test]
    public void LocalShift_BeyondTheSearchWidth_IsRejected() {
        var refB = PlanetaryFrames.Blur7(Texture(), W, H);
        var frameB = PlanetaryFrames.Blur7(Texture(30, 0), W, H);
        var p = new AlignmentPoints.Point { Cx = 120, Cy = 136, HalfBox = 16 };
        Assert.That(AlignmentPoints.LocalShift(refB, frameB, W, H, p, 0, 0, 14), Is.Null);
    }

    [Test]
    public void RampWeight_IsOneAtTheCentre_AndFadesToTheEdge() {
        var p = new AlignmentPoints.Point { Cx = 100, Cy = 100, HalfBox = 24 };
        Assert.That(AlignmentPoints.RampWeight(p, 100, 100), Is.EqualTo(1f).Within(0.05f));
        Assert.That(AlignmentPoints.RampWeight(p, 76, 100), Is.GreaterThan(0f).And.LessThan(0.1f));
        Assert.That(AlignmentPoints.RampWeight(p, 88, 100), Is.GreaterThan(AlignmentPoints.RampWeight(p, 80, 100)));
    }

    [Test]
    public void AccumulatePatch_ThenMerge_RecoversTheReferenceContent() {
        var reference = Texture();
        var moved = Texture(1.5, -2.25);                          // content moved by (1.5, -2.25)
        var p = new AlignmentPoints.Point { Cx = 128, Cy = 128, HalfBox = 24 };
        var acc = new float[W * H]; var wgt = new float[W * H];
        AlignmentPoints.AccumulatePatch(moved, W, H, p, dx: -1.5, dy: 2.25, gain: 1f, acc, wgt);
        var bg = new float[W * H];
        var merged = AlignmentPoints.Merge(acc, wgt, bg, stackSize: 1, blendThreshold: 0.2);
        // inside the patch the merged image matches the reference (bilinear resampling of a smooth field)
        double err = 0; int n = 0;
        for (int y = 110; y < 146; y++)
            for (int x = 110; x < 146; x++) { err += Math.Abs(merged[y * W + x] - reference[y * W + x]); n++; }
        Assert.That(err / n, Is.LessThan(60), "mean abs error inside the patch");
        // outside the patch nothing was accumulated: the background (0) shows through
        Assert.That(merged[10 * W + 10], Is.EqualTo(0));
    }

    [Test]
    public void Merge_FadesIntoTheBackground_WhereTheMeshIsThin() {
        var acc = new float[] { 1000f, 1000f, 0f }; var wgt = new float[] { 1f, 0.1f, 0f }; var bg = new float[] { 500f, 500f, 500f };
        var o = AlignmentPoints.Merge(acc, wgt, bg, stackSize: 5, blendThreshold: 0.2);   // full weight = 1
        Assert.That(o[0], Is.EqualTo(1000));                     // fully covered: AP value (acc/wgt)
        Assert.That(o[1], Is.EqualTo(10000 * 0.1 + 500 * 0.9).Within(1));   // 10% covered: 10% AP, 90% background
        Assert.That(o[2], Is.EqualTo(500));                      // uncovered: background
    }
}

using NUnit.Framework;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CC-3: pins the PixelMath expression grammar + evaluator. Numbers,
/// identifiers, parens, +, -, *, /, **, unary minus, function calls.
/// Most failures here would be felt by users as "my expression looks
/// right but the output is weird", make the parser bugs loud at
/// compile-time so we catch them in CI instead of debugging in the
/// field.
/// </summary>
[TestFixture]
public class PixelMathEvaluatorTests {

    private static readonly IReadOnlySet<string> RGB
        = new HashSet<string> { "R", "G", "B" };

    private static readonly IReadOnlySet<string> NB
        = new HashSet<string> { "Ha", "OIII", "SII" };

    private static IReadOnlyDictionary<string, float> Vars(params (string, float)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    // ─── numbers + arithmetic ────────────────────────────────────────

    [Test]
    public void Number_ParsesAndReturns() {
        var e = PixelMathEvaluator.Compile("42", RGB);
        Assert.That(e(Vars()), Is.EqualTo(42f));
    }

    [Test]
    public void Number_Decimal() {
        var e = PixelMathEvaluator.Compile("3.14", RGB);
        Assert.That(e(Vars()), Is.EqualTo(3.14f).Within(0.001f));
    }

    [Test]
    public void Number_ScientificNotation() {
        var e = PixelMathEvaluator.Compile("1.5e3", RGB);
        Assert.That(e(Vars()), Is.EqualTo(1500f));
    }

    [Test]
    public void Addition_BindsLeftToRight() {
        var e = PixelMathEvaluator.Compile("1 + 2 + 3", RGB);
        Assert.That(e(Vars()), Is.EqualTo(6f));
    }

    [Test]
    public void MulPrecedence_OverAddition() {
        // 2 + 3 * 4 = 14 (not 20). The classic precedence test that
        // would catch a parser that flattens everything left-to-right.
        var e = PixelMathEvaluator.Compile("2 + 3 * 4", RGB);
        Assert.That(e(Vars()), Is.EqualTo(14f));
    }

    [Test]
    public void Parens_OverridePrecedence() {
        var e = PixelMathEvaluator.Compile("(2 + 3) * 4", RGB);
        Assert.That(e(Vars()), Is.EqualTo(20f));
    }

    [Test]
    public void UnaryMinus_AppliesToOperand() {
        var e = PixelMathEvaluator.Compile("-5 + 3", RGB);
        Assert.That(e(Vars()), Is.EqualTo(-2f));
    }

    [Test]
    public void Power_RightAssociative() {
        // 2 ** 3 ** 2 = 2 ** (3 ** 2) = 2 ** 9 = 512.
        var e = PixelMathEvaluator.Compile("2 ** 3 ** 2", RGB);
        Assert.That(e(Vars()), Is.EqualTo(512f));
    }

    [Test]
    public void Division_BindsLeftToRight() {
        var e = PixelMathEvaluator.Compile("100 / 5 / 4", RGB);
        Assert.That(e(Vars()), Is.EqualTo(5f));
    }

    [Test]
    public void Division_ByZero_ReturnsZero_NotNaN() {
        // Per-pixel safety: a single black pixel in an input shouldn't
        // propagate NaN through the rest of the integration.
        var e = PixelMathEvaluator.Compile("R / G", RGB);
        var v = e(Vars(("R", 100f), ("G", 0f), ("B", 0f)));
        Assert.That(float.IsNaN(v), Is.False);
        Assert.That(v, Is.EqualTo(0f));
    }

    // ─── variables ───────────────────────────────────────────────────

    [Test]
    public void Variable_LooksUpInDict() {
        var e = PixelMathEvaluator.Compile("R + G + B", RGB);
        Assert.That(e(Vars(("R", 10f), ("G", 20f), ("B", 30f))), Is.EqualTo(60f));
    }

    [Test]
    public void UnknownVariable_ThrowsAtCompile() {
        // The whole point of pre-validating against knownVars: typos
        // surface immediately in the UI, not after the user has
        // queued a 90-second job. NB set excludes "Ha2".
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PixelMathEvaluator.Compile("Ha + Ha2", NB));
        Assert.That(ex!.Message, Does.Contain("Ha2"));
    }

    // ─── functions ───────────────────────────────────────────────────

    [Test]
    public void Min_TwoArgs() {
        var e = PixelMathEvaluator.Compile("min(R, G)", RGB);
        Assert.That(e(Vars(("R", 5f), ("G", 3f), ("B", 0f))), Is.EqualTo(3f));
    }

    [Test]
    public void Max_ThreeArgs() {
        var e = PixelMathEvaluator.Compile("max(R, G, B)", RGB);
        Assert.That(e(Vars(("R", 5f), ("G", 12f), ("B", 7f))), Is.EqualTo(12f));
    }

    [Test]
    public void Sqrt_ProtectsNegative() {
        // sqrt clamps negative arg to 0 to avoid NaN propagation; the
        // expression "Ha - OIII" can go negative on dim pixels.
        var e = PixelMathEvaluator.Compile("sqrt(Ha - OIII)", NB);
        var v = e(Vars(("Ha", 100f), ("OIII", 200f), ("SII", 0f)));
        Assert.That(float.IsNaN(v), Is.False);
        Assert.That(v, Is.EqualTo(0f));
    }

    [Test]
    public void Clamp_ThreeArgs() {
        var e = PixelMathEvaluator.Compile("clamp(R, 0, 100)", RGB);
        Assert.That(e(Vars(("R", 150f), ("G", 0f), ("B", 0f))), Is.EqualTo(100f));
        Assert.That(e(Vars(("R", -10f), ("G", 0f), ("B", 0f))), Is.EqualTo(0f));
        Assert.That(e(Vars(("R",  50f), ("G", 0f), ("B", 0f))), Is.EqualTo(50f));
    }

    [Test]
    public void Function_WrongArity_ThrowsAtEvaluation() {
        // The parser accepts any arg count; ApplyFunction validates
        // per-call. Errors surface as InvalidOperationException via
        // the delegate during compose, which becomes job.Error in the
        // UI.
        var e = PixelMathEvaluator.Compile("abs(R, G)", RGB);
        Assert.Throws<ArgumentException>(() =>
            e(Vars(("R", 1f), ("G", 2f), ("B", 0f))));
    }

    [Test]
    public void UnknownFunction_ThrowsAtEvaluation() {
        var e = PixelMathEvaluator.Compile("sinh(R)", RGB);
        Assert.Throws<ArgumentException>(() =>
            e(Vars(("R", 1f), ("G", 0f), ("B", 0f))));
    }

    // ─── realistic expressions ──────────────────────────────────────

    [Test]
    public void BoostNarrowband_HOO_Synth() {
        // Common HOO bicolor: R=Ha, G=mix, B=OIII.
        var rExp = PixelMathEvaluator.Compile("Ha", NB);
        var gExp = PixelMathEvaluator.Compile("0.5 * Ha + 0.5 * OIII", NB);
        var bExp = PixelMathEvaluator.Compile("OIII", NB);
        var vars = Vars(("Ha", 30000f), ("OIII", 10000f), ("SII", 0f));
        Assert.That(rExp(vars), Is.EqualTo(30000f));
        Assert.That(gExp(vars), Is.EqualTo(20000f));
        Assert.That(bExp(vars), Is.EqualTo(10000f));
    }

    [Test]
    public void ParseError_IncludesPosition() {
        // Empty parens: "()" hits unexpected ')' inside ParsePrimary.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PixelMathEvaluator.Compile("R + ()", RGB));
        Assert.That(ex!.Message, Does.Contain("col"));
    }

    [Test]
    public void EmptyExpression_Throws() {
        Assert.Throws<ArgumentException>(() =>
            PixelMathEvaluator.Compile("", RGB));
    }
}

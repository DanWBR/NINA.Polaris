namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Tiny expression evaluator for PixelMath: parses arithmetic over
/// named channel variables ("R", "Ha", "OIII", whatever the user
/// assigns) plus a small set of math functions, compiles each
/// expression to a delegate, then evaluates per-pixel during the
/// combine. Built recursive-descent, AOT-friendly (no codegen, no
/// expression trees, no reflection); suitable for the per-pixel hot
/// path and runs unchanged on the Raspberry Pi.
///
/// Grammar:
///   expr   := term (('+' | '-') term)*
///   term   := factor (('*' | '/') factor)*
///   factor := unary ('**' factor)?
///   unary  := ('-' | '+')? primary
///   primary:= NUMBER | IDENT | IDENT '(' args ')' | '(' expr ')'
///   args   := expr (',' expr)*
///
/// Supported functions: min, max, abs, pow, sqrt, exp, log, clamp.
/// All take 1, 2, or 3 arguments as appropriate (see ApplyFunction).
///
/// Variables resolve against a <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// of name → float, populated per-pixel by ChannelCombineService.
/// Unknown identifiers throw at PARSE time (via Compile + a known-
/// variables set), not at evaluation, so a typo in the expression
/// surfaces to the UI immediately rather than after the user has
/// queued a 90-second job.
///
/// Examples (all valid):
///   "0.7*R + 0.3*Ha"               // boost R with Ha
///   "sqrt(G * OIII)"               // bicolor synth
///   "(R + G + B) / 3"              // synthetic luminance
///   "clamp(R - background, 0, 65535)"
/// </summary>
public static class PixelMathEvaluator {

    /// <summary>
    /// Per-pixel evaluator delegate. Caller passes a dictionary mapping
    /// variable name → pixel value at the current coordinate; delegate
    /// returns the computed float (caller clamps to ushort range).
    /// </summary>
    public delegate float Eval(IReadOnlyDictionary<string, float> vars);

    /// <summary>
    /// Parse and compile an expression. <paramref name="knownVars"/>
    /// is the set of variable names the expression is allowed to
    /// reference; unknown identifiers (typos, missing channel) throw
    /// here so the job fails loudly before any frame I/O.
    /// </summary>
    public static Eval Compile(string expression, IReadOnlySet<string> knownVars) {
        if (string.IsNullOrWhiteSpace(expression)) {
            throw new ArgumentException("PixelMath: empty expression.");
        }
        var parser = new Parser(expression, knownVars);
        var ast = parser.ParseTopLevel();
        return ast.ToDelegate();
    }

    // ── AST nodes ────────────────────────────────────────────────────

    private abstract class Node {
        public abstract Eval ToDelegate();
    }

    private sealed class NumberNode : Node {
        public float Value;
        public override Eval ToDelegate() {
            float v = Value;
            return _ => v;
        }
    }

    private sealed class VariableNode : Node {
        public string Name = "";
        public override Eval ToDelegate() {
            string n = Name;
            return vars => vars.TryGetValue(n, out var v) ? v : 0f;
        }
    }

    private sealed class UnaryNode : Node {
        public Node Operand = null!;
        public bool Negate;
        public override Eval ToDelegate() {
            var inner = Operand.ToDelegate();
            return Negate ? (vars => -inner(vars)) : inner;
        }
    }

    private sealed class BinaryNode : Node {
        public Node Left = null!, Right = null!;
        public char Op;
        public override Eval ToDelegate() {
            var l = Left.ToDelegate();
            var r = Right.ToDelegate();
            return Op switch {
                '+' => vars => l(vars) + r(vars),
                '-' => vars => l(vars) - r(vars),
                '*' => vars => l(vars) * r(vars),
                '/' => vars => {
                    float rv = r(vars);
                    // Per-pixel divide-by-zero falls back to 0 rather
                    // than NaN; clamping at the writer end could
                    // produce confusing results if a NaN bubbled up.
                    return Math.Abs(rv) < 1e-6f ? 0f : l(vars) / rv;
                },
                _   => throw new InvalidOperationException(
                            $"PixelMath: unknown binary op '{Op}'."),
            };
        }
    }

    private sealed class PowerNode : Node {
        public Node Base = null!, Exponent = null!;
        public override Eval ToDelegate() {
            var b = Base.ToDelegate();
            var e = Exponent.ToDelegate();
            return vars => (float)Math.Pow(b(vars), e(vars));
        }
    }

    private sealed class FunctionNode : Node {
        public string Name = "";
        public List<Node> Args = new();
        public override Eval ToDelegate() {
            var argDelegates = Args.Select(a => a.ToDelegate()).ToList();
            string name = Name;
            int argc = argDelegates.Count;
            return vars => ApplyFunction(name, argDelegates, vars, argc);
        }
    }

    private static float ApplyFunction(string name, List<Eval> argDelegates,
                                        IReadOnlyDictionary<string, float> vars, int argc) {
        // Materialise args once (per pixel) so each delegate is
        // evaluated at most once even when the function uses an arg
        // more than once (e.g. clamp's arg used in both branches).
        Span<float> a = stackalloc float[Math.Max(1, argc)];
        for (int i = 0; i < argc; i++) a[i] = argDelegates[i](vars);
        switch (name) {
            case "min":
                if (argc < 2) throw new ArgumentException("min: needs >= 2 args.");
                {
                    float m = a[0];
                    for (int i = 1; i < argc; i++) if (a[i] < m) m = a[i];
                    return m;
                }
            case "max":
                if (argc < 2) throw new ArgumentException("max: needs >= 2 args.");
                {
                    float m = a[0];
                    for (int i = 1; i < argc; i++) if (a[i] > m) m = a[i];
                    return m;
                }
            case "abs":
                if (argc != 1) throw new ArgumentException("abs: needs 1 arg.");
                return Math.Abs(a[0]);
            case "pow":
                if (argc != 2) throw new ArgumentException("pow: needs 2 args.");
                return (float)Math.Pow(a[0], a[1]);
            case "sqrt":
                if (argc != 1) throw new ArgumentException("sqrt: needs 1 arg.");
                return (float)Math.Sqrt(Math.Max(0, a[0]));
            case "exp":
                if (argc != 1) throw new ArgumentException("exp: needs 1 arg.");
                return (float)Math.Exp(a[0]);
            case "log":
                if (argc != 1) throw new ArgumentException("log: needs 1 arg.");
                return a[0] > 0 ? (float)Math.Log(a[0]) : 0f;
            case "clamp":
                if (argc != 3) throw new ArgumentException("clamp: needs 3 args (value, min, max).");
                return Math.Clamp(a[0], a[1], a[2]);
            default:
                throw new ArgumentException($"PixelMath: unknown function '{name}'.");
        }
    }

    // ── parser ───────────────────────────────────────────────────────

    private sealed class Parser {
        private readonly string _src;
        private readonly IReadOnlySet<string> _known;
        private int _pos;

        public Parser(string src, IReadOnlySet<string> known) {
            _src = src;
            _known = known;
            _pos = 0;
        }

        public Node ParseTopLevel() {
            var node = ParseExpr();
            SkipWhitespace();
            if (_pos < _src.Length) {
                throw Fail($"unexpected trailing input '{_src[_pos..]}'.");
            }
            return node;
        }

        private Node ParseExpr() {
            var left = ParseTerm();
            while (true) {
                SkipWhitespace();
                if (_pos >= _src.Length) return left;
                char op = _src[_pos];
                if (op != '+' && op != '-') return left;
                _pos++;
                var right = ParseTerm();
                left = new BinaryNode { Left = left, Right = right, Op = op };
            }
        }

        private Node ParseTerm() {
            var left = ParseFactor();
            while (true) {
                SkipWhitespace();
                if (_pos >= _src.Length) return left;
                char op = _src[_pos];
                if (op != '*' && op != '/') return left;
                // Look ahead for '**' (power) which is parsed in
                // ParseFactor, not here.
                if (op == '*' && _pos + 1 < _src.Length && _src[_pos + 1] == '*') return left;
                _pos++;
                var right = ParseFactor();
                left = new BinaryNode { Left = left, Right = right, Op = op };
            }
        }

        private Node ParseFactor() {
            var left = ParseUnary();
            SkipWhitespace();
            if (_pos + 1 < _src.Length && _src[_pos] == '*' && _src[_pos + 1] == '*') {
                _pos += 2;
                // Right-associative power: a ** b ** c = a ** (b ** c).
                var right = ParseFactor();
                return new PowerNode { Base = left, Exponent = right };
            }
            return left;
        }

        private Node ParseUnary() {
            SkipWhitespace();
            if (_pos < _src.Length && (_src[_pos] == '-' || _src[_pos] == '+')) {
                bool negate = _src[_pos] == '-';
                _pos++;
                var inner = ParsePrimary();
                return new UnaryNode { Operand = inner, Negate = negate };
            }
            return ParsePrimary();
        }

        private Node ParsePrimary() {
            SkipWhitespace();
            if (_pos >= _src.Length) throw Fail("unexpected end of expression.");
            char c = _src[_pos];

            if (c == '(') {
                _pos++;
                var inner = ParseExpr();
                SkipWhitespace();
                if (_pos >= _src.Length || _src[_pos] != ')') {
                    throw Fail("missing ')'.");
                }
                _pos++;
                return inner;
            }

            if (char.IsDigit(c) || c == '.') {
                return ParseNumber();
            }

            if (char.IsLetter(c) || c == '_') {
                var ident = ReadIdentifier();
                SkipWhitespace();
                if (_pos < _src.Length && _src[_pos] == '(') {
                    return ParseFunctionCall(ident);
                }
                if (!_known.Contains(ident)) {
                    throw Fail(
                        $"unknown variable '{ident}'. Defined variables: " +
                        $"[{string.Join(", ", _known)}].");
                }
                return new VariableNode { Name = ident };
            }

            throw Fail($"unexpected character '{c}'.");
        }

        private NumberNode ParseNumber() {
            int start = _pos;
            while (_pos < _src.Length &&
                   (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) {
                _pos++;
            }
            // Scientific notation (1e6, 2.5e-3).
            if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E')) {
                _pos++;
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
            }
            var token = _src[start.._pos];
            if (!float.TryParse(token,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float v)) {
                throw Fail($"invalid number '{token}'.");
            }
            return new NumberNode { Value = v };
        }

        private string ReadIdentifier() {
            int start = _pos;
            while (_pos < _src.Length &&
                   (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) {
                _pos++;
            }
            return _src[start.._pos];
        }

        private FunctionNode ParseFunctionCall(string name) {
            _pos++;   // consume '('
            var node = new FunctionNode { Name = name };
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == ')') {
                _pos++;
                return node;
            }
            while (true) {
                node.Args.Add(ParseExpr());
                SkipWhitespace();
                if (_pos >= _src.Length) throw Fail($"missing ')' for {name}().");
                if (_src[_pos] == ')') { _pos++; return node; }
                if (_src[_pos] != ',') throw Fail($"expected ',' or ')' in {name}() args.");
                _pos++;
            }
        }

        private void SkipWhitespace() {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }

        private InvalidOperationException Fail(string msg) {
            return new InvalidOperationException(
                $"PixelMath parse error at col {_pos + 1}: {msg} " +
                $"In expression: \"{_src}\"");
        }
    }
}

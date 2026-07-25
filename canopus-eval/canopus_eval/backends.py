"""Model backends, behind one interface so the same eval set runs everywhere.

Three, for three jobs:

  mock          no model. Proves the harness itself is not the thing that is broken.
  transformers  desktop iteration on the 5070 Ti. Fast to try prompts and models.
  ortgenai      the real target. Same runtime the app will use, on the device.

The desktop/device split matters: a number from `transformers` on a workstation
tells you whether the *model* can do the task. It tells you nothing about whether
a quantized export can do it on a phone, at what latency, or without cooking the
battery. Phase 0 is only closed by `ortgenai` on physical hardware. Use
`transformers` to decide *which* model is worth exporting.

TOOL CALLING: we do not hand-roll a prompt format. Qwen3 and Gemma both ship a
chat template that takes `tools=`, emitting whatever special tokens that model was
trained on, which beats any JSON-in-the-system-prompt scheme we could invent. On
the device, ORT GenAI's grammar-constrained decoding is what makes the JSON valid
by construction, which is precisely why Phase 0 measures tool *choice* and not
JSON syntax.
"""
from __future__ import annotations

import json
import re
import time
from typing import Any, Protocol

from .catalog import openai_tools
from .scenarios import Scenario

SYSTEM = (
    "You are Canopus, an assistant that operates an astrophotography rig running "
    "N.I.N.A. Polaris. You are given the rig's current state and a set of tools.\n"
    "\n"
    "Rules:\n"
    "1. Focus quality, star size and sub quality are measured, never eyeballed. "
    "Use get_image_stats and reason about HFR and star count.\n"
    "2. Tools that move the mount, the focuser, or stop a session change the "
    "user's night. Call them only when the user explicitly asks for that action. "
    "A complaint or an observation is not a request to act: measure, report, and "
    "let the user decide.\n"
    "3. You do not know the sky. Never state a target's coordinates, constellation, "
    "size or magnitude from memory, and never pass coordinates you recalled to a "
    "tool. Look them up with search_catalog. Your memory of catalogues is "
    "unreliable, and a wrong number here points the telescope at nothing.\n"
    "4. The rig summary above is only a set of flags: what is connected, and "
    "whether guiding, a sequence or a live stack is running. It carries no "
    "measurements. Any question about a value, a quality or a progress figure "
    "needs get_status. Answer with no tool only when the flags already settle the "
    "question, or when it is about a concept (what dithering is, why flats "
    "matter). Do not call a tool to look busy.\n"
    "5. Call at most one tool."
)
# Rule 3 is the expensive lesson from the first real run, and it is worth spelling
# out because the failure was invisible twice over.
#
# Asked "aponta para M42", Qwen3-4B emitted slew_to(ra=5.4825, dec=24.105). M42 is
# at dec -5.39: 29 degrees wrong, a mechanically valid slew to empty sky that the
# mount guards cannot catch, and that the eval scored as a PASS.
#
# Asked "onde fica a NGC 7000?", it answered, fluently, that NGC 7000 is "também
# conhecida como Oríon", sits in Orion, at 05h35m +05d28m. NGC 7000 is the North
# America Nebula, in Cygnus, at 20h59m +44d31m. Wrong object, wrong constellation,
# both coordinates wrong, stated with complete confidence.
#
# Same disease, two costumes: args and prose. The schema fix (slew_to takes a name)
# removes it from args by construction. Only the prompt can remove it from prose.


def compact_state(full: dict[str, Any]) -> dict[str, Any]:
    """Reduce a full /api/system/status payload to the hint injected per turn.

    BOOLEANS ONLY, AND THAT IS THE DESIGN, not a shortcut. It follows the
    Assistant, which is live in production and injects exactly four fields
    (`tab`, `guiding`, `liveStacking`, `selectedFramePath`), all flags, while
    `get_status` is what returns "equipment connection, sequence progress, guider
    RMS, live-stack frame count, focus, meridian time".

    Three reasons this is right, and the first is the one that bites:

    1. A flag cannot be misread as a measurement. Inject `rmsPx: 4.8` and the
       model will answer "guiding is fine" from a stale number it was handed.
       Inject `guiding: true` and any question about *quality* has to fetch.
    2. Tokens. The full status carries equipment, liveStack, guider, autoFocus,
       meridianFlip, sequence, cameraStream, videoRecording, slewPreview, host
       metrics, and job lists. That is not something to put in every turn on a
       phone with a battery.
    3. Staleness. A flag ages gracefully; a number does not.

    `tab` and `selectedFramePath` are dropped: Canopus has no UI to be on a tab of.
    """
    eq = full.get("equipment", {}) or {}
    guider = eq.get("guider", {}) or full.get("guider", {}) or {}
    return {
        "connected": sorted(k for k, v in eq.items() if isinstance(v, dict) and v.get("connected")),
        "guiding": guider.get("state") == "Guiding" or bool(guider.get("guiding")),
        "sequenceRunning": bool((full.get("sequence") or {}).get("running")),
        "liveStacking": bool((full.get("liveStack") or {}).get("isRunning")),
    }


def build_messages(scenario: Scenario) -> list[dict[str, str]]:
    # The scenario holds the FULL status, because that is what capture.py reads
    # off the simulator and what get_status returns. Compaction happens here, at
    # prompt time, exactly where the app will do it.
    return [
        {"role": "system", "content": SYSTEM},
        {
            "role": "user",
            "content": (
                f"Rig summary:\n```json\n"
                f"{json.dumps(compact_state(scenario.state), ensure_ascii=False)}\n```\n\n"
                f"{scenario.user}"
            ),
        },
    ]


ToolCall = tuple[str, dict[str, Any]] | None


def apply_tool_template(tokenizer, msgs: list[dict], think: bool = False) -> str:
    """Render messages + the tool catalog with a model's chat template.

    Shared by the transformers and ortgenai backends so the exported int4 model is
    fed the identical prompt as the bf16 one: the comparison must isolate
    quantization, not accidentally change the prompt. `tokenizer` is an HF
    AutoTokenizer (ORT GenAI's own tokenizer has no chat template).
    """
    for kwargs in (
        {"tools": openai_tools(), "enable_thinking": think},
        {"tools": openai_tools()},
    ):
        try:
            return tokenizer.apply_chat_template(
                msgs, add_generation_prompt=True, tokenize=False, **kwargs
            )
        except (TypeError, ValueError):
            continue
    msgs = list(msgs)
    if "Tools:" not in msgs[0]["content"]:
        msgs[0] = {
            **msgs[0],
            "content": msgs[0]["content"] + "\n\nTools:\n" + json.dumps(openai_tools()),
        }
    return tokenizer.apply_chat_template(
        msgs, add_generation_prompt=True, tokenize=False
    )


class Backend(Protocol):
    name: str

    def generate(self, scenario: Scenario) -> tuple[ToolCall, str, int]:
        """Return (tool_call, raw_text, latency_ms)."""
        ...


# ---------------------------------------------------------------------------
# Parsing. Templates differ in how they emit a call, so accept the common shapes
# rather than betting on one. A model that produced a correct call in a shape we
# failed to parse would look like a model failure, which is the worst kind of
# measurement bug: it points at the wrong thing.
# ---------------------------------------------------------------------------

_FENCE = re.compile(r"```(?:json)?\s*(\{.*?\})\s*```", re.S)
_TAGGED = re.compile(r"<tool_call>\s*(\{.*?\})\s*</tool_call>", re.S)
# Qwen3 reasons inside <think>. That block is full of the tool's *name* and of
# hypothetical JSON, so parsing before stripping it reads the model's musings as
# its decision. An unterminated block (generation cut off mid-thought) is dropped
# to the end: there is no answer after it, only an interrupted thought.
_THINK = re.compile(r"<think>.*?</think>", re.S)
_THINK_OPEN = re.compile(r"<think>.*$", re.S)

# The XML function-call format, which Qwen3.5 emits and JSON parsing cannot read:
#   <tool_call>
#     <function=search_catalog>
#       <parameter=query>
#       NGC 7000
#       </parameter>
#     </function>
#   </tool_call>
# Missing this cost Qwen3.5-4B a 0.238 on the first gen-5 run: it chose the right
# tool every time and every call scored as "answered with text". Same class of bug
# as the truncation miss, and the same lesson: a format the parser cannot read
# looks exactly like a model that cannot decide.
_XML_FUNC = re.compile(r"<function\s*=\s*([^>\s]+)\s*>(.*?)</function>", re.S)
_XML_PARAM = re.compile(r"<parameter\s*=\s*([^>\s]+)\s*>(.*?)</parameter>", re.S)

# The `call:NAME{k:v, k2:v2}` format Gemma 4 emits (unquoted keys and values):
#   call:search_catalog{query:NGC 7000}   call:get_status{}   call:dither_now{pixels:0}
# Third model, third surface. Qwen3-4B: JSON in <tool_call>. Qwen3.5: XML
# <function=>/<parameter=>. Gemma 4: this. All three got their native `tools=`
# chat template, all three chose correct tools, and each one first scored ~0.24
# because the parser could not read its output. The recurring lesson, now three
# times over: a format the harness cannot parse is indistinguishable from a model
# that cannot decide, and the fix is always the parser, never the prompt. If a
# fourth model brings a fourth format, this whack-a-mole is the signal to stop
# regexing and lean on the tokenizer's own tool-call parsing instead.
# Match only up to the opening brace; the relaxed parser then balances nesting
# from there. A non-greedy `{.*?}` would stop at the first inner '}' and truncate
# nested args (which is exactly the bug that mangled Gemma's create_plan).
_CALL_BRACE_OPEN = re.compile(r"\bcall:\s*(\w+)\s*\{")


def strip_thinking(text: str) -> str:
    return _THINK_OPEN.sub("", _THINK.sub("", text)).strip()


def _coerce(v: str) -> Any:
    """Parameter values arrive as text; recover their JSON type where possible.

    "3" -> 3, "true" -> True, "M42" -> "M42". Without this a numeric arg like
    dither pixels would reach the tool as a string and fail schema validation.
    """
    v = v.strip()
    try:
        return json.loads(v)
    except json.JSONDecodeError:
        return v


def _parse_xml_function(text: str) -> ToolCall:
    m = _XML_FUNC.search(text)
    if not m:
        return None
    name, inner = m.group(1).strip(), m.group(2)
    args = {k.strip(): _coerce(val) for k, val in _XML_PARAM.findall(inner)}
    return (name, args)


def _relaxed_value(s: str, i: int) -> tuple[Any, int]:
    """Parse one value from Gemma's relaxed JSON (unquoted keys and string values)
    starting at i, returning (value, next_index).

    Needed because Gemma's `call:NAME{...}` can nest: it built create_plan's
    targets array perfectly as `targets:[{frames:[{count:40,...}],name:M31}]`, and
    the old comma-split parser mangled it into "targets missing", scoring a correct
    model output as a failure. Fourth parser artifact of the session; this one
    handles the nesting rather than pretending it away.
    """
    while i < len(s) and s[i].isspace():
        i += 1
    if i >= len(s):
        return "", i
    if s[i] == "{":  # object
        obj: dict[str, Any] = {}
        i += 1
        while i < len(s) and s[i] != "}":
            while i < len(s) and (s[i].isspace() or s[i] == ","):
                i += 1
            if i >= len(s) or s[i] == "}":
                break
            key_start = i
            while i < len(s) and s[i] != ":":
                i += 1
            key = s[key_start:i].strip()
            i += 1  # skip ':'
            val, i = _relaxed_value(s, i)
            obj[key] = val
        return obj, i + 1
    if s[i] == "[":  # array
        arr: list[Any] = []
        i += 1
        while i < len(s) and s[i] != "]":
            while i < len(s) and (s[i].isspace() or s[i] == ","):
                i += 1
            if i >= len(s) or s[i] == "]":
                break
            val, i = _relaxed_value(s, i)
            arr.append(val)
        return arr, i + 1
    # Scalar: read until a delimiter at this depth. Bare strings may hold spaces
    # ("M31 hoje"), so only , } ] terminate it.
    start = i
    while i < len(s) and s[i] not in ",}]":
        i += 1
    return _coerce(s[start:i]), i


def _parse_call_brace(text: str) -> ToolCall:
    m = _CALL_BRACE_OPEN.search(text)
    if not m:
        return None
    name = m.group(1).strip()
    # Parse from the opening brace so nested {}/[] balance correctly, rather than
    # trusting the non-greedy `{.*?}` which stops at the first inner '}'.
    obj, _ = _relaxed_value(text, m.end() - 1)
    return (name, obj if isinstance(obj, dict) else {})


def parse_tool_call(text: str) -> ToolCall:
    text = strip_thinking(text)
    # XML function format first: its tags are unambiguous, and it may sit right
    # after a prose preamble ("Vou apontar o telescópio para M42.\n\n<tool_call>...").
    xml = _parse_xml_function(text)
    if xml:
        return xml
    brace = _parse_call_brace(text)
    if brace:
        return brace
    for pat in (_TAGGED, _FENCE):
        m = pat.search(text)
        if m:
            obj = _load(m.group(1))
            if obj:
                return _normalize(obj)
    # Bare JSON object somewhere in the reply.
    start = text.find("{")
    while start != -1:
        obj = _load(text[start:])
        if obj:
            call = _normalize(obj)
            if call:
                return call
        start = text.find("{", start + 1)
    return None


def _load(s: str) -> dict[str, Any] | None:
    try:
        return json.loads(s)
    except json.JSONDecodeError:
        # Trailing prose after the object is common; retry on the balanced prefix.
        depth = 0
        for i, ch in enumerate(s):
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(s[: i + 1])
                    except json.JSONDecodeError:
                        return None
        return None


def _normalize(obj: Any) -> ToolCall:
    if not isinstance(obj, dict):
        return None
    # {"name": ..., "arguments": {...}} and the OpenAI {"function": {...}} wrapper.
    fn = obj.get("function") if isinstance(obj.get("function"), dict) else obj
    name = fn.get("name")
    if not isinstance(name, str):
        return None
    args = fn.get("arguments", fn.get("parameters", {}))
    if isinstance(args, str):
        args = _load(args) or {}
    return (name, args if isinstance(args, dict) else {})


# ---------------------------------------------------------------------------


class MockBackend:
    """Keyword routing. Not a model: a control.

    It exists so a red eval can be blamed on the model rather than on the harness.
    It is deliberately naive, and it fails the traps, which is the point: if the
    real numbers look like these, the model is adding nothing.
    """

    name = "mock"

    def __init__(self, model: str = "keyword-router") -> None:
        self.model = model

    @staticmethod
    def _route(u: str) -> ToolCall:
        if "dither" in u:
            return ("dither_now", {"pixels": 3})
        if "autofoco" in u or "foco" in u or "estrela" in u:
            return ("get_image_stats", {})
        if "aponta" in u or "vai para" in u:
            return ("slew_to", {"ra": 5.588, "dec": -5.39})
        if "onde fica" in u:
            return ("search_catalog", {"query": "NGC 7000"})
        if "hoje" in u:
            return ("get_tonights_best", {"limit": 10})
        if "rig" in u or "guiagem" in u:
            return ("get_status", {})
        return None

    def generate(self, scenario: Scenario) -> tuple[ToolCall, str, int]:
        t0 = time.perf_counter()
        call = self._route(scenario.user.lower())
        ms = int((time.perf_counter() - t0) * 1000)
        return call, json.dumps(call, ensure_ascii=False) if call else "", ms

    def run_messages(self, msgs: list[dict]) -> tuple[ToolCall, str, int]:
        # Multi-step control: route the first call on the user text, then stop
        # once a tool result has come back. The mock deliberately does NOT do
        # follow-up steps, so it fails the two-step workflows (does step 1, stops)
        # and passes the one-step ones. That is the useful floor: it proves the
        # harness can tell a model that completes a sequence from one that quits
        # after the first call.
        t0 = time.perf_counter()
        already_acted = any(m.get("role") == "tool" for m in msgs)
        if already_acted:
            return None, "", int((time.perf_counter() - t0) * 1000)
        user = next((m["content"] for m in msgs if m.get("role") == "user"), "")
        call = self._route(user.lower())
        ms = int((time.perf_counter() - t0) * 1000)
        return call, json.dumps(call, ensure_ascii=False) if call else "", ms


class TransformersBackend:
    """Desktop, for choosing which model to export. Not a device number."""

    name = "transformers"

    def __init__(
        self,
        model: str,
        dtype: str = "auto",
        device: str | None = None,
        max_new_tokens: int = 1024,
        think: bool = False,
        load_4bit: bool = False,
    ) -> None:
        import torch
        import transformers
        from transformers import AutoTokenizer  # lazy: heavy

        # Plain bf16 + .to() for models that fit one GPU. A dense 4B is ~8GB and
        # fits the 5070 Ti's 16GB whole. But the "E4B" multimodal models do NOT: an
        # effective-4B like Gemma 4 E4B carries a vision tower and per-layer
        # embeddings, ~8B raw params, ~16GB in bf16, which OOM'd this card at 96%
        # VRAM with nothing left for activations. load_4bit is the fix, and it is
        # the MORE representative run, not a compromise: the phone ships int4, so a
        # 4-bit desktop number is closer to the device than bf16 ever was. Verified
        # working on this Blackwell (sm_120) card, where bitsandbytes is dicey.
        self.load_4bit = load_4bit
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        self.model = model
        # think=False by default. Qwen3 reasons before answering, and on the first
        # real run that ate the entire budget: the model decided correctly inside
        # <think> and was cut off before emitting the call, which scored as a miss.
        # Raising the budget fixes the truncation, but the latency is the actual
        # objection: 13s median on a 5070 Ti is already past usable, and the target
        # is a phone. Picking 1 of 8 tools should not need a chain of thought.
        # Keep the flag: "does thinking buy accuracy, and at what latency" is a
        # question worth answering with numbers rather than assuming.
        self.think = think
        self.max_new_tokens = max_new_tokens
        self._tok = AutoTokenizer.from_pretrained(model)

        # Try auto-classes in order. Qwen3.5-4B declares architecture
        # `Qwen3_5ForConditionalGeneration` and Gemma 4 `Gemma4ForConditionalGeneration`:
        # both are text-generation-capable but not registered under
        # AutoModelForCausalLM, so loading them that way raises. The image-text and
        # generic vision classes cover the current multimodal 4Bs; CausalLM stays
        # first for the plain text models (Qwen3-4B). generate() is identical across
        # all of them, so nothing downstream cares which one loaded.
        kwargs: dict[str, Any] = {"dtype": dtype}
        if load_4bit:
            from transformers import BitsAndBytesConfig

            # NF4, the config the smoke test confirmed on this card. device_map is
            # required with a quantization config (bitsandbytes places the layers),
            # and it replaces the later .to() rather than fighting it.
            kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_quant_type="nf4",
                bnb_4bit_compute_dtype=torch.bfloat16,
            )
            kwargs["device_map"] = self.device

        last_err: Exception | None = None
        for cls_name in (
            "AutoModelForCausalLM",
            "AutoModelForImageTextToText",
            "AutoModelForVision2Seq",
            "AutoModel",
        ):
            cls = getattr(transformers, cls_name, None)
            if cls is None:
                continue
            try:
                self._m = cls.from_pretrained(model, **kwargs)
                if not load_4bit:  # 4-bit is already placed by device_map
                    self._m = self._m.to(self.device)
                self._loaded_via = cls_name
                break
            except (ValueError, TypeError, KeyError) as e:
                last_err = e
        else:
            raise RuntimeError(
                f"no auto-class could load {model!r}; last error: {last_err}"
            )
        self._m.eval()

    def _apply_template(self, msgs: list[dict]) -> str:
        return apply_tool_template(self._tok, msgs, think=self.think)

    def run_messages(self, msgs: list[dict]) -> tuple[ToolCall, str, int]:
        """Core turn: render a messages list, generate, parse. Reused by the
        single-turn path and by the multi-step runner (which owns the growing
        messages list across turns)."""
        import torch

        ids = self._tok(self._apply_template(msgs), return_tensors="pt").to(self._m.device)
        t0 = time.perf_counter()
        with torch.inference_mode():
            out = self._m.generate(
                **ids, max_new_tokens=self.max_new_tokens, do_sample=False
            )
        ms = int((time.perf_counter() - t0) * 1000)
        text = self._tok.decode(
            out[0][ids["input_ids"].shape[-1] :], skip_special_tokens=True
        )
        # Flag truncation explicitly. Silent truncation is what made the first run
        # look like a model failure when it was a budget failure.
        if out[0].shape[-1] - ids["input_ids"].shape[-1] >= self.max_new_tokens:
            text += "\n[TRUNCATED: hit max_new_tokens]"
        return parse_tool_call(text), text, ms

    def generate(self, scenario: Scenario) -> tuple[ToolCall, str, int]:
        return self.run_messages(build_messages(scenario))


class OrtGenAIBackend:
    """The one that counts: the exact int4 ONNX model that ships to the phone,
    run through ONNX Runtime GenAI. Validating it on the desktop first isolates
    what quantization + ONNX export cost, before any device engineering.

    `model` is a directory produced by the genai model builder (int4), holding the
    ONNX, genai_config.json, and tokenizer files. The HF AutoTokenizer is loaded
    from that same directory purely for the chat template (the genai Tokenizer has
    none), so the prompt is byte-identical to the transformers run.

    Phase 0 is only truly closed by running this ON a phone: the desktop says
    nothing about NNAPI, CoreML, thermals, or a 2-4GB model meeting the app's
    memory guard (written for 200MB GraXpert models). But it says everything about
    whether the quantized weights still choose the right tool.
    """

    name = "ortgenai"

    def __init__(
        self,
        model: str,
        max_new_tokens: int = 1024,
        think: bool = False,
        provider: str = "cpu",
    ) -> None:
        import onnxruntime_genai as og  # lazy: not installed in the base env
        from transformers import AutoTokenizer

        self.model = model
        self.think = think
        self.max_new_tokens = max_new_tokens
        self._og = og
        # EP selection differs across genai versions. 0.11.x defaults to CPU from
        # a bare Model(path); 0.14.x refuses to default and its explicit-EP path is
        # broken on this Windows build (no EP loads). We pin 0.11.0 for desktop
        # validation, so the simple path is correct; the Config branch stays as a
        # fallback for a build where an explicit EP is needed and works. On the
        # phone this becomes NNAPI / CoreML, same int4 weights.
        try:
            self._model = og.Model(model)
        except Exception:
            cfg = og.Config(model)
            cfg.clear_providers()
            cfg.append_provider(provider)
            self._model = og.Model(cfg)
        self._og_tok = og.Tokenizer(self._model)
        # HF tokenizer from the same dir, for apply_chat_template only.
        self._hf_tok = AutoTokenizer.from_pretrained(model)

    def run_messages(self, msgs: list[dict]) -> tuple[ToolCall, str, int]:
        og = self._og
        prompt = apply_tool_template(self._hf_tok, msgs, think=self.think)
        input_tokens = self._og_tok.encode(prompt)

        params = og.GeneratorParams(self._model)
        params.set_search_options(max_length=len(input_tokens) + self.max_new_tokens,
                                  do_sample=False)

        t0 = time.perf_counter()
        gen = og.Generator(self._model, params)
        gen.append_tokens(input_tokens)
        while not gen.is_done():
            gen.generate_next_token()
        ms = int((time.perf_counter() - t0) * 1000)

        # get_sequence(0) is the full sequence (prompt + generation); slice off the
        # prompt so parsing sees only what the model produced.
        full = gen.get_sequence(0)
        new = full[len(input_tokens):]
        text = self._og_tok.decode(new)
        return parse_tool_call(text), text, ms

    def generate(self, scenario: Scenario) -> tuple[ToolCall, str, int]:
        return self.run_messages(build_messages(scenario))



class OpenAIBackend:
    """An OpenAI-compatible /v1 endpoint: llama-server, Ollama, LM Studio.

    This exists because the other backends measure a model, not a DEPLOYMENT.
    `transformers` runs HF weights on a workstation GPU; the SBC tier actually
    ships a GGUF served by llama-server, and that path has its own tool-calling
    behaviour (the server applies the model's chat template and may emit native
    OpenAI `tool_calls` instead of in-text tags). Pointing the eval at the real
    server is the only way to score what users will run.

    Tools go in the request body rather than being baked into the prompt: that
    is what the SBC provider does in production, and letting the server own the
    template is the whole point of testing through it.
    """

    name = "openai"

    def __init__(
        self,
        model: str,
        base_url: str = "http://127.0.0.1:8080/v1",
        max_new_tokens: int = 1024,
        think: bool = False,
        **_ignored,
    ) -> None:
        import httpx

        self.model = model
        self.base_url = base_url.rstrip("/")
        self.max_new_tokens = max_new_tokens
        self.think = think
        # Generous read timeout: a cold CPU-only 4B on an SBC can take minutes
        # for the first turn, and a timeout here would be scored as a model
        # failure -- the measurement bug this harness keeps warning about.
        self._client = httpx.Client(timeout=httpx.Timeout(600.0, connect=15.0))

    def run_messages(self, msgs: list[dict]) -> tuple[ToolCall, str, int]:
        body: dict[str, Any] = {
            "model": self.model,
            "messages": msgs,
            "tools": openai_tools(),
            "tool_choice": "auto",
            "temperature": 0,
            "max_tokens": self.max_new_tokens,
            "stream": False,
        }
        if not self.think:
            # Harmless on servers that do not know it; suppresses Qwen3 thinking.
            body["chat_template_kwargs"] = {"enable_thinking": False}
        t0 = time.perf_counter()
        r = self._client.post(f"{self.base_url}/chat/completions", json=body)
        ms = int((time.perf_counter() - t0) * 1000)
        if r.status_code != 200:
            # A transport failure is NOT a model decision. Return "no call" but
            # keep the body in the text so the report shows a broken endpoint
            # instead of scoring it as the model refusing to act.
            return None, f"[HTTP {r.status_code}] {r.text[:400]}", ms
        data = r.json()
        choice = (data.get("choices") or [{}])[0]
        msg = choice.get("message") or {}
        text = msg.get("content") or ""

        # Native tool_calls win when present: that is the server having parsed
        # the model's own format for us, which is strictly more reliable than
        # re-parsing prose.
        calls = msg.get("tool_calls") or []
        if calls:
            fn = (calls[0] or {}).get("function") or {}
            raw_args = fn.get("arguments")
            args = raw_args
            if isinstance(raw_args, str):
                try:
                    args = json.loads(raw_args) if raw_args.strip() else {}
                except json.JSONDecodeError:
                    args = {}
            if not isinstance(args, dict):
                args = {}
            # ToolCall is the alias `tuple[str, dict] | None`, not a class.
            name = fn.get("name")
            call = (name, args) if name else None
            return call, text or json.dumps(calls), ms

        # No native call: the model may still have written one in text.
        return parse_tool_call(text), text, ms

    def generate(self, scenario: Scenario) -> tuple[ToolCall, str, int]:
        return self.run_messages(build_messages(scenario))


def make_backend(kind: str, model: str, **kw) -> Backend:
    if kind == "mock":
        return MockBackend(model)
    if kind == "transformers":
        return TransformersBackend(model, **kw)
    if kind == "ortgenai":
        # ortgenai ignores load_4bit (the model is already int4) and device.
        return OrtGenAIBackend(
            model,
            max_new_tokens=kw.get("max_new_tokens", 1024),
            think=kw.get("think", False),
        )
    if kind == "openai":
        return OpenAIBackend(model, **kw)
    raise SystemExit(
        f"unknown backend {kind!r}: use mock, transformers, ortgenai or openai"
    )

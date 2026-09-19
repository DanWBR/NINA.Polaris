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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services.External;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The "Cloud API with your key" configuration: the key is stored on
/// the host, patched with relay-token semantics, and never part of what the
/// endpoints hand to a client.</summary>
[TestFixture]
public class CanopusApiConfigTests {
    private string _dir = "";
    private string _suggestions = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-canopus-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _suggestions = Path.Combine(_dir, "api-models.json");
        File.WriteAllText(_suggestions, """{"anthropic":["claude-sonnet-5"],"openai":["gpt-5"],"compatible":[]}""");
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private CanopusApiConfigService Svc() =>
        new(Path.Combine(_dir, "canopus", "api-config.json"), _suggestions, NullLogger<CanopusApiConfigService>.Instance);

    [Test]
    public void Defaults_WhenNothingIsStored() {
        var c = Svc().Get();
        Assert.That(c.Provider, Is.EqualTo("anthropic"));
        Assert.That(c.HasKey, Is.False);
        Assert.That(Svc().Validate(), Does.Contain("API key"));
    }

    [Test]
    public void Update_RoundTripsThroughTheFile_AndKeySemantics() {
        var svc = Svc();
        svc.Update(new CanopusApiConfigUpdate(Provider: "OpenAI", Model: " gpt-5 ", ApiKey: " sk-1 "));
        var again = Svc();   // fresh instance reads the file
        var c = again.Get();
        Assert.That(c.Provider, Is.EqualTo("openai"));
        Assert.That(c.Model, Is.EqualTo("gpt-5"));
        Assert.That(c.ApiKey, Is.EqualTo("sk-1"));
        Assert.That(again.Validate(), Is.Null);

        again.Update(new CanopusApiConfigUpdate(Model: "gpt-5-mini"));     // null key keeps it
        Assert.That(again.Get().ApiKey, Is.EqualTo("sk-1"));
        again.Update(new CanopusApiConfigUpdate(ApiKey: "sk-2"));          // value replaces
        Assert.That(again.Get().ApiKey, Is.EqualTo("sk-2"));
        again.Update(new CanopusApiConfigUpdate(ApiKey: ""));              // empty clears
        Assert.That(again.HasKey, Is.False);
    }

    [Test]
    public void Update_RejectsAnUnknownProvider() {
        Assert.Throws<ArgumentException>(() => Svc().Update(new CanopusApiConfigUpdate(Provider: "gemini")));
    }

    [Test]
    public void Validate_CompatibleNeedsABaseUrl_AndAModel() {
        var svc = Svc();
        svc.Update(new CanopusApiConfigUpdate(Provider: "compatible", ApiKey: "k"));
        Assert.That(svc.Validate(), Does.Contain("base URL"));
        svc.Update(new CanopusApiConfigUpdate(BaseUrl: "http://localhost:11434/v1"));
        Assert.That(svc.Validate(), Does.Contain("model"));
        svc.Update(new CanopusApiConfigUpdate(Model: "qwen3"));
        Assert.That(svc.Validate(), Is.Null);
    }

    [Test]
    public void GetPublic_NeverCarriesTheKey() {
        var svc = Svc();
        svc.Update(new CanopusApiConfigUpdate(ApiKey: "sk-secret-value", Model: "claude-sonnet-5"));
        var json = JsonSerializer.Serialize(svc.GetPublic());
        Assert.That(json, Does.Not.Contain("sk-secret-value"));
        Assert.That(json, Does.Not.Contain("apiKey"));
        Assert.That(json, Does.Contain("\"hasKey\":true"));
        Assert.That(json, Does.Contain("claude-sonnet-5"));
        Assert.That(svc.Suggestions()["openai"], Is.EqualTo(new[] { "gpt-5" }));
    }

    [Test]
    public void AgentEnvironment_PerMode() {
        var local = CanopusServerService.BuildAgentEnvironment(CanopusMode.Local, 8791, null);
        Assert.That(local["CANOPUS_LOCAL_LLM_URL"], Is.EqualTo("http://127.0.0.1:8791"));
        Assert.That(local["CANOPUS_LOCAL_TIER"], Is.EqualTo("1"));
        Assert.That(local["CANOPUS_BASE_PATH"], Is.EqualTo("/canopus"));
        Assert.That(local.Keys.Any(k => k.StartsWith("CANOPUS_API_")), Is.False);

        var api = CanopusServerService.BuildAgentEnvironment(CanopusMode.Api, 8791,
            new CanopusApiConfig("anthropic", "", "claude-sonnet-5", "sk-1"));
        Assert.That(api["CANOPUS_API_PROVIDER"], Is.EqualTo("anthropic"));
        Assert.That(api["CANOPUS_API_KEY"], Is.EqualTo("sk-1"));
        Assert.That(api["CANOPUS_API_MODEL"], Is.EqualTo("claude-sonnet-5"));
        Assert.That(api["CANOPUS_API_BASE_URL"], Is.EqualTo(""));
        Assert.That(api["CANOPUS_BASE_PATH"], Is.EqualTo("/canopus"));
        Assert.That(api.ContainsKey("CANOPUS_LOCAL_TIER"), Is.False, "the API mode gets the full catalog");
        Assert.That(api.ContainsKey("CANOPUS_LOCAL_LLM_URL"), Is.False);
    }

    [Test]
    public void ParseModelIds_ReadsOpenAIAndAnthropicListings() {
        Assert.That(CanopusEndpoints.ParseModelIds("""{"data":[{"id":"gpt-5"},{"id":"gpt-5-mini"}]}"""),
            Is.EqualTo(new[] { "gpt-5", "gpt-5-mini" }));
        Assert.That(CanopusEndpoints.ParseModelIds("""{"data":[{"id":"claude-sonnet-5","display_name":"x"}],"has_more":false}"""),
            Is.EqualTo(new[] { "claude-sonnet-5" }));
        Assert.That(CanopusEndpoints.ParseModelIds("not json"), Is.Empty);
        Assert.That(CanopusEndpoints.ParseModelIds("""{"models":[]}"""), Is.Empty);
    }
}

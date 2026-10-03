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

namespace NINA.Polaris.Services.Bge;

/// <summary>Where the operator wants the background model computed.</summary>
public enum BgeWhere {
    /// <summary>Host if it can, otherwise the browser. The default, and the only
    /// setting that does something sensible on every board without the operator
    /// having to know which accelerators theirs has.</summary>
    Auto = 0,
    /// <summary>This process only. Nothing leaves the host; on a board with no
    /// accelerator this means no correction, stated rather than hidden.</summary>
    Host = 1,
    /// <summary>The browser only. Keeps the host's cores for capture and
    /// stacking, and needs a tab open.</summary>
    Client = 2,
}

/// <summary>
/// Sends a background-model request to the host or to the browser, by the rig's
/// setting, and keeps the corrector ignorant of the choice.
///
/// Auto prefers the host, because an accelerator answers in about a tenth of a
/// second with no network in the path, and falls to the browser when no lane in
/// this process both exists and has a model. On an Allwinner or Raspberry Pi
/// board that fallback is the whole feature.
/// </summary>
public sealed class BgeProducerRouter : IBgeModelProducer {

    private readonly HostBgeModelProducer _host;
    private readonly ClientBgeModelProducer _client;

    public BgeProducerRouter(HostBgeModelProducer host, ClientBgeModelProducer client) {
        _host = host;
        _client = client;
    }

    /// <summary>The operator's choice. Set from the rig before each session.</summary>
    public BgeWhere Where { get; set; } = BgeWhere.Auto;

    /// <summary>Which producer a request would go to right now, or null when
    /// none would take it. This is what the status line reports, so the operator
    /// can see Auto's decision rather than guess at it.</summary>
    public IBgeModelProducer? Chosen => Where switch {
        BgeWhere.Host => _host.CanRun ? _host : null,
        BgeWhere.Client => _client.CanRun ? _client : null,
        _ => _host.CanRun ? _host : (_client.CanRun ? (IBgeModelProducer)_client : null),
    };

    /// <summary>"host" or "client" once a choice has been made, else "none".
    /// Reported rather than inferred: on a board where the host lane looks
    /// present but has no model, the difference matters.</summary>
    public string Name => Chosen?.Name ?? "none";

    /// <summary>The accelerator the host lane last used, for the status line.</summary>
    public string? HostLane => _host.Lane;

    public bool CanRun => Chosen != null;

    public Task<float[]?> RunAsync(float[] nhwcTensor, int tile, CancellationToken ct) {
        var chosen = Chosen;
        if (chosen == null) return Task.FromResult<float[]?>(null);
        return chosen.RunAsync(nhwcTensor, tile, ct);
    }
}

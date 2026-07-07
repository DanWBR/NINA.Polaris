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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Persists native-SDK camera control values (gain, offset, cooler, gamma,
/// USB speed, …) so they survive disconnect/reconnect and app restarts. The
/// live SDK forgets everything on close, so without this the config panel
/// would reset every session.
///
/// Values are keyed by the camera's <see cref="ICamera.DeviceName"/> — i.e.
/// per physical camera, NOT per rig — so a camera that appears in more than
/// one rig keeps its tuning. Written to
/// <c>{LocalAppData}/NINA.Polaris/native-camera-controls.json</c>.
/// </summary>
public sealed class NativeCameraControlStore {
    private readonly object _gate = new();
    private readonly string _path;
    // camera DeviceName -> (control id -> value+auto)
    private Dictionary<string, Dictionary<string, StoredControl>> _data = new();

    public NativeCameraControlStore() {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA.Polaris");
        try { Directory.CreateDirectory(dir); } catch { }
        _path = Path.Combine(dir, "native-camera-controls.json");
        Load();
    }

    /// <summary>One persisted control: its last value and auto flag.</summary>
    public sealed record StoredControl(double Value, bool Auto);

    /// <summary>Record (or overwrite) a control value for a camera and flush.</summary>
    public void Set(string? deviceName, string controlId, double value, bool auto) {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(controlId)) return;
        lock (_gate) {
            if (!_data.TryGetValue(deviceName, out var perCam)) {
                perCam = new Dictionary<string, StoredControl>();
                _data[deviceName] = perCam;
            }
            perCam[controlId] = new StoredControl(value, auto);
            Save();
        }
    }

    /// <summary>Saved controls for a camera, or empty if none stored yet.</summary>
    public IReadOnlyDictionary<string, StoredControl> Get(string? deviceName) {
        lock (_gate) {
            if (!string.IsNullOrWhiteSpace(deviceName)
                && _data.TryGetValue(deviceName!, out var perCam))
                return new Dictionary<string, StoredControl>(perCam);
            return new Dictionary<string, StoredControl>();
        }
    }

    /// <summary>
    /// Re-apply every saved control to a freshly-connected camera. Best-effort:
    /// controls the SDK no longer exposes (or that fail to set) are skipped.
    /// Only writes ids the camera currently advertises as writable so we don't
    /// poke stale/removed options. Returns the number of controls applied.
    /// </summary>
    public int ApplySaved(ICamera? cam) {
        if (cam == null || !cam.IsConnected) return 0;
        var saved = Get(cam.DeviceName);
        if (saved.Count == 0) return 0;
        int applied = 0;
        HashSet<string> writable;
        try {
            writable = cam.GetControls()
                .Where(c => c.Writable)
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);
        } catch {
            return 0;
        }
        foreach (var (id, sc) in saved) {
            if (!writable.Contains(id)) continue;
            try {
                if (cam.SetControl(id, sc.Value, sc.Auto)) applied++;
            } catch {
                // Non-fatal — a single bad control shouldn't block the rest.
            }
        }
        return applied;
    }

    private void Load() {
        try {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, StoredControl>>>(json);
            if (parsed != null) _data = parsed;
        } catch {
            // Corrupt/unreadable store is non-fatal; start fresh.
            _data = new();
        }
    }

    private void Save() {
        try {
            var json = JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        } catch {
            // Persistence failure is non-fatal; the live SDK values still apply.
        }
    }
}

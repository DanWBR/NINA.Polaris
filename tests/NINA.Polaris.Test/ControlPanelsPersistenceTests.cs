// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class ControlPanelsPersistenceTests {
    [Test]
    public void CloneActiveRigAs_DeepCopiesControlPanels() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-panels-" + Guid.NewGuid().ToString("N"));
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = dir })
                .Build();
            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            var active = profiles.ActiveEquipmentProfile;

            profiles.UpdateEquipmentProfile(active.Id, r => {
                r.ControlPanels.Add(new ControlPanelDef {
                    Id = "p1", Title = "Cooling", Visible = true,
                    Left = 120, Top = 80, Width = 300, Height = 220, Z = 5,
                    Dock = "right", DockOrder = 2,
                    Widgets = {
                        new ControlWidgetDef {
                            Id = "w1", Label = "Cooler target", Kind = "slider", Source = "action",
                            Action = "camera.coolerTarget", Unit = "°C", Min = -20, Max = 20, Step = 1
                        },
                        new ControlWidgetDef {
                            Id = "w2", Label = "Dew heater", Kind = "toggle", Source = "switch",
                            ChannelKey = "DEW_PWM.CH1"
                        }
                    }
                });
            });

            var clone = profiles.CloneActiveRigAs("Copy");
            Assert.That(clone.ControlPanels, Has.Count.EqualTo(1), "clone dropped the panels");
            var p = clone.ControlPanels[0];
            Assert.That(p.Title, Is.EqualTo("Cooling"));
            Assert.That(p.Left, Is.EqualTo(120));
            Assert.That(p.Z, Is.EqualTo(5));
            Assert.That(p.Dock, Is.EqualTo("right"));
            Assert.That(p.DockOrder, Is.EqualTo(2));
            Assert.That(p.Widgets, Has.Count.EqualTo(2));
            Assert.That(p.Widgets[0].Action, Is.EqualTo("camera.coolerTarget"));
            Assert.That(p.Widgets[1].ChannelKey, Is.EqualTo("DEW_PWM.CH1"));

            // Deep copy: mutating the clone must not touch the source rig.
            p.Widgets[0].Label = "changed";
            p.Title = "changed";
            var stored = profiles.ActiveEquipmentProfile.ControlPanels[0];
            Assert.That(stored.Title, Is.EqualTo("Cooling"));
            Assert.That(stored.Widgets[0].Label, Is.EqualTo("Cooler target"));
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

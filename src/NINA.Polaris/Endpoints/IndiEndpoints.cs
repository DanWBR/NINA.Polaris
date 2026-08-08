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

using System.Net.Sockets;
using NINA.INDI.Client;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class IndiEndpoints {
    public static void MapIndiEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/indi");

        group.MapPost("/connect", async (IndiClient client, IndiConnectRequest request) => {
            try {
                await client.ConnectAsync();
                return Results.Ok(new {
                    status = "connected",
                    host = client.Host,
                    port = client.Port
                });
            } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused
                                            || ex.SocketErrorCode == SocketError.HostNotFound
                                            || ex.SocketErrorCode == SocketError.HostUnreachable
                                            || ex.SocketErrorCode == SocketError.NetworkUnreachable) {
                // 502 Bad Gateway, the most common case: indiserver isn't running
                // at the configured host:port. Translate to a user-facing message
                // instead of leaking the raw localised OS error as a 500.
                return Results.Json(new {
                    error = "indi_unreachable",
                    detail = $"INDI server not reachable at {client.Host}:{client.Port}. " +
                             "Start indiserver on that host or update Indi:Host / Indi:Port (or the rig's INDI endpoint).",
                    socketError = ex.SocketErrorCode.ToString()
                }, statusCode: 502);
            } catch (TimeoutException ex) {
                return Results.Json(new {
                    error = "indi_timeout",
                    detail = ex.Message
                }, statusCode: 504);
            } catch (Exception ex) {
                return Results.Json(new {
                    error = "indi_connect_failed",
                    detail = ex.Message
                }, statusCode: 502);
            }
        });

        group.MapPost("/disconnect", async (IndiClient client) => {
            await client.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapGet("/status", (IndiClient client) => {
            return Results.Ok(new {
                connected = client.IsConnected,
                host = client.Host,
                port = client.Port,
                devices = client.GetDeviceNames()
            });
        });

        group.MapGet("/devices", (IndiClient client) => {
            var devices = new List<object>();
            foreach (var deviceName in client.GetDeviceNames()) {
                if (client.Devices.TryGetValue(deviceName, out var props)) {
                    var groups = props.Values
                        .Select(p => p.Group)
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .ToList();

                    devices.Add(new {
                        name = deviceName,
                        propertyCount = props.Count,
                        groups
                    });
                }
            }
            return Results.Ok(new { devices });
        });

        group.MapGet("/devices/{deviceName}/properties", (IndiClient client, string deviceName) => {
            if (!client.Devices.TryGetValue(deviceName, out var props))
                return Results.NotFound(new { error = $"Device '{deviceName}' not found" });

            var result = props.Values.Select(p => new {
                name = p.Name,
                label = p.Label,
                group = p.Group,
                state = p.State.ToString(),
                permission = p.Permission.ToString(),
                type = p.GetType().Name.Replace("Indi", "").Replace("Property", "")
            });

            return Results.Ok(new { device = deviceName, properties = result });
        });

        // Which serial node a driver is pointed at, plus the stable choices.
        //
        // Serial drivers default to /dev/ttyUSB0, and ttyUSBn numbering follows
        // enumeration order at boot. Two USB-serial devices on one rig and the
        // loser silently talks to the wrong hardware, with nothing in the UI
        // that even names the port. Field report 2026-08-06: a Gemini focuser
        // bound to a port that had a ZWO device on it, in the field, at night.
        group.MapGet("/devices/{deviceName}/port", (
                IndiClient client, UsbScanService usb, string deviceName) => {
            if (!client.Devices.ContainsKey(deviceName))
                return Results.NotFound(new { error = $"Device '{deviceName}' not found" });

            var current = client.GetDevicePort(deviceName);
            var scan = usb.Scan();
            return Results.Ok(new {
                device = deviceName,
                // false = this driver has no DEVICE_PORT, i.e. it is not serial
                // and the picker should not be offered at all.
                serial = current != null,
                port = current,
                // by-id paths survive a reboot; ttyUSBn does not, which is the
                // whole reason this endpoint exists.
                options = scan.SerialPorts.Select(p => new {
                    value = "/dev/serial/by-id/" + p.ByIdName,
                    label = p.ByIdName,
                    resolvesTo = p.Device,
                }).ToList(),
            });
        });

        group.MapPut("/devices/{deviceName}/port", async (
                IndiClient client, string deviceName, IndiPortRequest request) => {
            if (!client.Devices.ContainsKey(deviceName))
                return Results.NotFound(new { error = $"Device '{deviceName}' not found" });
            var port = (request?.Port ?? "").Trim();
            if (port.Length == 0)
                return Results.BadRequest(new { error = "No port given" });
            if (client.GetDevicePort(deviceName) == null) {
                return Results.BadRequest(new {
                    error = $"'{deviceName}' exposes no DEVICE_PORT, so it is not a serial device."
                });
            }
            await client.SetDevicePortAsync(deviceName, port);
            return Results.Ok(new { device = deviceName, port });
        });
    }

    public record IndiConnectRequest(string? Host = "localhost", int? Port = 7624);
    public record IndiPortRequest(string? Port);
}
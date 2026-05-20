using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class CameraEndpoints {
    public static void MapCameraEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/camera");

        group.MapPost("/capture", async (EquipmentManager equip, ImageRelayService relay,
            LiveStackingService liveStack, CaptureRequest request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            try {
                if (request.Binning > 0)
                    await equip.Camera.SetBinningAsync(request.Binning, request.Binning);

                var imageData = await equip.Camera.CaptureAsync(request.Exposure);

                if (liveStack.IsRunning)
                    await liveStack.AddFrameAsync(imageData);
                else
                    await relay.RelayImageAsync(imageData);

                var stats = imageData.Statistics;
                return Results.Ok(new {
                    status = "complete",
                    width = imageData.Properties.Width,
                    height = imageData.Properties.Height,
                    stats = new {
                        mean = stats.Mean,
                        median = stats.Median,
                        stdev = stats.StDev,
                        starCount = stats.StarCount,
                        hfr = stats.HFR,
                        min = stats.Min,
                        max = stats.Max
                    }
                });
            } catch (OperationCanceledException) {
                return Results.Ok(new { status = "cancelled" });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.AbortExposureAsync();
            return Results.Ok(new { status = "aborted" });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.Ok(new {
                    connected = false,
                    state = "disconnected",
                    temperature = (double?)null,
                    coolerOn = false,
                    binX = 0, binY = 0
                });

            return Results.Ok(new {
                connected = equip.Camera.IsConnected,
                state = equip.Camera.State.ToString(),
                temperature = NanToNull(equip.Camera.Temperature),
                coolerOn = equip.Camera.CoolerOn,
                binX = equip.Camera.BinX,
                binY = equip.Camera.BinY,
                maxX = equip.Camera.MaxX,
                maxY = equip.Camera.MaxY,
                pixelSizeX = NanToNull(equip.Camera.PixelSizeX),
                pixelSizeY = NanToNull(equip.Camera.PixelSizeY),
                bitDepth = equip.Camera.BitDepth
            });
        });

        group.MapPost("/cooler", async (EquipmentManager equip, CoolerRequest request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.SetCoolerAsync(request.Enabled);
            if (request.TargetTemperature.HasValue)
                await equip.Camera.SetTemperatureAsync(request.TargetTemperature.Value);

            return Results.Ok(new { coolerOn = request.Enabled, target = request.TargetTemperature });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectCamera(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected. Use POST /api/camera/select/{name} first" });

            await equip.Camera.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Camera.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    static double? NanToNull(double v) => double.IsNaN(v) ? null : v;

    public record CaptureRequest(double Exposure = 1.0, int Gain = 100, int Binning = 1, string? Filter = null);
    public record CoolerRequest(bool Enabled, double? TargetTemperature = null);
}

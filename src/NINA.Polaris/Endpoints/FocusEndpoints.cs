using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// FOCUS tab's Manual Assist subtab endpoints. Currently only
/// hosts the Bahtinov mask analyser; future sub-features (donut
/// metric, FWHM gaussian fit) land here too.
/// </summary>
public static class FocusEndpoints {
    public static void MapFocusEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/focus");

        // POST /api/focus/bahtinov [{ starX, starY }]
        // Analyses the last frame ImageRelayService cached. The
        // client-side Manual Assist loop ensures a fresh capture
        // lands first; piggybacking on the cache avoids forcing a
        // second exposure per tick (which would halve fps).
        group.MapPost("/bahtinov", (BahtinovRequest? req, ImageRelayService relay) => {
            var img = relay.LatestImage;
            if (img == null) {
                return Results.Json(new {
                    ok = false,
                    error = "no recent frame; trigger a capture first via Start loop or Snap once"
                });
            }
            var pixels = img.PixelData.ToArray();
            var result = BahtinovAnalyzer.Analyze(
                pixels, img.Width, img.Height,
                starX: req?.StarX, starY: req?.StarY);
            return Results.Ok(result);
        });
    }

    public record BahtinovRequest(int? StarX, int? StarY);
}

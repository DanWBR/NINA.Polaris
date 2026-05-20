using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class ImageEndpoints {
    public static void MapImageEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/image");

        group.MapGet("/latest/preview", (ImageRelayService relay, int? quality) => {
            var jpeg = relay.GetLatestJpeg(quality ?? 85);
            if (jpeg == null)
                return Results.NotFound(new { error = "No image available" });

            return Results.File(jpeg, "image/jpeg");
        });

        group.MapGet("/latest/stats", (ImageRelayService relay) => {
            var image = relay.GetLatestImage();
            if (image == null)
                return Results.NotFound(new { error = "No image available" });

            return Results.Ok(new {
                width = image.Width,
                height = image.Height,
                bitDepth = image.BitDepth,
                bayerPattern = image.BayerPattern.ToString()
            });
        });
    }
}

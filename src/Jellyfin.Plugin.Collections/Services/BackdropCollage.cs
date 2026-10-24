using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Builds a 16:9 collage from member backdrops. Jellyfin's own collage builder only makes square and poster shapes.
/// </summary>
internal static class BackdropCollage
{
    /// <summary>
    /// The most backdrops used.
    /// </summary>
    internal const int MaxTiles = 4;

    private const int Width = 1920;
    private const int Height = 1080;
    private const int Gap = 6;

    /// <summary>
    /// Where each backdrop goes: 2 side by side, 3 as one large tile and two stacked, 4 as a 2x2 grid.
    /// </summary>
    /// <param name="count">The number of backdrops.</param>
    /// <returns>One rectangle per backdrop.</returns>
    internal static IReadOnlyList<SKRectI> Layout(int count)
    {
        const int Left = (Width - Gap) / 2;
        const int Right = Left + Gap;
        const int Top = (Height - Gap) / 2;
        const int Bottom = Top + Gap;
        return count switch
        {
            <= 1 => [new SKRectI(0, 0, Width, Height)],
            2 => [new SKRectI(0, 0, Left, Height), new SKRectI(Right, 0, Width, Height)],
            3 => [new SKRectI(0, 0, Left, Height), new SKRectI(Right, 0, Width, Top), new SKRectI(Right, Bottom, Width, Height)],
            _ => [new SKRectI(0, 0, Left, Top), new SKRectI(Right, 0, Width, Top), new SKRectI(0, Bottom, Left, Height), new SKRectI(Right, Bottom, Width, Height)]
        };
    }

    /// <summary>
    /// The centred part of an image that fills the tile without stretching.
    /// </summary>
    /// <param name="width">The image width.</param>
    /// <param name="height">The image height.</param>
    /// <param name="tile">The tile.</param>
    /// <returns>The source rectangle.</returns>
    internal static SKRect Crop(int width, int height, SKRectI tile)
    {
        var scale = Math.Max((float)tile.Width / width, (float)tile.Height / height);
        var cropWidth = tile.Width / scale;
        var cropHeight = tile.Height / scale;
        var x = (width - cropWidth) / 2;
        var y = (height - cropHeight) / 2;
        return new SKRect(x, y, x + cropWidth, y + cropHeight);
    }

    /// <summary>
    /// Writes the collage as a JPEG.
    /// </summary>
    /// <param name="paths">The backdrops (at most <see cref="MaxTiles"/> are used).</param>
    /// <param name="output">The output file.</param>
    internal static void Build(IReadOnlyList<string> paths, string output)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var count = Math.Min(paths.Count, MaxTiles);
        var tiles = Layout(count);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height)) ?? throw new InvalidOperationException("Could not create the image surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        for (var i = 0; i < count; i++)
        {
            using var image = SKImage.FromEncodedData(paths[i]) ?? throw new InvalidDataException("Could not read " + Path.GetFileName(paths[i]) + ".");
            canvas.DrawImage(image, Crop(image.Width, image.Height, tiles[i]), tiles[i], sampling, paint);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var stream = File.Create(output);
        data.SaveTo(stream);
    }
}

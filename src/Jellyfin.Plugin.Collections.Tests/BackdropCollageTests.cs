using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Collections.Services;
using SkiaSharp;

namespace Jellyfin.Plugin.Collections.Tests;

public class BackdropCollageTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Layout_TilesStayInsideTheFrame_WithoutOverlapping(int count)
    {
        var tiles = BackdropCollage.Layout(count);

        Assert.Equal(count, tiles.Count);
        Assert.All(tiles, t => Assert.True(t.Left >= 0 && t.Top >= 0 && t.Right <= 1920 && t.Bottom <= 1080 && t.Width > 0 && t.Height > 0));
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.False(tiles[i].IntersectsWith(tiles[j]), $"tiles {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Layout_FourTiles_AreSixteenByNine()
    {
        Assert.All(BackdropCollage.Layout(4), t => Assert.InRange((double)t.Width / t.Height, 1.75, 1.8));
    }

    [Fact]
    public void Crop_KeepsTheAspectRatio_AndCentres()
    {
        var tile = new SKRectI(0, 0, 957, 1080);

        var crop = BackdropCollage.Crop(1920, 1080, tile);

        Assert.Equal((double)tile.Width / tile.Height, crop.Width / crop.Height, 3);
        Assert.Equal(1080, crop.Height, 3);
        Assert.Equal(1920 - crop.Right, crop.Left, 3);
    }

    [Fact]
    public void Build_WritesA1920x1080Jpeg_WithEachBackdropInItsTile()
    {
        var dir = Directory.CreateTempSubdirectory("pc-backdrop-");
        try
        {
            var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
            var paths = colors.Select((c, i) => WriteImage(Path.Combine(dir.FullName, i + ".png"), c)).ToList();
            var output = Path.Combine(dir.FullName, "out.jpg");

            BackdropCollage.Build(paths, output);

            using var result = SKBitmap.Decode(output);
            Assert.Equal(1920, result.Width);
            Assert.Equal(1080, result.Height);
            var tiles = BackdropCollage.Layout(4);
            for (var i = 0; i < 4; i++)
            {
                var pixel = result.GetPixel(tiles[i].MidX, tiles[i].MidY);
                Assert.True(Math.Abs(pixel.Red - colors[i].Red) < 40 && Math.Abs(pixel.Green - colors[i].Green) < 40 && Math.Abs(pixel.Blue - colors[i].Blue) < 40, $"tile {i} is {pixel}");
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static string WriteImage(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(1280, 720);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}

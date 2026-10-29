using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PoVoiceHud.Core.Screen;

/// <summary>One capture of a monitor: 32-bit BGRA pixels, top row first, already scaled down for the model.</summary>
public sealed class ScreenShot(int width, int height, byte[] bgra)
{
    // The thumbnail compared between captures: coarse enough that a video tile or a blinking caret moves few of its cells.
    private const int ThumbWidth = 64, ThumbHeight = 36;
    private const int CellChange = 16;              // grey levels a cell must move to count as changed
    private const double ChangedShare = 0.03;       // the share of cells that must change: the four video tiles of a call stay under it

    public int Width => width;
    public int Height => height;
    public ReadOnlyMemory<byte> Bgra => bgra;

    /// <summary>The picture in 64×36 grey cells, each the average brightness of the pixels it covers.</summary>
    public byte[] Thumbnail()
    {
        var sums = new long[ThumbWidth * ThumbHeight];
        var counts = new int[sums.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * ThumbHeight / height * ThumbWidth;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var cell = row + x * ThumbWidth / width;
                sums[cell] += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
                counts[cell]++;
            }
        }
        return [.. sums.Select((sum, cell) => (byte)(counts[cell] == 0 ? 0 : sum / counts[cell]))];
    }

    /// <summary>Two thumbnails show a different screen: more than a small share of their cells changed noticeably.</summary>
    public static bool Differs(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return true;
        var changed = a.Where((value, i) => Math.Abs(value - b[i]) > CellChange).Count();
        return changed > a.Length * ChangedShare;
    }

    /// <summary>The picture as a JPEG, for the model.</summary>
    public async Task<byte[]> JpegAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var jpeg = new byte[stream.Size];
        await stream.GetInputStreamAt(0).ReadAsync(jpeg.AsBuffer(), (uint)jpeg.Length, InputStreamOptions.None);
        return jpeg;
    }
}

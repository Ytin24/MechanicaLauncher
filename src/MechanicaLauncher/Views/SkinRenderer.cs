using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MechanicaLauncher.Views;

// Composes a flat "front view" body render from a 64×64 Minecraft skin PNG. No CDN round-trip, so
// preview updates the instant the upload completes.
//
// Skin UV layout (Minecraft 1.8+ 64×64, classic 4-px-wide arms):
//   Head front        : 8,  8 → 16, 16   (8×8)
//   Head overlay front: 40, 8 → 48, 16
//   Body front        : 20, 20 → 28, 32  (8×12)
//   Body overlay front: 20, 36 → 28, 48
//   Right leg front   : 4,  20 → 8,  32  (4×12)
//   Right leg overlay : 4,  36 → 8,  48
//   Left leg front    : 20, 52 → 24, 64
//   Left leg overlay  : 4,  52 → 8,  64
//   Right arm front   : 44, 20 → 48, 32  (4×12)
//   Right arm overlay : 44, 36 → 48, 48
//   Left arm front    : 36, 52 → 40, 64
//   Left arm overlay  : 52, 52 → 56, 64
public static class SkinRenderer
{
    private const int BodyW = 16; // 4 arm + 8 body + 4 arm
    private const int BodyH = 32; // 8 head + 12 body + 12 legs

    public static async Task<byte[]> RenderFrontBodyAsync(byte[] skinPng, int scale = 4)
    {
        var src = await DecodeBgraAsync(skinPng);
        var dst = new byte[BodyW * scale * BodyH * scale * 4];

        // Head 8×8 placed at x=4..12, y=0..8
        CopyBlock(src, 8, 8, 8, 8, dst, 4, 0, scale);
        CopyBlock(src, 40, 8, 8, 8, dst, 4, 0, scale, overlay: true);

        // Right arm (viewer's left) 4×12 at x=0..4, y=8..20
        CopyBlock(src, 44, 20, 4, 12, dst, 0, 8, scale);
        CopyBlock(src, 44, 36, 4, 12, dst, 0, 8, scale, overlay: true);

        // Body 8×12 at x=4..12, y=8..20
        CopyBlock(src, 20, 20, 8, 12, dst, 4, 8, scale);
        CopyBlock(src, 20, 36, 8, 12, dst, 4, 8, scale, overlay: true);

        // Left arm 4×12 at x=12..16, y=8..20
        CopyBlock(src, 36, 52, 4, 12, dst, 12, 8, scale);
        CopyBlock(src, 52, 52, 4, 12, dst, 12, 8, scale, overlay: true);

        // Right leg (viewer's left) 4×12 at x=4..8, y=20..32
        CopyBlock(src, 4, 20, 4, 12, dst, 4, 20, scale);
        CopyBlock(src, 4, 36, 4, 12, dst, 4, 20, scale, overlay: true);

        // Left leg 4×12 at x=8..12, y=20..32
        CopyBlock(src, 20, 52, 4, 12, dst, 8, 20, scale);
        CopyBlock(src, 4, 52, 4, 12, dst, 8, 20, scale, overlay: true);

        return await EncodePngAsync(dst, BodyW * scale, BodyH * scale);
    }

    public static async Task<byte[]> RenderHeadAsync(byte[] skinPng, int scale = 8)
    {
        var src = await DecodeBgraAsync(skinPng);
        var dst = new byte[8 * scale * 8 * scale * 4];
        CopyBlock(src, 8, 8, 8, 8, dst, 0, 0, scale);
        CopyBlock(src, 40, 8, 8, 8, dst, 0, 0, scale, overlay: true);
        return await EncodePngAsync(dst, 8 * scale, 8 * scale);
    }

    // --- helpers -----------------------------------------------------------

    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeBgraAsync(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    private static void CopyBlock((byte[] Pixels, int Width, int Height) src,
                                  int sx, int sy, int sw, int sh,
                                  byte[] dst, int dx, int dy, int scale,
                                  bool overlay = false)
    {
        int dstW = BodyW * scale;
        for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++)
        {
            int si = ((sy + y) * src.Width + (sx + x)) * 4;
            if (si + 3 >= src.Pixels.Length) continue;
            byte b = src.Pixels[si], g = src.Pixels[si + 1], r = src.Pixels[si + 2], a = src.Pixels[si + 3];
            if (overlay && a == 0) continue;
            for (int oy = 0; oy < scale; oy++)
            for (int ox = 0; ox < scale; ox++)
            {
                int di = (((dy + y) * scale + oy) * dstW + (dx + x) * scale + ox) * 4;
                if (di + 3 >= dst.Length) continue;
                if (overlay)
                {
                    // Simple alpha-over: dst = src over dst, where src has premultiplied-ish blend.
                    byte da = dst[di + 3];
                    float sa = a / 255f;
                    dst[di]     = (byte)(b * sa + dst[di]     * (1 - sa));
                    dst[di + 1] = (byte)(g * sa + dst[di + 1] * (1 - sa));
                    dst[di + 2] = (byte)(r * sa + dst[di + 2] * (1 - sa));
                    dst[di + 3] = (byte)Math.Min(255, da + a);
                }
                else
                {
                    dst[di] = b; dst[di + 1] = g; dst[di + 2] = r; dst[di + 3] = a;
                }
            }
        }
    }

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        stream.Seek(0);
        var buf = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(buf);
        return buf;
    }
}

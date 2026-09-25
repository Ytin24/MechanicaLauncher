using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nitidus.Native;

namespace MechanicaLauncher.Desktop;

internal static class MediaCache
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim gate = new(4, 4);
    private static readonly ConcurrentDictionary<string, ImageSource> cache = new();
    private static readonly ConcurrentQueue<string> order = new();
    public static ImageSource LoadLocal(string? path)
    {
        if (path == null || !File.Exists(path)) return ImageSource.Empty;
        try
        {
            var file = new FileInfo(path);
            if (file.Length > 20 * 1024 * 1024) return ImageSource.Empty;
            string key = path + "|" + file.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(key, out var image)) return image;
            image = LoadBytes(File.ReadAllBytes(path)); Add(key, image); return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException) { return ImageSource.Empty; }
    }
    public static ImageSource LoadBytes(byte[] bytes)
    {
        try { return ResizeImage(bytes); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            using var stream = new MemoryStream(bytes);
            var native = ImageLoader.Load(stream);
            var frame = native.FrameAt(0, out _);
            if (frame != null && frame.Source.Width * frame.Source.Height * native.FrameCount > 40_000_000)
                throw new InvalidDataException("Image is too large.");
            return native;
        }
    }
    private static ImageSource ResizeImage(byte[] bytes)
    {
        using var original = new MemoryStream(bytes);
        using var bitmap = System.Drawing.Image.FromStream(original);
        if ((long)bitmap.Width * bitmap.Height > 40_000_000) throw new InvalidDataException("Image is too large.");
        float scale = Math.Min(1f, 1200f / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = new System.Drawing.Bitmap(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
        using (var graphics = System.Drawing.Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, 0, 0, resized.Width, resized.Height);
        }
        using var png = new MemoryStream(); resized.Save(png, System.Drawing.Imaging.ImageFormat.Png); png.Position = 0;
        return ImageLoader.Load(png);
    }
    public static async Task<ImageSource> LoadRemote(string? address, CancellationToken token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) return ImageSource.Empty;
        if (cache.TryGetValue(uri.AbsoluteUri, out var image)) return image;
        await gate.WaitAsync(token);
        try
        {
            if (cache.TryGetValue(uri.AbsoluteUri, out image)) return image;
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 12 * 1024 * 1024) return ImageSource.Empty;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[65536]; int count;
            while ((count = await input.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + count > 12 * 1024 * 1024) return ImageSource.Empty;
                buffer.Write(chunk, 0, count);
            }
            image = await Task.Run(() => LoadBytes(buffer.ToArray()), token);
            Add(uri.AbsoluteUri, image); return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException || ex is OperationCanceledException && !token.IsCancellationRequested) { return ImageSource.Empty; }
        finally { gate.Release(); }
    }
    private static void Add(string key, ImageSource image)
    {
        if (!cache.TryAdd(key, image)) return;
        order.Enqueue(key);
        while (cache.Count > 100 && order.TryDequeue(out var oldest)) cache.TryRemove(oldest, out _);
    }
}

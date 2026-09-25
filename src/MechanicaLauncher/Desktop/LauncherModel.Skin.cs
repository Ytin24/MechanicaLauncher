using System.Globalization;
using Nitidus.Motion;
using Drawing = System.Drawing;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    private Drawing.Bitmap? skinBitmap;
    private readonly List<Drawing.Bitmap> skinUndo = [];
    private int skinRevision;
    private bool painting;
    public string SkinColor { get; set; } = "#8EBD9D";
    private string skinTool = "pencil";
    public bool SkinEraser { get => skinTool == "eraser"; set { skinTool = value ? "eraser" : "pencil"; Changed(); } }
    public bool SkinFill { get => skinTool == "fill"; set { skinTool = value ? "fill" : "pencil"; Changed(); } }
    public bool SkinPicker { get => skinTool == "picker"; set { skinTool = value ? "picker" : "pencil"; Changed(); } }
    public bool CanUndoSkin => skinUndo.Count > 0;
    public void OpenSkinEditor()
    {
        bool load = skinBitmap == null && MicrosoftAccount;
        skinBitmap ??= new(64, 64);
        Navigate("skin");
        if (load) LoadCurrentSkin();
    }
    public void LoadCurrentSkin() => Run(async () =>
    {
        if (skinUndo.Count > 0 && !await Confirm(T("Заменить рисунок текущим скином?", "Replace drawing with your current skin?"), T("Можно отменить замену кнопкой «Отменить». ", "You can undo this replacement."), T("Загрузить", "Load"))) return;
        int revision = skinRevision; var token = PageToken;
        string id = Settings.Uuid is not ("" or "0") ? Settings.Uuid : Settings.Username;
        var bytes = await accountHttp.GetByteArrayAsync("https://minotar.net/skin/" + Uri.EscapeDataString(id), token);
        token.ThrowIfCancellationRequested();
        if (skinRevision != revision) return;
        LoadSkinBytes(bytes);
    });
    internal void LoadSkinBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Drawing.Image.FromStream(stream);
        if (image.Width != 64 || image.Height is not (32 or 64)) throw new InvalidDataException(T("Размер скина: 64×64 или 64×32 PNG.", "Skin must be a 64×64 or 64×32 PNG."));
        var next = new Drawing.Bitmap(64, 64);
        using (var graphics = Drawing.Graphics.FromImage(next)) graphics.DrawImageUnscaled(image, 0, 0);
        RememberSkin(); skinBitmap?.Dispose(); skinBitmap = next; ++skinRevision; Changed();
    }
    private void RememberSkin()
    {
        if (skinBitmap == null) return;
        if (skinUndo.Count == 20) { skinUndo[0].Dispose(); skinUndo.RemoveAt(0); }
        skinUndo.Add(new(skinBitmap));
    }
    public void UndoSkin()
    {
        if (skinUndo.Count == 0) return;
        skinBitmap?.Dispose(); skinBitmap = skinUndo[^1]; skinUndo.RemoveAt(skinUndo.Count - 1);
        ++skinRevision; Changed();
    }
    public void LoadSkinFile() => Run(() =>
    {
        var path = Platform.PickFile("Minecraft skin|*.png");
        if (path == null) return Task.CompletedTask;
        LoadSkinBytes(File.ReadAllBytes(path)); return Task.CompletedTask;
    });
    public void SaveSkinFile() => Run(() =>
    {
        var path = Platform.SaveFile("Minecraft skin|*.png", "skin.png");
        if (path != null) skinBitmap?.Save(path, Drawing.Imaging.ImageFormat.Png);
        return Task.CompletedTask;
    });
    public void UploadEditedSkin() => Run(() => WithBusy(async () =>
    {
        if (skinBitmap == null) return;
        await UploadSkinBytes(EncodeSkin());
    }));
    internal byte[] EncodeSkin()
    {
        using var stream = new MemoryStream(); skinBitmap?.Save(stream, Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }
    internal void DrawSkin(Element element, ICanvas canvas, Rect clip)
    {
        if (skinBitmap == null) return;
        var bounds = element.Bounds; float cell = Math.Min(bounds.Width, bounds.Height) / 64;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                var pixel = skinBitmap.GetPixel(x, y);
                var rect = new Rect(bounds.X + x * cell, bounds.Y + y * cell, cell, cell);
                canvas.Rectangle(rect, (x / 4 + y / 4) % 2 == 0 ? Palette.Hover : Palette.Surface, clip: clip);
                if (pixel.A > 0) canvas.Rectangle(rect, Color.Hex((uint)(pixel.R << 16 | pixel.G << 8 | pixel.B)) with { A = pixel.A / 255f }, clip: clip);
            }
    }
    internal void SkinInput(Element canvas, MotionInput input)
    {
        if (Page != "skin" || DialogOpen || skinBitmap == null) { painting = false; return; }
        if (input.Kind is MotionInputKind.Up or MotionInputKind.Cancel) { painting = false; return; }
        if (input.Kind == MotionInputKind.Down) painting = canvas.Bounds.Contains(input.X, input.Y);
        if (!painting || !canvas.Bounds.Contains(input.X, input.Y)) return;
        if (!SkinEraser && !SkinPicker && (!InstanceMedia.IsAccent(SkinColor) || !uint.TryParse(SkinColor[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))) return;
        float cell = Math.Min(canvas.Bounds.Width, canvas.Bounds.Height) / 64;
        int x = Math.Clamp((int)((input.X - canvas.Bounds.X) / cell), 0, 63), y = Math.Clamp((int)((input.Y - canvas.Bounds.Y) / cell), 0, 63);
        if (SkinPicker)
        {
            var pixel = skinBitmap.GetPixel(x, y); SkinColor = $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
            SkinPicker = false; painting = false; Changed(); return;
        }
        if (input.Kind == MotionInputKind.Down) RememberSkin();
        var color = SkinEraser ? Drawing.Color.Transparent : Drawing.ColorTranslator.FromHtml(SkinColor);
        if (SkinFill && !SkinEraser)
        {
            FillSkin(x, y, color); painting = false;
        }
        else skinBitmap.SetPixel(x, y, color);
        ++skinRevision; Changed();
    }
    private void FillSkin(int x, int y, Drawing.Color color)
    {
        int target = skinBitmap!.GetPixel(x, y).ToArgb();
        if (target == color.ToArgb()) return;
        var stack = new Stack<(int X, int Y)>(); stack.Push((x, y));
        while (stack.TryPop(out var point))
        {
            if (point.X is < 0 or >= 64 || point.Y is < 0 or >= 64 || skinBitmap.GetPixel(point.X, point.Y).ToArgb() != target) continue;
            skinBitmap.SetPixel(point.X, point.Y, color);
            stack.Push((point.X - 1, point.Y)); stack.Push((point.X + 1, point.Y));
            stack.Push((point.X, point.Y - 1)); stack.Push((point.X, point.Y + 1));
        }
    }
}

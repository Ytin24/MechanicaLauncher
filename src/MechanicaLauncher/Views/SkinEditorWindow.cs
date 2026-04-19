using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace MechanicaLauncher.Views;

// In-launcher pixel editor for 64×64 Minecraft skins. Built on a Canvas of rectangles so we get
// pixel-perfect rendering at 8× zoom without WinUI's bilinear scaling blurring the pixel art.
public sealed class SkinEditorWindow
{
    private static readonly HttpClient Http = new();

    private const int SkinSize = 64;
    private const int Pixel = 8; // display size of 1 skin pixel in the editor

    private readonly XamlRoot _xamlRoot;
    private readonly Core.Profiles.LauncherSettings _settings;
    private readonly Func<byte[], Task> _uploadAsync;

    private readonly Color[,] _pixels = new Color[SkinSize, SkinSize];
    private readonly Rectangle[,] _cells = new Rectangle[SkinSize, SkinSize];
    private Color _color = Microsoft.UI.Colors.Red;
    private string _tool = "pencil"; // pencil | eraser | fill | eyedropper
    private bool _pointerDown;

    public SkinEditorWindow(XamlRoot xamlRoot, Core.Profiles.LauncherSettings settings, Func<byte[], Task> uploadAsync)
    {
        _xamlRoot = xamlRoot;
        _settings = settings;
        _uploadAsync = uploadAsync;
    }

    public async Task ShowAsync()
    {
        var canvas = BuildCanvas();
        var colorPicker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsAlphaEnabled = true,
            IsHexInputVisible = true,
            IsAlphaSliderVisible = true,
            IsAlphaTextInputVisible = false,
            IsMoreButtonVisible = false,
            Width = 280,
        };
        colorPicker.Color = _color;
        colorPicker.ColorChanged += (_, args) => _color = args.NewColor;

        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddTool(string tag, string glyph, string tip)
        {
            var b = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 14 },
                MinWidth = 42, MinHeight = 36, Tag = tag,
                CornerRadius = new CornerRadius(6),
            };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) =>
            {
                _tool = tag;
                foreach (var child in toolRow.Children.OfType<Button>())
                    child.Background = new SolidColorBrush((string)child.Tag! == _tool
                        ? Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)
                        : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            };
            toolRow.Children.Add(b);
        }
        AddTool("pencil", "\uE929", "Pencil");
        AddTool("eraser", "\uE75C", "Eraser");
        AddTool("fill", "\uE771", "Bucket fill");
        AddTool("eyedropper", "\uE7A8", "Eyedropper");
        // trigger initial highlight
        ((Button)toolRow.Children[0]).Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));

        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };

        var side = new StackPanel { Spacing = 10, MinWidth = 280 };
        side.Children.Add(new TextBlock { Text = "Tools", FontSize = 12, Opacity = 0.7 });
        side.Children.Add(toolRow);
        side.Children.Add(new TextBlock { Text = "Color", FontSize = 12, Opacity = 0.7 });
        side.Children.Add(colorPicker);
        side.Children.Add(status);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = canvas,
        });
        content.Children.Add(side);

        var dialog = new ContentDialog
        {
            Title = "Skin editor",
            Content = content,
            PrimaryButtonText = "Save and upload",
            SecondaryButtonText = "Reset to current",
            CloseButtonText = "Cancel",
            XamlRoot = _xamlRoot,
        };
        // Default ContentDialogMaxWidth is ~640 — ColorPicker gets clipped. Bump it.
        dialog.Resources["ContentDialogMaxWidth"] = 1200.0;
        dialog.Resources["ContentDialogMaxHeight"] = 900.0;

        status.Text = "Loading current skin...";
        await LoadCurrentSkinAsync(status);

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            status.Text = "Reloading current skin...";
            await LoadCurrentSkinAsync(status);
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            status.Text = "Encoding PNG...";
            try
            {
                var png = await EncodePngAsync();
                status.Text = "Uploading...";
                await _uploadAsync(png);
                status.Text = "Uploaded.";
                await Task.Delay(800);
                dialog.Hide();
            }
            catch (Exception ex) { status.Text = $"Failed: {ex.Message}"; }
        };

        await dialog.ShowAsync();
    }

    private Canvas BuildCanvas()
    {
        var canvas = new Canvas
        {
            Width = SkinSize * Pixel,
            Height = SkinSize * Pixel,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
        };

        for (int y = 0; y < SkinSize; y++)
        for (int x = 0; x < SkinSize; x++)
        {
            var rect = new Rectangle
            {
                Width = Pixel, Height = Pixel,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Canvas.SetLeft(rect, x * Pixel);
            Canvas.SetTop(rect, y * Pixel);
            canvas.Children.Add(rect);
            _cells[x, y] = rect;
        }

        canvas.PointerPressed += (_, e) => { _pointerDown = true; PaintFromPointer(canvas, e); };
        canvas.PointerMoved += (_, e) => { if (_pointerDown) PaintFromPointer(canvas, e); };
        canvas.PointerReleased += (_, _) => _pointerDown = false;
        canvas.PointerExited += (_, _) => _pointerDown = false;
        canvas.PointerCaptureLost += (_, _) => _pointerDown = false;
        return canvas;
    }

    private void PaintFromPointer(Canvas canvas, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(canvas).Position;
        var x = (int)(pos.X / Pixel);
        var y = (int)(pos.Y / Pixel);
        if (x < 0 || y < 0 || x >= SkinSize || y >= SkinSize) return;

        switch (_tool)
        {
            case "pencil": SetPixel(x, y, _color); break;
            case "eraser": SetPixel(x, y, Microsoft.UI.Colors.Transparent); break;
            case "fill":   FloodFill(x, y, _color); break;
            case "eyedropper": _color = _pixels[x, y]; break;
        }
    }

    private void SetPixel(int x, int y, Color c)
    {
        _pixels[x, y] = c;
        _cells[x, y].Fill = new SolidColorBrush(c);
    }

    private void FloodFill(int x, int y, Color newColor)
    {
        var target = _pixels[x, y];
        if (target.R == newColor.R && target.G == newColor.G && target.B == newColor.B && target.A == newColor.A) return;
        var stack = new Stack<(int, int)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (px, py) = stack.Pop();
            if (px < 0 || py < 0 || px >= SkinSize || py >= SkinSize) continue;
            var cur = _pixels[px, py];
            if (cur.R != target.R || cur.G != target.G || cur.B != target.B || cur.A != target.A) continue;
            SetPixel(px, py, newColor);
            stack.Push((px + 1, py)); stack.Push((px - 1, py));
            stack.Push((px, py + 1)); stack.Push((px, py - 1));
        }
    }

    private async Task LoadCurrentSkinAsync(TextBlock status)
    {
        try
        {
            var id = !string.IsNullOrEmpty(_settings.Uuid) && _settings.Uuid != "0"
                ? _settings.Uuid
                : !string.IsNullOrEmpty(_settings.Username) && _settings.Username != "Player"
                    ? _settings.Username
                    : "Steve";
            var bytes = await Http.GetByteArrayAsync($"https://minotar.net/skin/{id}");
            var decoder = await BitmapDecoder.CreateAsync(new MemoryStream(bytes).AsRandomAccessStream());
            var frame = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var raw = frame.DetachPixelData();
            int w = (int)decoder.PixelWidth;
            // Copy into our model (first 64×64; Minecraft skins may ship at 64×32 legacy — pad empty)
            int h = Math.Min(SkinSize, (int)decoder.PixelHeight);
            for (int y = 0; y < SkinSize; y++)
            for (int x = 0; x < SkinSize; x++)
            {
                if (x < w && y < h)
                {
                    int i = (y * w + x) * 4;
                    var c = Color.FromArgb(raw[i + 3], raw[i + 2], raw[i + 1], raw[i]);
                    SetPixel(x, y, c);
                }
                else SetPixel(x, y, Microsoft.UI.Colors.Transparent);
            }
            status.Text = "Loaded.";
        }
        catch (Exception ex) { status.Text = $"Load failed: {ex.Message}"; }
    }

    private async Task<byte[]> EncodePngAsync()
    {
        var buf = new byte[SkinSize * SkinSize * 4];
        for (int y = 0; y < SkinSize; y++)
        for (int x = 0; x < SkinSize; x++)
        {
            var c = _pixels[x, y];
            int i = (y * SkinSize + x) * 4;
            buf[i] = c.B; buf[i + 1] = c.G; buf[i + 2] = c.R; buf[i + 3] = c.A;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, SkinSize, SkinSize, 96, 96, buf);
        await encoder.FlushAsync();
        stream.Seek(0);
        var result = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(result);
        return result;
    }
}

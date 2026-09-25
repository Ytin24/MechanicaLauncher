using System.Numerics;

namespace MechanicaLauncher.Desktop;

internal sealed class LauncherFrame(ITextService textService) : ICanvas
{
    private readonly List<Action<ICanvas>> commands = [];
    public ITextService TextService { get; } = textService;
    public void Draw(ICanvas canvas)
    {
        foreach (var command in commands) command(canvas);
    }
    public void PushTransform(Matrix3x2 transform, float opacity = 1, Rect? clip = null)
        => commands.Add(canvas => canvas.PushTransform(transform, opacity, clip));
    public void PopTransform() => commands.Add(canvas => canvas.PopTransform());
    public void Rectangle(Rect bounds, Color color, float radius = 0, Rect? clip = null)
        => commands.Add(canvas => canvas.Rectangle(bounds, color, radius, clip));
    public void Gradient(Rect bounds, Color top, Color bottom, float radius = 0, Rect? clip = null)
        => commands.Add(canvas => canvas.Gradient(bounds, top, bottom, radius, clip));
    public void Shapes(ReadOnlySpan<Shape> shapes)
    {
        var copy = shapes.ToArray();
        commands.Add(canvas => canvas.Shapes(copy));
    }
    public void Text(string text, float x, float y, float size, Color color, Rect? clip = null,
        string fontFamily = "Segoe UI", Alignment alignment = Alignment.Left, float maxWidth = 4096, bool wrap = false)
        => Text(text, x, y, new TextFormat(size, fontFamily, maxWidth, alignment, wrap), color, clip);
    public void Text(string text, float x, float y, TextFormat format, Color color, Rect? clip = null)
        => commands.Add(canvas => canvas.Text(text, x, y, format, color, clip));
    public void Image(ImageSource source, Rect bounds, Rect? clip = null)
        => commands.Add(canvas => canvas.Image(source, bounds, clip));
}

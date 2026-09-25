using System.Numerics;

namespace MechanicaLauncher.Desktop;

internal static class LauncherArtwork
{
    internal static void Block(Element node, ICanvas canvas, Rect clip)
    {
        var b = node.Bounds;
        float x = b.X + b.Width / 2, y = b.Y + 15;
        void Face(Matrix3x2 transform, uint color)
        {
            canvas.PushTransform(transform);
            try { canvas.Rectangle(new(0, 0, 1, 1), Color.Hex(color)); }
            finally { canvas.PopTransform(); }
        }
        Face(new(23, 12, 0, 23, x - 23, y + 12), 0x55784b);
        Face(new(23, -12, 0, 23, x, y + 24), 0x335a42);
        Face(new(23, 12, -23, 12, x, y), 0xa5cb7f);
        Face(new(8, 4, -8, 4, x + 2, y + 5), 0xbedb92);
    }
}

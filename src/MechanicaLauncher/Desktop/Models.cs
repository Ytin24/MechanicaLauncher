using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MechanicaLauncher.Desktop;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed(string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }
}

public sealed class ItemModel : ObservableModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    // Setters notify only when the value really changes, so in-place updates (download progress, speed)
    // refresh just the bound property instead of re-evaluating the whole card.
    private string title = "", meta = "", description = "", primary = "", secondary = "", tertiary = "";
    private bool enabled = true, selected, compact, hasProgress;
    private double progress;
    private ImageSource image = ImageSource.Empty;
    public string Title { get => title; set => Set(ref title, value); }
    public string Meta { get => meta; set => Set(ref meta, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string Primary { get => primary; set => Set(ref primary, value); }
    public string Secondary { get => secondary; set => Set(ref secondary, value); }
    public string Tertiary { get => tertiary; set => Set(ref tertiary, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public bool Compact { get => compact; set => Set(ref compact, value); }
    public bool HasProgress { get => hasProgress; set => Set(ref hasProgress, value); }
    public double Progress { get => progress; set => Set(ref progress, value); }
    public ImageSource Image { get => image; set { if (Set(ref image, value)) Changed(nameof(HasImage)); } }
    public bool HasImage => !ReferenceEquals(Image, ImageSource.Empty);
    public Action? Action { get; set; }
    public Action? Action2 { get; set; }
    public Action? Action3 { get; set; }
    public void Invoke() { if (Enabled) Action?.Invoke(); }
    public void Invoke2() { if (Enabled) Action2?.Invoke(); }
    public void Invoke3() { if (Enabled) Action3?.Invoke(); }
}

public sealed class FieldModel(string id, string label, string value = "") : ObservableModel
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    private string value = value;
    public string Value { get => value; set => Set(ref this.value, value); }
    public bool IsToggle { get; init; }
    private bool isChecked;
    public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }
}

public sealed record LauncherPalette(Color Background, Color Surface, Color Hover, Color Border,
    Color Foreground, Color Muted, Color Accent, Color OnAccent, Color Pane, Color Hero, Color Error)
{
    public static LauncherPalette Dark { get; } = new(
        Color.Hex(0x141a1b), Color.Hex(0x1e2728), Color.Hex(0x2c393a), Color.Hex(0x344244),
        Color.Hex(0xe9efed), Color.Hex(0xa6b7b4), Color.Hex(0x9bd5b1), Color.Hex(0x153321),
        Color.Hex(0x111718), Color.Hex(0x233c32), Color.Hex(0xffb5a9));
    public static LauncherPalette Light { get; } = new(
        Color.Hex(0xcdd3cf), Color.Hex(0xe0e5df), Color.Hex(0xc1cec4), Color.Hex(0x9aaa9f),
        Color.Hex(0x202d27), Color.Hex(0x495c50), Color.Hex(0x2d6847), Color.Hex(0xffffff),
        Color.Hex(0xbec8c0), Color.Hex(0xb4cdbb), Color.Hex(0x9c2c25));
    public NitidusTheme Theme => NitidusTheme.Dark with
    {
        Background = Background, Surface = Surface, Hover = Hover, Pressed = Pane,
        Foreground = Foreground, Muted = Muted, Border = Border, Accent = Accent,
        OnAccent = OnAccent, Error = Error, Radius = 9, ControlHeight = 38, FontSize = 14
    };
}

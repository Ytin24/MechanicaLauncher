using System.Globalization;
using System.Text;
using DiscordRPC;
using MechanicaLauncher.Core.Instances;

namespace MechanicaLauncher.Core.Discord;

internal sealed record PresenceActivity(string Details, string State, string ImageText, DateTimeOffset? StartedAt)
{
    public RichPresence ToPresence() => new()
    {
        Details = Details,
        State = State,
        Timestamps = StartedAt.HasValue ? new Timestamps { Start = StartedAt.Value.UtcDateTime } : null,
        Assets = new Assets { LargeImageKey = DiscordPresence.MinecraftImage, LargeImageText = ImageText },
        Buttons = [new Button { Label = "Mechanica Launcher", Url = "https://github.com/Ytin24/MechanicaLauncher" }]
    };

    public static PresenceActivity Create(string? name, string? version, LoaderType loader, int mods,
        DateTimeOffset? startedAt, McState state, PresenceOptions options)
    {
        var ru = options.Language == "ru";
        string L(string en, string russian) => ru ? russian : en;
        var loaderName = loader == LoaderType.None ? "Vanilla" : loader.ToString();
        var info = string.Join(" · ", new[] { Text(name ?? "Minecraft", 48), Text(version ?? "", 24), loaderName }.Where(s => s.Length > 0));
        if (mods > 0 && options.ShowMods && loader != LoaderType.None)
            info += " · " + L($"{mods} mods", $"модов: {mods}");
        var details = state.Type switch
        {
            McStateType.Launcher => L("In the launcher", "В лаунчере"),
            McStateType.Preparing => L("Preparing an instance", "Подготовка сборки"),
            McStateType.Starting => L("Starting Minecraft", "Запуск Minecraft"),
            McStateType.Running => L("Minecraft is running", "Minecraft запущен"),
            McStateType.Menu => L("Main menu", "Главное меню"),
            McStateType.Connecting => L("Connecting to a server", "Подключение к серверу"),
            McStateType.SinglePlayer => L("Singleplayer", "Одиночная игра"),
            _ => L("Multiplayer", "Сетевая игра")
        };
        if ((state.Type is McStateType.Connecting or McStateType.MultiPlayer) && options.ShowServer && !string.IsNullOrWhiteSpace(state.Server))
        {
            var address = state.Server.Trim('[', ']');
            if (address.Contains(':')) address = $"[{address}]";
            if (state.Port.HasValue && state.Port != 25565) address += ":" + state.Port.Value;
            details += " · " + address;
        }
        if (state.Type is McStateType.SinglePlayer or McStateType.MultiPlayer)
        {
            if (state.Type == McStateType.SinglePlayer && !string.IsNullOrWhiteSpace(state.World))
                details += " · " + Text(state.World, 40);
            if (options.ShowDimension && state.Dimension != null)
                details += " · " + (state.Dimension switch
                {
                    "overworld" => L("Overworld", "Верхний мир"),
                    "the_nether" => L("Nether", "Незер"),
                    "the_end" => L("The End", "Энд"),
                    _ => ""
                });
            if (options.ShowAchievements && state.Achievement != null)
                details = L("Advancement: ", "Достижение: ") + state.Achievement;
        }
        var imageText = string.IsNullOrWhiteSpace(name) ? "Mechanica Launcher" : L("Instance: ", "Сборка: ") + name;
        if (state.Gamemode != null && (state.Type is McStateType.SinglePlayer or McStateType.MultiPlayer))
            imageText += " · " + (state.Gamemode switch
            {
                "creative" => L("Creative", "Творческий"),
                "survival" => L("Survival", "Выживание"),
                "adventure" => L("Adventure", "Приключение"),
                "spectator" => L("Spectator", "Наблюдатель"),
                _ => ""
            });
        if (state.Type == McStateType.Launcher) info = L("Choosing the next adventure", "Выбирает сборку");
        return new(Text(details, 128), Text(info, 128), Text(imageText, 128), startedAt);
    }

    internal static string Text(string value, int maxBytes)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
            }
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
                builder.Append(rune);
        }
        var cleaned = builder.ToString().Trim();
        if (Encoding.UTF8.GetByteCount(cleaned) <= maxBytes) return cleaned;
        builder.Clear();
        var bytes = 0;
        foreach (var rune in cleaned.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes - 3) break;
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString().TrimEnd() + "…";
    }
}

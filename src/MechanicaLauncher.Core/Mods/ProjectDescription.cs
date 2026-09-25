using Markdig;
using System.Text;

namespace MechanicaLauncher.Core.Mods;

public static class ProjectDescription
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string GetNavigationUri(string html) =>
        "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

    public static string Render(string markdown, bool dark)
    {
        var background = dark ? "#252e28" : "#e0e6e0";
        var foreground = dark ? "#edf3ee" : "#1c2920";
        var subtle = dark ? "#b4c4b8" : "#465a4b";
        var accent = dark ? "#a1d4ab" : "#256237";
        var html = Markdown.ToHtml(markdown, Pipeline);
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root { color-scheme: {{(dark ? "dark" : "light")}}; }
            * { box-sizing: border-box; }
            html, body { margin: 0; background: {{background}}; color: {{foreground}}; }
            body { padding: 20px 24px; font: 14px/1.65 'Segoe UI', sans-serif; overflow-wrap: anywhere; }
            h1,h2,h3,h4 { line-height: 1.3; margin: 1.4em 0 .5em; }
            body > :first-child { margin-top: 0; }
            a { color: {{accent}}; text-underline-offset: 3px; }
            img, video { max-width: 100%; height: auto; border-radius: 8px; }
            img { object-fit: contain; }
            table { display: block; max-width: 100%; overflow-x: auto; border-collapse: collapse; }
            th,td { padding: 8px 12px; border: 1px solid {{subtle}}; }
            pre { overflow-x: auto; padding: 14px; border: 1px solid {{subtle}}; border-radius: 8px; }
            code { font: 12px/1.6 Consolas, monospace; }
            blockquote { margin: 16px 0; padding-left: 16px; border-left: 3px solid {{subtle}}; color: {{subtle}}; }
            hr { border: 0; border-top: 1px solid {{subtle}}; opacity: .4; margin: 24px 0; }
            [style], font { color: inherit !important; background-color: transparent !important; }
            a[style] { color: {{accent}} !important; }
            iframe, form, input, button, object, embed { display: none !important; }
            </style></head><body>{{html}}</body></html>
            """;
    }
}

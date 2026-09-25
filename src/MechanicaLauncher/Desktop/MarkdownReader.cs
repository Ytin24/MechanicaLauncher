using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MechanicaLauncher.Desktop;

internal static class MarkdownReader
{
    internal sealed record Block(ItemModel Item, string? ImageUrl = null);
    public static IEnumerable<Block> Read(string markdown, Action<string> open)
    {
        var document = Markdown.Parse(markdown);
        foreach (var block in document)
            foreach (var item in ReadBlock(block, open)) yield return item;
    }
    private static IEnumerable<Block> ReadBlock(Markdig.Syntax.Block block, Action<string> open)
    {
        if (block is LeafBlock leaf)
        {
            string text = leaf.Inline == null ? leaf.Lines.ToString() : Plain(leaf.Inline);
            if (block is HtmlBlock)
            {
                foreach (Match match in Regex.Matches(text, "<img\\b[^>]*\\bsrc\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase))
                    yield return new(new() { Meta = "Modrinth" }, WebUtility.HtmlDecode(match.Groups["url"].Value));
                text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(text, "<(script|style)\\b.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>", " "));
            }
            if (!string.IsNullOrWhiteSpace(text))
                yield return new(block is HeadingBlock ? new() { Title = text.Trim() } : new() { Description = text.Trim() });
            if (leaf.Inline != null)
                foreach (var link in Links(leaf.Inline))
                {
                    string url = link.Url ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                    string label = Plain(link);
                    if (link.IsImage) yield return new(new() { Meta = label }, url);
                    else yield return new(new() { Primary = label.Length == 0 ? uri.Host : label.Length > 30 ? label[..28] + "…" : label, Action = () => open(url) });
                }
        }
        if (block is ContainerBlock container)
            foreach (var child in container)
                foreach (var item in ReadBlock(child, open)) yield return item;
    }
    private static string Plain(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var node in container)
            switch (node)
            {
                case LiteralInline literal: builder.Append(literal.Content); break;
                case CodeInline code: builder.Append(code.Content); break;
                case LineBreakInline: builder.AppendLine(); break;
                case ContainerInline nested when nested is not LinkInline { IsImage: true }: builder.Append(Plain(nested)); break;
            }
        return builder.ToString();
    }
    private static IEnumerable<LinkInline> Links(ContainerInline container)
    {
        foreach (var node in container)
        {
            if (node is LinkInline link) yield return link;
            if (node is ContainerInline nested) foreach (var child in Links(nested)) yield return child;
        }
    }
}

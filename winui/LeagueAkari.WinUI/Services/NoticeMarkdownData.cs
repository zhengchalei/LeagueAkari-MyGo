using System.Net;
using System.Text.RegularExpressions;

namespace LeagueAkari.WinUI.Services;

public sealed record NoticeDocumentBlock(string Kind, string Text, int Level = 0, string[][]? Cells = null);

public static class NoticeMarkdownData
{
    public static IReadOnlyList<NoticeDocumentBlock> Parse(string source)
    {
        var blocks = new List<NoticeDocumentBlock>(); var lines = NormalizeHtml(source).Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd(); if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                string fence = line.TrimStart()[..3]; var code = new List<string>(); while (++i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal)) code.Add(lines[i]);
                blocks.Add(new("code", string.Join('\n', code))); continue;
            }
            if (i + 1 < lines.Length && line.Contains('|') && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$"))
            {
                var rows = new List<string[]> { Cells(line) }; i++;
                while (i + 1 < lines.Length && lines[i + 1].Contains('|') && !string.IsNullOrWhiteSpace(lines[i + 1])) rows.Add(Cells(lines[++i]));
                blocks.Add(new("table", "", Cells: rows.ToArray())); continue;
            }
            var heading = Regex.Match(line, @"^\s*(#{1,6})\s+(.+?)(?:\s+#+)?$");
            if (heading.Success) { blocks.Add(new("heading", heading.Groups[2].Value, heading.Groups[1].Length)); continue; }
            if (Regex.IsMatch(line, @"^\s*(?:---+|\*\*\*+|___+)\s*$")) { blocks.Add(new("rule", "")); continue; }
            var quote = Regex.Match(line, @"^\s*>\s?(.*)$"); if (quote.Success) { blocks.Add(new("quote", quote.Groups[1].Value)); continue; }
            var list = Regex.Match(line, @"^\s*(?:[-+*]|(\d+)\.)\s+(.+)$");
            if (list.Success) { blocks.Add(new("list", (list.Groups[1].Success ? list.Groups[1].Value + ". " : "• ") + list.Groups[2].Value)); continue; }
            if (i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*(=+|-+)\s*$")) { blocks.Add(new("heading", line, lines[++i].TrimStart()[0] == '=' ? 1 : 2)); continue; }
            blocks.Add(new("paragraph", line));
        }
        return blocks;
    }
    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
    public static string NormalizeHtml(string source)
    {
        var code = new List<string>();
        source = Regex.Replace(source, @"(```|~~~)[\s\S]*?\1|`[^`\n]+`", match => { code.Add(match.Value); return "\uE000NOTICECODE" + (code.Count - 1) + "\uE001"; });
        // Keep text and semantic HTML from markdown-it(html:true), without executing remote markup.
        source = Regex.Replace(source, @"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<h([1-6])\b[^>]*>([\s\S]*?)</h\1\s*>", match => "\n" + new string('#', int.Parse(match.Groups[1].Value)) + " " + match.Groups[2].Value + "\n", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<(strong|b)\b[^>]*>([\s\S]*?)</\1\s*>", "**$2**", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<(em|i)\b[^>]*>([\s\S]*?)</\1\s*>", "*$2*", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>([\s\S]*?)</a\s*>", "[$2]($1)", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<img\b[^>]*>", match => { string Attribute(string key) => Regex.Match(match.Value, key + @"\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase).Groups[1].Value; return "![" + Attribute("alt") + "](" + Attribute("src") + ")"; }, RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<table\b[^>]*>([\s\S]*?)</table\s*>", match =>
        {
            var rows = Regex.Matches(match.Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr\s*>", RegexOptions.IgnoreCase).Select(row => Regex.Matches(row.Groups[1].Value, @"<t[hd]\b[^>]*>([\s\S]*?)</t[hd]\s*>", RegexOptions.IgnoreCase).Select(cell => cell.Groups[1].Value.Trim()).ToArray()).Where(row => row.Length > 0).ToArray();
            if (rows.Length == 0) return "";
            return "\n|" + string.Join('|', rows[0]) + "|\n|" + string.Join('|', rows[0].Select(_ => "---")) + "|\n" + string.Join('\n', rows.Skip(1).Select(row => "|" + string.Join('|', row) + "|")) + "\n";
        }, RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<(br|hr)\b[^>]*>|</?(p|div|section|article|ul|ol|blockquote|pre)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"</?[A-Za-z][^>]*>", "");
        source = WebUtility.HtmlDecode(source);
        return Regex.Replace(source, "\uE000NOTICECODE([0-9]+)\uE001", match => code[int.Parse(match.Groups[1].Value)]);
    }
}

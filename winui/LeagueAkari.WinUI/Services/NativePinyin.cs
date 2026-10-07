using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

/// <summary>The pinyin-pro default, no-tone search conversion used by upstream string-match.ts.</summary>
public static class NativePinyin
{
    private sealed record Phrase(string[] Characters, string[] Readings, double Probability);
    private sealed record DictionaryData(FrozenDictionary<string, string> Characters, FrozenDictionary<string, Phrase[]> Endings, FrozenDictionary<string, string> Lowercase, int[] Cased, int[] CaseIgnorable);
    private sealed record Segment(Phrase Phrase, int Start, Segment? Next);
    private sealed record Probability(double Value, int Scale, Segment? Segments, Phrase? Pending = null, int Start = 0);
    private static readonly Lazy<DictionaryData> Dictionary = new(Load);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private const int CacheLimit = 2048;

    private static DictionaryData Load()
    {
        using var compressed = new MemoryStream(System.Convert.FromBase64String(NativePinyinData.GzipBase64));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var data = JsonDocument.Parse(gzip);
        var characters = data.RootElement.GetProperty("characters").EnumerateObject().ToFrozenDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var phrases = data.RootElement.GetProperty("phrases").EnumerateArray().Select(p => new Phrase(Runes(p[0].GetString()!), p[1].GetString()!.Split(' '), p[2].GetDouble()));
        // Aho-Corasick emits longer suffixes first; the upstream reverse DP visits shortest first.
        var endings = phrases.GroupBy(p => p.Characters[^1]).ToFrozenDictionary(g => g.Key, g => g.OrderBy(p => p.Characters.Length).ToArray(), StringComparer.Ordinal);
        var lowercase = data.RootElement.GetProperty("lowercase").EnumerateObject().ToFrozenDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        return new(characters, endings, lowercase, data.RootElement.GetProperty("cased").EnumerateArray().Select(p => p.GetInt32()).ToArray(), data.RootElement.GetProperty("caseIgnorable").EnumerateArray().Select(p => p.GetInt32()).ToArray());
    }

    public static string Convert(string text)
    {
        if (text.Length == 0) return "";
        lock (CacheLock) if (Cache.TryGetValue(text, out var cached)) return cached;
        var result = Transliterate(text);
        lock (CacheLock)
        {
            if (!Cache.ContainsKey(text))
            {
                if (Cache.Count == CacheLimit) Cache.Remove(CacheOrder.Dequeue());
                Cache.Add(text, result); CacheOrder.Enqueue(text);
            }
        }
        return result;
    }

    private static string[] Runes(string text) => text.EnumerateRunes().Select(r => r.ToString()).ToArray();

    private static Probability Normalize(Probability score) => score.Value < 1e-300 ? score with { Value = score.Value * 1e300, Scale = score.Scale + 1 } : score;
    private static Probability Best(Probability? existing, Probability candidate) => existing is not null && (existing.Scale < candidate.Scale || existing.Scale == candidate.Scale && existing.Value > candidate.Value) ? existing : candidate;

    private static string Transliterate(string text)
    {
        var data = Dictionary.Value;
        var characters = Runes(text);
        var scores = new Probability?[characters.Length];
        // Match pinyin-pro segmentit=2, including its probability rescaling and equal-score tie order.
        for (int end = characters.Length - 1; end >= 0; end--)
        {
            var tail = end + 1 == characters.Length ? new Probability(1, 0, null) : scores[end + 1]!;
            if (data.Endings.TryGetValue(characters[end], out var phrases))
                foreach (var phrase in phrases)
                {
                    int start = end - phrase.Characters.Length + 1;
                    if (start < 0) continue;
                    bool matches = true;
                    for (int i = 0; i < phrase.Characters.Length; i++) if (characters[start + i] != phrase.Characters[i]) { matches = false; break; }
                    if (matches) scores[start] = Best(scores[start], Normalize(new(phrase.Probability * tail.Value, tail.Scale, tail.Segments, phrase, start)));
                }
            // The upstream fallback resets decimal rather than inheriting tail.decimal.
            scores[end] = Best(scores[end], Normalize(new(1e-13 * tail.Value, 0, tail.Segments)));
            if (scores[end]!.Pending is { } chosen)
            {
                var score = scores[end]!;
                scores[end] = score with { Segments = new(chosen, score.Start, score.Segments), Pending = null };
            }
        }
        var selected = scores[0]!.Segments;
        var output = new StringBuilder(text.Length * 3);
        for (int i = 0; i < characters.Length;)
        {
            if (selected is not null && selected.Start == i)
            {
                for (int j = 0; j < selected.Phrase.Characters.Length; j++) output.Append(j < selected.Phrase.Readings.Length ? selected.Phrase.Readings[j] : "");
                i += selected.Phrase.Characters.Length; selected = selected.Next;
            }
            else
            {
                string character = characters[i];
                if (character == "々") output.Append(i > 0 && data.Characters.TryGetValue(characters[i - 1], out var previous) ? previous : "tong");
                else if (character == "了" && (i == 0 || !data.Characters.ContainsKey(characters[i - 1]))) output.Append("liao");
                else output.Append(data.Characters.GetValueOrDefault(character, character));
                i++;
            }
        }
        return output.ToString();
    }

    public static bool Matches(string pattern, string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        if (IsSubsequence(pattern, title)) return true;
        string titlePinyin = Convert(title);
        return IsSubsequence(pattern, titlePinyin) || IsSubsequence(Convert(pattern), titlePinyin);
    }

    public static bool IsSubsequence(string pattern, string title)
    {
        if (pattern.Length == 0) return true;
        if (title.Length == 0) return false;
        pattern = Lowercase(pattern); title = Lowercase(title);
        int index = 0;
        foreach (char character in title) if (index < pattern.Length && character == pattern[index]) index++;
        return index == pattern.Length;
    }

    private static bool HasProperty(int[] boundaries, int character)
    {
        int index = Array.BinarySearch(boundaries, character);
        return index < 0 ? (~index & 1) == 1 : (index & 1) == 0;
    }

    private static string Lowercase(string text)
    {
        if (text.All(c => c < 128)) return text.ToLowerInvariant();
        var data = Dictionary.Value;
        var characters = text.EnumerateRunes().ToArray();
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < characters.Length; i++)
        {
            if (characters[i].Value == 0x3a3)
            {
                int before = i - 1, after = i + 1;
                while (before >= 0 && HasProperty(data.CaseIgnorable, characters[before].Value)) before--;
                while (after < characters.Length && HasProperty(data.CaseIgnorable, characters[after].Value)) after++;
                bool final = before >= 0 && HasProperty(data.Cased, characters[before].Value) && (after == characters.Length || !HasProperty(data.Cased, characters[after].Value));
                result.Append(final ? 'ς' : 'σ');
            }
            else
            {
                string character = characters[i].ToString();
                result.Append(data.Lowercase.GetValueOrDefault(character, character));
            }
        }
        return result.ToString();
    }
}

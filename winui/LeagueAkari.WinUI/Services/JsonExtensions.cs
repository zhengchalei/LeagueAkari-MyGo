using System.Text.Json;
namespace LeagueAkari.WinUI.Services;
public static class JsonExtensions
{
 public static JsonElement Field(this JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var field)?field:default;
 public static string Text(this JsonElement value,string name,string fallback="")=>value.Field(name).ValueKind is JsonValueKind.String?value.Field(name).GetString()??fallback:value.Field(name).ValueKind is JsonValueKind.Number?value.Field(name).ToString():fallback;
 public static double Number(this JsonElement value,string name,double fallback=0)=>value.Field(name).TryNumber(fallback);
 public static double TryNumber(this JsonElement value,double fallback=0)=>value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var result)?result:fallback;
 public static IEnumerable<JsonElement> Items(this JsonElement value)=>value.ValueKind==JsonValueKind.Array?value.EnumerateArray():Array.Empty<JsonElement>();
 public static bool Boolean(this JsonElement value,string name)=>value.Field(name).ValueKind==JsonValueKind.True;
}

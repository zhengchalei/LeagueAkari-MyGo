using System.Text.Json;
namespace LeagueAkari.WinUI.Services;
public sealed class BackendClient
{
    public Task<JsonElement> CallAsync(string ns,string method,params object?[] args)=>throw new NotSupportedException("Fixture tests do not access the running client");
    public Task<JsonElement> StateAsync(string ns,string? state=null)=>throw new NotSupportedException("Fixture tests do not access the running client");
}

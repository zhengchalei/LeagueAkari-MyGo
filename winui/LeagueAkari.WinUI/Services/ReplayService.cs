using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed class ReplayService
{
    private readonly Func<string,string,object?[],Task<JsonElement>> _call;
    public ReplayService(BackendClient backend):this((ns,method,args)=>backend.CallAsync(ns,method,args)){}
    public ReplayService(Func<string,string,object?[],Task<JsonElement>> call)=>_call=call;
    private Task<JsonElement> Lcu(string method,string path,object?body=null)=>_call("winui-backend","lcuRequest",[method,path,body]);
    public async Task<JsonElement> InitializeAsync(JsonElement raw,string source)
    {
        var game=MatchData.Game(raw);var config=await Lcu("GET","/lol-replays/v1/configuration");if(!config.Boolean("isReplaysEnabled"))return default;
        var id=(long)game.Number("gameId");
        await Lcu("POST",$"/lol-replays/v2/metadata/{id}/create",new{gameVersion=source=="sgp"?game.Text("gameVersion"):config.Text("gameVersion"),gameType=game.Text("gameType"),queueId=game.Number("queueId"),gameEnd=source=="sgp"?game.Number("gameEndTimestamp"):game.Number("gameCreation")+game.Number("gameDuration")*1000});
        return await MetadataAsync(id);
    }
    public Task<JsonElement> MetadataAsync(long id)=>Lcu("GET",$"/lol-replays/v1/metadata/{id}");
    public Task<JsonElement> ExecuteAsync(long id,string state)
    {
        if(state is not("download" or "watch"))throw new InvalidOperationException("Replay is not ready to download or watch.");
        return Lcu("POST",$"/lol-replays/v1/rofls/{id}/"+(state=="watch"?"watch":"download"),new{componentType="replay-button_match-history"});
    }
}

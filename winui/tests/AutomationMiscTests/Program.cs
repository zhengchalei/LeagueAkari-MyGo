using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();
var friends = J("""[{"puuid":"off","gameName":"Alice","gameTag":"NA","availability":"offline"},{"puuid":"chat","gameName":"Alice","gameTag":"EU","availability":"chat"},{"puuid":"away","gameName":"B","gameTag":"CN","availability":"away"},{"puuid":"dnd","gameName":"C","gameTag":"CN","availability":"dnd"}]""");
Check(AutomationMiscData.Friends(friends, "").Select(f=>f.Text("puuid")).SequenceEqual(new[]{"dnd","away","chat","off"}), "Original friend priority dnd then away then chat then offline");
Check(AutomationMiscData.Friends(friends, " alice#eu ").Single().Text("puuid") == "chat", "Trimmed case-insensitive RiotID search");
var client = J("""{"connectionState":"connected","auth":{"pid":1,"port":10}}"""); var lobby = J("""{"lobby":{"id":"room"}}""");
var scheduled = new HashSet<string>{"off"}; var writes = new List<string[]>(); bool fail=false;
TaskCompletionSource? gate=null; string delayed="";
async Task<JsonElement> Call(string ns,string method,object?[] args)
{
    if (method=="snapshot") { string part=args[1]?.ToString()??""; if(part==delayed && gate!=null)await gate.Task; return args[0]?.ToString()=="auto-gameflow-main"?JsonSerializer.SerializeToElement(new{friendsToBeInvited=scheduled.ToArray()}):part=="lobby"?lobby:client; }
    if(method=="http-request")return JsonSerializer.SerializeToElement(new{data=friends});
    if(method=="setFriendsToBeInvited") { if(fail)throw new Exception("save failed"); var ids=(string[])args[0]!; writes.Add(ids);scheduled=ids.ToHashSet();return J("{}"); }
    throw new Exception("Unexpected call");
}
var controller=new AutomationInvitationsController(Call);controller.Activate();await controller.RefreshAsync();
Check(controller.CanSchedule("dnd") && !controller.CanSchedule("missing"), "Scheduling requires current friend and connected lobby");
await controller.ToggleAsync("chat");Check(writes.Count==1&&scheduled.SetEquals(new[]{"off","chat"}),"Schedule retains existing authoritative invitations");
await controller.ToggleAsync("chat");Check(writes.Count==2&&scheduled.SetEquals(new[]{"off"}),"Cancel removes only selected scheduled friend");
fail=true;await controller.ToggleAsync("chat");Check(controller.Error?.Message=="save failed"&&!controller.Scheduled.Contains("chat")&&!controller.Busy,"Failed save retains original plan and permits retry");
fail=false;await controller.ToggleAsync("chat");Check(controller.Error==null&&controller.Scheduled.Contains("chat"),"Retry updates plan after success");
gate=new();delayed="lobby";
var request=controller.ToggleAsync("away");controller.Deactivate();gate.SetResult();await request;
Check(writes.Count==3&&!controller.Busy,"Unloaded preflight never submits late invitation");
controller.Activate();delayed="";await controller.RefreshAsync();
client=J("""{"connectionState":"connected","auth":{"pid":2,"port":20}}""");await controller.ToggleAsync("away");
Check(writes.Count==3&&!controller.Scheduled.Contains("away"),"Different client preflight cancels write and clears old ownership");
await controller.RefreshAsync();lobby=J("""{"lobby":null}""");await controller.ToggleAsync("away");
Check(writes.Count==3&&!controller.InLobby&&!controller.CanSchedule("away"),"Leaving room disables plan and blocks stale click");
controller.ApplyEvent(J("""{"name":"update-state-prop/auto-gameflow-main:state","args":["friendsToBeInvited",["dnd"]]}"""));
Check(controller.Scheduled.SetEquals(new[]{"dnd"}),"External invitation plan updates visible selection");
controller.ConnectionChanged(J("""{"connectionState":"disconnected"}"""));Check(!controller.Connected&&!controller.InLobby&&controller.Friends.ValueKind==JsonValueKind.Undefined,"Disconnect removes previous friends and lobby gate");

bool replyFail=false;var texts=new List<string>();TaskCompletionSource? replyGate=null;
var reply=new AutoReplyDraft(async text=>{if(replyGate!=null)await replyGate.Task;if(replyFail)throw new Exception("reply failed");texts.Add(text);});reply.Activate("old");
await reply.SaveAsync();Check(texts.Count==0&&!reply.Dirty,"Unchanged reply has no write");
reply.Text="new";replyFail=true;await reply.SaveAsync();Check(reply.Text=="new"&&reply.Saved=="old"&&reply.Dirty&&reply.Error?.Message=="reply failed"&&!reply.Busy,"Failed reply preserves dirty text for retry");
replyFail=false;await reply.SaveAsync();Check(reply.Saved=="new"&&!reply.Dirty&&reply.Error==null,"Reply retry commits successful text");
reply.Update("external");Check(reply.Text=="external"&&!reply.Dirty,"External reply settings replace local draft like original watcher");
reply.Text="";await reply.SaveAsync();Check(reply.Saved==""&&texts.Last()=="","Empty reply is saved rather than silently discarded");
reply.Text="pending";replyGate=new();var saving=reply.SaveAsync();await reply.SaveAsync();Check(reply.Busy,"Duplicate save shares busy guard");reply.Deactivate();reply.Activate("another");replyGate.SetResult();await saving;
Check(reply.Text=="another"&&reply.Saved=="another"&&!reply.Busy,"Old save completion cannot mutate returned page");
reply.Text="pending again";replyGate=new();saving=reply.SaveAsync();reply.Update("new external setting");replyGate.SetResult();await saving;
Check(reply.Saved=="new external setting"&&reply.Text==reply.Saved&&!reply.Dirty&&!reply.Busy,"External setting arriving during save stays authoritative");
replyGate=null;
client=J("""{"connectionState":"connected","auth":{"pid":2,"port":20}}""");lobby=J("""{"lobby":{"id":"room"}}""");await controller.RefreshAsync();
gate=new();delayed="lobby";var refreshing=controller.RefreshAsync();
controller.ApplyEvent(J("""{"name":"update-state-prop/auto-gameflow-main:state","args":["friendsToBeInvited",["away"]]}"""));gate.SetResult();await refreshing;
Check(controller.Scheduled.SetEquals(new[]{"away"})&&controller.Friends.Items().Count()==4,"Late refresh retains newer invitation event and loads friend rows");
Console.WriteLine($"Automation misc: {passed} controllable business contracts passed; no League writes");

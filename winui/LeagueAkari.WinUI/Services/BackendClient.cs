using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed class BackendClient : IAsyncDisposable
{
 private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> _pending=new();
 private readonly SemaphoreSlim _write=new(1,1);
 private readonly CancellationTokenSource _stop=new();
 private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
 private NamedPipeServerStream? _pipe;
 private StreamWriter? _writer;
 private Process? _process;
 private long _next;
 public bool IsReady=>_ready.Task.IsCompletedSuccessfully;
 public event Action<JsonElement>? EventReceived;
 public Func<JsonElement,Task<object?>>? HostCall;
 public async Task StartAsync(string? executable=null)
 {
  string pipeName="league-akari-winui-"+Guid.NewGuid().ToString("N");
  _pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
  executable??=Path.Combine(AppContext.BaseDirectory,"LeagueAkari.Backend.exe");
#if DEBUG
  if(Environment.GetEnvironmentVariable("LEAGUE_AKARI_WINUI_BACKEND") is {Length:>0} fixtureBackend)executable=fixtureBackend;
#endif
  var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory};
  start.ArgumentList.Add("--winui-backend");start.ArgumentList.Add(pipeName);
  start.ArgumentList.Add("--host-pid");start.ArgumentList.Add(Environment.ProcessId.ToString());
  start.ArgumentList.Add("--host-exe");start.ArgumentList.Add(Environment.ProcessPath!);
  if(Environment.GetEnvironmentVariable("LEAGUE_AKARI_WINUI_USER_DATA") is {Length:>0} userData){start.ArgumentList.Add("--user-data");start.ArgumentList.Add(userData);}
  _process=Process.Start(start)??throw new InvalidOperationException("无法启动 Go 后端");
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
  await _pipe.WaitForConnectionAsync(timeout.Token);
  _writer=new StreamWriter(_pipe,new UTF8Encoding(false),65536,true){AutoFlush=true};
  _=ReadLoopAsync();
  await _ready.Task.WaitAsync(timeout.Token);
 }
 private async Task ReadLoopAsync()
 {
  try{
   using var reader=new StreamReader(_pipe!,Encoding.UTF8,false,65536,true);
   while(await reader.ReadLineAsync(_stop.Token) is { } line){
    using var doc=JsonDocument.Parse(line);var message=doc.RootElement.Clone();
    switch(message.GetProperty("type").GetString()){
     case "ready":_ready.TrySetResult();break;
     case "response":
      if(_pending.TryRemove(message.GetProperty("id").GetString()!,out var task)){
       var result=message.GetProperty("result");
       if(result.GetProperty("success").GetBoolean())task.TrySetResult(result.TryGetProperty("data",out var data)?data.Clone():JsonSerializer.SerializeToElement<object?>(null));
       else task.TrySetException(new InvalidOperationException(ErrorMessage(result)));
      }break;
     case "event":EventReceived?.Invoke(message.GetProperty("event").Clone());break;
     case "host-call":_ = HandleHostCallAsync(message);break;
    }
   }
   throw new IOException("Go 后端连接已关闭");
  }catch(Exception ex){_ready.TrySetException(ex);foreach(var entry in _pending.Values)entry.TrySetException(ex);_pending.Clear();}
 }
 private async Task HandleHostCallAsync(JsonElement request)
 {
  object result;
  try {var data=HostCall is null?throw new InvalidOperationException("宿主操作尚未连接"):await HostCall(request);result=new{success=true,data};}
  catch(Exception ex){result=new{success=false,error=new{message=ex.Message}};}
  await WriteAsync(new{type="host-response",id=request.GetProperty("id").GetString(),result});
 }
 private static string ErrorMessage(JsonElement result)=>result.TryGetProperty("error",out var error)?error.ValueKind==JsonValueKind.String?error.GetString()!:error.TryGetProperty("message",out var message)?message.GetString()!:error.ToString():"操作失败";
 public async Task<JsonElement> CallAsync(string ns,string method,params object?[] args)
 {
  string id=Interlocked.Increment(ref _next).ToString();var response=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
  _pending[id]=response;
  try {await WriteAsync(new{id,type="call",@namespace=ns,method,args});return await response.Task.WaitAsync(TimeSpan.FromSeconds(45),_stop.Token);}
  finally{_pending.TryRemove(id,out _);}
 }
 public Task<JsonElement> StateAsync(string ns,string state="state")=>CallAsync("winui-backend","snapshot",ns,state);
 public async Task<byte[]?> ImageAsync(string path){if(string.IsNullOrEmpty(path))return null;var value=await CallAsync("winui-backend","image",path);return value.TryGetProperty("base64",out var data)?Convert.FromBase64String(data.GetString()!):null;}
 private async Task WriteAsync(object value){await _write.WaitAsync(_stop.Token);try{if(_writer is null)throw new IOException("后端尚未连接");await _writer.WriteLineAsync(JsonSerializer.Serialize(value));}finally{_write.Release();}}
 public async ValueTask DisposeAsync(){_stop.Cancel();_pipe?.Dispose();if(_process is {HasExited:false}){try{await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){_process.Kill(true);}}_process?.Dispose();_write.Dispose();_stop.Dispose();}
}

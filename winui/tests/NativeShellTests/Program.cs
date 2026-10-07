using System.Diagnostics;
using System.Runtime.InteropServices;
using LeagueAkari.WinUI.Services;

if(args is ["duplicate",var childIdentity]){
 Console.WriteLine("attempting");
 bool acquired=NativeShell.AcquireInstance(childIdentity);
 NativeShell.ReleaseInstance();
 return acquired?10:0;
}
if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Real Windows HWND tests require Windows");
int passed=0;
foreach(var mode in new[]{"startup","hidden","minimized"}){
 string identity="LeagueAkari.WinUI.Test."+Guid.NewGuid().ToString("N");
 if(!NativeShell.AcquireInstance(identity))throw new Exception("Fixture primary could not acquire isolated mutex");
 nint hwnd=0;NativeShell? shell=null;Process? duplicate=null;
 try{
  int activations=0;
  void CreateFixture(){
   hwnd=Native.CreateWindowEx(0,"STATIC","NativeShell isolated test",0x00cf0000,0,0,900,650,0,0,0,0);
   if(hwnd==0)throw new Exception("Could not create fixture HWND");
   shell=new NativeShell(hwnd,action=>{if(action!="main")throw new Exception("Wrong activation action");activations++;Native.ShowWindow(hwnd,9);});
  }
  if(mode!="startup"){
   CreateFixture();Native.ShowWindow(hwnd,mode=="minimized"?6:0);
  }
  var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
  start.ArgumentList.Add("duplicate");start.ArgumentList.Add(identity);
  duplicate=Process.Start(start)??throw new Exception("Fixture second process failed");
  if(duplicate.StandardOutput.ReadLine()!="attempting")throw new Exception("Fixture second process did not start");
  if(mode=="startup"){
   Thread.Sleep(250); // Reproduce mutex-held / subclass-not-installed startup gap.
   CreateFixture();
  }
  var deadline=Stopwatch.StartNew();
  while(deadline.Elapsed<TimeSpan.FromSeconds(5)&&(activations==0||!duplicate.HasExited)){
   while(Native.PeekMessage(out var message,0,0,0,1)){Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);}
   Thread.Sleep(10);
  }
  if(!duplicate.HasExited||duplicate.ExitCode!=0||activations!=1)throw new Exception($"{mode}: duplicate did not activate exactly once ({activations})");
  if(!Native.IsWindowVisible(hwnd)||Native.IsIconic(hwnd))throw new Exception($"{mode}: primary not restored");
  Console.WriteLine($"PASS actual two-process {mode} activation");passed++;
 }finally{
  shell?.Dispose();if(hwnd!=0)Native.DestroyWindow(hwnd);
  if(duplicate is not null){if(!duplicate.HasExited)duplicate.Kill();duplicate.Dispose();}
  NativeShell.ReleaseInstance();
 }
 if(!NativeShell.AcquireInstance(identity))throw new Exception("Instance lock survived orderly shutdown");
 NativeShell.ReleaseInstance();
 Console.WriteLine($"PASS {mode} orderly exit releases mutex");passed++;
}
Console.WriteLine($"NativeShell: {passed} real Windows checks passed; isolated HWND/mutex only");
return 0;

static class Native{
 [StructLayout(LayoutKind.Sequential)]public struct Message{public nint Hwnd;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X,Y;public uint Private;}
 [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode)]public static extern nint CreateWindowEx(uint ex,string klass,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
 [DllImport("user32.dll")]public static extern bool DestroyWindow(nint hwnd);
 [DllImport("user32.dll")]public static extern bool ShowWindow(nint hwnd,int command);
 [DllImport("user32.dll")]public static extern bool IsWindowVisible(nint hwnd);
 [DllImport("user32.dll")]public static extern bool IsIconic(nint hwnd);
 [DllImport("user32.dll",EntryPoint="PeekMessageW")]public static extern bool PeekMessage(out Message message,nint hwnd,uint first,uint last,uint remove);
 [DllImport("user32.dll")]public static extern bool TranslateMessage(ref Message message);
 [DllImport("user32.dll",EntryPoint="DispatchMessageW")]public static extern nint DispatchMessage(ref Message message);
}
namespace LeagueAkari.WinUI.Services{static class Localization{public static string Text(string zh,string en)=>zh;}}

using System.Runtime.InteropServices;
namespace LeagueAkari.WinUI.Services;

// Keep the tray and activation callback on the existing WinUI HWND; no hidden UI process.
public sealed class NativeShell : IDisposable
{
 private const uint TrayMessage=0x8031;
 private static uint ActivateMessage=RegisterWindowMessage("LeagueAkari.WinUI.Activate");
 private static string ActivationProperty="LeagueAkari.WinUI.HostWindow";
 private static readonly uint TaskbarCreatedMessage=RegisterWindowMessage("TaskbarCreated");
 private static Mutex? _instance;
 private readonly nint _hwnd;
 private readonly SubclassProc _callback;
 private NotifyIconData _icon;
 private readonly Action<string> _action;
 public static bool AcquireInstance(string identity="LeagueAkari.WinUI"){
  if(_instance is not null)return true;
  ActivateMessage=RegisterWindowMessage(identity+".Activate");ActivationProperty=identity+".HostWindow";
  var candidate=new Mutex(false,"Local\\"+identity+".Host",out bool first);
  if(!first){
   candidate.Dispose();
   // The first process can hold its mutex before MainWindow has installed its
   // callback. Wait briefly for that HWND rather than losing a startup click.
   var deadline=System.Diagnostics.Stopwatch.StartNew();
   do{
    nint target=0;EnumWindows((hwnd,_)=>{if(GetProp(hwnd,ActivationProperty)!=0){target=hwnd;return false;}return true;},0);
    if(target!=0){GetWindowThreadProcessId(target,out uint pid);AllowSetForegroundWindow(pid);PostMessage(target,ActivateMessage,0,0);return false;}
    Thread.Sleep(100);
   }while(deadline.Elapsed<TimeSpan.FromSeconds(2));
   return false;
  }
  _instance=candidate;return true;
 }
 public static void ReleaseInstance(){_instance?.Dispose();_instance=null;}
 public NativeShell(nint hwnd,Action<string> action){_hwnd=hwnd;_action=action;_callback=WindowProc;SetWindowSubclass(hwnd,_callback,31,0);SetProp(hwnd,ActivationProperty,1);ChangeWindowMessageFilterEx(hwnd,ActivateMessage,1,0);var icon=LoadImage(0,Path.Combine(AppContext.BaseDirectory,"LA_ICON.ico"),1,32,32,0x10);if(icon==0)icon=LoadIcon(0,(nint)32512);SendMessage(hwnd,0x80,0,icon);SendMessage(hwnd,0x80,1,icon);_icon=new(){cbSize=(uint)Marshal.SizeOf<NotifyIconData>(),hWnd=hwnd,uID=1,uFlags=1|2|4,uCallbackMessage=TrayMessage,hIcon=icon,szTip="LeagueAkari · WinUI 3",szInfo="",szInfoTitle=""};ShellNotifyIcon(0,ref _icon);}
 private nint WindowProc(nint hwnd,uint message,nuint wp,nint lp,nuint id,nuint data){
  if(message==0x24){var result=DefSubclassProc(hwnd,message,wp,lp);var limits=Marshal.PtrToStructure<MinMaxInfo>(lp);double scale=GetDpiForWindow(hwnd)/96d;limits.MinTrackSize=new(){X=(int)Math.Ceiling(840*scale),Y=(int)Math.Ceiling(600*scale)};Marshal.StructureToPtr(limits,lp,false);return result;}
  if(message==TaskbarCreatedMessage){ShellNotifyIcon(0,ref _icon);return 0;}
  if(message==ActivateMessage){_action("main");return 0;}
  if(message==TrayMessage){var action=(uint)lp&0xffff;if(action==0x202||action==0x203){_action("main");return 0;}if(action==0x205){ShowMenu();return 0;}}
  return DefSubclassProc(hwnd,message,wp,lp);
 }
 private void ShowMenu(){var menu=CreatePopupMenu();var items=new[]{("main","打开主窗口","Open main window"),("mini","Mini 窗口","Mini window"),("opgg","OP.GG 数据","OP.GG data"),("ongoing-window","对局悬浮窗","Ongoing game window"),("cd-window","CD 计时","Cooldown timer"),("launch","启动 LOL 客户端","Launch League client"),("quit","退出","Quit")};try{for(int i=0;i<items.Length;i++)AppendMenu(menu,0,(nuint)(i+1),Localization.Text(items[i].Item2,items[i].Item3));GetCursorPos(out var point);SetForegroundWindow(_hwnd);uint selected=TrackPopupMenu(menu,0x100|0x2,point.X,point.Y,0,_hwnd,0);if(selected>0&&selected<=items.Length)_action(items[(int)selected-1].Item1);}finally{DestroyMenu(menu);}}
 public void Dispose(){RemoveProp(_hwnd,ActivationProperty);ShellNotifyIcon(2,ref _icon);RemoveWindowSubclass(_hwnd,_callback,31);}
 [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate nint SubclassProc(nint hwnd,uint message,nuint wp,nint lp,nuint id,nuint data);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyIconData{public uint cbSize;public nint hWnd;public uint uID,uFlags,uCallbackMessage;public nint hIcon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string szTip;public uint dwState,dwStateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string szInfo;public uint uTimeout;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string szInfoTitle;public uint dwInfoFlags;public Guid guidItem;public nint hBalloonIcon;}
 [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)]private struct MinMaxInfo{public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
 [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
 [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(nint hwnd,SubclassProc callback,nuint id,nuint data);
 [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc callback,nuint id);
 [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint hwnd,uint message,nuint wp,nint lp);
 [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW")]private static extern bool ShellNotifyIcon(uint command,ref NotifyIconData data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
 [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool PostMessage(nint hwnd,uint message,nuint wp,nint lp);
 private delegate bool EnumWindowsProc(nint hwnd,nint data);
 [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowsProc callback,nint data);
 [DllImport("user32.dll",EntryPoint="GetPropW",CharSet=CharSet.Unicode)]private static extern nint GetProp(nint hwnd,string name);
 [DllImport("user32.dll",EntryPoint="SetPropW",CharSet=CharSet.Unicode)]private static extern bool SetProp(nint hwnd,string name,nint data);
 [DllImport("user32.dll",EntryPoint="RemovePropW",CharSet=CharSet.Unicode)]private static extern nint RemoveProp(nint hwnd,string name);
 [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
 [DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(uint processId);
 [DllImport("user32.dll")]private static extern bool ChangeWindowMessageFilterEx(nint hwnd,uint message,uint action,nint changeFilter);
 [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint SendMessage(nint hwnd,uint message,nuint wp,nint lp);
 [DllImport("user32.dll",EntryPoint="LoadImageW",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string name,uint type,int width,int height,uint flags);
 [DllImport("user32.dll",EntryPoint="LoadIconW")]private static extern nint LoadIcon(nint instance,nint resource);
 [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
 [DllImport("user32.dll",EntryPoint="AppendMenuW",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(nint menu,uint flags,nuint id,string text);
 [DllImport("user32.dll")]private static extern uint TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint hwnd,nint rect);
 [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
 [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
}

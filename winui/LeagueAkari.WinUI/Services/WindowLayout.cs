using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed class WindowLayout : IDisposable
{
 private readonly Window _window;
 private readonly BackendClient _backend;
 private readonly string _ns;
 private readonly DispatcherTimer _save=new(){Interval=TimeSpan.FromMilliseconds(450)};
 private bool _ready;
 private bool _applying;
 private bool _disposed;
 private bool _persisting;
 private double _opacity=1;
 private bool _clickThrough;
 private readonly SemaphoreSlim _persistGate=new(1,1);
 private global::Windows.Graphics.RectInt32? _pendingReposition;
 public WindowLayout(Window window,BackendClient backend,string name){_window=window;_backend=backend;_ns="window-manager-main/"+name;_save.Tick+=async(_,_)=>{_save.Stop();await PersistAsync();};window.AppWindow.Changed+=Changed;backend.EventReceived+=SettingUpdated;}
 public async Task RestoreAsync(){var settings=await _backend.CallAsync("setting-factory-main","getByPrefix",_ns,"");if(_disposed)return;_applying=true;try{ApplyBounds(settings.Field("trackedBounds"));if(_window.AppWindow.Presenter is OverlappedPresenter presenter){presenter.IsAlwaysOnTop=settings.Boolean("pinned");if(settings.Boolean("maximized"))presenter.Maximize();else if(presenter.State==OverlappedPresenterState.Maximized)presenter.Restore();}SetOpacity(settings.Number("opacity",1));_ready=true;}finally{_applying=false;}}
 private void ApplyBounds(JsonElement bounds){if(bounds.Number("width")<400||bounds.Number("height")<300)return;ApplyRectangle(new((int)bounds.Number("x"),(int)bounds.Number("y"),(int)bounds.Number("width"),(int)bounds.Number("height")));}
 private void ApplyRectangle(global::Windows.Graphics.RectInt32 saved){var work=DisplayArea.GetFromRect(saved,DisplayAreaFallback.Nearest).WorkArea;saved.Width=Math.Min(saved.Width,work.Width);saved.Height=Math.Min(saved.Height,work.Height);saved.X=Math.Clamp(saved.X,work.X,work.X+work.Width-saved.Width);saved.Y=Math.Clamp(saved.Y,work.Y,work.Y+work.Height-saved.Height);_window.AppWindow.MoveAndResize(saved);}
 private void SettingUpdated(JsonElement envelope){if(_disposed||!_ready||envelope.Text("namespace")!="mobx-utils-main"||envelope.Text("name")!="update-state-prop/"+_ns+":settings")return;var args=envelope.Field("args").Items().ToArray();if(args.Length<2||args[0].ValueKind!=JsonValueKind.String)return;var key=args[0].GetString();if(_persisting&&key is "trackedBounds" or "maximized")return;var value=args[1].Clone();_window.DispatcherQueue.TryEnqueue(()=>{if(_disposed)return;_applying=true;try{switch(key){case "pinned":if(_window.AppWindow.Presenter is OverlappedPresenter p)p.IsAlwaysOnTop=value.ValueKind==JsonValueKind.True;break;case "opacity":SetOpacity(value.TryNumber());break;case "trackedBounds":ApplyBounds(value);break;case "maximized":if(_window.AppWindow.Presenter is OverlappedPresenter m){if(value.ValueKind==JsonValueKind.True)m.Maximize();else if(m.State==OverlappedPresenterState.Maximized)m.Restore();}break;}}finally{_applying=false;}});}
 private void Changed(AppWindow sender,AppWindowChangedEventArgs args){if(_pendingReposition is { } pending&&(_window.AppWindow.Presenter as OverlappedPresenter)?.State==OverlappedPresenterState.Restored){_pendingReposition=null;ApplyRectangle(pending);}if(_ready&&!_applying&&!_disposed&&(args.DidPositionChange||args.DidSizeChange||args.DidPresenterChange)){_save.Stop();_save.Start();}}
 public async Task PersistAsync(){await _persistGate.WaitAsync();try{if(!_ready||_disposed||(_window.AppWindow.Presenter as OverlappedPresenter)?.State==OverlappedPresenterState.Minimized)return;_persisting=true;var state=(_window.AppWindow.Presenter as OverlappedPresenter)?.State;var p=_window.AppWindow.Position;var s=_window.AppWindow.Size;await _backend.CallAsync("setting-factory-main","set",_ns,"maximized",state==OverlappedPresenterState.Maximized);if(state==OverlappedPresenterState.Restored)await _backend.CallAsync("setting-factory-main","set",_ns,"trackedBounds",new{x=p.X,y=p.Y,width=s.Width,height=s.Height});}catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}finally{_persisting=false;_persistGate.Release();}}
 public void Center(){var work=DisplayArea.GetFromWindowId(_window.AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;var size=_window.AppWindow.Size;_window.AppWindow.Move(new(work.X+(work.Width-size.Width)/2,work.Y+(work.Height-size.Height)/2));}
 public void RepositionIfInvisible(){var position=_window.AppWindow.Position;var size=_window.AppWindow.Size;var saved=new global::Windows.Graphics.RectInt32(position.X,position.Y,size.Width,size.Height);var work=DisplayArea.GetFromRect(saved,DisplayAreaFallback.Nearest).WorkArea;var width=Math.Max(0,Math.Min(saved.X+saved.Width,work.X+work.Width)-Math.Max(saved.X,work.X));var height=Math.Max(0,Math.Min(saved.Y+saved.Height,work.Y+work.Height)-Math.Max(saved.Y,work.Y));if(size.Width<=work.Width&&size.Height<=work.Height&&(double)width*height>=.98*size.Width*size.Height)return;if((_window.AppWindow.Presenter as OverlappedPresenter)?.State==OverlappedPresenterState.Minimized){_pendingReposition=saved;return;}ApplyRectangle(saved);}
 public void SetOpacity(double opacity){_opacity=Math.Clamp(opacity,.1,1);var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(_window);var style=GetWindowLongPtr(hwnd,-20).ToInt64();if(_opacity>=1&&!_clickThrough){SetWindowLongPtr(hwnd,-20,(nint)(style&~0x80000));return;}SetWindowLongPtr(hwnd,-20,(nint)(style|0x80000));SetLayeredWindowAttributes(hwnd,0,(byte)(_opacity*255),2);}
 public void SetClickThrough(bool enabled){_clickThrough=enabled;var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(_window);var style=GetWindowLongPtr(hwnd,-20).ToInt64();SetWindowLongPtr(hwnd,-20,(nint)(enabled?style|0x20:style&~0x20));SetOpacity(_opacity);}
 public void Dispose(){_disposed=true;_save.Stop();_window.AppWindow.Changed-=Changed;_backend.EventReceived-=SettingUpdated;}
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
 [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
 [DllImport("user32.dll")]private static extern bool SetLayeredWindowAttributes(nint hwnd,uint color,byte alpha,uint flags);
}

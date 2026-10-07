using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System.Text.Json;
using LeagueAkari.WinUI.Services;
using LeagueAkari.WinUI.Pages;
namespace LeagueAkari.WinUI;
public sealed class MainWindow : Window
{
 private readonly BackendClient _backend=new();
 private readonly NavigationView _navigation=new(){PaneDisplayMode=NavigationViewPaneDisplayMode.Left,IsBackButtonVisible=NavigationViewBackButtonVisible.Collapsed};
 private readonly Grid _host=new();
 private readonly ConnectionPanel _connection;
 private readonly HostNotifications _notifications;
 private readonly ProfileBackground _background;
 private string _activePage="history";
 private readonly TextBlock _status=new(){Text="正在启动后台服务…",Margin=new Thickness(20)};
 private readonly Dictionary<string,UIElement> _pages=new();
 private readonly WindowLayout _layout;
 private readonly NativeAppearance _appearance;
 private readonly IDisposable _appearanceBinding;
 private readonly MainRouteState _route = new();
 private bool _autoRouteWhenGameStarts=true;
 private bool _navigationRestored;
 private bool _applyingNavigation;
 private bool _sidebarCollapsed;
 private bool _quit;
 private bool _closing;
 private readonly NativeShell _shell;
 public MainWindow(){
  _appearance=new(_backend);_background=new(_backend);
  Title="LeagueAkari · WinUI 3";AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1200,820));
  _layout=new(this,_backend,"main-window");
  _notifications=new(_backend,()=>((FrameworkElement)Content).XamlRoot,QuitAsync,()=>{_navigation.SelectedItem=_navigation.SettingsItem;});_notifications.AppTitleChanged+=SetAppTitle;
  _shell=new(WinRT.Interop.WindowNative.GetWindowHandle(this),action=>DispatcherQueue.TryEnqueue(async()=>{try{switch(action){case "main":AppWindow.Show();(AppWindow.Presenter as OverlappedPresenter)?.Restore();Activate();break;case "quit":await QuitAsync();break;case "launch":await _backend.CallAsync("client-installation-main","launchLeagueClient");break;default:await NavigateAsync(action);break;}}catch(Exception ex){_status.Text=ex.Message;}}));
  AppWindow.Closing+=(sender,e)=>{if(_quit)return;e.Cancel=true;if(!_closing)_=RequestCloseAsync();};
  foreach(var (tag,label,glyph) in new[]{("history","我的战绩","\uE9D9"),("ongoing","对局","\uE7FC"),("automation","自动操作","\uE945"),("toolkit","工具集","\uE90F"),("mini","迷你窗口","\uE737")})_navigation.MenuItems.Add(new NavigationViewItem{Content=label,Tag=tag,Icon=new FontIcon{Glyph=glyph}});
  var layout=new Grid();layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_connection=new ConnectionPanel(_backend);layout.Children.Add(_connection);Grid.SetRow(_notifications.Panel,1);layout.Children.Add(_notifications.Panel);var body=new Grid();body.Children.Add(_background.Image);body.Children.Add(_host);Grid.SetRow(body,2);layout.Children.Add(body);_navigation.Content=layout;_host.Children.Add(_status);Content=_navigation;_appearanceBinding=_appearance.Watch(this,_navigation);_appearance.Changed+=()=>{foreach(var item in _navigation.MenuItems.OfType<DependencyObject>())NativeTextBinding.TranslateTree(item);if(_navigation.SettingsItem is DependencyObject settings)NativeTextBinding.TranslateTree(settings);};
  _navigation.PaneOpened+=async(_,_)=>await SaveNavigationAsync(false);_navigation.PaneClosed+=async(_,_)=>await SaveNavigationAsync(true);
  _navigation.SelectionChanged+=async(_,e)=>{if(e.IsSettingsSelected)await NavigateAsync("settings");else if(e.SelectedItem is NavigationViewItem item)await NavigateAsync(item.Tag?.ToString()??"history");};
  AppWindow.Changed+=(_,args)=>{if(args.DidVisibilityChange||args.DidPresenterChange)_notifications.SetVisible(AppWindow.IsVisible&&((AppWindow.Presenter as OverlappedPresenter)?.State!=OverlappedPresenterState.Minimized));};
  _backend.EventReceived+=OnBackendEvent;
  _backend.HostCall=HandleHostCallAsync;
  Closed+=async(_,_)=>{_mini?.Shutdown();_opgg?.Shutdown();foreach(var utility in _utilities.Values)utility.Shutdown();_shell.Dispose();_layout.Dispose();_notifications.AppTitleChanged-=SetAppTitle;_notifications.Dispose();_appearanceBinding.Dispose();_appearance.Dispose();_backend.EventReceived-=OnBackendEvent;try{await _backend.DisposeAsync();}finally{NativeShell.ReleaseInstance();Application.Current.Exit();}};
  _=InitializeAsync();
 }
 private async Task InitializeAsync(){try{await _backend.StartAsync();await _connection.RefreshAsync();await _layout.RestoreAsync();await _appearance.StartAsync();await _notifications.StartAsync();var collapsed=await _backend.CallAsync("setting-factory-main","get","main-window-ui-renderer","sidebarCollapsed");_sidebarCollapsed=collapsed.ValueKind==JsonValueKind.True;_navigation.IsPaneOpen=!_sidebarCollapsed;_navigationRestored=true;_autoRouteWhenGameStarts=(await _backend.CallAsync("setting-factory-main","get","ongoing-game-main","autoRouteWhenGameStarts")).ValueKind!=JsonValueKind.False;var routeStates=await Task.WhenAll(_backend.StateAsync("league-client-main"),_backend.StateAsync("league-client-main","gameflow"),_backend.StateAsync("league-client-main","champSelect"));_route.Initialize(routeStates[0],routeStates[1],routeStates[2]);_navigation.SelectedItem=_navigation.MenuItems[0];await NavigateAsync("history");if(_route.InMatch&&_autoRouteWhenGameStarts)_navigation.SelectedItem=_navigation.MenuItems[1];}catch(Exception ex){_status.Text="启动失败："+ex.Message;}}
 private void SetAppTitle(string title)=>Title=title;
 private async Task SaveNavigationAsync(bool collapsed){if(!_navigationRestored||_applyingNavigation||_sidebarCollapsed==collapsed)return;_sidebarCollapsed=collapsed;await _backend.CallAsync("setting-factory-main","set","main-window-ui-renderer","sidebarCollapsed",collapsed);}
 private async Task NavigateAsync(string page){
  try{
   if(page=="mini"){await ShowMiniAsync();return;}
   if(page=="opgg"){await ShowOpggAsync();return;}
   if(page is "ongoing-window" or "cd-window"){await ShowUtilityAsync(page=="cd-window"?"cd-timer-window":"ongoing-game-window");return;}
   if(!_pages.TryGetValue(page,out var content)){
    content=page switch{"history"=>new HistoryTabsPage(_backend),"ongoing"=>new OngoingPage(_backend,OpenPlayer,CollectMatches),"automation"=>new AutomationPage(_backend),"settings"=>new SettingsPage(_backend,_notifications.ShowReleaseAsync),_=>new ToolkitPage(_backend)};if(content is ToolkitPage toolkit){toolkit.DraftOpened+=()=>_navigation.SelectedItem=_navigation.MenuItems[1];toolkit.PlayerOpened+=OpenPlayer;}if(content is SettingsPage settingsPage)settingsPage.PlayerOpened+=OpenPlayer;_pages[page]=content;
   }
   if(_host.Children.Count==1&&ReferenceEquals(_host.Children[0],content))return;_activePage=page;_background.Image.Visibility=page=="history"?Visibility.Collapsed:_background.Image.Visibility;_host.Children.Clear();_host.Children.Add(content);if(page!="history")_=RefreshSelfBackgroundAsync();
  }catch(Exception ex){_host.Children.Clear();_status.Text=ex.Message;_host.Children.Add(_status);}
 }
 private async void CollectMatches(string puuid,string server,int?champion,string?position){await NavigateAsync("history");_navigation.SelectedItem=_navigation.MenuItems[0];if(_pages["history"] is HistoryTabsPage tabs)await tabs.CollectAsync(puuid,server,champion,position);}
 private async void OpenPlayer(string puuid,string server){await NavigateAsync("history");if(_pages["history"] is HistoryTabsPage tabs)tabs.OpenPlayer(puuid,server);_navigation.SelectedItem=_navigation.MenuItems[0];}
 private async Task RefreshSelfBackgroundAsync(){try{if(!_backend.IsReady||_quit||_activePage=="history")return;var state=await _backend.StateAsync("league-client-main","summoner");string puuid=state.Field("me").Text("puuid");if(puuid.Length==0){_background.Image.Visibility=Visibility.Collapsed;return;}var source=new PlayerDataSource(_backend);await source.ConfigureAsync("","lcu");await _background.RefreshAsync(source,puuid);if(_activePage=="history")_background.Image.Visibility=Visibility.Collapsed;}catch{_background.Image.Visibility=Visibility.Collapsed;}}
 private void OnBackendEvent(JsonElement ev){
  string eventName=ev.Text("name");var backgroundArgs=ev.Field("args").Items().ToArray();string? backgroundKey=backgroundArgs.ElementAtOrDefault(0).ValueKind==JsonValueKind.String?backgroundArgs[0].GetString():null;if(ev.Text("namespace")=="mobx-utils-main"&&(eventName=="update-state-prop/league-client-main:summoner"||eventName=="update-state-prop/main-window-ui-renderer:settings"&&backgroundKey=="useProfileSkinAsBackground"||eventName=="update-state-prop/window-manager-main:settings"&&backgroundKey=="backgroundMaterial"))DispatcherQueue.TryEnqueue(()=>{_=RefreshSelfBackgroundAsync();});
  DispatcherQueue.TryEnqueue(async()=>{try{
   if(ev.Text("namespace")=="mobx-utils-main"&&eventName=="update-state-prop/main-window-ui-renderer:settings"&&backgroundKey=="sidebarCollapsed"){
    bool open=backgroundArgs.ElementAtOrDefault(1).ValueKind!=JsonValueKind.True;
    _sidebarCollapsed=!open;
    if(_navigation.IsPaneOpen!=open){_applyingNavigation=true;try{_navigation.IsPaneOpen=open;}finally{_applyingNavigation=false;}}return;
   }
   if(ev.Text("namespace")=="mobx-utils-main"&&eventName=="update-state-prop/ongoing-game-main:settings"&&backgroundKey=="autoRouteWhenGameStarts"){_autoRouteWhenGameStarts=backgroundArgs.ElementAtOrDefault(1).ValueKind!=JsonValueKind.False;return;}
   if(_route.Apply(ev) is not { } transition)return;
   if(transition.EnteredMatch&&_autoRouteWhenGameStarts)_navigation.SelectedItem=_navigation.MenuItems[1];
   else if(transition.LeftMatch&&_navigation.SelectedItem==_navigation.MenuItems[1])_navigation.SelectedItem=_navigation.MenuItems[0];
   if(transition.GameEnded&&_pages.GetValueOrDefault("history") is HistoryTabsPage tabs){var ongoing=await _backend.StateAsync("ongoing-game-main");await tabs.RefreshAfterGameEndAsync(ongoing.Field("teams"));}
  }catch(Exception ex){_status.Text=ex.Message;}});
 }
 private Windows.MiniWindow? _mini;
 private Windows.OpggWindow? _opgg;
 private readonly Dictionary<string,Windows.UtilityWindow> _utilities=new();
 private async Task<Windows.UtilityWindow> EnsureUtilityAsync(string name){if(!_utilities.TryGetValue(name,out var window)){var settings=await _backend.CallAsync("setting-factory-main","getByPrefix","window-manager-main/"+name,"");window=name=="cd-timer-window"?new Windows.CDTimerWindow(_backend,settings):new Windows.OngoingWindow(_backend,settings,(puuid,server)=>{AppWindow.Show();(AppWindow.Presenter as OverlappedPresenter)?.Restore();Activate();OpenPlayer(puuid,server);});_utilities[name]=window;}return window;}
 private async Task ShowUtilityAsync(string name){(await EnsureUtilityAsync(name)).Show();}
 private async Task ShowOpggAsync(){_opgg??=new Windows.OpggWindow(_backend);_opgg.Show();await Task.CompletedTask;}
 private async Task ShowMiniAsync(){
  await EnsureMiniAsync();_mini!.Show();
 }
 private async Task EnsureMiniAsync(){
  if(_mini is null){
   var wrapped=await _backend.CallAsync("setting-factory-main","getByPrefix","window-manager-main/aux-window","");
   _mini=new Windows.MiniWindow(()=>_backend.CallAsync("winui-backend","miniSnapshot"),(kind,id,value)=>_backend.CallAsync("winui-backend","miniAction",new{kind,id,value}),_backend.ImageAsync,(key,value)=>_backend.CallAsync("setting-factory-main","set","window-manager-main/aux-window",key,value),wrapped);
  }
 }
 private Task<object?> HandleHostCallAsync(JsonElement request){
  var result=new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
  DispatcherQueue.TryEnqueue(async()=>{try{
   string ns=request.Text("namespace"),method=request.Text("method");var args=request.Field("args").Items().ToArray();
   if(ns.StartsWith("window-manager-main/")){result.TrySetResult(await WindowCallAsync(ns.Split('/')[1],method,args));return;}
   if(ns=="host-ui"&&method=="confirm"){result.TrySetResult(await ConfirmAsync(args[0].GetString()!,args[1].GetString()!));return;}
   if(ns=="host-ui"&&method=="fileDialog"){result.TrySetResult(await FileDialogAsync(args[0].GetString()!,args.ElementAtOrDefault(1).GetString()??"settings.json"));return;}
   if(ns=="host-ui"&&method=="relaunchAsAdministrator"){try{NativeShell.ReleaseInstance();var relaunched=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"})??throw new InvalidOperationException("管理员启动未创建进程");relaunched.Dispose();await QuitAsync();result.TrySetResult(null);}catch(Exception ex){if(!_quit&&!NativeShell.AcquireInstance())throw new InvalidOperationException("管理员启动失败，其他实例已接管单实例锁",ex);if(ex is System.ComponentModel.Win32Exception cancelled&&cancelled.NativeErrorCode==1223)throw new InvalidOperationException("已取消管理员启动");throw;}return;}
   if(method is "openExternal" or "openPath"){System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[0].GetString()!){UseShellExecute=true});result.TrySetResult(null);return;}
   if(method=="exit"){await QuitAsync();result.TrySetResult(null);return;}
   if(method=="readClipboardText"){var clipboard=global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();result.TrySetResult(clipboard.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)?await clipboard.GetTextAsync():"");return;}
   throw new NotSupportedException($"宿主操作尚未迁移：{ns}.{method}");
  }catch(Exception ex){result.TrySetException(ex);}});return result.Task;
 }
 private async Task<bool> ConfirmAsync(string title,string text){var dialog=new ContentDialog{Title=title,Content=text,PrimaryButtonText="确定",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,XamlRoot=((FrameworkElement)Content).XamlRoot};return await NativeDialogs.ShowAsync(dialog)==ContentDialogResult.Primary;}
 private async Task<string?> FileDialogAsync(string kind,string name){var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);if(kind=="save"){var picker=new global::Windows.Storage.Pickers.FileSavePicker{SuggestedFileName=Path.GetFileNameWithoutExtension(name)};picker.FileTypeChoices.Add("JSON 设置",new List<string>{".json"});WinRT.Interop.InitializeWithWindow.Initialize(picker,hwnd);return (await picker.PickSaveFileAsync())?.Path;}else{var picker=new global::Windows.Storage.Pickers.FileOpenPicker();picker.FileTypeFilter.Add(".json");WinRT.Interop.InitializeWithWindow.Initialize(picker,hwnd);return (await picker.PickSingleFileAsync())?.Path;}}
 private async Task<object?> WindowCallAsync(string name,string method,JsonElement[] args){
  if(name is "cd-timer-window" or "ongoing-game-window"){if(!_utilities.ContainsKey(name)&&method is "hide" or "close" or "applySettings")return null;return await (await EnsureUtilityAsync(name)).HandleAsync(method,args);}
  if(name=="opgg-window"){if(_opgg is null&&method is "hide" or "close" or "applySettings")return null;_opgg??=new Windows.OpggWindow(_backend);return await _opgg.HandleAsync(method,args);}
  if(name=="aux-window"){if(_mini is null&&method is "hide" or "close" or "applySettings")return null;await EnsureMiniAsync();if(method=="applySettings"){_mini!.ApplySettings(await _backend.CallAsync("setting-factory-main","getByPrefix","window-manager-main/aux-window",""));return null;}return await _mini!.HandleAsync(method,args);}
  if(name!="main-window")throw new NotSupportedException($"窗口功能尚未接入：{name}.{method}");
  var presenter=AppWindow.Presenter as OverlappedPresenter;
  switch(method){
   case "ensure":break;
   case "show":case "restore":AppWindow.Show();presenter?.Restore();Activate();break;
   case "hide":AppWindow.Hide();break;
   case "toggle":if(AppWindow.IsVisible)AppWindow.Hide();else {AppWindow.Show();presenter?.Restore();Activate();}break;
   case "minimize":presenter?.Minimize();break;
   case "maximize":presenter?.Maximize();break;
   case "unmaximize":presenter?.Restore();break;
   case "closeMainWindowForce":await QuitAsync();break;
   case "closeMainWindow":case "close":await RequestCloseAsync(args.ElementAtOrDefault(0).ValueKind==JsonValueKind.String?args[0].GetString():null);break;
   case "setTitle":Title=args[0].GetString()!;break;
   case "getSize":return new[]{AppWindow.Size.Width,AppWindow.Size.Height};
   case "getPosition":return new[]{AppWindow.Position.X,AppWindow.Position.Y};
   case "setSize":AppWindow.Resize(new((int)args[0].TryNumber(),(int)args[1].TryNumber()));break;
   case "setPosition":AppWindow.Move(new((int)args[0].TryNumber(),(int)args[1].TryNumber()));break;
   case "resetPosition":_layout.Center();break;
   case "repositionWindowIfInvisible":_layout.RepositionIfInvisible();break;
   case "setPinned":if(presenter is not null)presenter.IsAlwaysOnTop=args[0].ValueKind==JsonValueKind.True;await _backend.CallAsync("setting-factory-main","set","window-manager-main/main-window","pinned",args[0]);break;
   case "setOpacity":_layout.SetOpacity(args[0].TryNumber());await _backend.CallAsync("setting-factory-main","set","window-manager-main/main-window","opacity",args[0]);break;
   case "setIgnoreMouseEvents":_layout.SetClickThrough(args[0].ValueKind==JsonValueKind.True);break;
   case "applySettings":await _layout.RestoreAsync();break;
   default:throw new NotSupportedException($"原生窗口不支持此操作：{name}.{method}");
  }return null;
 }
 private async Task RequestCloseAsync(string? requestedAction=null)
 {
  if(_closing)return;
  _closing=true;
  try
  {
   var storedAction=await _backend.CallAsync("setting-factory-main","get","window-manager-main/main-window","closeAction");
   string? action=requestedAction??storedAction.GetString();
   if(action=="ask")
   {
    AppWindow.Show();(AppWindow.Presenter as OverlappedPresenter)?.Restore();Activate();
    var tray=new RadioButton{Content=Localization.Key("window.closeConfirm.options.minimize-to-tray"),IsChecked=true,GroupName="close-action"};
    var quit=new RadioButton{Content=Localization.Key("window.closeConfirm.options.quit"),GroupName="close-action"};
    var remember=new CheckBox{Content=Localization.Key("window.closeConfirm.remember"),Margin=new Thickness(0,12,0,0)};
    var options=new StackPanel{Spacing=6};options.Children.Add(tray);options.Children.Add(quit);options.Children.Add(remember);
    var dialog=new ContentDialog{Title=Localization.Key("window.closeConfirm.title"),Content=options,PrimaryButtonText=Localization.Key("window.closeConfirm.ok"),CloseButtonText=Localization.Key("window.closeConfirm.cancel"),DefaultButton=ContentDialogButton.Primary,XamlRoot=((FrameworkElement)Content).XamlRoot};
    if(await NativeDialogs.ShowAsync(dialog)!=ContentDialogResult.Primary)return;
    action=quit.IsChecked==true?"quit":"minimize-to-tray";
    if(remember.IsChecked==true)await _backend.CallAsync("setting-factory-main","set","window-manager-main/main-window","closeAction",action);
   }
   if(action=="minimize-to-tray"){AppWindow.Hide();return;}
   await QuitAsync();
  }
  catch(Exception ex){_status.Text=ex.Message;}
  finally{_closing=false;}
 }
 private async Task QuitAsync(){if(_quit)return;await _layout.PersistAsync();if(_mini is not null)await _mini.PersistAsync();if(_opgg is not null)await _opgg.PersistAsync();foreach(var utility in _utilities.Values)await utility.PersistAsync();_quit=true;_mini?.Shutdown();_opgg?.Shutdown();foreach(var utility in _utilities.Values)utility.Shutdown();Close();}
}



using Microsoft.UI.Xaml;
using LeagueAkari.WinUI.Services;
namespace LeagueAkari.WinUI;
public partial class App : Application
{
 private MainWindow? _main;
 public App(){ InitializeComponent(); UnhandledException += (_,e) => { File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"winui-error.log"),e.Exception+Environment.NewLine); }; }
 protected override void OnLaunched(LaunchActivatedEventArgs args){if(!NativeShell.AcquireInstance()){Exit();return;}_main=new MainWindow();_main.Activate(); }
}

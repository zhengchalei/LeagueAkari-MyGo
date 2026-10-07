using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
namespace LeagueAkari.WinUI.Services;
public sealed class GameAssets
{
 private readonly BackendClient _backend;private readonly NativeImages _images;private readonly Dictionary<string,Task<Dictionary<int,JsonElement>>> _catalogs=new();
 public GameAssets(BackendClient backend){_backend=backend;_images=new(backend);}
 public async Task<JsonElement> EntryAsync(string kind,int id){if(!_catalogs.TryGetValue(kind,out var task)){task=ReadAsync(kind);_catalogs[kind]=task;}try{return (await task).GetValueOrDefault(id);}catch{_catalogs.Remove(kind);return default;}}
 private async Task<Dictionary<int,JsonElement>> ReadAsync(string kind){var value=await _backend.CallAsync("winui-backend","lcuRequest","GET","/lol-game-data/assets/v1/"+kind+".json",null);return value.Items().Where(e=>e.Number("id")>0).GroupBy(e=>(int)e.Number("id")).ToDictionary(g=>g.Key,g=>g.First());}
 public Image Icon(string kind,int id,int size=28){var image=new Image{Width=size,Height=size};if(id>0)_=FillAsync(image,kind,id);return image;}
 private async Task FillAsync(Image image,string kind,int id){if(kind=="profile-icons"){await _images.SetAsync(image,$"/lol-game-data/assets/v1/profile-icons/{id}.jpg");return;}var entry=await EntryAsync(kind,id);var path=entry.Text("iconPath",entry.Text("squarePortraitPath"));if(kind=="champion-summary")path=$"/lol-game-data/assets/v1/champion-icons/{id}.png";if(path.Length>0)await _images.SetAsync(image,path);string text=entry.Text("name",id.ToString());if(entry.Text("description") is {Length:>0} description)text+="\n"+System.Text.RegularExpressions.Regex.Replace(description,"<[^>]*>","");ToolTipService.SetToolTip(image,text);}
 public StackPanel IconRow(string kind,IEnumerable<int> ids,int size=24){var row=new StackPanel{Orientation=Microsoft.UI.Xaml.Controls.Orientation.Horizontal,Spacing=3};foreach(int id in ids)row.Children.Add(Icon(kind,id,size));return row;}
}

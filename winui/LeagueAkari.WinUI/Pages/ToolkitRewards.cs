using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel Rewards() { var panel = Panel(); panel.Children.Add(new RewardClaimToolsView(_backend)); return panel; }
    private StackPanel Loot()
    {
        var p = Panel(); var list = new ListView { MaxHeight = 520 }; var search = new TextBox { Header = "搜索战利品" }; var loot = new Dictionary<string, JsonElement>();
        void Render() { list.Items.Clear(); foreach (var item in loot.Values.Where(l => l.Text("itemDesc").Contains(search.Text, StringComparison.OrdinalIgnoreCase) || l.Text("lootName").Contains(search.Text, StringComparison.OrdinalIgnoreCase))) list.Items.Add(new TextBlock { Text = $"{item.Text("itemDesc", item.Text("lootName"))} × {(int)item.Number("count")} · {item.Text("type")}" }); }
        p.Children.Add(Action("刷新战利品", async () => { var data = await Lcu("GET", "/lol-loot/v1/player-loot-map"); loot = data.EnumerateObject().ToDictionary(l => l.Name, l => l.Value); Render(); })); p.Children.Add(search); search.TextChanged += (_, _) => Render(); p.Children.Add(list);
        return p;
    }
}

using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(string label, bool condition) { if (!condition) throw new Exception(label); passed++; }
object Style(int id, int[] allowed, int offset) => new { id, allowedSubStyles = allowed, slots = new[] {
    new { type="kKeyStone",perks=new[]{offset+1,offset+2}},new {type="kMixedRegularSplashable",perks=new[]{offset+3,offset+4}},
    new {type="kMixedRegularSplashable",perks=new[]{offset+5,offset+6}},new {type="kMixedRegularSplashable",perks=new[]{offset+7,offset+8}},
    new {type="kStatMod",perks=new[]{5005,5008}},new {type="kStatMod",perks=new[]{5008,5002}},new {type="kStatMod",perks=new[]{5002,5001}}}};
var catalog = JsonSerializer.SerializeToElement(new { schemaVersion=2, styles=new Dictionary<string,object> { ["8000"]=Style(8000,[8100],100),["8100"]=Style(8100,[8000],200) } });
var saved = new RuneConfig(8000,8100,[101,103,105,107,203,205,5005,5008,5002]);
var draft = new ChampionConfigDraft(catalog,saved,new(4,14));
Check("Nine-slot saved page is initially valid and unchanged",draft.ValidRunes()&&!draft.RunesChanged);
draft.SelectSecondaryPerk(0,204);draft.SelectSecondaryPerk(2,207);
Check("Changing an existing secondary row updates its recency before selecting a third row",draft.Runes!.SelectedPerkIds.Skip(4).Take(2).SequenceEqual([204,207]));
draft.RestoreRunes();draft.SelectSecondaryPerk(2,207);
Check("Restoring resets secondary history from saved ordered selections",draft.Runes!.SelectedPerkIds.Skip(4).Take(2).SequenceEqual([205,207]));
draft.RestoreRunes();draft.SelectSecondaryPerk(0,203);draft.SelectSecondaryPerk(2,207);
Check("Clicking already selected perk leaves original replacement history unchanged",draft.Runes!.SelectedPerkIds.Skip(4).Take(2).SequenceEqual([205,207]));
draft.RestoreRunes();draft.SelectSecondaryPerk(0,204);draft.SelectPrimary(8000);draft.SelectSecondary(8100);draft.SelectSecondaryPerk(2,207);
Check("Reselecting same styles retains secondary edit recency",draft.Runes!.SelectedPerkIds.Skip(4).Take(2).SequenceEqual([204,207]));
draft.SelectPrimaryPerk(1,101);Check("Rune from another primary row is rejected",draft.Runes!.SelectedPerkIds[1]==103);
var secondaryBeforeShard=draft.Runes!.SelectedPerkIds.Skip(4).Take(2).ToArray();
draft.SelectPrimaryPerk(6,5008);Check("Stat shard writes slot six without modifying secondary perks",draft.Runes!.SelectedPerkIds[6]==5008&&draft.Runes.SelectedPerkIds.Skip(4).Take(2).SequenceEqual(secondaryBeforeShard));
draft.SelectSecondary(8000);Check("Primary style is not accepted as its own secondary",draft.Runes!.SubStyleId==8100);
var invalid = new ChampionConfigDraft(catalog,new(0,0,[]),null);
Check("Missing primary style initializes first catalog style and nine slots like mounted original editor",invalid.Runes!.PrimaryStyleId==8000&&invalid.Runes.SubStyleId==8100&&invalid.Runes.SelectedPerkIds.Length==9&&!invalid.ValidRunes());
var schema = new ChampionConfigDraft(JsonSerializer.SerializeToElement(new { schemaVersion=1 }),saved,null);
schema.ClearRunes();Check("Unsupported schema still permits removing saved configuration",schema.ValidRunes()&&schema.RunesChanged&&schema.RunePayload is null);
draft.NormalizeSpells([6,4]);Check("Mode transition replaces unavailable spell but preserves available slot",draft.Spells==new SpellConfig(4,6)&&draft.ValidSpells([6,4]));
draft.RestoreSpells();draft.NormalizeSpells([6,4]);draft.RestoreSpells();Check("Normalizing draft never overwrites saved restore baseline",draft.Spells==new SpellConfig(4,14));
draft.SelectSpell(true,14,[4,14]);Check("Selecting other slot swaps distinct spells",draft.Spells==new SpellConfig(14,4));
draft.NormalizeSpells([4]);Check("One available spell cannot save a duplicate pair",!draft.ValidSpells([4]));
draft.ClearSpells();Check("Clearing spells is a local null payload",draft.SpellPayload is null&&draft.SpellsChanged);
var order = ChampionConfigEditorData.Sort([new(3001,"PVE",true,true),new(1,"A",true,false),new(2,"B",true,true),new(3,"C",false,true),new(4,"D",false,false)]);
Check("Original configured-both, rune, spell, empty order precedes all PVE",order.Select(c=>c.Id).SequenceEqual([2,1,3,4,3001]));
Check("Six original modes retain their spell catalog game mode",ChampionConfigEditorData.Modes.Length==6&&ChampionConfigEditorData.GameMode("ranked")=="CLASSIC"&&ChampionConfigEditorData.GameMode("aram")=="ARAM"&&ChampionConfigEditorData.GameMode("ultbook")=="ULTBOOK");
var writes = new List<(string Namespace,string Method,object?[] Args)>();
var pending = new TaskCompletionSource<JsonElement>(); bool fail=false;
var writer = new ChampionConfigWriter((ns,method,args)=>{writes.Add((ns,method,args));return fail?Task.FromException<JsonElement>(new InvalidOperationException("Save rejected")):pending.Task;});
draft.RestoreRunes();draft.SelectPrimaryPerk(1,104);draft.RestoreSpells();
var save = writer.SaveAsync(147,"ranked","middle",true,draft,[4,14]);
Check("One configuration write disables additional save dispatch",writer.IsWriting&&!await writer.SaveAsync(147,"ranked","middle",false,draft,[4,14])&&writes.Count==1);
var payload = (JsonElement)writes[0].Args[2]!;
draft.SelectPrimaryPerk(1,103);
Check("In-flight save snapshots exact nine-field rune payload",payload.Number("primaryStyleId")==8000&&payload.Number("subStyleId")==8100&&payload.Field("selectedPerkIds").Items().Count()==9&&payload.Field("selectedPerkIds").Items().ElementAt(1).TryNumber()==104);
Check("Ranked contract is original updateRunes champion/key/payload",writes[0].Namespace=="auto-champ-config-main"&&writes[0].Method=="updateRunes"&&(int)writes[0].Args[0]! ==147&&writes[0].Args[1]?.ToString()=="ranked-middle");
pending.SetResult(default);Check("Completed write releases save serialization",await save&&!writer.IsWriting);
draft.ClearSpells();pending=new();var remove=writer.SaveAsync(147,"aram","middle",false,draft,[4,14]);
Check("Mode-only deletion request uses null and original updateSummonerSpells",writes[^1].Method=="updateSummonerSpells"&&writes[^1].Args[1]?.ToString()=="aram"&&writes[^1].Args[2] is null);
pending.SetResult(default);await remove;
fail=true;draft.RestoreSpells();draft.SelectSpell(true,14,[4,14]);
try { await writer.SaveAsync(147,"normal","default",false,draft,[4,14]);Check("Failure surfaces for retry",false); } catch(InvalidOperationException ex) { Check("Failure retains unsaved draft and releases writing gate",ex.Message=="Save rejected"&&!writer.IsWriting&&draft.SpellsChanged&&draft.Spells==new SpellConfig(14,4)); }
draft.RestoreSpells();Check("Restore after failure cancels local edit without another write",!draft.SpellsChanged&&writes.Count==3);
Console.WriteLine($"Champion config business contracts: {passed} passed");

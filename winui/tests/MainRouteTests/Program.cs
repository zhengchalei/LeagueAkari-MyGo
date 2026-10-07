using System.Text.Json;
using LeagueAkari.WinUI.Services;

JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Event(string section, string key, object? value, string ns = "mobx-utils-main") => J(new { @namespace = ns, name = "update-state-prop/league-client-main:" + section, args = new object?[] { key, value } });
int count = 0;
void Check(bool value, string scenario) { if (!value) throw new Exception(scenario); count++; }
var route = new MainRouteState();
Check(!route.Initialize(J(new { connectionState = "connected" }), J(new { phase = "Lobby" }), J(new { session = (object?)null })).EnteredMatch, "Startup lobby remains history");
Check(route.Apply(Event("champSelect", "session", new { isSpectating = false }))?.EnteredMatch == true, "Session arriving before phase enters match");
foreach (var phase in new[] { "ChampSelect", "GameStart", "InProgress", "Reconnect", "WaitingForStats" })
    Check(route.Apply(Event("gameflow", "phase", phase)) is { EnteredMatch: false, LeftMatch: false, GameEnded: false }, "Manual history navigation survives " + phase);
Check(route.Apply(Event("gameflow", "phase", "PreEndOfGame")) is { GameEnded: true, LeftMatch: false }, "Refresh begins at PreEndOfGame");
Check(route.Apply(Event("gameflow", "phase", "EndOfGame")) is { GameEnded: false, LeftMatch: false }, "No second end refresh while selection session is retained");
Check(route.Apply(Event("champSelect", "session", null))?.LeftMatch == true, "Clearing final session leaves match");
Check(route.Apply(Event("gameflow", "phase", "Lobby"))?.LeftMatch == false, "No duplicate leave");
Check(route.Apply(Event("champSelect", "session", new { isSpectating = true }))?.EnteredMatch == false, "Spectating session alone does not route");
Check(route.Apply(Event("gameflow", "phase", "InProgress"))?.EnteredMatch == true, "Original phase rule includes spectating InProgress");
Check(route.Apply(Event("state", "connectionState", "disconnected"))?.LeftMatch == true, "Disconnect leaves despite stale game phase");
Check(route.Apply(Event("state", "connectionState", "connected"))?.EnteredMatch == true, "Reconnect to active match routes once");
Check(route.Apply(Event("gameflow", "phase", "Lobby", "unrelated")) == null && route.InMatch, "Ignore foreign namespace");
Check(route.Apply(Event("gameflow", "session", new { })) == null && route.InMatch, "Ignore unrelated gameflow property");
var initial = new MainRouteState();
Check(initial.Initialize(J(new { connectionState = "connected" }), J(new { phase = "InProgress" }), J(new { })).EnteredMatch, "Startup in match");
Check(!new MainRouteState().Initialize(J(new { connectionState = "disconnected" }), J(new { phase = "InProgress" }), J(new { session = new { isSpectating = false } })).EnteredMatch, "Disconnected startup stays history");
Console.WriteLine($"{count} original navigation lifecycle fixtures passed.");

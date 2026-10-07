using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(string name, bool result) { if (!result) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
JsonElement Availability(bool endpoint, bool token) => JsonSerializer.SerializeToElement(new { availability = new { sgpServerId = "TENCENT_HN1", serversSupported = new { matchHistory = endpoint } }, isTokenReady = token, leagueServers = new { servers = new Dictionary<string, object> { ["TENCENT_HN1"] = new { matchHistory = endpoint ? "https://fixture.invalid" : "" } } } });
var calls = new List<(string Method, object?[] Args)>();
Task<JsonElement> Request(string ns, string method, object?[] args) { calls.Add((method, args)); return Task.FromResult(JsonSerializer.SerializeToElement(method == "sgpRequest" ? new { json = (object)new { gameId = 123 } } : (object)new { gameId = 123 })); }
var source = new PlayerDataSource(Request, (_, _) => Task.FromResult(Availability(true, true)));
string preference = "sgp";
var service = new GameLookupData(source, () => Task.FromResult(preference));
var result = await service.LoadAsync(123);
Check("Preferred SGP resolves current server", result.Source == "sgp" && result.Server == "TENCENT_HN1");
Check("SGP summary request uses entitlements and ID", calls[^1].Method == "sgpRequest" && calls[^1].Args[1]?.ToString() == "entitlements" && calls[^1].Args[3]?.ToString() == "/match-history-query/v1/products/lol/@akari:sgpServerSubId@_123/SUMMARY");
preference = "lcu"; result = await service.LoadAsync(123);
Check("Read latest preference on each inspection", result.Source == "lcu" && calls[^1].Method == "lcuRequest");
Check("LCU summary endpoint preserves ID", calls[^1].Args[1]?.ToString() == "/lol-match-history/v1/games/123");
var fallback = new GameLookupData(new(Request, (_, _) => Task.FromResult(Availability(false, true))), () => Task.FromResult("sgp"));
result = await fallback.LoadAsync(123); Check("Unavailable SGP falls back to LCU like ConnectedMatchPreviewer", result.Source == "lcu" && result.FallbackReason == "sgp-api-unavailable");
var waiting = new GameLookupData(new(Request, (_, _) => Task.FromResult(Availability(true, false))), () => Task.FromResult("sgp"));
try { await waiting.LoadAsync(123); Check("Waiting token reports unavailable", false); } catch (InvalidOperationException) { passed++; }
var wrong = new GameLookupData(new((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { gameId = 456 })), (_, _) => Task.FromResult(Availability(false, true))), () => Task.FromResult("lcu"));
try { await wrong.LoadAsync(123); Check("Mismatched returned game rejected", false); } catch (InvalidOperationException) { passed++; }
foreach (double id in new[] { double.NaN, double.PositiveInfinity, 0, -1, 1.5, 9007199254740992 }) Check("Invalid game ID rejected " + id, !GameLookupData.ValidGameId(id));
Check("Positive integer valid", GameLookupData.ValidGameId(123));
Console.WriteLine($"Game lookup contracts: {passed} passed");

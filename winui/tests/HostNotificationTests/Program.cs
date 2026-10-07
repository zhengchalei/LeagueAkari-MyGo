using LeagueAkari.WinUI.Services;
int passed = 0;
void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": " + actual); passed++; }
long now = 1800000000000; long threeDays = 3 * 24 * 60 * 60 * 1000L;
Equal(true, HostNotificationRules.ShowStreamingHint(false, true, false, false, 0, now), "Client HidePlayerNames alone prompts");
Equal(false, HostNotificationRules.ShowStreamingHint(true, false, false, false, now - threeDays + 1, now), "Dismissal suppresses until exact three-day boundary");
Equal(true, HostNotificationRules.ShowStreamingHint(true, false, false, false, now - threeDays, now), "Boundary permits reminder again");
Equal(false, HostNotificationRules.ShowStreamingHint(true, true, true, false, 0, now), "Already private streamer mode suppresses");
Equal(false, HostNotificationRules.ShowStreamingHint(true, true, false, true, 0, now), "Never show dominates either detection source");
Equal("1h 1m", HostNotificationRules.FormatSeconds(3661), "Login queue does not wrap an hour to one minute");
Equal("1day 2h", HostNotificationRules.FormatSeconds(93600), "Queue preserves days");
Equal("1m 1s", HostNotificationRules.FormatSeconds(61), "Short queue matches original format");
var combo = new NotificationKeyCombo("AKARI", 250);
foreach (char ch in "aKAr") Equal(false, combo.Push(ch, 100), "Partial combo is case insensitive"); Equal(true, combo.Push('i', 101), "Full combo triggers");
combo.Push('A', 1000); combo.Push('K', 1100); combo.Push('A', 1400); combo.Push('R', 1401); Equal(false, combo.Push('I', 1402), "Expired prefix cannot complete");
foreach (char ch in "XAKAR") combo.Push(ch, 1500); Equal(true, combo.Push('I', 1501), "Mismatch restarts from matching first character");
var credit = new FunnyPricingCredit(); credit.Open(); for (int i = 0; i < 29; i++) credit.TopUp(); Equal(29000d, credit.Balance, "Initial one-point top ups reach29");
Equal(50d, credit.TopUp(), "Next tier contributes .05 points"); for (int i = 0; i < 17; i++) credit.TopUp(); Equal(29900d, credit.Balance, "Second tier reaches29.9");
for (int i = 0; i < 50; i++) credit.TopUp(); Equal(29950d, credit.Balance, "Third tier reaches29.95"); Equal(.5, credit.TopUp(), "Moving-button tier contributes .0005 points");
Equal(false, credit.Buy("pro", 30000), "Insufficient balance does not purchase"); credit.Gift(); Equal(true, credit.Buy("pro", 30000), "Gift combo permits purchase"); Equal("pro", credit.Current, "Current plan tracks purchase");
credit.Open(); Equal(0d, credit.Balance, "Reopening resets credit"); Equal("pro", credit.Current, "Reopening preserves plan"); Equal(false, credit.Buy("pro", 30000), "Current plan is not charged again");
Console.WriteLine($"Notification dismissal, queue formatting, combos and pricing: {passed} passed");
var blocks = NoticeMarkdownData.Parse("# Release\n\n| Feature | Status |\n| --- | --- |\n| Native | **Ready** |\n\n> Detail\n\n```cs\nif (value < limit) return;\n```\n\n![Preview](https://example.test/preview.png)");
Equal("heading,table,quote,code,paragraph", string.Join(',', blocks.Select(block => block.Kind)), "Release markdown preserves semantic blocks");
Equal("**Ready**", blocks[1].Cells![1][1], "Table inline style retained");
Equal("if (value < limit) return;", blocks[3].Text, "Code text retains comparison operator");
Equal("List<T> value = new();", NoticeMarkdownData.Parse("```cs\nList<T> value = new();\n```")[0].Text, "HTML processing preserves generic types in code fences");
var html = NoticeMarkdownData.Parse("<h2>Notice &amp; terms</h2><p><strong>Free</strong> <a href='https://example.test'>Read more</a></p><table><tr><th>Mode</th><th>Value</th></tr><tr><td>ARAM</td><td>+10%</td></tr></table><img alt='preview' src='https://example.test/a.png'><script>bad()</script>");
Equal("Notice & terms", html[0].Text, "HTML heading readable with decoded entities");
Equal(true, html.Any(block => block.Text.Contains("**Free**") && block.Text.Contains("[Read more](https://example.test)")), "HTML emphasis and links retain meaning");
Equal("+10%", html.First(block => block.Kind == "table").Cells![1][1], "HTML table converted to native cells");
Equal(false, html.Any(block => block.Text.Contains("<p>") || block.Text.Contains("bad()")), "Raw tags do not leak into notice text");
Console.WriteLine($"Notice documents and notification contracts: {passed} passed");
foreach (string priority in new[] { "low", "medium" })
{
    var announcement = new AnnouncementReadState("notice-1", priority, "older");
    Equal("announcements.modal.read", announcement.PrimaryButtonKey, "Unread before actual opening");
    Equal(true, announcement.Open(), "Opening automatically records " + priority + " read");
    Equal("announcements.modal.close", announcement.PrimaryButtonKey, "Opening immediately changes read action to close");
    Equal(false, announcement.MarkRead(), "Close primary does not persist duplicate read");
}
var high = new AnnouncementReadState("high-1", "high", "older"); Equal(false, high.Open(), "High priority remains unread on open"); Equal("announcements.modal.read", high.PrimaryButtonKey, "High priority keeps read button"); Equal(true, high.MarkRead(), "High priority primary explicitly marks read"); Equal("announcements.modal.close", high.PrimaryButtonKey, "Read high announcement becomes close");
var existing = new AnnouncementReadState("notice-1", "high", "notice-1"); Equal("announcements.modal.close", existing.PrimaryButtonKey, "Reopened acknowledged announcement closes"); Equal(false, existing.MarkRead(), "No duplicate persisted write on reopened announcement");
Console.WriteLine($"All notification contracts including announcement read transitions: {passed} passed");

package nativemini

import (
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"

	"github.com/egoist/mygo/ui"
)

func viewFixture() Snapshot {
	return Snapshot{
		Connected: true, Phase: "FINALIZATION", ChampionID: 1, ChampionName: "安妮", IconPath: "/annie.png", ShowSkins: true,
		Choices:      []ChampionChoice{{ID: 1, Name: "安妮", IconPath: "/annie.png", Selected: true, Enabled: true}, {ID: 2, Name: "奥拉夫", IconPath: "/olaf.png", Enabled: true}},
		Balance:      []BalanceRow{{Type: "damage-taken", Name: "承受伤害", Value: "−10%", Original: "90%", Effect: "buffed"}, {Type: "damage-dealt", Name: "造成伤害", Value: "−5%", Original: "95%", Effect: "nerfed"}},
		BalanceKnown: true, Source: "RESG", Version: "16.19", CanSelectSkin: true,
		Skins: []SkinChoice{{ID: 1000, Name: "原始皮肤", ImagePath: "/annie.png", Enabled: true, Selected: true}, {ID: 1001, Name: "哥特萝莉", ImagePath: "/goth.png", Enabled: true}},
	}
}

func TestNativeViewChoicesDispatchWithoutOptimisticSelection(t *testing.T) {
	s := viewFixture()
	var actions []Action
	v := NewView(func() Snapshot { return s }, nil, func(action Action) { actions = append(actions, action) })
	tester := ui.NewTester(v.Draw, 340, 620)
	if !tester.HasText("−10%") || !tester.HasText("−5%") || !tester.HasText("已有皮肤 (2)") {
		t.Fatalf("missing persistent balance / skin content: %v", tester.Texts())
	}
	if err := tester.Click("奥拉夫"); err != nil {
		t.Fatal(err)
	}
	if err := tester.Click("哥特萝莉"); err != nil {
		t.Fatal(err)
	}
	if len(actions) != 2 || actions[0].Kind != "champion" || actions[0].ID != 2 || actions[1].Kind != "skin" || actions[1].ID != 1001 {
		t.Fatalf("unexpected click dispatch: %+v", actions)
	}
	if !s.Skins[0].Selected || s.Skins[1].Selected {
		t.Fatal("view changed selection without a client response")
	}
	s.Skins[1].Enabled = false
	tester.Frame()
	if err := tester.Click("哥特萝莉"); err != nil {
		t.Fatal(err)
	}
	if len(actions) != 2 {
		t.Fatal("disabled skin dispatched a write")
	}
	s.Choices[1].Enabled = false
	tester.Frame()
	if err := tester.Click("奥拉夫"); err != nil {
		t.Fatal(err)
	}
	if len(actions) != 2 {
		t.Fatal("disabled champion dispatched a write")
	}
}

func TestNativeViewEnglishAndBalancePolarity(t *testing.T) {
	s := viewFixture()
	v := NewView(func() Snapshot { return s }, nil, nil)
	v.Controls = func() Controls { return Controls{Locale: "en", Theme: "dark"} }
	tester := ui.NewTester(v.Draw, 340, 620)
	if !tester.HasText("Damage taken") || !tester.HasText("Damage dealt") || !tester.HasText("Owned skins (2)") {
		t.Fatalf("missing English native labels: %v", tester.Texts())
	}
	// Both numbers are negative, but taking less damage is a green buff.
	// Check actual native-rendered text colors, not just the model's effect.
	for _, check := range []struct {
		text  string
		green bool
	}{{"−10%", true}, {"−5%", false}} {
		r, ok := tester.Find(check.text)
		if !ok {
			t.Fatalf("missing value %s", check.text)
		}
		found := false
		for y := int(r.Y); y < int(r.Y+r.H); y++ {
			for x := int(r.X); x < int(r.X+r.W); x++ {
				p := tester.Image().RGBAAt(x, y)
				if check.green && int(p.G) > int(p.R)+50 || !check.green && int(p.R) > int(p.G)+50 {
					found = true
				}
			}
		}
		if !found {
			t.Fatalf("wrong rendered polarity color for %s", check.text)
		}
	}
}

func TestNativeViewRerollAndPauseControls(t *testing.T) {
	s := viewFixture()
	s.Rerolls, s.CanReroll = 2, true
	var actions []Action
	v := NewView(func() Snapshot { return s }, nil, func(action Action) { actions = append(actions, action) })
	v.Controls = func() Controls { return Controls{CanDodge: true} }
	tester := ui.NewTester(v.Draw, 340, 760)
	for _, label := range []string{"重随机 (2)", "重随机并取回", "disable-auto", "立即秒退"} {
		if err := tester.Click(label); err != nil {
			t.Fatal(err)
		}
	}
	if len(actions) != 4 || actions[0].Kind != "reroll" || actions[1].Kind != "reroll-grab-back" || actions[2].Kind != "disable-auto" || !actions[2].Value || actions[3].Kind != "dodge" {
		t.Fatalf("wrong native ancillary actions: %+v", actions)
	}
}

func TestNativeViewLoungeControlsAndPin(t *testing.T) {
	s := Snapshot{Connected: true, Phase: "ReadyCheck"}
	controls := Controls{CanAccept: true, CanCancelAutoAccept: true, CanCancelAutoMatchmaking: true}
	var actions []Action
	v := NewView(func() Snapshot { return s }, nil, func(action Action) { actions = append(actions, action) })
	v.Controls = func() Controls { return controls }
	tester := ui.NewTester(v.Draw, 340, 620)
	for _, label := range []string{"置顶", "接受对局", "拒绝", "取消自动接受", "取消自动匹配"} {
		if err := tester.Click(label); err != nil {
			t.Fatal(err)
		}
	}
	want := []string{"pin", "accept", "decline", "cancel-auto-accept", "cancel-auto-matchmaking"}
	for i, action := range actions {
		if action.Kind != want[i] {
			t.Fatalf("action %d: %+v", i, action)
		}
	}
	if len(actions) != len(want) || !actions[0].Value {
		t.Fatalf("wrong controls: %+v", actions)
	}
	s.Connected = false
	tester.Frame()
	if tester.HasText("接受对局") || !tester.HasText("等待英雄联盟客户端连接") {
		t.Fatal("disconnected view retained stale queue controls")
	}
}

func TestNativeViewSkinsHiddenAndScrollableAtNarrowWidth(t *testing.T) {
	s := viewFixture()
	s.ShowSkins = false
	v := NewView(func() Snapshot { return s }, nil, nil)
	tester := ui.NewTester(v.Draw, 340, 420)
	if tester.HasText("已有皮肤") {
		t.Fatal("hidden skin setting ignored")
	}
	s.ShowSkins = true
	for i := 0; i < 30; i++ {
		s.Skins = append(s.Skins, SkinChoice{ID: 2000 + i, Name: "测试皮肤", Enabled: true})
	}
	tester.Frame()
	tester.Scroll(180, 370, 0, 500)
	tester.SetDark(true)
	if tester.Image().Bounds().Dx() != 340 {
		t.Fatal("narrow native surface size changed")
	}
}

func TestNativeViewVirtualSkinsOnlyLoadVisibleImagesAndSelectLast(t *testing.T) {
	s := viewFixture()
	s.Skins = nil
	for i := 0; i < 180; i++ {
		s.Skins = append(s.Skins, SkinChoice{ID: 1000 + i, Name: fmt.Sprintf("皮肤 %d", i), ImagePath: fmt.Sprintf("/skin-%d.png", i), Enabled: true})
	}
	requested := map[string]bool{}
	var actions []Action
	v := NewView(func() Snapshot { return s }, func(path string) *ui.Bitmap {
		requested[path] = true
		return nil
	}, func(action Action) { actions = append(actions, action) })
	tester := ui.NewTester(v.Draw, 340, 620)
	// First-frame layout measures the viewport; subsequent frames construct
	// only its visible rows, including the small list overscan.
	if len(requested) > 96 {
		t.Fatalf("virtual grid loaded %d assets before scrolling", len(requested))
	}
	clear(requested)
	tester.Frame()
	if len(requested) > 24 {
		t.Fatalf("settled virtual grid requested %d assets", len(requested))
	}
	if tester.HasText("皮肤 179") {
		t.Fatal("last item built before scrolling")
	}
	r, ok := tester.Find("skin-grid")
	if !ok || r.H > 228 {
		t.Fatalf("skin viewport missing or too tall: %+v", r)
	}
	tester.Scroll(r.X+r.W/2, r.Y+r.H/2, 0, 100000)
	if !tester.HasText("皮肤 179") {
		t.Fatalf("last item missing after scroll: %v", tester.Texts())
	}
	if err := tester.Click("皮肤 179"); err != nil {
		t.Fatal(err)
	}
	if len(actions) != 1 || actions[0].Kind != "skin" || actions[0].ID != 1179 {
		t.Fatalf("last virtual skin dispatch wrong: %+v", actions)
	}
	if len(requested) > 45 {
		t.Fatalf("scroll requested non-visible skin assets: %d", len(requested))
	}
	// Switching heroes resets the old scrolled position, making a new hero's
	// first skins immediately accessible rather than showing an empty viewport.
	s.ChampionID = 2
	s.Skins = []SkinChoice{{ID: 2000, Name: "新英雄原始皮肤", Enabled: true}}
	tester.Frame()
	if !tester.HasText("新英雄原始皮肤") {
		t.Fatal("new hero retained previous virtual skin scroll position")
	}
}

// Set NATIVE_MINI_SCREENSHOTS to an evidence directory to keep software-rendered
// frames of the real native view. These fixtures never access or modify the LCU.
func TestNativeViewScreenshotHarness(t *testing.T) {
	dir := os.Getenv("NATIVE_MINI_SCREENSHOTS")
	if dir == "" {
		t.Skip("optional visual evidence")
	}
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	s := viewFixture()
	s.Choices = append(s.Choices, ChampionChoice{ID: 3, Name: "卡莎", Enabled: true}, ChampionChoice{ID: 4, Name: "烬", Enabled: true}, ChampionChoice{ID: 5, Name: "提莫", Enabled: true})
	// Colored fixture images verify the native image path; live images are loaded
	// separately by the desktop asset cache.
	asset := func(path string) *ui.Bitmap {
		img := image.NewRGBA(image.Rect(0, 0, 80, 80))
		for y := 0; y < 80; y++ {
			for x := 0; x < 80; x++ {
				img.SetRGBA(x, y, color.RGBA{uint8(60 + x), uint8(70 + y), 130, 255})
			}
		}
		return ui.NewBitmap(img)
	}
	v := NewView(func() Snapshot { return s }, asset, nil)
	v.Controls = func() Controls { return Controls{CanDodge: true, Pinned: true} }
	tester := ui.NewTester(v.Draw, 340, 620)
	for _, theme := range []string{"light", "dark"} {
		tester.SetDark(theme == "dark")
		f, err := os.Create(filepath.Join(dir, "native-mini-"+theme+".png"))
		if err != nil {
			t.Fatal(err)
		}
		err = png.Encode(f, tester.Image())
		_ = f.Close()
		if err != nil {
			t.Fatal(err)
		}
	}
}

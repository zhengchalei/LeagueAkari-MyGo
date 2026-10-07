package nativemini

import (
	"context"
	"strings"
	"sync"
	"testing"
	"time"
)

func fixture(hero int) Inputs {
	return Inputs{ShowSkins: true, Client: map[string]any{
		"state":            map[string]any{"connectionState": "connected"},
		"gameflow":         map[string]any{"phase": "ChampSelect", "session": map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "KIWI"}}}},
		"champSelect":      map[string]any{"currentChampion": hero, "currentPickableChampionIds": []any{23, 421, 202}, "skinSelectorInfo": map[string]any{"selectedSkinId": 23000}, "session": map[string]any{"benchEnabled": true, "allowSubsetChampionPicks": true, "allowSkinSelection": true, "timer": map[string]any{"phase": "BAN_PICK"}, "benchChampions": []any{map[string]any{"championId": 202}}, "localPlayerCellId": 1, "actions": []any{[]any{map[string]any{"type": "pick", "id": 7, "actorCellId": 1, "completed": false}}}}},
		"lobbyTeamBuilder": map[string]any{"champSelect": map[string]any{"subsetChampionList": []any{23, 421, 23}}},
	}, Kiwi: map[string]any{"version": "16.19", "cached": true, "balance": map[string]any{"23": map[string]any{"id": 23, "adjustments": []any{
		map[string]any{"type": "damage-dealt", "value": 0.95, "display": "percentage", "effect": "nerfed"},
		map[string]any{"type": "damage-taken", "value": 0.9, "display": "percentage", "effect": "buffed"},
		map[string]any{"type": "attack-speed-growth", "value": 2.5, "formattedValue": "+2.5%", "display": "literal", "effect": "buffed"},
		map[string]any{"type": "special", "description": "额外机制", "effect": "neutral"},
	}}}}}
}
func TestSubsetChoicesAndBalance(t *testing.T) {
	s := Build(fixture(23))
	if len(s.Choices) != 3 || !s.Choices[1].Enabled || s.Choices[2].Enabled {
		t.Fatalf("invalid subset choices: %+v", s.Choices)
	}
	if !s.BalanceKnown || s.Source != "Bilibili RESG" || s.Version != "16.19" || !s.Cached {
		t.Fatalf("metadata lost: %+v", s)
	}
	if s.Balance[0].Value != "−10%" || s.Balance[0].Effect != "buffed" || s.Balance[1].Value != "+2.5%" || s.Balance[2].Value != "−5%" {
		t.Fatalf("invalid deltas: %+v", s.Balance)
	}
	if len(s.Notes) != 1 {
		t.Fatal("special notes lost")
	}
	in := fixture(23)
	in.Client["gameflow"].(map[string]any)["session"].(map[string]any)["gameData"].(map[string]any)["queue"].(map[string]any)["gameMode"] = "ARAM"
	if Build(in).BalanceKnown {
		t.Fatal("KIWI must not leak to ordinary ARAM")
	}
}
func TestOwnedSkinsIncludesOnlyUsableOwnedChromas(t *testing.T) {
	skins := OwnedSkins(23, []any{
		map[string]any{"championId": 23, "id": 23000, "name": "base", "unlocked": true, "splashPath": "base.jpg", "childSkins": []any{map[string]any{"id": 23001, "unlocked": true, "chromaPreviewPath": "chroma.jpg"}, map[string]any{"id": 23002, "unlocked": false}, map[string]any{"id": 23003, "unlocked": true, "disabled": true}}},
		map[string]any{"championId": 421, "id": 421000, "unlocked": true},
		map[string]any{"championId": 23, "id": 23004, "unlocked": false},
	}, map[string]any{"skins": []any{map[string]any{"id": 23000, "name": "蛮族之王", "chromas": []any{map[string]any{"id": 23001, "name": "炫彩"}}}}})
	if len(skins) != 2 || skins[0].Name != "蛮族之王" || skins[1].Name != "炫彩" || skins[1].ImagePath != "chroma.jpg" {
		t.Fatalf("wrong owned skins: %+v", skins)
	}
}
func TestChooseSubsetUsesPersonalActionAndRejectsBenchBeforeFinalization(t *testing.T) {
	var path, method string
	var body map[string]any
	m := New(func(_ context.Context, verb, endpoint string, value any) (any, error) {
		method = verb
		path = endpoint
		body, _ = value.(map[string]any)
		return nil, nil
	})
	in := fixture(0)
	in.ShowSkins = false
	m.Refresh(context.Background(), in)
	if err := m.ChooseChampion(context.Background(), 202, true); err == nil {
		t.Fatal("bench hero must not be pickable before finalization")
	}
	if err := m.ChooseChampion(context.Background(), 421, true); err != nil {
		t.Fatal(err)
	}
	if method != "PATCH" || path != "/lol-champ-select/v1/session/actions/7" || body["completed"] != true || body["championId"] != 421 {
		t.Fatalf("invalid write %s %s %+v", method, path, body)
	}
	if m.Snapshot().ChampionID != 0 {
		t.Fatal("write must not optimistically select champion")
	}
}
func TestLateSkinsCannotReplaceNewHero(t *testing.T) {
	first := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	m := New(func(_ context.Context, _, path string, _ any) (any, error) {
		if path == "/lol-champ-select/v1/skin-carousel-skins" {
			held := false
			once.Do(func() { held = true; close(first) })
			if held {
				<-release
				return []any{map[string]any{"championId": 23, "id": 23000, "unlocked": true}}, nil
			}
			return []any{map[string]any{"championId": 421, "id": 421000, "unlocked": true}}, nil
		}
		return map[string]any{}, nil
	})
	m.Refresh(context.Background(), fixture(23))
	<-first
	m.Refresh(context.Background(), fixture(421))
	deadline := time.Now().Add(time.Second)
	for m.Snapshot().SkinsLoading && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	close(release)
	time.Sleep(10 * time.Millisecond)
	s := m.Snapshot()
	if s.ChampionID != 421 || len(s.Skins) != 1 || s.Skins[0].ID != 421000 {
		t.Fatalf("stale skin contamination %+v", s)
	}
	if err := m.ChooseSkin(context.Background(), 23000); err == nil || !strings.Contains(err.Error(), "皮肤") {
		t.Fatal("old hero skin allowed")
	}
}

func awaitSkins(t *testing.T, m *Model) Snapshot {
	t.Helper()
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) {
		s := m.Snapshot()
		if !s.SkinsLoading && len(s.Skins) > 0 {
			return s
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatal("skin load did not finish")
	return Snapshot{}
}

func TestSkinSettingEnableLoadsAndDisableRejectsPendingResult(t *testing.T) {
	started := make(chan struct{})
	release := make(chan struct{})
	finished := make(chan struct{})
	var once sync.Once
	m := New(func(_ context.Context, _, path string, _ any) (any, error) {
		if path == "/lol-champ-select/v1/skin-carousel-skins" {
			held := false
			once.Do(func() { held = true; close(started) })
			if held {
				<-release
			}
			return []any{map[string]any{"championId": 23, "id": 23000, "unlocked": true}}, nil
		}
		select {
		case <-finished:
		default:
			close(finished)
		}
		return map[string]any{}, nil
	})
	in := fixture(23)
	in.ShowSkins = false
	m.Refresh(context.Background(), in)
	if m.Snapshot().SkinsLoading {
		t.Fatal("hidden skins should not load")
	}
	in.ShowSkins = true
	m.Refresh(context.Background(), in)
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("enabling skins did not issue a request")
	}
	in.ShowSkins = false
	m.Refresh(context.Background(), in)
	close(release)
	<-finished
	time.Sleep(10 * time.Millisecond)
	s := m.Snapshot()
	if s.ShowSkins || s.SkinsLoading || len(s.Skins) != 0 || s.CanSelectSkin {
		t.Fatalf("hidden skin response became visible: %+v", s)
	}
	in.ShowSkins = true
	m.Refresh(context.Background(), in)
	if len(awaitSkins(t, m).Skins) != 1 {
		t.Fatal("reenabling did not refetch skins")
	}
}

func TestLeavingChampSelectClearsRetainedHeroSkinsAndReloadsNextSession(t *testing.T) {
	var mu sync.Mutex
	requests := 0
	m := New(func(_ context.Context, _, path string, _ any) (any, error) {
		if path == "/lol-champ-select/v1/skin-carousel-skins" {
			mu.Lock()
			requests++
			id := 23000 + requests
			mu.Unlock()
			return []any{map[string]any{"championId": 23, "id": id, "unlocked": true}}, nil
		}
		return map[string]any{}, nil
	})
	in := fixture(23)
	m.Refresh(context.Background(), in)
	first := awaitSkins(t, m)
	in = fixture(23)
	in.Client["gameflow"].(map[string]any)["phase"] = "InProgress"
	m.Refresh(context.Background(), in)
	s := m.Snapshot()
	if len(s.Skins) != 0 || s.SkinsLoading || s.CanSelectSkin {
		t.Fatal("retained champion leaked session skins")
	}
	m.Refresh(context.Background(), fixture(23))
	second := awaitSkins(t, m)
	if first.Skins[0].ID == second.Skins[0].ID {
		t.Fatal("new session reused previous session skins")
	}
}

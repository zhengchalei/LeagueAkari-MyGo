package nativemini

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestBenchSubsetOrderMatchesOriginalAndKeepsCurrentCardUsable(t *testing.T) {
	in := fixture(23)
	in.ShowSkins = false
	cs := in.Client["champSelect"].(map[string]any)
	cs["currentPickableChampionIds"] = []any{421, 202}
	in.Client["lobbyTeamBuilder"].(map[string]any)["champSelect"].(map[string]any)["subsetChampionList"] = []any{202, 421, 23}
	s := Build(in)
	ids := []int{}
	for _, c := range s.Choices {
		ids = append(ids, c.ID)
	}
	if !reflect.DeepEqual(ids, []int{23, 421, 202}) || !s.Choices[0].Enabled {
		t.Fatalf("subset-only heroes must precede existing bench; selected remains usable: %+v", s.Choices)
	}
}

func TestChampionTooltipCarriesAdjustmentDetailsAndCannotMutateModel(t *testing.T) {
	m := New(nil)
	m.Refresh(context.Background(), fixture(23))
	s := m.Snapshot()
	if !s.Choices[0].BalanceKnown || len(s.Choices[0].Balance) != 3 || len(s.Choices[0].Notes) != 1 || s.Choices[1].BalanceKnown {
		t.Fatalf("candidate balance tooltip information missing %+v", s.Choices)
	}
	s.Choices[0].Balance[0].Name = "mutated"
	s.Choices[0].Notes[0] = "mutated"
	if m.Snapshot().Choices[0].Balance[0].Name == "mutated" || m.Snapshot().Choices[0].Notes[0] == "mutated" {
		t.Fatal("tooltip consumer mutated model snapshot")
	}
}

func TestSkinFailureFromPriorHeroCannotMarkCurrentHeroFailed(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	m := New(func(_ context.Context, method, path string, _ any) (any, error) {
		if method == "GET" {
			if strings.Contains(path, "carousel") {
				return []any{map[string]any{"championId": 23, "id": 23001, "unlocked": true}, map[string]any{"championId": 421, "id": 421001, "unlocked": true}}, nil
			}
			return map[string]any{}, nil
		}
		close(started)
		<-release
		return nil, errors.New("old skin failed")
	})
	m.Refresh(context.Background(), fixture(23))
	awaitSkins(t, m)
	done := make(chan error, 1)
	go func() { done <- m.ChooseSkin(context.Background(), 23001) }()
	<-started
	m.Refresh(context.Background(), fixture(421))
	awaitSkins(t, m)
	close(release)
	<-done
	s := m.Snapshot()
	if s.ChampionID != 421 || s.SkinApplyFailed || s.Error != "" || s.PendingSkinID != 0 || !s.Skins[0].Enabled {
		t.Fatalf("old hero request contaminated current skin UI %+v", s)
	}
}

func TestSelectionInvalidatedBeforeRequestNeverWrites(t *testing.T) {
	in := fixture(0)
	in.ShowSkins = false
	writes := 0
	m := New(func(context.Context, string, string, any) (any, error) { writes++; return nil, nil })
	m.Refresh(context.Background(), in)
	var once sync.Once
	m.OnChange = func() {
		if m.Snapshot().Busy {
			once.Do(func() {
				in.Client["gameflow"].(map[string]any)["phase"] = "InProgress"
				m.OnChange = nil
				m.Refresh(context.Background(), in)
			})
		}
	}
	if err := m.ChooseChampion(context.Background(), 421, true); err == nil || writes != 0 {
		t.Fatal("phase changed before request but stale action still wrote")
	}
	if s := m.Snapshot(); s.Busy || s.Error != "" {
		t.Fatalf("stale action polluted next phase %+v", s)
	}
}

func TestThreeChoicePreviewUsesFirstPendingPersonalPick(t *testing.T) {
	in := fixture(0)
	in.ShowSkins = false
	session := in.Client["champSelect"].(map[string]any)["session"].(map[string]any)
	session["actions"] = []any{
		[]any{map[string]any{"type": "ban", "id": 1, "actorCellId": 1}, map[string]any{"type": "pick", "id": 2, "actorCellId": 9}},
		[]any{map[string]any{"type": "pick", "id": 3, "actorCellId": 1, "completed": true}, map[string]any{"type": "pick", "id": 7, "actorCellId": 1}},
		[]any{map[string]any{"type": "pick", "id": 8, "actorCellId": 1}},
	}
	writes := 0
	m := New(func(_ context.Context, method, path string, body any) (any, error) {
		writes++
		if method != "PATCH" || path != "/lol-champ-select/v1/session/actions/7" || body.(map[string]any)["completed"] != false {
			t.Fatalf("wrong preview: %s %s %+v", method, path, body)
		}
		return nil, nil
	})
	m.Refresh(context.Background(), in)
	if err := m.ChooseChampion(context.Background(), 421, false); err != nil {
		t.Fatal(err)
	}
	if writes != 1 || m.Snapshot().ChampionID != 0 {
		t.Fatal("preview must not lock or optimistically change champion")
	}
}

func TestRerollVisibilityAndRemainingFollowOriginalPhases(t *testing.T) {
	for _, tt := range []struct {
		name, phase   string
		subset, allow bool
		count         int
		show, can     bool
		remaining     int
	}{
		{"three-choice without reroll", "BAN_PICK", true, true, 0, false, false, 0},
		{"normal aram exhausted", "FINALIZATION", false, true, 0, true, false, 0},
		{"special supplied reroll", "BAN_PICK", true, false, 1, true, true, 1},
		{"outside usable timer", "PLANNING", true, true, 2, true, false, 0},
		{"unsupported finalization", "FINALIZATION", true, true, 0, false, false, 0},
	} {
		t.Run(tt.name, func(t *testing.T) {
			in := fixture(23)
			session := in.Client["champSelect"].(map[string]any)["session"].(map[string]any)
			session["timer"] = map[string]any{"phase": tt.phase}
			session["allowSubsetChampionPicks"] = tt.subset
			session["allowRerolling"] = tt.allow
			session["rerollsRemaining"] = tt.count
			s := Build(in)
			if s.ShowReroll != tt.show || s.CanReroll != tt.can || s.Rerolls != tt.remaining {
				t.Fatalf("wrong reroll projection %+v", s)
			}
		})
	}
}

func TestSkinRequestPendingFailureRecoveryAndClientConfirmation(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	var mu sync.Mutex
	writes := 0
	m := New(func(_ context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			if strings.Contains(path, "carousel") {
				return []any{map[string]any{"championId": 23, "id": 23001, "name": "Owned chroma", "unlocked": true}}, nil
			}
			return map[string]any{}, nil
		}
		mu.Lock()
		writes++
		attempt := writes
		mu.Unlock()
		if body.(map[string]any)["selectedSkinId"] != 23001 {
			t.Fatal("wrong chroma write")
		}
		if attempt == 1 {
			close(started)
			<-release
			return nil, errors.New("skin rejected")
		}
		return nil, nil
	})
	m.Refresh(context.Background(), fixture(23))
	awaitSkins(t, m)
	done := make(chan error, 1)
	go func() { done <- m.ChooseSkin(context.Background(), 23001) }()
	<-started
	s := m.Snapshot()
	if !s.Busy || s.PendingSkinID != 23001 || s.PendingSkinName != "Owned chroma" || s.Skins[0].Enabled || s.SelectedSkinID != 23000 {
		t.Fatalf("missing pending/confirmed state %+v", s)
	}
	if err := m.ChooseSkin(context.Background(), 23001); err == nil {
		t.Fatal("duplicate pending skin accepted")
	}
	close(release)
	if <-done == nil {
		t.Fatal("failure swallowed")
	}
	s = m.Snapshot()
	if !s.SkinApplyFailed || s.Busy || s.PendingSkinID != 0 || !s.Skins[0].Enabled {
		t.Fatalf("failed skin not recoverable %+v", s)
	}
	if err := m.ChooseSkin(context.Background(), 23001); err != nil {
		t.Fatal(err)
	}
	s = m.Snapshot()
	if s.SkinApplyFailed || s.Error != "" || s.SelectedSkinID != 23000 || s.Skins[0].Selected {
		t.Fatal("retry must clear failure without claiming client selection")
	}
	in := fixture(23)
	in.Client["champSelect"].(map[string]any)["skinSelectorInfo"].(map[string]any)["selectedSkinId"] = 23001
	m.Refresh(context.Background(), in)
	if !m.Snapshot().Skins[0].Selected {
		t.Fatal("client confirmed chroma not selected")
	}
}

func TestDisabledParentCanExposeOwnedChromaWithoutExposingLockedFamily(t *testing.T) {
	s := OwnedSkins(23, []any{
		map[string]any{"championId": 23, "id": 23001, "unlocked": true, "disabled": true, "childSkins": []any{map[string]any{"id": 23002, "name": "Enabled child", "unlocked": true, "tilePath": "child"}}},
		map[string]any{"championId": 23, "id": 23003, "unlocked": false, "childSkins": []any{map[string]any{"id": 23004, "unlocked": true}}},
	}, nil)
	if len(s) != 1 || s[0].ID != 23002 || s[0].ImagePath != "child" {
		t.Fatalf("wrong family ownership %+v", s)
	}
}

func TestRerollGrabBackReservesOperationAndUsesFreshBench(t *testing.T) {
	for _, mode := range []string{"available", "taken", "leave", "new-session", "cancel"} {
		t.Run(mode, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			started, release := make(chan struct{}), make(chan struct{})
			swaps := 0
			in := fixture(23)
			in.ShowSkins = false
			session := in.Client["champSelect"].(map[string]any)["session"].(map[string]any)
			session["gameId"] = 100
			session["timer"] = map[string]any{"phase": "FINALIZATION"}
			session["rerollsRemaining"] = 1
			m := New(func(_ context.Context, method, path string, _ any) (any, error) {
				if strings.HasSuffix(path, "reroll") {
					return nil, nil
				}
				if method == "GET" {
					close(started)
					<-release
					gameID := 100
					if mode == "new-session" {
						gameID = 101
					}
					bench := []any{map[string]any{"championId": 23}}
					if mode == "taken" {
						bench = nil
					}
					return map[string]any{"gameId": gameID, "localPlayerCellId": 1, "benchEnabled": true, "timer": map[string]any{"phase": "FINALIZATION"}, "benchChampions": bench, "myTeam": []any{map[string]any{"cellId": 1, "championId": 421}}}, nil
				}
				swaps++
				if path != "/lol-champ-select/v1/session/bench/swap/23" {
					t.Fatalf("wrong swap %s", path)
				}
				return nil, nil
			})
			m.Refresh(ctx, in)
			done := make(chan error, 1)
			go func() { done <- m.Reroll(ctx, true) }()
			select {
			case <-started:
			case <-time.After(time.Second):
				t.Fatal("session not reread")
			}
			if !m.Snapshot().Busy || m.Snapshot().CanReroll {
				t.Fatal("grab back reservation lost")
			}
			if err := m.Reroll(ctx, true); err == nil {
				t.Fatal("duplicate reroll allowed")
			}
			if mode == "leave" {
				in.Client["gameflow"].(map[string]any)["phase"] = "InProgress"
				m.Refresh(ctx, in)
			}
			if mode == "cancel" {
				cancel()
			}
			close(release)
			err := <-done
			if mode == "available" {
				if err != nil || swaps != 1 {
					t.Fatalf("valid grab back failed %v %d", err, swaps)
				}
			} else if err == nil || swaps != 0 {
				t.Fatalf("invalid grab back wrote: %s %v %d", mode, err, swaps)
			}
			if m.Snapshot().Busy {
				t.Fatal("operation stuck busy")
			}
			if mode != "leave" && m.Snapshot().ChampionID != 23 {
				t.Fatal("fresh GET overwrote client-confirmed selection")
			}
		})
	}
}

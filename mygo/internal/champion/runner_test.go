package champion

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"testing"
)

type request struct {
	method, path string
	body         any
}

type fakeClient struct {
	phase      string
	hero       int
	position   string
	gameID     int64
	mode       string
	queue      string
	canAdd     bool
	pages      []perkPage
	writes     []request
	reads      int
	before     func(context.Context, string, string, any)
	writeError error
}

type cachedClient struct{ *fakeClient }

func (c cachedClient) GameplayState() map[string]any {
	return map[string]any{
		"gameflow": map[string]any{"phase": c.phase, "session": map[string]any{"gameData": map[string]any{
			"gameId": c.gameID, "queue": map[string]any{"gameMode": c.mode, "type": c.queue},
		}}},
		"champSelect": map[string]any{"session": map[string]any{"gameId": c.gameID, "localPlayerCellId": 2,
			"myTeam": []any{map[string]any{"cellId": 2, "championId": c.hero, "assignedPosition": c.position}}}},
	}
}

func (c cachedClient) State() map[string]any {
	return map[string]any{"gameData": map[string]any{"champions": map[string]any{"103": map[string]any{"name": "阿狸"}}}}
}

func mockClient() *fakeClient {
	return &fakeClient{phase: "ChampSelect", hero: 103, gameID: 12345, mode: "ARAM", queue: "ARAM_UNRANKED_5x5",
		canAdd: true, pages: []perkPage{{ID: 77, Name: "[Timo] old hero", IsEditable: true}}}
}

func (c *fakeClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
	if c.before != nil {
		c.before(ctx, method, path, body)
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if method != http.MethodGet {
		c.writes = append(c.writes, request{method, path, body})
		if c.writeError != nil {
			return nil, c.writeError
		}
		if method == http.MethodPost && path == "/lol-perks/v1/pages/" {
			return perkPage{ID: 99}, nil
		}
		return nil, nil
	}
	c.reads++
	switch path {
	case "/lol-gameflow/v1/gameflow-phase":
		return c.phase, nil
	case "/lol-gameflow/v1/session":
		return map[string]any{"gameData": map[string]any{"gameId": c.gameID, "queue": map[string]any{"gameMode": c.mode, "type": c.queue}}}, nil
	case "/lol-champ-select/v1/session":
		return map[string]any{"gameId": c.gameID, "localPlayerCellId": 2, "myTeam": []any{map[string]any{"cellId": 2, "championId": c.hero, "assignedPosition": c.position}}}, nil
	case "/lol-perks/v1/inventory":
		return map[string]any{"canAddCustomPage": c.canAdd}, nil
	case "/lol-perks/v1/pages":
		return c.pages, nil
	}
	return nil, fmt.Errorf("unexpected request %s %s", method, path)
}

func TestRuneAndSpellApplicationDeduplicatesAndFollowsHeroChanges(t *testing.T) {
	store := newStore(t)
	client := mockClient()
	runner := New(client, store, nil)
	defer runner.Close()
	for _, id := range []int{103, 147} {
		if err := runner.UpdateRunes(id, "aram", testRunes()); err != nil {
			t.Fatal(err)
		}
		if err := runner.UpdateSpells(id, "aram", &SpellsConfig{4, 32}); err != nil {
			t.Fatal(err)
		}
	}
	setEnabled(t, store, true)
	for i := 0; i < 2; i++ {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(client.writes) != 3 {
		t.Fatalf("expected rune update, currentpage and spells exactly once, got %+v", client.writes)
	}
	if client.writes[0].path != "/lol-perks/v1/pages/77" || client.writes[1].body != 77 || client.writes[2].method != http.MethodPatch {
		t.Fatalf("unexpected rune/spell endpoint contract: %+v", client.writes)
	}
	if runner.State()["runesStatus"] != "applied" || runner.State()["spellsStatus"] != "applied" {
		t.Fatalf("application status missing: %+v", runner.State())
	}
	client.hero = 147
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(client.writes) != 6 {
		t.Fatal("switched champion did not reapply both configurations")
	}
	client.phase = "Lobby"
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	client.phase = "ChampSelect"
	client.gameID++
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(client.writes) != 9 {
		t.Fatal("next champion select did not apply")
	}
}

func TestRunePageCreationAndFullInventoryReplacement(t *testing.T) {
	for _, create := range []bool{true, false} {
		t.Run(fmt.Sprint(create), func(t *testing.T) {
			store := newStore(t)
			client := mockClient()
			client.canAdd = create
			client.pages = []perkPage{{ID: 1, Name: "Preset", IsEditable: false}, {ID: 2, Name: "Custom", IsEditable: true}, {ID: 3, Name: "Current", IsEditable: true, Current: true}}
			runner := New(client, store, nil)
			defer runner.Close()
			if err := runner.UpdateRunes(103, "aram", testRunes()); err != nil {
				t.Fatal(err)
			}
			setEnabled(t, store, true)
			if err := runner.Tick(context.Background()); err != nil {
				t.Fatal(err)
			}
			if create {
				if len(client.writes) != 3 || client.writes[0].method != http.MethodPost || client.writes[1].path != "/lol-perks/v1/pages/99" {
					t.Fatalf("new rune page contract: %+v", client.writes)
				}
				if client.writes[0].body.(map[string]any)["primaryStyleId"] != "8200" {
					t.Fatal("creation requires string primaryStyleId")
				}
			} else if len(client.writes) != 2 || client.writes[0].path != "/lol-perks/v1/pages/3" {
				t.Fatalf("full inventory must use editable current page: %+v", client.writes)
			}
		})
	}
}

func TestConfigurationRemovalAndRestoreAppliesAgain(t *testing.T) {
	store := newStore(t)
	client := mockClient()
	runner := New(client, store, nil)
	defer runner.Close()
	setEnabled(t, store, true)
	for _, config := range []*SpellsConfig{{4, 32}, nil, {4, 32}} {
		if err := runner.UpdateSpells(103, "aram", config); err != nil {
			t.Fatal(err)
		}
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(client.writes) != 2 {
		t.Fatalf("restored configuration was not applied: %+v", client.writes)
	}
}

func TestDisabledAndUnselectedStatesNeverWrite(t *testing.T) {
	store := newStore(t)
	client := mockClient()
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.UpdateRunes(103, "aram", testRunes()); err != nil {
		t.Fatal(err)
	}
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if client.reads != 0 || len(client.writes) != 0 {
		t.Fatal("disabled feature queried the client")
	}
	setEnabled(t, store, true)
	client.hero = 0
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	client.phase = "InProgress"
	client.hero = 103
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(client.writes) != 0 {
		t.Fatal("configuration wrote without a selected hero in champion select")
	}
}

func TestHeroSwitchAndDisableInterruptPendingRuneWrites(t *testing.T) {
	for _, disable := range []bool{false, true} {
		t.Run(fmt.Sprint(disable), func(t *testing.T) {
			store := newStore(t)
			client := mockClient()
			runner := New(client, store, nil)
			defer runner.Close()
			if err := runner.UpdateRunes(103, "aram", testRunes()); err != nil {
				t.Fatal(err)
			}
			setEnabled(t, store, true)
			client.before = func(ctx context.Context, method, path string, body any) {
				if path == "/lol-perks/v1/inventory" {
					if disable {
						setEnabled(t, store, false)
					} else {
						client.hero = 147
					}
				}
			}
			if err := runner.Tick(context.Background()); !errors.Is(err, context.Canceled) {
				t.Fatalf("expected canceled old selection, got %v", err)
			}
			if len(client.writes) != 0 {
				t.Fatalf("stale rune configuration was written: %+v", client.writes)
			}
		})
	}
}

func TestFailedSpellApplicationEmitsOnceUntilConfigurationChanges(t *testing.T) {
	store := newStore(t)
	client := mockClient()
	client.writeError = errors.New("spell not available in this mode")
	events := 0
	runner := New(client, store, func(namespace, name string, args ...any) {
		if namespace != Namespace || name != "error-spells-update" {
			t.Errorf("unexpected event %s.%s", namespace, name)
		}
		events++
	})
	defer runner.Close()
	if err := runner.UpdateSpells(103, "aram", &SpellsConfig{4, 12}); err != nil {
		t.Fatal(err)
	}
	setEnabled(t, store, true)
	_ = runner.Tick(context.Background())
	_ = runner.Tick(context.Background())
	if len(client.writes) != 1 || events != 1 {
		t.Fatal("failed configuration should not repeatedly overwrite or notify")
	}
	if err := runner.UpdateSpells(103, "aram", &SpellsConfig{4, 32}); err != nil {
		t.Fatal(err)
	}
	_ = runner.Tick(context.Background())
	if len(client.writes) != 2 || events != 2 {
		t.Fatal("new configuration was not retried")
	}
}

func TestCachedClientStateUsesLiveWriteGuardAndChineseChampionName(t *testing.T) {
	store := newStore(t)
	client := cachedClient{mockClient()}
	client.position, client.mode, client.queue = "middle", "CLASSIC", "RANKED_SOLO_5x5"
	client.before = func(ctx context.Context, method, path string, body any) {
		if path == "/lol-gameflow/v1/session" {
			t.Fatal("cached client should not poll the gameflow session again")
		}
	}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.UpdateRunes(103, "ranked-default", testRunes()); err != nil {
		t.Fatal(err)
	}
	setEnabled(t, store, true)
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(client.writes) != 2 || client.writes[0].body.(map[string]any)["name"] != "[LeagueAkari-MyGo] 阿狸 - 中路" {
		t.Fatalf("cached champion data not applied correctly: %+v", client.writes)
	}
	client.hero = 147
	if err := runner.guard(context.Background(), selection{ChampionID: 103, CellID: 2, GameID: client.gameID, Position: "middle"}); !errors.Is(err, context.Canceled) {
		t.Fatal("write guard must detect a live hero switch despite the cached selection")
	}
}

package champion

import (
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func newStore(t *testing.T) *settings.Store {
	t.Helper()
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func setEnabled(t *testing.T, store *settings.Store, enabled bool) {
	t.Helper()
	if err := store.Set(Namespace, "enabled", enabled); err != nil {
		t.Fatal(err)
	}
}

func testRunes() *RunesConfig {
	return &RunesConfig{PrimaryStyleID: 8200, SubStyleID: 8300, SelectedPerkIDs: []int{8214, 8226, 8210, 8237, 8304, 8345, 5008, 5008, 5011}}
}

func TestSaveAndClearConfigsKeepRendererStorageShape(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, err := settings.New(path)
	if err != nil {
		t.Fatal(err)
	}
	runner := New(nil, store, nil)
	defer runner.Close()
	if err := runner.UpdateRunes(103, "aram", testRunes()); err != nil {
		t.Fatal(err)
	}
	if err := runner.UpdateRunes(147, "normal", testRunes()); err != nil {
		t.Fatal(err)
	}
	if err := runner.UpdateSummonerSpells(103, "aram", map[string]any{"spell1Id": 4, "spell2Id": 32}); err != nil {
		t.Fatal(err)
	}
	if err := runner.UpdateRunes(103, "aram", nil); err != nil {
		t.Fatal(err)
	}
	loaded, err := settings.New(path)
	if err != nil {
		t.Fatal(err)
	}
	config, err := loadConfig(loaded)
	if err != nil {
		t.Fatal(err)
	}
	if config.Runes["103"]["aram"] != nil || config.Runes["147"]["normal"].PrimaryStyleID != 8200 {
		t.Fatalf("clear overwrote other hero configuration: %+v", config)
	}
	if config.SummonerSpells["103"]["aram"].Spell2ID != 32 {
		t.Fatal("summonerSpells contract was not persisted")
	}
	if err := runner.UpdateSpells(103, "aram", &SpellsConfig{Spell1ID: 4, Spell2ID: 4}); err == nil {
		t.Fatal("invalid duplicate spells were accepted")
	}
	if err := runner.UpdateRunes(0, "normal", testRunes()); err == nil {
		t.Fatal("invalid hero was accepted")
	}
}

func TestModeAndIndependentRankedFallback(t *testing.T) {
	normal, aram, fallback := testRunes(), testRunes(), testRunes()
	rankedSpells := &SpellsConfig{Spell1ID: 4, Spell2ID: 12}
	config := Config{
		Runes:          map[string]map[string]*RunesConfig{"103": {"normal": normal, "aram": aram, "ranked-default": fallback}},
		SummonerSpells: map[string]map[string]*SpellsConfig{"103": {"ranked-middle": rankedSpells}},
	}
	for _, item := range []struct {
		mode, queue string
		runes       *RunesConfig
		spells      *SpellsConfig
	}{
		{"CLASSIC", "NORMAL", normal, nil},
		{"ARAM", "ARAM_UNRANKED_5x5", aram, nil},
		{"KIWI", "ARAM_UNRANKED_5x5", aram, nil},
		{"CLASSIC", "RANKED_SOLO_5x5", fallback, rankedSpells},
		{"CHERRY", "CHERRY", nil, nil},
	} {
		runes, spells := resolve(config, selection{ChampionID: 103, Position: "middle", Mode: item.mode, QueueType: item.queue})
		if runes != item.runes || spells != item.spells {
			t.Errorf("mode %s returned unexpected config", item.mode)
		}
	}
}

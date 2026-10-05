package settings

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestSettingsPersistAndSnapshotsAreIsolated(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nested", "settings.json")
	store, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if store.Get(GameflowNamespace, "autoHonorEnabled") != false {
		t.Fatal("automation must default to off")
	}
	if err := store.Set(GameflowNamespace, "autoHonorStrategy", "all-member"); err != nil {
		t.Fatal(err)
	}
	if err := store.Set("custom", "config", map[string]any{"champions": []int{1, 2}}); err != nil {
		t.Fatal(err)
	}
	value := store.Get("custom", "config").(map[string]any)
	value["champions"] = []int{3}
	snapshot := store.Snapshot(GameflowNamespace)
	snapshot["autoHonorStrategy"] = "opt-out"
	reopened, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if reopened.Get(GameflowNamespace, "autoHonorStrategy") != "all-member" {
		t.Fatal("selection did not persist")
	}
	if !reflect.DeepEqual(reopened.Get("custom", "config"), map[string]any{"champions": []any{float64(1), float64(2)}}) {
		t.Fatal("caller mutated stored value")
	}
}

func TestImportOverlaysElectronNamespacesAndNotifiesOutsideLock(t *testing.T) {
	directory := t.TempDir()
	store, err := New(filepath.Join(directory, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("mygo", "theme", "light"); err != nil {
		t.Fatal(err)
	}
	export := filepath.Join(directory, "electron.json")
	if err := os.WriteFile(export, []byte(`{"auto-gameflow-main":{"autoHonorEnabled":true,"autoHonorStrategy":"all-member"},"auto-select-main":{"pickConfig":{"ARAM":{"enabled":true}}}}`), 0600); err != nil {
		t.Fatal(err)
	}
	notifications := 0
	unsubscribe := store.OnChange(func(namespace, key string) { notifications++; _ = store.Snapshot(namespace) })
	if err := store.Import(export); err != nil {
		t.Fatal(err)
	}
	if notifications != 2 {
		t.Fatalf("got %d notifications", notifications)
	}
	if store.Get(GameflowNamespace, "autoHonorEnabled") != true || store.Get("mygo", "theme") != "light" {
		t.Fatal("import discarded or omitted settings")
	}
	if store.Get(GameflowNamespace, "autoAcceptEnabled") != false {
		t.Fatal("defaults must remain available")
	}
	unsubscribe()
	if err := store.Set(GameflowNamespace, "autoHonorEnabled", false); err != nil {
		t.Fatal(err)
	}
	if notifications != 2 {
		t.Fatal("listener was not removed")
	}
}

func TestFailedSaveLeavesSettingsUnchanged(t *testing.T) {
	directory := t.TempDir()
	path := filepath.Join(directory, "settings.json")
	store, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	if err := store.Set(GameflowNamespace, "autoHonorEnabled", true); err == nil {
		t.Fatal("expected invalid destination to fail")
	}
	if store.Get(GameflowNamespace, "autoHonorEnabled") != false {
		t.Fatal("failed write changed in-memory preferences")
	}
}

func TestExplicitSettingsRemainDistinctFromDefaultsAfterReopen(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, _ := New(path)
	store.ApplyDefaults(map[string]map[string]any{GameflowNamespace: {"autoHonorEnabled": true}, "other": {"theme": "dark"}})
	if store.HasPersisted(GameflowNamespace, "autoHonorEnabled") || store.Get(GameflowNamespace, "autoHonorEnabled") != true {
		t.Fatal("applying defaults must not mark a user choice")
	}
	if err := store.Set(GameflowNamespace, "autoHonorEnabled", false); err != nil {
		t.Fatal(err)
	}
	store.ApplyDefaults(map[string]map[string]any{GameflowNamespace: {"autoHonorEnabled": true}})
	if store.Get(GameflowNamespace, "autoHonorEnabled") != false {
		t.Fatal("defaults replaced an explicit false")
	}
	reopened, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if !reopened.HasPersisted(GameflowNamespace, "autoHonorEnabled") || reopened.HasPersisted(GameflowNamespace, "autoAcceptEnabled") || reopened.HasPersisted("other", "theme") {
		t.Fatal("persistence metadata was lost or defaults became explicit")
	}
}

func TestOriginalExportImportHonorsNamespaceAndPrefix(t *testing.T) {
	dir := t.TempDir()
	store, _ := New(filepath.Join(dir, "settings.json"))
	export := filepath.Join(dir, "akari.json")
	data := `{"type":"league-akari-settings","databaseVersion":7,"data":[{"key":"auto-misc-main/autoReplyEnabled","value":true},{"key":"auto-misc-main/statusMessage","value":"hello"},{"key":"other/config/nested","value":42}]}`
	if err := os.WriteFile(export, []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
	if err := store.ImportScoped(export, MiscNamespace, "autoReply"); err != nil {
		t.Fatal(err)
	}
	if store.Get(MiscNamespace, "autoReplyEnabled") != true || !store.HasPersisted(MiscNamespace, "autoReplyEnabled") || store.HasPersisted(MiscNamespace, "statusMessage") || store.Get("other", "config/nested") != nil {
		t.Fatal("scope leaked into other settings")
	}
	if err := store.Import(export); err != nil {
		t.Fatal(err)
	}
	if store.Get("other", "config/nested") != float64(42) {
		t.Fatal("nested key was not preserved")
	}
}

func TestDeletePrefixRestoresDefaultsAndKeepsOtherNamespaces(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, _ := New(path)
	for namespace, keys := range map[string]map[string]any{MiscNamespace: {"autoReplyEnabled": true, "autoReplyText": "reply", "statusMessage": "keep"}, "other": {"autoReplyEnabled": true}} {
		for key, value := range keys {
			if err := store.Set(namespace, key, value); err != nil {
				t.Fatal(err)
			}
		}
	}
	if err := store.DeletePrefix(MiscNamespace, "autoReply"); err != nil {
		t.Fatal(err)
	}
	reopened, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if reopened.Get(MiscNamespace, "autoReplyEnabled") != false || reopened.HasPersisted(MiscNamespace, "autoReplyEnabled") || reopened.Get(MiscNamespace, "statusMessage") != "keep" || reopened.Get("other", "autoReplyEnabled") != true {
		t.Fatal("prefix deletion changed another scope or did not restore defaults")
	}
}

func TestLegacySettingsArePersistedChoices(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	if err := os.WriteFile(path, []byte(`{"auto-gameflow-main":{"autoHonorEnabled":false}}`), 0600); err != nil {
		t.Fatal(err)
	}
	store, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if !store.HasPersisted(GameflowNamespace, "autoHonorEnabled") || store.HasPersisted(GameflowNamespace, "autoAcceptEnabled") {
		t.Fatal("legacy key presence must be retained")
	}
}

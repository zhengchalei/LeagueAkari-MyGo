package platform

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestImportedWindowSettingsApplyAndFollowCurrentPhase(t *testing.T) {
	for _, enabled := range []bool{true, false} {
		t.Run(map[bool]string{true: "enable", false: "disable"}[enabled], func(t *testing.T) {
			store := testStore(t)
			namespace := "window-manager-main/aux-window"
			storeSet(t, store, namespace, "enabled", !enabled)
			storeSet(t, store, namespace, "autoShow", false)
			backend := &fakeClient{state: map[string]any{
				"state":       map[string]any{"connectionState": "connected"},
				"gameflow":    map[string]any{"phase": "ChampSelect"},
				"champSelect": map[string]any{"session": map[string]any{}},
			}}
			calls := []string{}
			service := New(Options{Native: gameNative(), Client: backend, Store: store, WindowAction: func(name, method string, _ []any) (any, error) {
				if name == "aux-window" {
					calls = append(calls, method)
					if method == "applySettings" && (store.Get(namespace, "pinned") != true || store.Get(namespace, "opacity") != 0.6) {
						t.Fatal("window applied before import values were saved")
					}
				}
				return nil, nil
			}})
			defer service.Close()
			service.followWindows(context.Background())
			calls = nil
			data, err := json.Marshal(map[string]any{"version": 1, "namespaces": map[string]any{
				namespace: map[string]any{"enabled": enabled, "autoShow": true, "pinned": true, "opacity": 0.6},
			}})
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "import.json")
			if err := os.WriteFile(path, data, 0600); err != nil {
				t.Fatal(err)
			}
			if err := store.Import(path); err != nil {
				t.Fatal(err)
			}
			expected := "show"
			if !enabled {
				expected = "close"
			}
			if len(calls) != 2 || calls[0] != "applySettings" || calls[1] != expected {
				t.Fatal("import did not apply window style and current phase", calls)
			}
		})
	}
}

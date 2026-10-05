package settings

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const GameflowNamespace = "auto-gameflow-main"
const SelectNamespace = "auto-select-main"
const MiscNamespace = "auto-misc-main"
const RespawnNamespace = "respawn-timer-main"

// Store owns the same namespace/key settings contract as the Electron renderer.
type Store struct {
	mu           sync.RWMutex
	path         string
	values       map[string]map[string]any
	explicit     map[string]map[string]bool
	defaults     map[string]map[string]any
	listeners    map[uint64]func(namespace, key string)
	nextListener uint64
}

type document struct {
	Version    int                       `json:"version"`
	Namespaces map[string]map[string]any `json:"namespaces"`
	Persisted  map[string][]string       `json:"persisted"`
}

func New(path string) (*Store, error) {
	store := &Store{path: path, values: Defaults(), defaults: Defaults(), explicit: map[string]map[string]bool{}, listeners: make(map[uint64]func(string, string))}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return store, nil
	}
	if err != nil {
		return nil, err
	}
	values, err := parse(data)
	if err != nil {
		return nil, fmt.Errorf("read settings: %w", err)
	}
	merge(store.values, values)
	var saved document
	_ = json.Unmarshal(data, &saved)
	if saved.Persisted != nil {
		for namespace, keys := range saved.Persisted {
			for _, key := range keys {
				markPersisted(store.explicit, namespace, key)
			}
		}
	} else {
		for namespace, entries := range values {
			for key := range entries {
				markPersisted(store.explicit, namespace, key)
			}
		}
	}
	return store, nil
}

// HasPersisted distinguishes user choices and imports from in-memory defaults.
func (store *Store) HasPersisted(namespace, key string) bool {
	store.mu.RLock()
	defer store.mu.RUnlock()
	return store.explicit[namespace][key]
}

// ApplyDefaults fills unconfigured keys without turning defaults into user choices.
func (store *Store) ApplyDefaults(values map[string]map[string]any) {
	store.mu.Lock()
	defer store.mu.Unlock()
	merge(store.defaults, values)
	for namespace, entries := range values {
		if store.values[namespace] == nil {
			store.values[namespace] = map[string]any{}
		}
		for key, value := range entries {
			if !store.explicit[namespace][key] {
				store.values[namespace][key] = clone(value)
			}
		}
	}
}

func (store *Store) Get(namespace, key string) any {
	store.mu.RLock()
	defer store.mu.RUnlock()
	return clone(store.values[namespace][key])
}

func (store *Store) Snapshot(namespace string) map[string]any {
	store.mu.RLock()
	defer store.mu.RUnlock()
	if values, ok := clone(store.values[namespace]).(map[string]any); ok {
		return values
	}
	return map[string]any{}
}

func (store *Store) All() map[string]map[string]any {
	store.mu.RLock()
	defer store.mu.RUnlock()
	return copyNamespaces(store.values)
}

func (store *Store) Decode(namespace string, target any) error {
	data, err := json.Marshal(store.Snapshot(namespace))
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}

func (store *Store) Set(namespace, key string, value any) error {
	if namespace == "" || key == "" {
		return errors.New("settings namespace and key are required")
	}
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	var normalized any
	if err := json.Unmarshal(data, &normalized); err != nil {
		return err
	}
	store.mu.Lock()
	next := copyNamespaces(store.values)
	if next[namespace] == nil {
		next[namespace] = map[string]any{}
	}
	next[namespace][key] = normalized
	explicit := copyExplicit(store.explicit)
	markPersisted(explicit, namespace, key)
	if err := store.save(next, explicit); err != nil {
		store.mu.Unlock()
		return err
	}
	store.values = next
	store.explicit = explicit
	listeners := store.copyListeners()
	store.mu.Unlock()
	for _, listener := range listeners {
		listener(namespace, key)
	}
	return nil
}

// Import overlays an Electron export without discarding MyGo-only namespaces.
func (store *Store) Import(path string) error {
	return store.ImportScoped(path, "", "")
}

// ImportScoped imports only a requested namespace and key prefix; an empty namespace
// means all namespaces, and an empty prefix means every key within that scope.
func (store *Store) ImportScoped(path, namespace, prefix string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	values, err := parse(data)
	if err != nil {
		return err
	}
	for current, entries := range values {
		if namespace != "" && current != namespace {
			delete(values, current)
			continue
		}
		for key := range entries {
			if !strings.HasPrefix(key, prefix) {
				delete(entries, key)
			}
		}
	}
	store.mu.Lock()
	next := copyNamespaces(store.values)
	merge(next, values)
	explicit := copyExplicit(store.explicit)
	for namespace, entries := range values {
		for key := range entries {
			markPersisted(explicit, namespace, key)
		}
	}
	if err := store.save(next, explicit); err != nil {
		store.mu.Unlock()
		return err
	}
	store.values = next
	store.explicit = explicit
	listeners := store.copyListeners()
	store.mu.Unlock()
	for namespace := range values {
		for _, listener := range listeners {
			listener(namespace, "")
		}
	}
	return nil
}

func (store *Store) Delete(namespace, key string) error {
	return store.removeMatching(namespace, func(candidate string) bool { return candidate == key })
}

func (store *Store) DeletePrefix(namespace, prefix string) error {
	return store.removeMatching(namespace, func(key string) bool { return strings.HasPrefix(key, prefix) })
}

func (store *Store) removeMatching(namespace string, matches func(string) bool) error {
	if namespace == "" {
		return errors.New("settings namespace is required")
	}
	store.mu.Lock()
	next := copyNamespaces(store.values)
	explicit := copyExplicit(store.explicit)
	var removed []string
	for key := range next[namespace] {
		if matches(key) {
			delete(explicit[namespace], key)
			if value, exists := store.defaults[namespace][key]; exists {
				next[namespace][key] = clone(value)
			} else {
				delete(next[namespace], key)
			}
			removed = append(removed, key)
		}
	}
	if len(removed) == 0 {
		store.mu.Unlock()
		return nil
	}
	if err := store.save(next, explicit); err != nil {
		store.mu.Unlock()
		return err
	}
	store.values = next
	store.explicit = explicit
	listeners := store.copyListeners()
	store.mu.Unlock()
	for _, key := range removed {
		for _, listener := range listeners {
			listener(namespace, key)
		}
	}
	return nil
}

// OnChange lets active automation cancel immediately when its configuration changes.
func (store *Store) OnChange(listener func(namespace, key string)) func() {
	store.mu.Lock()
	store.nextListener++
	id := store.nextListener
	store.listeners[id] = listener
	store.mu.Unlock()
	return func() { store.mu.Lock(); delete(store.listeners, id); store.mu.Unlock() }
}

func (store *Store) copyListeners() []func(string, string) {
	listeners := make([]func(string, string), 0, len(store.listeners))
	for _, listener := range store.listeners {
		listeners = append(listeners, listener)
	}
	return listeners
}

func (store *Store) save(values map[string]map[string]any, explicit map[string]map[string]bool) error {
	persisted := map[string][]string{}
	for namespace, keys := range explicit {
		for key, configured := range keys {
			if configured {
				persisted[namespace] = append(persisted[namespace], key)
			}
		}
	}
	data, err := json.MarshalIndent(document{Version: 1, Namespaces: values, Persisted: persisted}, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(store.path), 0700); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(store.path), ".timo-settings-*.json")
	if err != nil {
		return err
	}
	name := file.Name()
	defer os.Remove(name)
	if err := file.Chmod(0600); err != nil {
		file.Close()
		return err
	}
	if _, err := file.Write(data); err != nil {
		file.Close()
		return err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	return os.Rename(name, store.path)
}

func parse(data []byte) (map[string]map[string]any, error) {
	var original struct {
		Type string `json:"type"`
		Data []struct {
			Key   string          `json:"key"`
			Value json.RawMessage `json:"value"`
		} `json:"data"`
	}
	if err := json.Unmarshal(data, &original); err != nil {
		return nil, err
	}
	if original.Type == "league-akari-settings" {
		if original.Data == nil {
			return nil, errors.New("original settings export is missing data")
		}
		values := map[string]map[string]any{}
		for _, entry := range original.Data {
			namespace, key, ok := strings.Cut(entry.Key, "/")
			if !ok || namespace == "" || key == "" || len(entry.Value) == 0 {
				return nil, errors.New("invalid original settings entry")
			}
			var value any
			if err := json.Unmarshal(entry.Value, &value); err != nil {
				return nil, err
			}
			if values[namespace] == nil {
				values[namespace] = map[string]any{}
			}
			values[namespace][key] = value
		}
		return values, nil
	}
	var wrapper document
	if err := json.Unmarshal(data, &wrapper); err != nil {
		return nil, err
	}
	if wrapper.Namespaces != nil {
		return wrapper.Namespaces, nil
	}
	var values map[string]map[string]any
	if err := json.Unmarshal(data, &values); err != nil {
		return nil, err
	}
	if values == nil {
		return nil, errors.New("settings document must be an object")
	}
	return values, nil
}

func clone(value any) any {
	data, err := json.Marshal(value)
	if err != nil {
		return nil
	}
	var result any
	if json.Unmarshal(data, &result) != nil {
		return nil
	}
	return result
}

func copyNamespaces(values map[string]map[string]any) map[string]map[string]any {
	next := make(map[string]map[string]any, len(values))
	for namespace, entries := range values {
		next[namespace] = make(map[string]any, len(entries))
		for key, value := range entries {
			next[namespace][key] = clone(value)
		}
	}
	return next
}

func markPersisted(values map[string]map[string]bool, namespace, key string) {
	if values[namespace] == nil {
		values[namespace] = map[string]bool{}
	}
	values[namespace][key] = true
}

func copyExplicit(values map[string]map[string]bool) map[string]map[string]bool {
	next := map[string]map[string]bool{}
	for namespace, keys := range values {
		for key, configured := range keys {
			if configured {
				markPersisted(next, namespace, key)
			}
		}
	}
	return next
}

func merge(target, source map[string]map[string]any) {
	for namespace, entries := range source {
		if target[namespace] == nil {
			target[namespace] = map[string]any{}
		}
		for key, value := range entries {
			target[namespace][key] = clone(value)
		}
	}
}

func Defaults() map[string]map[string]any {
	return map[string]map[string]any{
		GameflowNamespace: {
			"autoHonorEnabled": false, "autoHonorStrategy": "prefer-lobby-member",
			"playAgainEnabled": false, "autoAcceptEnabled": false, "autoAcceptDelaySeconds": 0,
			"autoReconnectEnabled": false, "autoMatchmakingEnabled": false,
			"autoMatchmakingMaximumMatchDuration": 0, "autoMatchmakingRematchStrategy": "never",
			"autoMatchmakingRematchFixedDuration": 2, "autoMatchmakingDelaySeconds": 5,
			"autoMatchmakingMinimumMembers": 1, "autoMatchmakingWaitForInvitees": true,
			"autoSkipLeaderEnabled": false, "autoHandleInvitationsEnabled": false,
			"rejectInvitationWhenAway": false, "invitationHandlingStrategies": map[string]any{},
			"autoSendARAMTeamSideEnabled": false, "autoSendARAMTeamSideVisibleToTeam": false,
		},
		SelectNamespace: {"pickConfig": map[string]any{}, "banConfig": map[string]any{}},
		MiscNamespace: {
			"autoReplyEnabled": false, "autoReplyEnableOnAway": false, "autoReplyText": "",
			"lockOfflineStatus": false, "autoSetStatusMessageEnabled": false, "statusMessage": "",
			"autoSetRankedStatusEnabled": false,
			"rankedStatus":               map[string]any{"queue": "RANKED_SOLO_5x5", "tier": "CHALLENGER", "division": "I"},
		},
		RespawnNamespace: {"enabled": false},
	}
}

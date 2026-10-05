package respawn

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type fakeClient struct {
	request func(context.Context, string, string, any) (any, error)
}

func (f fakeClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return f.request(ctx, method, path, body)
}
func (f fakeClient) GameJSON(ctx context.Context, method, path string, body any) (any, error) {
	return f.request(ctx, method, path, body)
}
func newStore(t *testing.T) *settings.Store {
	t.Helper()
	s, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	return s
}
func enable(t *testing.T, s *settings.Store, value bool) {
	t.Helper()
	if err := s.Set(settings.RespawnNamespace, "enabled", value); err != nil {
		t.Fatal(err)
	}
}
func info(s *Service) Info {
	return s.State()[settings.RespawnNamespace].(map[string]any)["info"].(Info)
}

func TestDisabledTimerMakesNoRequests(t *testing.T) {
	client := fakeClient{func(context.Context, string, string, any) (any, error) {
		t.Fatal("disabled timer queried client")
		return nil, nil
	}}
	s := New(client, client, newStore(t), nil)
	if err := s.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestTimerMatchesRiotIDAndTracksEachDeathDuration(t *testing.T) {
	store := newStore(t)
	enable(t, store, true)
	dead := true
	left := 30.0
	phase := "InProgress"
	emits := 0
	lcu := fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return phase, nil
		}
		return map[string]any{"gameName": "玩家", "tagLine": "123", "internalName": "internal"}, nil
	}}
	game := fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method != "GET" || path != "/liveclientdata/playerlist" {
			t.Fatal(path)
		}
		return []any{map[string]any{"riotId": "other#123", "isDead": true, "respawnTimer": 99}, map[string]any{"riotId": "玩家#123", "isDead": dead, "respawnTimer": left}}, nil
	}}
	s := New(lcu, game, store, func(string, string, ...any) { emits++ })
	_ = s.Tick(context.Background())
	left = 18
	_ = s.Tick(context.Background())
	if info(s) != (Info{TimeLeft: 18, TotalTime: 30, IsDead: true}) {
		t.Fatal(info(s))
	}
	dead = false
	left = 0
	_ = s.Tick(context.Background())
	if info(s).TotalTime != 30 {
		t.Fatal("death duration lost on resurrection")
	}
	dead = true
	left = 45
	_ = s.Tick(context.Background())
	if info(s).TotalTime != 45 {
		t.Fatal("second death total did not reset")
	}
	_ = s.Tick(context.Background())
	if emits != 4 {
		t.Fatalf("duplicate state update emits=%d", emits)
	}
	phase = "EndOfGame"
	_ = s.Tick(context.Background())
	if info(s) != (Info{}) {
		t.Fatal("state retained after game")
	}
}

func TestTimerUsesLegacyNameAndResetsImmediatelyWhenDisabled(t *testing.T) {
	store := newStore(t)
	enable(t, store, true)
	lcu := fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return "InProgress", nil
		}
		return map[string]any{"displayName": "legacy", "internalName": "legacy"}, nil
	}}
	game := fakeClient{func(context.Context, string, string, any) (any, error) {
		return []any{map[string]any{"summonerName": "LEGACY", "isDead": true, "respawnTimer": 5}}, nil
	}}
	s := New(lcu, game, store, nil)
	_ = s.Tick(context.Background())
	if !info(s).IsDead {
		t.Fatal("legacy name lookup failed")
	}
	enable(t, store, false)
	_ = s.Tick(context.Background())
	if info(s) != (Info{}) {
		t.Fatal("disabled timer retained data")
	}
}

func TestLiveClientErrorDoesNotInventDeathState(t *testing.T) {
	store := newStore(t)
	enable(t, store, true)
	lcu := fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return "InProgress", nil
		}
		return map[string]any{"gameName": "player", "tagLine": "1"}, nil
	}}
	game := fakeClient{func(context.Context, string, string, any) (any, error) { return nil, errors.New("game unavailable") }}
	s := New(lcu, game, store, nil)
	if err := s.Tick(context.Background()); err == nil || info(s) != (Info{}) {
		t.Fatal("live client failure ignored")
	}
}

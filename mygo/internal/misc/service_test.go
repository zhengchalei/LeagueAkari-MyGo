package misc

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type fakeClient struct {
	request func(context.Context, string, string, any) (any, error)
}

func (f fakeClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
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
func set(t *testing.T, s *settings.Store, key string, value any) {
	t.Helper()
	if err := s.Set(settings.MiscNamespace, key, value); err != nil {
		t.Fatal(err)
	}
}

func TestConstructionAndDisabledTickMakeNoRequests(t *testing.T) {
	s := New(fakeClient{func(context.Context, string, string, any) (any, error) {
		t.Fatal("disabled service contacted client")
		return nil, nil
	}}, newStore(t), nil)
	defer s.Close()
	if err := s.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestLoginWaitsForPresenceToSettleAndAppliesOncePerConnection(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	set(t, store, "statusMessage", "ready")
	set(t, store, "autoSetRankedStatusEnabled", true)
	me := &chatMe{Availability: "chat", SummonerID: 10}
	var writes []map[string]any
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			return me, nil
		}
		writes = append(writes, body.(map[string]any))
		return nil, nil
	}}, store, nil)
	defer s.Close()
	now := time.Unix(100, 0)
	s.now = func() time.Time { return now }
	for _, elapsed := range []time.Duration{0, time.Second, 2 * time.Second, 3 * time.Second} {
		now = time.Unix(100, 0).Add(elapsed)
		if err := s.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(writes) != 2 || writes[0]["statusMessage"] != "ready" {
		t.Fatalf("writes=%v", writes)
	}
	if _, present := writes[1]["lol"].(map[string]any)["rankedLeagueDivision"]; present {
		t.Fatal("apex tiers must omit division")
	}
	me = nil
	if err := s.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	me = &chatMe{Availability: "chat", SummonerID: 10}
	_ = s.Tick(context.Background())
	now = now.Add(2 * time.Second)
	_ = s.Tick(context.Background())
	if len(writes) != 4 {
		t.Fatal("new connection did not reset login automation")
	}
}

func TestManualStatusPreemptsPendingLoginAndRankPayloadKeepsNormalDivision(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	var writes []map[string]any
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			return chatMe{Availability: "chat"}, nil
		}
		writes = append(writes, body.(map[string]any))
		return nil, nil
	}}, store, nil)
	defer s.Close()
	now := time.Unix(100, 0)
	s.now = func() time.Time { return now }
	_ = s.Tick(context.Background())
	if _, err := s.Call(context.Background(), "applyStatusMessage", []any{"manual"}); err != nil {
		t.Fatal(err)
	}
	now = now.Add(time.Hour)
	_ = s.Tick(context.Background())
	if len(writes) != 1 || writes[0]["statusMessage"] != "manual" {
		t.Fatal("pending automatic message overwrote manual choice")
	}
	if _, err := s.Call(context.Background(), "applyRankedStatus", []any{map[string]any{"queue": "RANKED_FLEX_SR", "tier": "GOLD", "division": "II"}}); err != nil {
		t.Fatal(err)
	}
	if writes[1]["lol"].(map[string]any)["rankedLeagueDivision"] != "II" {
		t.Fatal("normal tier division lost")
	}
}

func TestOfflineLockOnlyRestoresAnOfflineTransition(t *testing.T) {
	store := newStore(t)
	set(t, store, "lockOfflineStatus", true)
	availability := "chat"
	writes := 0
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			return chatMe{Availability: availability}, nil
		}
		if body.(map[string]any)["availability"] != "offline" {
			t.Fatal(body)
		}
		writes++
		return nil, nil
	}}, store, nil)
	defer s.Close()
	for _, state := range []string{"chat", "away", "offline", "chat", "chat", "offline", "away"} {
		availability = state
		if err := s.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if writes != 2 {
		t.Fatalf("writes=%d", writes)
	}
}

func TestChatEventsReplyOnlyToOthersWhenAwayAndDoNotRepeat(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoReplyEnabled", true)
	set(t, store, "autoReplyEnableOnAway", true)
	set(t, store, "autoReplyText", "稍后回复")
	availability := "chat"
	writes := 0
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			if body.(map[string]any)["body"] != "稍后回复" {
				t.Fatal(body)
			}
			return nil, nil
		}
		switch path {
		case "/lol-chat/v1/me":
			return chatMe{Availability: availability}, nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"summonerId": 10}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}, store, nil)
	defer s.Close()
	message := chatMessage{ID: "1", Type: "chat", FromSummonerID: 20}
	event := func() {
		t.Helper()
		if err := s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/1", "Create", message); err != nil {
			t.Fatal(err)
		}
	}
	event()
	availability = "away"
	event()
	event()
	message.FromSummonerID = 10
	message.ID = "self"
	event()
	message.FromSummonerID = 20
	message.ID = "history"
	message.IsHistorical = true
	event()
	set(t, store, "autoReplyEnabled", false)
	message.ID = "disabled"
	message.IsHistorical = false
	event()
	if writes != 1 {
		t.Fatalf("writes=%d", writes)
	}
}

func TestPollingSeedsExistingMessagesAndRepliesToNewMessages(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoReplyEnabled", true)
	set(t, store, "autoReplyText", "reply")
	messages := []chatMessage{{ID: "old", Type: "chat", FromSummonerID: 20}}
	writes := 0
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		switch path {
		case "/lol-chat/v1/me":
			return chatMe{Availability: "chat"}, nil
		case "/lol-chat/v1/conversations":
			return []any{map[string]any{"id": "friend", "type": "chat"}, map[string]any{"id": "lobby", "type": "championSelect"}}, nil
		case "/lol-chat/v1/conversations/friend/messages":
			return messages, nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"summonerId": 10}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}, store, nil)
	defer s.Close()
	if err := s.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	messages = append(messages, chatMessage{ID: "new", Type: "chat", FromSummonerID: 20})
	_ = s.Tick(context.Background())
	_ = s.Tick(context.Background())
	if writes != 1 {
		t.Fatalf("writes=%d", writes)
	}
}

func TestReplySwitchChangeCancelsPendingWrite(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoReplyEnabled", true)
	set(t, store, "autoReplyText", "reply")
	writes := 0
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		if path == "/lol-chat/v1/me" {
			return chatMe{}, nil
		}
		set(t, store, "autoReplyEnabled", false)
		return map[string]any{"summonerId": 10}, nil
	}}, store, nil)
	defer s.Close()
	err := s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/1", "Create", chatMessage{Type: "chat", FromSummonerID: 20})
	if !errors.Is(err, context.Canceled) || writes != 0 {
		t.Fatalf("err=%v writes=%d", err, writes)
	}
}

func TestLoginFailureDoesNotPreventIndependentRankAction(t *testing.T) {
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	set(t, store, "autoSetRankedStatusEnabled", true)
	writes := 0
	s := New(fakeClient{func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			return chatMe{}, nil
		}
		writes++
		if writes == 1 {
			return nil, errors.New("status failed")
		}
		return nil, nil
	}}, store, nil)
	defer s.Close()
	now := time.Unix(1, 0)
	s.now = func() time.Time { return now }
	_ = s.Tick(context.Background())
	now = now.Add(2 * time.Second)
	if err := s.Tick(context.Background()); err == nil || writes != 2 {
		t.Fatalf("err=%v writes=%d", err, writes)
	}
}

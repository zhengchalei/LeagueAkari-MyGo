package champion

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type championHTTPClient struct {
	lc    *client.Client
	state map[string]any
}

func (c championHTTPClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return c.lc.JSON(ctx, method, path, body)
}
func (c championHTTPClient) RequestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	return c.lc.RequestScope(ctx)
}
func (c championHTTPClient) State() map[string]any { return c.state }

func TestChampionHTTPAnnouncesBeforePickAndAppliesBothConfigurationsIndependently(t *testing.T) {
	var mu sync.Mutex
	hero := 0
	writes := []request{}
	inventoryStarted, releaseInventory, spellApplied := make(chan struct{}, 1), make(chan struct{}), make(chan struct{}, 1)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" {
			var body any
			_ = json.NewDecoder(r.Body).Decode(&body)
			mu.Lock()
			writes = append(writes, request{r.Method, r.URL.Path, body})
			mu.Unlock()
			if r.Method == "PATCH" {
				spellApplied <- struct{}{}
			}
			if r.Method == "POST" && strings.Contains(r.URL.Path, "/pages") {
				_, _ = fmt.Fprint(w, `{"id":99}`)
			} else {
				_, _ = fmt.Fprint(w, `{}`)
			}
			return
		}
		switch r.URL.Path {
		case "/lol-gameflow/v1/gameflow-phase":
			_, _ = fmt.Fprint(w, `"ChampSelect"`)
		case "/lol-gameflow/v1/session":
			_, _ = fmt.Fprint(w, `{"gameData":{"gameId":100,"queue":{"gameMode":"ARAM","type":"ARAM_UNRANKED_5x5"}}}`)
		case "/lol-champ-select/v1/session":
			mu.Lock()
			id := hero
			mu.Unlock()
			_, _ = fmt.Fprintf(w, `{"gameId":100,"localPlayerCellId":2,"myTeam":[{"cellId":2,"championId":%d,"assignedPosition":""}]}`, id)
		case "/lol-perks/v1/inventory":
			inventoryStarted <- struct{}{}
			select {
			case <-releaseInventory:
			case <-r.Context().Done():
				return
			}
			_, _ = fmt.Fprint(w, `{"canAddCustomPage":true}`)
		case "/lol-perks/v1/pages":
			t.Error("creation branch should not fetch all pages")
		default:
			_, _ = fmt.Fprint(w, `{}`)
		}
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	store := newStore(t)
	c := championHTTPClient{lc: lc, state: map[string]any{"chat": map[string]any{"conversations": map[string]any{"championSelect": map[string]any{"id": "room"}}}, "gameData": map[string]any{"champions": map[string]any{"103": map[string]any{"name": "阿狸"}}}}}
	r := New(c, store, nil)
	defer r.Close()
	if err := r.UpdateRunes(103, "aram", testRunes()); err != nil {
		t.Fatal(err)
	}
	if err := r.UpdateSpells(103, "aram", &SpellsConfig{4, 32}); err != nil {
		t.Fatal(err)
	}
	setEnabled(t, store, true)
	if err := r.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	if len(writes) != 1 || writes[0].path != "/lol-chat/v1/conversations/room/messages" || !strings.Contains(writes[0].body.(map[string]any)["body"].(string), "已配置的英雄：阿狸") || writes[0].body.(map[string]any)["type"] != "celebration" {
		t.Fatal("room announcement unavailable before pick", writes)
	}
	hero = 103
	mu.Unlock()
	done := make(chan error, 1)
	go func() { done <- r.Tick(context.Background()) }()
	select {
	case <-inventoryStarted:
	case <-time.After(time.Second):
		t.Fatal("rune inventory not requested")
	}
	select {
	case <-spellApplied:
	case <-time.After(time.Second):
		close(releaseInventory)
		<-done
		t.Fatal("slow rune request blocked independent spells")
	}
	close(releaseInventory)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if err := r.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	messages := []string{}
	created, selected, updated, spells := 0, 0, 0, 0
	for _, write := range writes {
		switch write.path {
		case "/lol-chat/v1/conversations/room/messages":
			messages = append(messages, write.body.(map[string]any)["body"].(string))
		case "/lol-perks/v1/pages/":
			created++
			if write.body.(map[string]any)["primaryStyleId"] != "8200" {
				t.Fatal("wrong creation style type")
			}
		case "/lol-perks/v1/pages/99":
			updated++
			if write.body.(map[string]any)["name"] != "[Akari] 阿狸" {
				t.Fatal("wrong page name")
			}
		case "/lol-perks/v1/currentpage":
			selected++
		case "/lol-champ-select/v1/session/my-selection":
			spells++
		}
	}
	if created != 1 || updated != 1 || selected != 1 || spells != 1 || len(messages) != 3 {
		t.Fatalf("original create/update/select plus announcement/apply feedback not exactly once: %+v", writes)
	}
}

func TestChampionHTTPFailureHasOriginalErrorEventAndChatFeedback(t *testing.T) {
	var mu sync.Mutex
	messages := []string{}
	failChat := false
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == "PATCH" {
			http.Error(w, "spell unavailable", 400)
			return
		}
		if r.Method == "POST" {
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			mu.Lock()
			messages = append(messages, body["body"].(string))
			fail := failChat
			mu.Unlock()
			if fail {
				http.Error(w, "chat unavailable", 500)
				return
			}
			_, _ = fmt.Fprint(w, `{}`)
			return
		}
		switch r.URL.Path {
		case "/lol-gameflow/v1/gameflow-phase":
			_, _ = fmt.Fprint(w, `"ChampSelect"`)
		case "/lol-gameflow/v1/session":
			_, _ = fmt.Fprint(w, `{"gameData":{"gameId":100,"queue":{"gameMode":"ARAM","type":"ARAM_UNRANKED_5x5"}}}`)
		case "/lol-champ-select/v1/session":
			_, _ = fmt.Fprint(w, `{"gameId":100,"localPlayerCellId":2,"myTeam":[{"cellId":2,"championId":103}]}`)
		}
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	store := newStore(t)
	events, chatErrors := 0, 0
	r := New(championHTTPClient{lc: lc, state: map[string]any{"chat": map[string]any{"conversations": map[string]any{"championSelect": map[string]any{"id": "room"}}}}}, store, func(ns, name string, args ...any) {
		if ns == Namespace && name == "error-spells-update" {
			if args[0].(map[string]any)["message"] == "" {
				t.Error("error message missing")
			}
			events++
		}
		if ns == Namespace && name == "error-chat-send" {
			chatErrors++
		}
	})
	defer r.Close()
	_ = r.UpdateSpells(103, "aram", &SpellsConfig{4, 32})
	setEnabled(t, store, true)
	if err := r.Tick(context.Background()); err == nil {
		t.Fatal("failed spells hid actual HTTP error")
	}
	if events != 1 {
		t.Fatal("original error-spells-update absent")
	}
	mu.Lock()
	failChat = true
	mu.Unlock()
	r.sendChat(context.Background(), "反馈")
	if chatErrors != 1 {
		t.Fatal("failed feedback did not emit original error-chat-send")
	}
	mu.Lock()
	defer mu.Unlock()
	if len(messages) != 3 || !strings.Contains(messages[1], "召唤师技能更新失败") {
		t.Fatal("application failure chat feedback absent", messages)
	}
}

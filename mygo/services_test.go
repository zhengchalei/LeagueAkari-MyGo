package main

import (
	"context"
	"encoding/json"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestAnnouncementBodyAndNotificationMetadata(t *testing.T) {
	raw := "---\r\nsummary: '更新说明'\r\nalertLevel: high\r\n---\r\n正文"
	value := parseAnnouncement(raw)
	if value["content"] != "正文" || asObject(value["frontMatter"])["alertLevel"] != "high" {
		t.Fatal(value)
	}
	if value["uniqueId"] == parseAnnouncement(raw + "2")["uniqueId"] {
		t.Fatal("announcement identity did not change")
	}
}
func TestRuntimeInfoSupportsExistingDebugPanel(t *testing.T) {
	d := &Desktop{started: time.Now()}
	value := d.runtimeInfo()
	if len(asObject(value["os"])["cpus"].([]any)) == 0 || asObject(value["os"])["totalmem"].(uint64) == 0 || value["argv"] == nil {
		t.Fatal(value)
	}
	if _, err := json.Marshal(value); err != nil {
		t.Fatal(err)
	}
}
func TestProxyCancellationActuallyCancelsBackend(t *testing.T) {
	started := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { close(started); <-r.Context().Done() }))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{BaseURL: server.URL})
	d := &Desktop{client: lc}
	done := make(chan struct{})
	go func() {
		request := httptest.NewRequest("GET", "http://akari.localhost/league-client/wait", nil)
		request.Header.Set("x-akari-proxy-request-id", "cancel-test")
		d.proxy(httptest.NewRecorder(), request)
		close(done)
	}()
	<-started
	if _, err := d.dispatch(context.Background(), "akari-protocol-main", "cancelProxyRequest", []any{"cancel-test"}); err != nil {
		t.Fatal(err)
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("proxy request did not cancel")
	}
}
func TestFixedTextsCRUDKeepsSettingsAndOrder(t *testing.T) {
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	d := &Desktop{store: store}
	first, err := d.sendCall(context.Background(), "createFixedTextPresetItem", nil)
	if err != nil {
		t.Fatal(err)
	}
	second, _ := d.sendCall(context.Background(), "createFixedTextPresetItem", nil)
	id := asObject(first)["id"]
	_, err = d.sendCall(context.Background(), "updateFixedTextPresetItem", []any{id, object{"content": "第一行\n第二行", "title": "测试"}})
	if err != nil {
		t.Fatal(err)
	}
	d.sendCall(context.Background(), "moveFixedTextPresetItem", []any{asObject(second)["id"], "up"})
	data, _ := json.Marshal(store.Get("in-game-send-main", "fixedTextPresetItems"))
	if !strings.Contains(string(data), "第一行") {
		t.Fatal(string(data))
	}
	d.sendCall(context.Background(), "deleteFixedTextPresetItem", []any{id})
	if len(client.List(store.Get("in-game-send-main", "fixedTextPresetItems"))) != 1 {
		t.Fatal("delete failed")
	}
}

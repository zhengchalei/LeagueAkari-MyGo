package update

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestFailedDownloadEmitsOriginalNotificationErrorShape(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(503) }))
	defer server.Close()
	notifications := []map[string]any{}
	s := New(Options{Directory: t.TempDir(), HTTP: server.Client(), Emit: func(ns, event string, args ...any) {
		if event == "error-download-update" {
			notifications = append(notifications, map[string]any{"namespace": ns, "args": args})
		}
	}})
	s.download(context.Background(), map[string]any{"downloadUrl": server.URL, "size": 42})
	if len(notifications) != 1 || notifications[0]["namespace"] != "self-update-main" || !strings.Contains(notifications[0]["args"].([]any)[0].(map[string]any)["message"].(string), "503") {
		t.Fatalf("original download failure notification missing %+v", notifications)
	}
	if s.State()["updateProgressInfo"].(map[string]any)["phase"] != "download-failed" {
		t.Fatal("state failed phase no longer available")
	}
}
func TestCancelledDownloadDoesNotEmitFailureNotification(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	failures := 0
	s := New(Options{Directory: t.TempDir(), Emit: func(ns, event string, args ...any) {
		if event == "error-download-update" {
			failures++
		}
	}})
	s.download(ctx, map[string]any{"downloadUrl": "http://127.0.0.1:1", "size": 1})
	if failures != 0 {
		t.Fatal("user cancellation notified as download failure")
	}
}

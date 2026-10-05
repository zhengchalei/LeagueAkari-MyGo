package client

import (
	"context"
	"crypto/sha1"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestEndpointEventsRouteParametersAndExcludeTokens(t *testing.T) {
	var calls [][]any
	c := New(func(namespace, name string, args ...any) {
		if namespace == "league-client-main" {
			calls = append(calls, args)
		}
	})
	id := c.Subscribe("/lol-chat/v1/friends/:id")
	c.dispatchEvent(map[string]any{"uri": "/lol-chat/v1/friends/123", "eventType": "Update", "data": map[string]any{"id": "123"}})
	if len(calls) != 1 || calls[0][0] != id || Map(calls[0][2])["id"] != "123" {
		t.Fatal("friend endpoint event lost route parameters")
	}
	if c.Subscribe("/entitlements/v1/token") != "" {
		t.Fatal("renderer can subscribe to credentials")
	}
	c.dispatchEvent(map[string]any{"uri": "/entitlements/v1/token", "eventType": "Update", "data": "sensitive"})
	if len(calls) != 1 {
		t.Fatal("credential event was forwarded")
	}
	if !c.Unsubscribe(id) || c.Unsubscribe(id) {
		t.Fatal("unsubscribe contract is inaccurate")
	}
}

func TestLCUWebSocketHandshakeAndReadOnlySubscription(t *testing.T) {
	received := make(chan []any, 1)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, buffer, err := w.(http.Hijacker).Hijack()
		if err != nil {
			t.Error(err)
			return
		}
		defer conn.Close()
		accept := sha1.Sum([]byte(r.Header.Get("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
		fmt.Fprintf(buffer, "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: %s\r\n\r\n", base64.StdEncoding.EncodeToString(accept[:]))
		buffer.Flush()
		_, _, message, err := readFrame(buffer)
		if err != nil {
			t.Error(err)
			return
		}
		var packet []any
		if json.Unmarshal(message, &packet) != nil || Number(packet[0]) != 5 || packet[1] != "OnJsonApiEvent" {
			t.Error("LCU subscription packet incorrect")
		}
		data := []byte(`[8,"OnJsonApiEvent",{"uri":"/lol-chat/v1/friends","eventType":"Update","data":[]}]`)
		_ = writeFrame(conn, 1, data)
	}))
	defer server.Close()
	c := NewWithOptions(func(namespace, name string, args ...any) {
		if namespace == "league-client-main" {
			received <- args
		}
	}, Options{HTTPClient: server.Client()})
	c.SetAuth(&Auth{BaseURL: server.URL, Password: "test-only"})
	c.Subscribe("/lol-chat/v1/friends")
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	_ = c.consumeEvents(ctx)
	select {
	case <-received:
	case <-ctx.Done():
		t.Fatal("LCU event stream did not deliver endpoint event")
	}
}

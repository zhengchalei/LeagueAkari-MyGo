package client

import (
	"context"
	"crypto/sha1"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

type socketFixture struct {
	server     *httptest.Server
	subscribed chan net.Conn
	closed     chan struct{}
}

func newSocketFixture(t *testing.T) *socketFixture {
	t.Helper()
	f := &socketFixture{subscribed: make(chan net.Conn, 4), closed: make(chan struct{}, 4)}
	f.server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, buffer, err := w.(http.Hijacker).Hijack()
		if err != nil {
			t.Error(err)
			return
		}
		defer conn.Close()
		accept := sha1.Sum([]byte(r.Header.Get("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
		fmt.Fprintf(buffer, "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: %s\r\n\r\n", base64.StdEncoding.EncodeToString(accept[:]))
		buffer.Flush()
		_, _, payload, err := readFrame(buffer)
		if err != nil {
			return
		}
		if string(payload) != `[5,"OnJsonApiEvent"]` {
			t.Errorf("wrong subscription %s", payload)
			return
		}
		f.subscribed <- conn
		_, _, _, _ = readFrame(buffer)
		f.closed <- struct{}{}
	}))
	t.Cleanup(f.server.Close)
	return f
}
func socketWait[T any](t *testing.T, ch <-chan T) T {
	t.Helper()
	select {
	case value := <-ch:
		return value
	case <-time.After(time.Second):
		t.Fatal("socket lifecycle did not react within one second")
		var zero T
		return zero
	}
}

func TestSocketLifecycleClosesOldStreamAndImmediatelyResubscribesSelectedPID(t *testing.T) {
	old, newClient := newSocketFixture(t), newSocketFixture(t)
	updates := make(chan string, 8)
	c := NewWithOptions(func(ns, event string, args ...any) {
		if event == "update-state-prop/league-client-main:summoner" && args[0] == "me" {
			updates <- String(Map(args[1])["puuid"])
		}
	}, Options{HTTPClient: old.server.Client()})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan struct{})
	go func() { c.eventLoop(ctx); close(done) }()
	c.SetAuth(&Auth{PID: 111, BaseURL: old.server.URL, Password: "old"})
	oldConn := socketWait(t, old.subscribed)
	if err := writeFrame(oldConn, 1, []byte(`[8,"OnJsonApiEvent",{"uri":"/lol-summoner/v1/current-summoner","eventType":"Update","data":{"puuid":"old"}}]`)); err != nil {
		t.Fatal(err)
	}
	if socketWait(t, updates) != "old" {
		t.Fatal("old initial stream event not received")
	}
	c.SetAuth(&Auth{PID: 222, BaseURL: newClient.server.URL, Password: "new"})
	socketWait(t, old.closed)
	newConn := socketWait(t, newClient.subscribed)
	if err := writeFrame(newConn, 1, []byte(`[8,"OnJsonApiEvent",{"uri":"/lol-summoner/v1/current-summoner","eventType":"Update","data":{"puuid":"new"}}]`)); err != nil {
		t.Fatal(err)
	}
	if socketWait(t, updates) != "new" {
		t.Fatal("new selected PID did not receive realtime events")
	}
	c.Disconnect()
	socketWait(t, newClient.closed)
	c.mu.RLock()
	active := c.eventsConnected
	c.mu.RUnlock()
	if active {
		t.Fatal("disconnect retained active stream flag")
	}
	c.SetAuth(&Auth{PID: 222, BaseURL: newClient.server.URL, Password: "new"})
	// SetAuth is an internal primitive; explicit connection clears the manual flag.
	c.mu.Lock()
	c.manualDisconnect = false
	c.mu.Unlock()
	socketWait(t, newClient.subscribed)
	cancel()
	socketWait(t, newClient.closed)
	socketWait(t, done)
}

// A buffered frame may finish its read after the transport is closed. This
// fixture deliberately returns one such frame to verify the generation guard.
type delayedSocket struct {
	mu                           sync.Mutex
	reader                       io.Reader
	readStarted, release, closed chan struct{}
	readOnce, closeOnce          sync.Once
}

func (s *delayedSocket) Read(p []byte) (int, error) {
	s.readOnce.Do(func() { close(s.readStarted); <-s.release })
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.reader.Read(p)
}
func (s *delayedSocket) Write(p []byte) (int, error) { return len(p), nil }
func (s *delayedSocket) Close() error                { s.closeOnce.Do(func() { close(s.closed) }); return nil }

func TestDelayedClosedSocketFrameCannotChangeNewConnectionOrRunSubscribers(t *testing.T) {
	for _, mode := range []string{"switch", "disconnect"} {
		t.Run(mode, func(t *testing.T) {
			frame := new(strings.Builder)
			if err := writeFrame(frame, 1, []byte(`[8,"OnJsonApiEvent",{"uri":"/lol-summoner/v1/current-summoner","eventType":"Update","data":{"puuid":"late-old"}}]`)); err != nil {
				t.Fatal(err)
			}
			socket := &delayedSocket{reader: strings.NewReader(frame.String()), readStarted: make(chan struct{}), release: make(chan struct{}), closed: make(chan struct{})}
			forwarded, handled := 0, 0
			c := NewWithOptions(func(ns, event string, args ...any) {
				if ns == "league-client-main" {
					forwarded++
				}
			}, Options{HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
				accept := sha1.Sum([]byte(r.Header.Get("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
				return &http.Response{StatusCode: 101, Body: socket, Header: http.Header{"Sec-Websocket-Accept": []string{base64.StdEncoding.EncodeToString(accept[:])}}, Request: r}, nil
			})}})
			c.SetAuth(connectionAuth(111))
			c.Subscribe("/lol-summoner/v1/current-summoner")
			c.SetEventHandler(func(string, string, any) { handled++ })
			done := make(chan error, 1)
			go func() { done <- c.consumeEvents(context.Background()) }()
			socketWait(t, socket.readStarted)
			if mode == "switch" {
				c.SetAuth(connectionAuth(222))
				c.set("summoner", "me", map[string]any{"puuid": "new"})
				c.mu.Lock()
				c.eventsConnected = true
				c.mu.Unlock()
			} else {
				c.Disconnect()
			}
			socketWait(t, socket.closed)
			close(socket.release)
			waitConnectionDone(t, done)
			if forwarded != 0 || handled != 0 {
				t.Fatalf("stale stream invoked handlers/subscribers %d %d", forwarded, handled)
			}
			me := Map(c.State()["summoner"])["me"]
			if mode == "switch" {
				if Map(me)["puuid"] != "new" {
					t.Fatal("late frame overwrote selected client")
				}
				c.mu.RLock()
				active := c.eventsConnected
				c.mu.RUnlock()
				if !active {
					t.Fatal("old stream cleanup cleared new stream flag")
				}
			} else if me != nil {
				t.Fatal("late frame restored disconnected summoner")
			}
		})
	}
}

func TestOldSocketChatAndChampionFramesCannotMutateNewSelection(t *testing.T) {
	c := New(nil)
	c.SetAuth(connectionAuth(111))
	old, cancel := c.scopeConnection(context.Background())
	defer cancel()
	c.SetAuth(connectionAuth(222))
	c.set("champSelect", "currentChampion", 421)
	c.replaceChatConversations([]any{map[string]any{"id": "lol-champ-select-new", "type": "championSelect"}})
	for _, event := range []map[string]any{
		{"uri": "/lol-champ-select/v1/current-champion", "eventType": "Update", "data": 23},
		{"uri": "/lol-gameflow/v1/gameflow-phase", "eventType": "Update", "data": "InProgress"},
		{"uri": "/lol-chat/v1/conversations", "eventType": "Delete"},
		{"uri": "/lol-chat/v1/conversations/lol-champ-select-new/participants/99", "eventType": "Create", "data": map[string]any{"summonerId": 99}},
	} {
		c.dispatchEventForConnection(old, event)
	}
	s := c.State()
	if Number(Map(s["champSelect"])["currentChampion"]) != 421 || Map(s["gameflow"])["phase"] != nil {
		t.Fatal("old selection event changed new champion")
	}
	if String(Map(Map(Map(s["chat"])["conversations"])["championSelect"])["id"]) != "lol-champ-select-new" || len(List(Map(Map(s["chat"])["participants"])["championSelect"])) != 0 {
		t.Fatal("old chat event changed new room")
	}
}

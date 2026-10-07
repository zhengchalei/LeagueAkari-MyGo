package winuiipc

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"testing"
	"time"
)

func TestCallCanWaitForHostDialogWithoutBlockingResponses(t *testing.T) {
	backend, host := net.Pipe()
	defer host.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	s := New(backend)
	done := make(chan error, 1)
	go func() {
		done <- s.Serve(ctx, func(ctx context.Context, message Message) Result {
			value, err := s.HostCall(ctx, "host-ui", "confirm", message.Args)
			if err != nil {
				return Result{Error: map[string]any{"message": err.Error()}}
			}
			return Result{Success: true, Data: value}
		})
	}()
	encoder, decoder := json.NewEncoder(host), json.NewDecoder(host)
	if err := encoder.Encode(Message{Type: "call", ID: "choose", Args: []any{"confirm"}}); err != nil {
		t.Fatal(err)
	}
	var request Message
	if err := decoder.Decode(&request); err != nil {
		t.Fatal(err)
	}
	if request.Type != "host-call" || request.Namespace != "host-ui" {
		t.Fatalf("unexpected host request: %+v", request)
	}
	if err := encoder.Encode(Message{Type: "host-response", ID: request.ID, Result: &Result{Success: true, Data: true}}); err != nil {
		t.Fatal(err)
	}
	var result Message
	if err := decoder.Decode(&result); err != nil {
		t.Fatal(err)
	}
	if result.Type != "response" || result.ID != "choose" || !result.Result.Success || result.Result.Data != true {
		t.Fatalf("invalid response: %+v", result)
	}
	host.Close()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-ctx.Done():
		t.Fatal("pipe disconnect did not terminate backend session")
	}
}
func TestPipeDisconnectCancelsHostDialog(t *testing.T) {
	backend, host := net.Pipe()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	s := New(backend)
	finished := make(chan error, 1)
	go s.Serve(ctx, func(ctx context.Context, message Message) Result {
		_, err := s.HostCall(ctx, "host-ui", "fileDialog", nil)
		finished <- err
		return Result{}
	})
	if err := json.NewEncoder(host).Encode(Message{Type: "call", ID: "dialog"}); err != nil {
		t.Fatal(err)
	}
	var message Message
	if err := json.NewDecoder(host).Decode(&message); err != nil {
		t.Fatal(err)
	}
	host.Close()
	select {
	case err := <-finished:
		if err == nil {
			t.Fatal("disconnected dialog succeeded")
		}
	case <-ctx.Done():
		t.Fatal("dialog blocked after host exited")
	}
}
func TestPanicBecomesFailedResponse(t *testing.T) {
	result := safeHandle(context.Background(), Message{}, func(context.Context, Message) Result { panic("bad input") })
	if result.Success || result.Error["message"] != "bad input" {
		t.Fatalf("invalid panic response: %+v", result)
	}
}
func TestMalformedEnvelopeClosesPipe(t *testing.T) {
	backend, host := net.Pipe()
	defer host.Close()
	done := make(chan error, 1)
	go func() {
		done <- New(backend).Serve(context.Background(), func(context.Context, Message) Result { return Result{} })
	}()
	if _, err := io.WriteString(host, "{\"type\":\"unexpected\"}\n"); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("accepted malformed envelope")
		}
	case <-time.After(time.Second):
		t.Fatal("invalid message did not close session")
	}
}

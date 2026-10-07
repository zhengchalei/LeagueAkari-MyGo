// Package winuiipc carries the existing renderer contract over a private,
// current-user named pipe owned by the WinUI host. No web server is exposed.
package winuiipc

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"sync"
	"sync/atomic"
)

const MaxMessageBytes = 32 << 20

type Result struct {
	Success bool           `json:"success"`
	Data    any            `json:"data"`
	Error   map[string]any `json:"error,omitempty"`
}
type Message struct {
	Type      string  `json:"type"`
	ID        string  `json:"id,omitempty"`
	Namespace string  `json:"namespace,omitempty"`
	Method    string  `json:"method,omitempty"`
	Args      []any   `json:"args,omitempty"`
	Result    *Result `json:"result,omitempty"`
	Event     any     `json:"event,omitempty"`
	Version   string  `json:"version,omitempty"`
}
type Session struct {
	stream   io.ReadWriteCloser
	writerMu sync.Mutex
	mu       sync.Mutex
	pending  map[string]chan Result
	next     atomic.Uint64
}

func New(stream io.ReadWriteCloser) *Session {
	return &Session{stream: stream, pending: make(map[string]chan Result)}
}
func (s *Session) Send(message Message) error {
	data, err := json.Marshal(message)
	if err != nil {
		return err
	}
	if len(data) > MaxMessageBytes {
		return errors.New("IPC message exceeds size limit")
	}
	s.writerMu.Lock()
	defer s.writerMu.Unlock()
	data = append(data, '\n')
	for len(data) > 0 {
		n, err := s.stream.Write(data)
		if err != nil {
			return err
		}
		if n == 0 {
			return io.ErrShortWrite
		}
		data = data[n:]
	}
	return nil
}
func (s *Session) HostCall(ctx context.Context, ns, method string, args []any) (any, error) {
	id := "host-" + strconv.FormatUint(s.next.Add(1), 10)
	reply := make(chan Result, 1)
	s.mu.Lock()
	s.pending[id] = reply
	s.mu.Unlock()
	defer func() { s.mu.Lock(); delete(s.pending, id); s.mu.Unlock() }()
	if err := s.Send(Message{Type: "host-call", ID: id, Namespace: ns, Method: method, Args: args}); err != nil {
		return nil, err
	}
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case result := <-reply:
		if !result.Success {
			return nil, fmt.Errorf("host %s.%s: %v", ns, method, result.Error["message"])
		}
		return result.Data, nil
	}
}

// Requests run concurrently so a file picker can await its host response while
// the reader continues accepting events and unrelated requests.
func (s *Session) Serve(ctx context.Context, handler func(context.Context, Message) Result) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	var handlers sync.WaitGroup
	defer func() { cancel(); _ = s.stream.Close(); handlers.Wait() }()
	done := make(chan struct{})
	defer close(done)
	go func() {
		select {
		case <-ctx.Done():
			_ = s.stream.Close()
		case <-done:
		}
	}()
	defer s.stream.Close()
	scanner := bufio.NewScanner(s.stream)
	scanner.Buffer(make([]byte, 64<<10), MaxMessageBytes)
	for scanner.Scan() {
		var request Message
		if err := json.Unmarshal(scanner.Bytes(), &request); err != nil {
			return fmt.Errorf("invalid IPC JSON: %w", err)
		}
		if request.Type == "host-response" {
			s.mu.Lock()
			target := s.pending[request.ID]
			s.mu.Unlock()
			if target != nil && request.Result != nil {
				select {
				case target <- *request.Result:
				default:
				}
			}
			continue
		}
		if request.Type != "call" || request.ID == "" {
			return errors.New("invalid IPC request envelope")
		}
		handlers.Add(1)
		go func() {
			defer handlers.Done()
			result := safeHandle(ctx, request, handler)
			if err := s.Send(Message{Type: "response", ID: request.ID, Result: &result}); err != nil {
				cancel()
			}
		}()
	}
	return scanner.Err()
}
func safeHandle(ctx context.Context, message Message, handler func(context.Context, Message) Result) (result Result) {
	defer func() {
		if failure := recover(); failure != nil {
			result = Result{Error: map[string]any{"message": fmt.Sprint(failure)}}
		}
	}()
	return handler(ctx, message)
}

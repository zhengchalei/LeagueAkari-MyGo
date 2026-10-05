package platform

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode/utf16"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func gameHTTPClient() *http.Client {
	return &http.Client{Timeout: 5 * time.Second, Transport: &http.Transport{TLSClientConfig: &tls.Config{InsecureSkipVerify: true}, MaxIdleConnsPerHost: 2}}
}

func (s *Service) gameRequest(ctx context.Context, method, path string, body any) (*http.Response, error) {
	parsed, err := url.Parse(path)
	if err != nil || parsed.IsAbs() || !strings.HasPrefix(path, "/") || strings.HasPrefix(path, "//") {
		return nil, errors.New("游戏数据接口路径无效")
	}
	var reader io.Reader
	if body != nil {
		if data, ok := body.([]byte); ok {
			reader = bytes.NewReader(data)
		} else {
			data, err := json.Marshal(body)
			if err != nil {
				return nil, err
			}
			reader = bytes.NewReader(data)
		}
	}
	request, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(s.options.GameBaseURL, "/")+path, reader)
	if err != nil {
		return nil, err
	}
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	return s.options.GameHTTPClient.Do(request)
}

func (s *Service) GameJSON(ctx context.Context, method, path string, body any) (any, error) {
	response, err := s.gameRequest(ctx, method, path, body)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode >= 400 {
		return nil, &client.StatusError{Status: response.StatusCode, Endpoint: strings.Split(path, "?")[0]}
	}
	var value any
	err = json.NewDecoder(response.Body).Decode(&value)
	if errors.Is(err, io.EOF) {
		return nil, nil
	}
	return value, err
}
func (s *Service) GameProxy(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Path
	if r.URL.RawQuery != "" {
		path += "?" + r.URL.RawQuery
	}
	var body any
	if r.Body != nil {
		data, err := io.ReadAll(io.LimitReader(r.Body, 8<<20))
		if err != nil {
			http.Error(w, "请求正文无效", 400)
			return
		}
		if len(data) > 0 {
			body = data
		}
	}
	response, err := s.gameRequest(r.Context(), r.Method, path, body)
	if err != nil {
		http.Error(w, err.Error(), 502)
		return
	}
	defer response.Body.Close()
	w.Header().Set("Content-Type", response.Header.Get("Content-Type"))
	w.WriteHeader(response.StatusCode)
	_, _ = io.Copy(w, response.Body)
}

func (s *Service) CancelSend() {
	s.mu.RLock()
	cancel := s.inputCancel
	s.mu.RUnlock()
	if cancel != nil {
		cancel()
	}
}
func (s *Service) SendText(ctx context.Context, text string) error {
	return s.SendLines(ctx, []string{text})
}

// Input is serialized and rechecks the foreground game before each key/string.
// Cancellation releases a pressed Enter but never continues into another app.
func (s *Service) SendLines(parent context.Context, lines []string) error {
	s.inputMu.Lock()
	defer s.inputMu.Unlock()
	ctx, cancel := context.WithCancel(parent)
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		cancel()
		return context.Canceled
	}
	s.inputCancel = cancel
	s.mu.Unlock()
	defer func() { cancel(); s.mu.Lock(); s.inputCancel = nil; s.mu.Unlock() }()
	foreground, err := s.IsGameForeground(ctx)
	if err != nil {
		return err
	}
	if !foreground {
		return errors.New("LOL 游戏未处于前台")
	}
	if s.native.Elevated(s.native.ForegroundPID()) && !s.IsElevated() {
		return errors.New("LOL 游戏权限高于 LeagueAkari-MyGo，原生输入不可用")
	}
	if support := client.Map(s.NativeSupport()["nativeInput"]); support["available"] != true {
		return errors.New("原生输入不可用，游戏权限可能高于 LeagueAkari-MyGo")
	}
	check := func() error {
		if err := ctx.Err(); err != nil {
			return err
		}
		foreground, err := s.IsGameForeground(ctx)
		if err != nil {
			return err
		}
		if !foreground {
			return errors.New("LOL 游戏已离开前台")
		}
		return nil
	}
	enter := func() error {
		if err := check(); err != nil {
			return err
		}
		if err := s.native.Key(13, true); err != nil {
			return err
		}
		defer s.native.Key(13, false)
		return wait(ctx, 20*time.Millisecond)
	}
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if err := enter(); err != nil {
			return err
		}
		if err := wait(ctx, 65*time.Millisecond); err != nil {
			return err
		}
		for _, code := range utf16.Encode([]rune(line)) {
			if err := check(); err != nil {
				return err
			}
			if err := s.native.Unicode(code, true); err != nil {
				return err
			}
			if err := s.native.Unicode(code, false); err != nil {
				return err
			}
		}
		if err := wait(ctx, 65*time.Millisecond); err != nil {
			return err
		}
		if err := enter(); err != nil {
			return err
		}
		if err := wait(ctx, 65*time.Millisecond); err != nil {
			return err
		}
	}
	return nil
}

func wait(ctx context.Context, delay time.Duration) error {
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

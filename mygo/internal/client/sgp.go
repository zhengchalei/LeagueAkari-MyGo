package client

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

type Server struct {
	MatchHistory    string `json:"matchHistory"`
	Common          string `json:"common"`
	RegionPathParam string `json:"regionPathParam,omitempty"`
}

func TencentServers() map[string]Server {
	hosts := map[string]string{
		"HN1": "hn1-k8s-sgp", "HN10": "hn10-k8s-sgp", "TJ100": "tj100-sgp", "TJ101": "tj101-sgp",
		"NJ100": "nj100-sgp", "GZ100": "gz100-sgp", "CQ100": "cq100-sgp", "BGP2": "bgp2-k8s-sgp",
		"PBE": "pbe-sgp", "PREPBE": "prepbe-sgp",
	}
	servers := map[string]Server{}
	for platform, host := range hosts {
		base := "https://" + host + ".lol.qq.com:21019"
		servers["TENCENT_"+platform] = Server{MatchHistory: base, Common: base}
	}
	return servers
}

func (c *Client) Servers() map[string]Server {
	c.mu.RLock()
	defer c.mu.RUnlock()
	copy := map[string]Server{}
	for key, value := range c.options.Servers {
		copy[key] = value
	}
	return copy
}
func (c *Client) SetServers(servers map[string]Server) {
	c.mu.Lock()
	defer c.mu.Unlock()
	for key, value := range servers {
		if strings.HasPrefix(key, "TENCENT_") && value.Common != "" && value.MatchHistory != "" {
			c.options.Servers[key] = value
		}
	}
}

func (c *Client) RefreshTokens(ctx context.Context) error {
	ctx, cancel := c.scopeConnection(ctx)
	defer cancel()
	c.tokensMu.Lock()
	defer c.tokensMu.Unlock()
	c.mu.RLock()
	if !c.connectionCurrentLocked(ctx) {
		c.mu.RUnlock()
		return context.Canceled
	}
	recent := time.Since(c.tokenTime) < time.Minute && c.entitlements != "" && c.leagueSession != ""
	c.mu.RUnlock()
	if recent {
		return nil
	}
	entitlements, err := c.JSON(ctx, http.MethodGet, "/entitlements/v1/token", nil)
	if err != nil {
		return err
	}
	league, err := c.JSON(ctx, http.MethodGet, "/lol-league-session/v1/league-session-token", nil)
	if err != nil {
		return err
	}
	accessToken, _ := Map(entitlements)["accessToken"].(string)
	leagueToken, _ := league.(string)
	if accessToken == "" || leagueToken == "" {
		return errors.New("SGP 登录凭据尚未就绪")
	}
	c.mu.Lock()
	if !c.connectionCurrentLocked(ctx) {
		c.mu.Unlock()
		return context.Canceled
	}
	c.entitlements = accessToken
	c.leagueSession = leagueToken
	c.tokenTime = time.Now()
	c.mu.Unlock()
	return nil
}

func (c *Client) TokenReady() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.entitlements != "" && c.leagueSession != ""
}

func (c *Client) sgpRequest(ctx context.Context, serverID, tokenType, method, endpoint string, body any) (*http.Response, error) {
	ctx, release := c.scopeConnection(ctx)
	keepScope := false
	defer func() {
		if !keepScope {
			release()
		}
	}()
	if serverID == "" {
		serverID = c.CurrentServer()
	}
	serverID = strings.ToUpper(serverID)
	c.mu.RLock()
	server, ok := c.options.Servers[serverID]
	c.mu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("SGP 大区未配置: %s", serverID)
	}
	parsed, err := url.Parse(endpoint)
	if err != nil || parsed.IsAbs() || !strings.HasPrefix(endpoint, "/") || strings.HasPrefix(endpoint, "//") {
		return nil, errors.New("无效的 SGP 接口路径")
	}
	if err := c.RefreshTokens(ctx); err != nil {
		return nil, err
	}
	c.mu.RLock()
	token := c.leagueSession
	if tokenType == "entitlements" {
		token = c.entitlements
	}
	c.mu.RUnlock()
	if tokenType != "entitlements" && tokenType != "league-session" {
		return nil, errors.New("不支持的 SGP 凭据类型")
	}
	subID := server.RegionPathParam
	if subID == "" {
		subID = strings.TrimPrefix(serverID, "TENCENT_")
	}
	endpoint = strings.ReplaceAll(endpoint, "@akari:sgpServerSubId@", subID)
	base := server.Common
	if tokenType == "entitlements" {
		base = server.MatchHistory
	}
	if base == "" {
		return nil, errors.New("此大区不支持目标 SGP 接口")
	}
	reader, err := encodeBody(body)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(base, "/")+endpoint, reader)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("User-Agent", "LeagueAkari-MyGo/0.5")
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	response, requestErr := c.options.SGPHTTPClient.Do(req)
	c.recordSGPConnection(ctx, response, requestErr)
	if requestErr == nil && response != nil && response.Body != nil {
		response.Body = &scopedSGPBody{ReadCloser: response.Body, release: release}
		keepScope = true
	}
	return response, requestErr
}

type scopedSGPBody struct {
	io.ReadCloser
	release context.CancelFunc
}

func (body *scopedSGPBody) Close() error { err := body.ReadCloser.Close(); body.release(); return err }

func (c *Client) recordSGPConnection(ctx context.Context, response *http.Response, err error) {
	// Original Axios counts successful responses and network failures without
	// any response. HTTP status errors belong to neither connectivity counter.
	if response != nil && (err != nil || response.StatusCode < 200 || response.StatusCode >= 300) {
		return
	}
	key := "connectionSuccessesCounted"
	c.stateUpdateMu.Lock()
	defer c.stateUpdateMu.Unlock()
	c.mu.Lock()
	if !c.connectionGenerationMatchesLocked(ctx) {
		c.mu.Unlock()
		return
	}
	if err == nil {
		c.sgpConnectionSuccesses++
	} else {
		key = "connectionFailuresCounted"
		c.sgpConnectionFailures++
	}
	count := c.sgpConnectionSuccesses
	if err != nil {
		count = c.sgpConnectionFailures
	}
	c.mu.Unlock()
	if c.emit != nil {
		c.emit("mobx-utils-main", "update-state-prop/sgp-main:state", key, count, map[string]any{"action": "update", "raw": true})
	}
}

func (c *Client) SGPJSON(ctx context.Context, serverID, tokenType, method, endpoint string, body any) (any, error) {
	response, err := c.sgpRequest(ctx, serverID, tokenType, method, endpoint, body)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode >= 400 {
		return nil, &StatusError{response.StatusCode, strings.Split(endpoint, "?")[0]}
	}
	var value any
	err = json.NewDecoder(response.Body).Decode(&value)
	if errors.Is(err, io.EOF) {
		return nil, nil
	}
	return value, err
}

func (c *Client) SGPJSONRaw(ctx context.Context, serverID, tokenType, method, endpoint string, body any) (json.RawMessage, error) {
	response, err := c.sgpRequest(ctx, serverID, tokenType, method, endpoint, body)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode >= 400 {
		return nil, &StatusError{response.StatusCode, strings.Split(endpoint, "?")[0]}
	}
	data, err := io.ReadAll(response.Body)
	return json.RawMessage(data), err
}

func (c *Client) SGPProxy(w http.ResponseWriter, r *http.Request) {
	endpoint := r.URL.Path
	if r.URL.RawQuery != "" {
		endpoint += "?" + r.URL.RawQuery
	}
	var body any
	if r.Body != nil {
		// The summoner endpoint requires Content-Length instead of chunked transfer.
		data, err := io.ReadAll(io.LimitReader(r.Body, 8*1024*1024))
		if err != nil {
			http.Error(w, "无效请求", 400)
			return
		}
		if len(data) > 0 {
			body = data
		}
	}
	response, err := c.sgpRequest(r.Context(), r.Header.Get("x-akari-sgp-server-id"), r.Header.Get("x-akari-token-type"), r.Method, endpoint, body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	defer response.Body.Close()
	if value := response.Header.Get("Content-Type"); value != "" {
		w.Header().Set("Content-Type", value)
	}
	if r.Method == http.MethodGet && response.StatusCode >= 200 && response.StatusCode < 300 && strings.HasSuffix(strings.TrimRight(r.URL.Path, "/"), "/SUMMARY") {
		data, err := io.ReadAll(response.Body)
		if err == nil {
			if compacted, _, err := StripUnusedMissions(data); err == nil {
				data = compacted
			}
		}
		w.WriteHeader(response.StatusCode)
		_, _ = w.Write(data)
		return
	}
	w.WriteHeader(response.StatusCode)
	_, _ = io.Copy(w, response.Body)
}

func Map(value any) map[string]any {
	if fields, ok := value.(map[string]any); ok && fields != nil {
		return fields
	}
	return map[string]any{}
}
func List(value any) []any {
	if values, ok := value.([]any); ok {
		return values
	}
	return []any{}
}
func Number(value any) int64 {
	switch v := value.(type) {
	case float64:
		return int64(v)
	case int:
		return int64(v)
	case int64:
		return v
	case json.Number:
		n, _ := v.Int64()
		return n
	}
	return 0
}
func String(value any) string { result, _ := value.(string); return result }

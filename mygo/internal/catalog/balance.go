package catalog

import (
	"context"
	"errors"
	"fmt"
	"html"
	"io"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
)

const defaultSource = "https://leagueoflegends.fandom.com/wiki/Module:ChampionData/data"

type Balance map[string]float64
type BalanceType struct {
	ID      int                `json:"id"`
	Balance map[string]Balance `json:"balance"`
}
type Service struct {
	source string
	mu     sync.RWMutex
	cached map[string]BalanceType
}

func New(sourceURL string) *Service {
	if sourceURL == "" {
		sourceURL = defaultSource
	}
	return &Service{source: sourceURL}
}

func (s *Service) FetchBalance(ctx context.Context, httpClient *http.Client) (map[string]BalanceType, error) {
	if httpClient == nil {
		httpClient = http.DefaultClient
	}
	endpoints := []string{s.source}
	if parsed, err := url.Parse(s.source); err == nil && parsed.Query().Get("action") != "raw" {
		query := parsed.Query()
		query.Set("action", "raw")
		parsed.RawQuery = query.Encode()
		endpoints = append(endpoints, parsed.String())
	}
	var lastErr error
	for _, endpoint := range endpoints {
		request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
		if err != nil {
			return nil, err
		}
		request.Header.Set("User-Agent", "LeagueAkari-MyGo/0.5 (+https://github.com/egoist/mygo)")
		response, err := httpClient.Do(request)
		if err != nil {
			lastErr = err
			continue
		}
		data, readErr := io.ReadAll(io.LimitReader(response.Body, 24<<20))
		response.Body.Close()
		if response.StatusCode < 200 || response.StatusCode >= 300 {
			lastErr = fmt.Errorf("平衡数据源返回 HTTP %d", response.StatusCode)
			continue
		}
		if readErr != nil {
			lastErr = readErr
			continue
		}
		balance, err := ParseBalance(string(data))
		if err != nil {
			lastErr = err
			continue
		}
		s.mu.Lock()
		s.cached = balance
		s.mu.Unlock()
		return copyBalance(balance), nil
	}
	return nil, lastErr
}

func (s *Service) Snapshot() map[string]BalanceType {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return copyBalance(s.cached)
}
func copyBalance(source map[string]BalanceType) map[string]BalanceType {
	if source == nil {
		return nil
	}
	out := make(map[string]BalanceType, len(source))
	for id, entry := range source {
		modes := map[string]Balance{}
		for mode, stats := range entry.Balance {
			values := Balance{}
			for key, value := range stats {
				values[key] = value
			}
			modes[mode] = values
		}
		out[id] = BalanceType{ID: entry.ID, Balance: modes}
	}
	return out
}

var preBlock = regexp.MustCompile(`(?is)<pre\b([^>]*)>(.*?)</pre\s*>`)
var htmlTags = regexp.MustCompile(`(?s)<[^>]*>`)

func luaSource(source string) (string, error) {
	trimmed := strings.TrimSpace(source)
	if !strings.HasPrefix(trimmed, "<") {
		return source, nil
	}
	for _, match := range preBlock.FindAllStringSubmatch(source, -1) {
		attributes := strings.ToLower(match[1])
		if strings.Contains(attributes, "mw-code") && strings.Contains(attributes, "mw-script") {
			return html.UnescapeString(htmlTags.ReplaceAllString(match[2], "")), nil
		}
	}
	return "", errors.New("平衡数据页面未包含 Lua 源码")
}

// ParseBalance follows the original Fandom schema. These legacy modes are
// independent of KIWI, whose balance data must come from its own source.
func ParseBalance(source string) (map[string]BalanceType, error) {
	raw, err := luaSource(source)
	if err != nil {
		return nil, err
	}
	all, err := parseLua(raw)
	if err != nil {
		return nil, err
	}
	modes := []string{"aram", "ar", "nb", "ofa", "urf", "usb"}
	properties := []string{"dmg_dealt", "dmg_taken", "healing", "shielding", "ability_haste", "mana_regen", "energy_regen", "attack_speed", "movement_speed", "tenacity"}
	out := map[string]BalanceType{}
	for _, value := range all {
		champion, ok := value.(luaTable)
		if !ok {
			continue
		}
		stats, ok := champion["stats"].(luaTable)
		if !ok {
			continue
		}
		id, ok := champion["id"].(float64)
		if !ok || id <= 0 || math.IsInf(id, 0) || math.IsNaN(id) {
			continue
		}
		balance := map[string]Balance{}
		for _, mode := range modes {
			fields, ok := stats[mode].(luaTable)
			if !ok {
				continue
			}
			adjustment := Balance{}
			for _, property := range properties {
				value, ok := fields[property].(float64)
				if !ok || math.IsInf(value, 0) || math.IsNaN(value) {
					continue
				}
				if (property == "dmg_dealt" || property == "dmg_taken" || property == "tenacity") && value == 1 {
					continue
				}
				adjustment[property] = value
			}
			if len(adjustment) > 0 {
				balance[mode] = adjustment
			}
		}
		if len(balance) > 0 {
			intID := int(math.Floor(id))
			out[strconv.Itoa(intID)] = BalanceType{ID: intID, Balance: balance}
		}
	}
	if len(out) == 0 {
		return nil, errors.New("平衡数据源没有可识别的英雄模式调整")
	}
	return out, nil
}

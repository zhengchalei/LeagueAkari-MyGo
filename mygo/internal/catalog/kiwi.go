package catalog

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const KiwiSourceURL = "https://www.bilibili.com/toy/resg/index.html"

type KiwiAdjustment struct {
	Type           string  `json:"type"`
	Value          float64 `json:"value"`
	Display        string  `json:"display"`
	EffectType     string  `json:"effectType"`
	Effect         string  `json:"effect"`
	Description    string  `json:"description,omitempty"`
	FormattedValue string  `json:"formattedValue,omitempty"`
}

type KiwiChampionBalance struct {
	ID          int              `json:"id"`
	Adjustments []KiwiAdjustment `json:"adjustments"`
}

type KiwiSnapshot struct {
	Balance     map[string]KiwiChampionBalance `json:"balance"`
	Version     string                         `json:"version"`
	SourceURL   string                         `json:"sourceUrl"`
	CollectedAt string                         `json:"collectedAt"`
	Revision    string                         `json:"revision"`
	LastUpdate  int64                          `json:"lastUpdate"`
	Cached      bool                           `json:"cached"`
}

var kiwiIframe = regexp.MustCompile(`<iframe\b[^>]*\bsrc=["']([^"']+)["']`)
var kiwiNumber = regexp.MustCompile(`^[+-]?(?:\d+(?:\.\d+)?|\.\d+)%?$`)

// RESG publishes JSON inside an ES module. Decode only the literal payload;
// remote JavaScript is never evaluated in either the host or the renderer.
func decodeKiwiModule(data []byte, target any) error {
	data = bytes.TrimSpace(bytes.TrimPrefix(data, []byte("\xef\xbb\xbf")))
	if !bytes.HasPrefix(data, []byte("export default ")) {
		return errors.New("RESG 数据模块格式不匹配")
	}
	data = bytes.TrimSpace(bytes.TrimPrefix(data, []byte("export default ")))
	data = bytes.TrimSpace(bytes.TrimSuffix(data, []byte(";")))
	return json.Unmarshal(data, target)
}

func ParseKiwiChampion(data []byte, expectedID int) (KiwiChampionBalance, error) {
	var detail struct {
		Champion struct {
			ID int `json:"id"`
		} `json:"champion"`
		Balance map[string]json.RawMessage `json:"bb"`
	}
	if err := decodeKiwiModule(data, &detail); err != nil {
		return KiwiChampionBalance{}, err
	}
	if expectedID <= 0 || detail.Champion.ID != expectedID {
		return KiwiChampionBalance{}, errors.New("RESG 英雄 ID 不匹配")
	}
	result := KiwiChampionBalance{ID: expectedID, Adjustments: []KiwiAdjustment{}}
	labels := make([]string, 0, len(detail.Balance))
	for label := range detail.Balance {
		labels = append(labels, label)
	}
	sort.Strings(labels)
	for _, label := range labels {
		var raw string
		if err := json.Unmarshal(detail.Balance[label], &raw); err != nil {
			var number float64
			if err := json.Unmarshal(detail.Balance[label], &number); err != nil {
				return KiwiChampionBalance{}, fmt.Errorf("RESG %s 字段格式无效", label)
			}
			raw = strconv.FormatFloat(number, 'f', -1, 64)
		}
		raw = strings.TrimSpace(raw)
		field := map[string]string{
			"造成伤害": "damage-dealt", "承受伤害": "damage-taken", "受到伤害": "damage-taken", "所受伤害": "damage-taken",
			"治疗效果": "healing", "护盾效果": "shielding", "技能急速": "ability-haste", "韧性": "tenacity",
			"攻击速度增长": "attack-speed-growth", "施法资源回复": "resource-regen",
		}[label]
		if field == "" || !kiwiNumber.MatchString(raw) {
			result.Adjustments = append(result.Adjustments, KiwiAdjustment{Type: "special", Display: "literal", EffectType: "neutral", Effect: "neutral", Description: label + " " + raw})
			continue
		}
		value, err := strconv.ParseFloat(strings.TrimSuffix(raw, "%"), 64)
		if err != nil {
			return KiwiChampionBalance{}, err
		}
		entry := KiwiAdjustment{Type: field, Value: value, Display: "literal", EffectType: "buff", FormattedValue: raw}
		baseline := 0.0
		if strings.HasSuffix(raw, "%") && field != "attack-speed-growth" {
			if strings.HasPrefix(raw, "+") || strings.HasPrefix(raw, "-") {
				entry.Value = 1 + value/100
			} else {
				entry.Value = value / 100
			}
			entry.Display = "percentage"
			baseline = 1
		}
		if field == "damage-taken" {
			entry.EffectType = "nerf"
		}
		if entry.Value == baseline {
			continue
		}
		if (entry.Value > baseline) == (entry.EffectType == "buff") {
			entry.Effect = "buffed"
		} else {
			entry.Effect = "nerfed"
		}
		result.Adjustments = append(result.Adjustments, entry)
	}
	return result, nil
}

func fetchKiwiFile(ctx context.Context, httpClient *http.Client, location string) ([]byte, error) {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, location, nil)
	if err != nil {
		return nil, err
	}
	request.Header.Set("User-Agent", "LeagueAkari-MyGo (+https://github.com/zhengchalei/LeagueAkari-MyGo)")
	response, err := httpClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("RESG 数据请求返回 HTTP %d", response.StatusCode)
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, (2<<20)+1))
	if len(data) > 2<<20 {
		return nil, errors.New("RESG 数据文件超过大小限制")
	}
	return data, err
}

// FetchKiwiBalance resolves the current toy revision, then checks its data version.
// Champion detail files are downloaded at most three at a time, only when the
// revision or dataset changes. A partial update never replaces a complete snapshot.
func FetchKiwiBalance(ctx context.Context, httpClient *http.Client, sourceURL string, previous KiwiSnapshot) (KiwiSnapshot, error) {
	page, err := fetchKiwiFile(ctx, httpClient, sourceURL)
	if err != nil {
		return KiwiSnapshot{}, err
	}
	match := kiwiIframe.FindSubmatch(page)
	if len(match) != 2 {
		return KiwiSnapshot{}, errors.New("未找到 RESG 数据页面")
	}
	source, err := url.Parse(sourceURL)
	if err != nil {
		return KiwiSnapshot{}, err
	}
	content, err := source.Parse(string(match[1]))
	if err != nil || (content.Hostname() != source.Hostname() && content.Hostname() != "www.bilibilitoy.com") {
		return KiwiSnapshot{}, errors.New("RESG 数据页面来源不匹配")
	}
	base := content.ResolveReference(&url.URL{Path: "./"}).String()
	data, err := fetchKiwiFile(ctx, httpClient, base+"api/v1/versions.js")
	if err != nil {
		return KiwiSnapshot{}, err
	}
	var versions []struct {
		Version     string `json:"version"`
		CollectedAt string `json:"collectedAt"`
	}
	if err := decodeKiwiModule(data, &versions); err != nil || len(versions) == 0 || versions[0].Version == "" {
		return KiwiSnapshot{}, errors.New("RESG 数据版本无效")
	}
	latest := versions[0]
	if previous.Version == latest.Version && previous.CollectedAt == latest.CollectedAt && previous.Revision == base && len(previous.Balance) > 0 {
		previous.Cached = false
		previous.LastUpdate = time.Now().UnixMilli()
		return previous, nil
	}
	versionBase := base + "api/v1/versions/" + url.PathEscape(latest.Version) + "/"
	data, err = fetchKiwiFile(ctx, httpClient, versionBase+"champions.js")
	if err != nil {
		return KiwiSnapshot{}, err
	}
	var inventory struct {
		Items []struct {
			ID int `json:"id"`
		} `json:"items"`
	}
	if err := decodeKiwiModule(data, &inventory); err != nil || len(inventory.Items) == 0 {
		return KiwiSnapshot{}, errors.New("RESG 英雄列表无效")
	}
	for _, champion := range inventory.Items {
		if champion.ID <= 0 {
			return KiwiSnapshot{}, errors.New("RESG 英雄列表包含无效 ID")
		}
	}
	result := KiwiSnapshot{Balance: map[string]KiwiChampionBalance{}, Version: latest.Version, SourceURL: sourceURL, Revision: base, CollectedAt: latest.CollectedAt}
	workCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	jobs := make(chan int)
	var wg sync.WaitGroup
	var mu sync.Mutex
	var firstErr error
	for worker := 0; worker < 3; worker++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for id := range jobs {
				data, err := fetchKiwiFile(workCtx, httpClient, versionBase+"champions/"+strconv.Itoa(id)+".js")
				var champion KiwiChampionBalance
				if err == nil {
					champion, err = ParseKiwiChampion(data, id)
				}
				mu.Lock()
				if err != nil {
					if firstErr == nil {
						firstErr = err
						cancel()
					}
				} else {
					result.Balance[strconv.Itoa(id)] = champion
				}
				mu.Unlock()
			}
		}()
	}
sendJobs:
	for _, champion := range inventory.Items {
		select {
		case jobs <- champion.ID:
		case <-workCtx.Done():
			break sendJobs
		}
	}
	close(jobs)
	wg.Wait()
	if firstErr != nil {
		return KiwiSnapshot{}, firstErr
	}
	if ctx.Err() != nil {
		return KiwiSnapshot{}, ctx.Err()
	}
	if len(result.Balance) != len(inventory.Items) {
		return KiwiSnapshot{}, errors.New("RESG 英雄数据不完整")
	}
	result.LastUpdate = time.Now().UnixMilli()
	return result, nil
}

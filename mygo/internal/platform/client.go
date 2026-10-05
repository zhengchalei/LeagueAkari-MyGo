package platform

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func (s *Service) peekClient(ctx context.Context, value any) (any, error) {
	auth, err := s.ResolveAuth(ctx, value)
	if err != nil {
		return nil, nil
	}
	connection := client.NewWithOptions(nil, client.Options{HTTPClient: s.options.GameHTTPClient})
	connection.SetAuth(auth)
	summoner, err := connection.JSON(ctx, http.MethodGet, "/lol-summoner/v1/current-summoner", nil)
	if err != nil {
		return nil, nil
	}
	iconID := client.Number(client.Map(summoner)["profileIconId"])
	request := httptest.NewRequest(http.MethodGet, fmt.Sprintf("/lol-game-data/assets/v1/profile-icons/%d.jpg", iconID), nil).WithContext(ctx)
	response := httptest.NewRecorder()
	connection.Proxy(response, request)
	if response.Code >= 400 {
		return nil, nil
	}
	contentType := response.Header().Get("Content-Type")
	if contentType == "" {
		contentType = "image/jpeg"
	}
	return map[string]any{"summoner": summoner, "profileIcon": "data:" + contentType + ";base64," + base64.StdEncoding.EncodeToString(response.Body.Bytes())}, nil
}

func (s *Service) writeItemSets(ctx context.Context, value any, clearPrevious bool) error {
	configPath, err := s.configPath(ctx)
	if err != nil {
		return err
	}
	directory := filepath.Join(filepath.Dir(configPath), "Global", "Recommended")
	var items []map[string]json.RawMessage
	if err := decode(value, &items); err != nil {
		return fmt.Errorf("装备方案格式错误: %w", err)
	}
	type itemFile struct {
		name string
		data []byte
	}
	files := make([]itemFile, 0, len(items))
	for _, item := range items {
		var uid string
		if json.Unmarshal(item["uid"], &uid) != nil || uid == "" || filepath.Base(uid) != uid || strings.ContainsAny(uid, `/\:`) {
			return errors.New("装备方案 uid 不是有效文件名")
		}
		data, err := json.Marshal(item)
		if err != nil {
			return err
		}
		files = append(files, itemFile{uid + ".json", data})
	}
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	if clearPrevious {
		entries, err := os.ReadDir(directory)
		if err != nil {
			return err
		}
		for _, entry := range entries {
			if !entry.IsDir() && (strings.HasPrefix(entry.Name(), "akari1") || strings.HasPrefix(entry.Name(), "timo1")) {
				if err := os.Remove(filepath.Join(directory, entry.Name())); err != nil {
					return err
				}
			}
		}
	}
	for _, file := range files {
		if err := ctx.Err(); err != nil {
			return err
		}
		if err := os.WriteFile(filepath.Join(directory, file.name), file.data, 0600); err != nil {
			return err
		}
	}
	return nil
}

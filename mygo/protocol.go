package main

import (
	"context"
	"encoding/json"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
)

func (d *Desktop) proxy(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Access-Control-Allow-Origin", "http://mygo.localhost")
	w.Header().Set("Access-Control-Allow-Headers", "*")
	w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS")
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	path := strings.TrimPrefix(r.URL.Path, "/")
	if id := r.Header.Get("x-akari-proxy-request-id"); id != "" {
		ctx, cancel := context.WithCancel(r.Context())
		d.proxyRequests.Store(id, cancel)
		defer func() { cancel(); d.proxyRequests.Delete(id) }()
		r = r.WithContext(ctx)
	}
	domain, rest, _ := strings.Cut(path, "/")
	clone := r.Clone(r.Context())
	u := *r.URL
	clone.URL = &u
	clone.URL.Path = "/" + rest
	switch domain {
	case "league-client":
		d.client.Proxy(w, clone)
	case "sgp":
		d.client.SGPProxy(w, clone)
	case "riot-client":
		d.playerAccountProxy(w, clone)
	case "game-client":
		d.platform.GameProxy(w, clone)
	case "local-image":
		serveLocalImage(w, r)
	default:
		http.Error(w, "未知数据接口", http.StatusNotFound)
	}
}

func serveLocalImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	imageURL, err := url.Parse(r.URL.Query().Get("url"))
	if err != nil || imageURL.Scheme != "file" || (imageURL.Host != "" && imageURL.Host != "localhost") {
		http.Error(w, "无效本地图片", 400)
		return
	}
	path := filepath.FromSlash(strings.TrimPrefix(imageURL.Path, "/"))
	if !filepath.IsAbs(path) {
		http.Error(w, "图片需要绝对路径", 400)
		return
	}
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".avif", ".ico":
	default:
		http.Error(w, "不支持的图片格式", 400)
		return
	}
	file, err := os.Open(path)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() {
		http.NotFound(w, r)
		return
	}
	http.ServeContent(w, r, info.Name(), info.ModTime(), file)
}

// National servers expose Riot IDs through LCU, so player lookups need no
// second authenticated Riot Client connection or credentials in the renderer.
func (d *Desktop) playerAccountProxy(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	write := func(value any, err error) {
		if err != nil {
			http.Error(w, err.Error(), http.StatusBadGateway)
			return
		}
		_ = json.NewEncoder(w).Encode(value)
	}
	if r.Method == http.MethodPost && r.URL.Path == "/player-account/lookup/v1/namesets-for-puuids" {
		var request struct {
			Puuids []string `json:"puuids"`
		}
		if json.NewDecoder(io.LimitReader(r.Body, 64*1024)).Decode(&request) != nil || len(request.Puuids) > 100 {
			http.Error(w, "无效玩家查询", 400)
			return
		}
		namesets := []any{}
		for _, puuid := range request.Puuids {
			value, err := d.client.JSON(r.Context(), http.MethodGet, "/lol-summoner/v2/summoners/puuid/"+puuid, nil)
			if err != nil {
				write(nil, err)
				return
			}
			summoner := client.Map(value)
			namesets = append(namesets, object{"puuid": puuid, "error": "", "gnt": object{"gameName": summoner["gameName"], "tagLine": summoner["tagLine"], "shadowGnt": false}})
		}
		write(object{"namesets": namesets}, nil)
		return
	}
	if r.Method == http.MethodGet && r.URL.Path == "/player-account/aliases/v1/lookup" {
		query := r.URL.Query()
		value, err := d.client.JSON(r.Context(), http.MethodPost, "/lol-summoner/v1/summoners/aliases", []any{object{"gameName": query.Get("gameName"), "tagLine": query.Get("tagLine")}})
		aliases := []any{}
		for _, item := range client.List(value) {
			row := client.Map(item)
			aliases = append(aliases, object{"puuid": row["puuid"], "alias": object{"game_name": row["gameName"], "tag_line": row["tagLine"]}})
		}
		write(aliases, err)
		return
	}
	// National clients expose the remaining Riot-client routes through their LCU host.
	d.client.Proxy(w, r)
}

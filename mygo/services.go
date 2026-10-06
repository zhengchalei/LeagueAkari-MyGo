package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/automation"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/catalog"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	selfupdate "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/update"
)

func (d *Desktop) migrateStorage() {
	base, err := os.UserConfigDir()
	if err != nil {
		return
	}
	paths := []string{filepath.Join(base, "Timo", "LeagueAkari.db"), filepath.Join(base, "league-akari", "LeagueAkari.db")}
	report, err := d.player.Migration(paths)
	if err != nil {
		log.Printf("Legacy migration: %v", err)
		d.emit("storage-main", "migration-error", err.Error())
		return
	}
	log.Printf("Legacy migration: %+v", report)
}
func (d *Desktop) prepareQuit(dataDirectory string) {
	if d.externalClient != nil {
		d.externalClient.CloseIdleConnections()
	}
	if d.platform != nil {
		d.platform.Close()
	}
	if d.game != nil {
		d.game.Close()
	}
	if d.player != nil {
		d.player.Close()
	}
	if d.updater != nil && d.updater.Prepared() != "" {
		executable, err := os.Executable()
		if err == nil {
			err = selfupdate.LaunchApply(d.updater.Prepared(), executable, dataDirectory)
		}
		if err != nil {
			log.Printf("Update apply: %v", err)
		}
	}
}
func (d *Desktop) updateCall(ctx context.Context, method string, args []any) (any, error) {
	switch method {
	case "checkUpdates":
		return d.updater.Check(ctx)
	case "startUpdate":
		if updateRepository() == "" {
			return object{"result": "failed", "reason": "LEAGUE_AKARI_MYGO_UPDATE_SOURCE_NOT_CONFIGURED"}, nil
		}
		return d.updater.Start(ctx, false)
	case "forceStartUpdate":
		if updateRepository() == "" {
			return object{"result": "failed", "reason": "LEAGUE_AKARI_MYGO_UPDATE_SOURCE_NOT_CONFIGURED"}, nil
		}
		return d.updater.Start(ctx, true)
	case "cancelUpdate":
		return d.updater.Cancel(), nil
	case "openNewUpdatesDir":
		dir, _ := mygo.App.Path(mygo.PathUserData)
		path := filepath.Join(dir, "new-updates")
		if err := os.MkdirAll(path, 0700); err != nil {
			return nil, err
		}
		return nil, mygo.Shell.OpenPath(path)
	case "uninstallApp":
		return d.uninstall(ctx)
	}
	return nil, fmt.Errorf("未知更新操作：%s", method)
}
func (d *Desktop) uninstall(ctx context.Context) (any, error) {
	response, err := mygo.Dialog.Message(mygo.MessageOptions{Parent: mygo.CallerWindow(ctx), Type: mygo.MessageWarning, Title: "卸载 LeagueAkari-MyGo", Message: "删除当前 LeagueAkari-MyGo 程序？", Detail: "便携版仅删除 LeagueAkari-MyGo.exe。玩家记录、配置和日志默认保留。", Buttons: []string{"取消", "卸载"}, DefaultButton: 0, CancelButton: 0, CheckboxLabel: "同时删除玩家记录、配置和日志"})
	if err != nil {
		return nil, err
	}
	if response.Button != 1 {
		return object{"result": "cancelled"}, nil
	}
	target, err := os.Executable()
	if err != nil {
		return nil, err
	}
	dataDirectory, _ := mygo.App.Path(mygo.PathUserData)
	if err = selfupdate.LaunchUninstall(target, dataDirectory, response.CheckboxChecked); err != nil {
		return nil, err
	}
	d.updater.Cancel()
	mygo.App.Quit()
	return object{"result": "ok"}, nil
}
func (d *Desktop) externalHTTP() *http.Client {
	if d.externalClient != nil {
		return d.externalClient
	}
	return newExternalHTTPClient(d.store)
}
func (d *Desktop) fetchJSON(ctx context.Context, location string) (any, error) {
	request, err := http.NewRequestWithContext(ctx, "GET", location, nil)
	if err != nil {
		return nil, err
	}
	request.Header.Set("User-Agent", "LeagueAkari-MyGo/"+appVersion)
	httpClient := d.externalHTTP()
	defer httpClient.CloseIdleConnections()
	response, err := httpClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, fmt.Errorf("数据服务返回 %d", response.StatusCode)
	}
	var value any
	err = json.NewDecoder(io.LimitReader(response.Body, 8<<20)).Decode(&value)
	return value, err
}
func (d *Desktop) apiCall(ctx context.Context, method string, args []any) (any, error) {
	base := client.String(asObject(d.state("akari-api-main", "state")["baseUrls"])["api"])
	if base == "" {
		base = "https://akari-api.yuru-yuri.com"
	}
	language := textArg(args, 0)
	if language == "" {
		language = "zh-CN"
	}
	location := ""
	switch method {
	case "getLatestNotice":
		location = "/notice/v1/latest?lang=" + url.QueryEscape(language)
	case "getConfig":
		resource := textArg(args, 0)
		valid := map[string]bool{"auto-select/groups": true, "ongoing-game/config": true, "sgp/league-servers": true, "sgp/supported-queues": true}
		if !valid[resource] {
			return nil, fmt.Errorf("无效配置资源")
		}
		location = "/config/v1/" + resource
	case "getLatestRelease", "getLastResortLatestRelease":
		_, err := d.updater.Check(ctx)
		return d.updater.Latest(), err
	case "getRelease":
		location = "/releases/v1/" + url.PathEscape(textArg(args, 0)) + "?lang=" + url.QueryEscape(textArg(args, 1))
	case "postStatisticsRecord":
		body, _ := json.Marshal(object{"version": textArg(args, 0)})
		request, err := http.NewRequestWithContext(ctx, "POST", base+"/statistics/v1/records", bytes.NewReader(body))
		if err != nil {
			return false, err
		}
		request.Header.Set("Content-Type", "application/json")
		httpClient := d.externalHTTP()
		defer httpClient.CloseIdleConnections()
		response, err := httpClient.Do(request)
		if err != nil {
			return false, err
		}
		response.Body.Close()
		return response.StatusCode >= 200 && response.StatusCode < 300, nil
	default:
		return nil, fmt.Errorf("未知数据服务操作：%s", method)
	}
	return d.fetchJSON(ctx, base+location)
}
func (d *Desktop) remoteCall(ctx context.Context, method string, args []any) (any, error) {
	if method != "testRepoLatency" {
		return nil, fmt.Errorf("未知配置操作：%s", method)
	}
	result := object{}
	var mu sync.Mutex
	var wg sync.WaitGroup
	for name, location := range map[string]string{"githubLatency": "https://api.github.com", "giteeLatency": "https://gitee.com/api/v5"} {
		wg.Add(1)
		go func(name, location string) {
			defer wg.Done()
			sum, count := float64(0), 0
			httpClient := d.externalHTTP()
			defer httpClient.CloseIdleConnections()
			for i := 0; i < 3; i++ {
				started := time.Now()
				request, _ := http.NewRequestWithContext(ctx, "HEAD", location, nil)
				response, err := httpClient.Do(request)
				if err == nil {
					response.Body.Close()
					sum += float64(time.Since(started).Milliseconds())
					count++
				}
			}
			value := float64(-1)
			if count > 0 {
				value = sum / float64(count)
			}
			mu.Lock()
			result[name] = value
			mu.Unlock()
		}(name, location)
	}
	wg.Wait()
	return result, nil
}
func (d *Desktop) refreshExtraAssets(ctx context.Context) {
	fandom := catalog.New("")
	for ctx.Err() == nil {
		var wg sync.WaitGroup
		for key, location := range map[string]string{"heroList": "https://game.gtimg.cn/images/lol/act/img/js/heroList/hero_list.js", "kiwiAugments": "https://game.gtimg.cn/images/lol/act/img/js/kiwi/kiwi_augments.json"} {
			wg.Add(1)
			go func(key, location string) {
				defer wg.Done()
				value, err := d.fetchJSON(ctx, location)
				if err != nil {
					log.Printf("Gtimg %s: %v", key, err)
					return
				}
				d.setStatic("extra-assets-main:gtimg", key, value)
				d.update("extra-assets-main", "gtimg", key, value)
			}(key, location)
		}
		wg.Wait()
		httpClient := d.externalHTTP()
		balance, err := fandom.FetchBalance(ctx, httpClient)
		httpClient.CloseIdleConnections()
		if err == nil {
			d.setStatic("extra-assets-main:fandom", "balance", balance)
			d.update("extra-assets-main", "fandom", "balance", balance)
		}
		if value, err := d.fetchJSON(ctx, "https://registry.npmjs.org/@leagueakari%2fbootstrap/latest"); err == nil {
			bootstrap := asObject(asObject(value)["akariBootstrap"])
			if bootstrap["schemaVersion"] == float64(1) && asObject(bootstrap["baseUrls"])["api"] != nil {
				d.setStatic("akari-api-main:state", "baseUrls", bootstrap["baseUrls"])
				d.update("akari-api-main", "state", "baseUrls", bootstrap["baseUrls"])
			}
		}
		d.refreshRemoteResources(ctx)
		if updateRepository() != "" && d.settingValue("remote-config-main", "updateLatestRelease") != false {
			if _, err := d.updater.Check(ctx); err != nil {
				log.Printf("Update check: %v", err)
			}
			release := d.updater.Latest()
			if release != nil && release["isNew"] == true && d.settingValue("self-update-main", "autoDownloadUpdates") == true && d.settingValue("self-update-main", "ignoreVersion") != release["version"] {
				_, _ = d.updater.Start(ctx, false)
			}
		}
		timer := time.NewTimer(30 * time.Minute)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
	}
}

func (d *Desktop) refreshRemoteResources(ctx context.Context) {
	source := "https://raw.githubusercontent.com/LeagueAkari/LeagueAkari-Config/refs/heads/main/config/"
	if d.settingValue("remote-config-main", "preferredSource") == "gitee" {
		source = "https://gitee.com/LeagueAkari/LeagueAkari-Config/raw/main/config/"
	}
	resources := map[string]string{"leagueServers": "sgp/league-servers", "supportedQueues": "sgp/supported-queues", "ongoingGameConfig": "ongoing-game/config", "autoSelectGroups": "auto-select/groups"}
	directory, _ := mygo.App.Path(mygo.PathUserData)
	var wg sync.WaitGroup
	for key, path := range resources {
		wg.Add(1)
		go func(key, path string) {
			defer wg.Done()
			cache := filepath.Join(directory, "remote-config", key+".json")
			var value any
			var err error
			if data, readErr := os.ReadFile(cache); readErr == nil {
				if json.Unmarshal(data, &value) == nil {
					d.applyRemoteResource(key, asObject(value))
				}
			}
			value, err = d.fetchJSON(ctx, source+path+".json")
			if err != nil {
				return
			}
			if d.applyRemoteResource(key, asObject(value)) {
				data, _ := json.Marshal(value)
				os.MkdirAll(filepath.Dir(cache), 0700)
				os.WriteFile(cache, data, 0600)
			}
		}(key, path)
	}
	wg.Wait()
	language := client.String(d.settingValue("app-common-main", "locale"))
	if language != "en" {
		language = "zh-CN"
	}
	location := strings.TrimSuffix(source, "config/") + "announcement/" + language + ".md"
	httpClient := d.externalHTTP()
	defer httpClient.CloseIdleConnections()
	request, _ := http.NewRequestWithContext(ctx, "GET", location, nil)
	response, err := httpClient.Do(request)
	if err != nil {
		return
	}
	defer response.Body.Close()
	if response.StatusCode == 200 {
		data, err := io.ReadAll(io.LimitReader(response.Body, 256<<10))
		if err == nil {
			content := string(data)
			announcement := parseAnnouncement(content)
			d.setStatic("remote-config-main:state", "announcement", announcement)
			d.update("remote-config-main", "state", "announcement", announcement)
		}
	}
}

func parseAnnouncement(raw string) object {
	content := strings.ReplaceAll(raw, "\r\n", "\n")
	front := object{}
	if strings.HasPrefix(content, "---\n") {
		if end := strings.Index(content[4:], "\n---"); end >= 0 {
			for _, line := range strings.Split(content[4:4+end], "\n") {
				key, value, ok := strings.Cut(line, ":")
				key = strings.TrimSpace(key)
				if ok && (key == "summary" || key == "alertLevel" || key == "title") {
					value = strings.Trim(strings.TrimSpace(value), "\"'")
					front[key] = value
				}
			}
			content = strings.TrimLeft(content[4+end+4:], "\n")
		}
	}
	hash := sha256.Sum256([]byte(raw))
	return object{"content": content, "frontMatter": front, "uniqueId": hex.EncodeToString(hash[:])}
}
func (d *Desktop) applyRemoteResource(key string, value object) bool {
	if len(value) == 0 {
		return false
	}
	switch key {
	case "leagueServers":
		encoded, _ := json.Marshal(value["servers"])
		servers := map[string]client.Server{}
		if json.Unmarshal(encoded, &servers) != nil || len(servers) == 0 {
			return false
		}
		for _, server := range servers {
			for _, endpoint := range []string{server.Common, server.MatchHistory} {
				if endpoint == "" {
					continue
				}
				parsed, err := url.Parse(endpoint)
				if err != nil || parsed.Scheme != "https" || parsed.Host == "" || parsed.User != nil {
					return false
				}
			}
		}
		d.client.SetServers(servers)
	case "supportedQueues":
		if value["queues"] == nil {
			return false
		}
	case "autoSelectGroups":
		var groups []automation.SelectGroup
		encoded, _ := json.Marshal(value["groups"])
		if json.Unmarshal(encoded, &groups) != nil || len(groups) == 0 {
			return false
		}
		d.automation.SetSelectGroups(groups)
	case "ongoingGameConfig":
		if value["spotlight"] == nil {
			return false
		}
	}
	d.setStatic("remote-config-main:state", key, value)
	d.update("remote-config-main", "state", key, value)
	return true
}

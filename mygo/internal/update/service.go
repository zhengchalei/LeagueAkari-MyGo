package update

import (
	"archive/zip"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
)

type Options struct {
	Directory, Version, Repository, APIBase string
	HTTP                                    *http.Client
	Emit                                    bridge.Emitter
}

const executableName = "LeagueAkari-MyGo.exe"
const legacyExecutableName = "Timo-MyGo.exe"

func portableExecutableName(name string) bool {
	return name == executableName || name == legacyExecutableName
}

func archivePriority(name, version string) int {
	name = strings.ToLower(name)
	version = strings.ToLower(strings.TrimPrefix(version, "v"))
	if name == "leagueakari-mygo-"+version+"-win-x64.zip" || name == "leagueakari-mygo-v"+version+"-win-x64.zip" {
		return 2
	}
	if strings.HasPrefix(name, "timo-") && strings.Contains(name, "mygo") && strings.Contains(name, "win-x64") && strings.HasSuffix(name, ".zip") && !strings.Contains(name, "electron") {
		return 1
	}
	return 0
}

type Service struct {
	mu       sync.Mutex
	options  Options
	state    map[string]any
	release  map[string]any
	cancel   context.CancelFunc
	prepared string
}

func New(options Options) *Service {
	if options.HTTP == nil {
		options.HTTP = &http.Client{Timeout: 30 * time.Minute}
	}
	if options.APIBase == "" {
		options.APIBase = "https://api.github.com"
	}
	service := &Service{options: options, state: map[string]any{"lastCheckAt": 0, "updateProgressInfo": nil, "lastUpdateResult": nil}}
	if bytes, err := os.ReadFile(filepath.Join(filepath.Dir(options.Directory), "last-update-result.json")); err == nil {
		var result any
		if json.Unmarshal(bytes, &result) == nil {
			service.state["lastUpdateResult"] = result
		}
		os.Remove(filepath.Join(filepath.Dir(options.Directory), "last-update-result.json"))
	}
	return service
}
func (s *Service) State() map[string]any {
	s.mu.Lock()
	defer s.mu.Unlock()
	copy := map[string]any{}
	for k, v := range s.state {
		copy[k] = v
	}
	return copy
}
func (s *Service) Latest() map[string]any { s.mu.Lock(); defer s.mu.Unlock(); return s.release }
func (s *Service) set(key string, value any) {
	s.mu.Lock()
	s.state[key] = value
	s.mu.Unlock()
	if s.options.Emit != nil {
		s.options.Emit("mobx-utils-main", "update-state-prop/self-update-main:state", key, value, map[string]any{"action": "update", "raw": true})
	}
}
func result(status, reason string) map[string]any {
	r := map[string]any{"result": status}
	if reason != "" {
		r["reason"] = reason
	}
	return r
}
func newer(version, current string) bool {
	parts := func(v string) []int {
		values := []int{}
		for _, part := range strings.Split(strings.TrimPrefix(v, "v"), ".") {
			n, _ := strconv.Atoi(strings.SplitN(part, "-", 2)[0])
			values = append(values, n)
		}
		return values
	}
	a, b := parts(version), parts(current)
	for i := 0; i < len(a) || i < len(b); i++ {
		x, y := 0, 0
		if i < len(a) {
			x = a[i]
		}
		if i < len(b) {
			y = b[i]
		}
		if x != y {
			return x > y
		}
	}
	return false
}
func (s *Service) Check(ctx context.Context) (map[string]any, error) {
	if s.options.Repository == "" {
		return result("failed", "LEAGUE_AKARI_MYGO_UPDATE_SOURCE_NOT_CONFIGURED"), nil
	}
	if len(strings.Split(s.options.Repository, "/")) != 2 || strings.ContainsAny(s.options.Repository, "?\\# ") {
		return nil, errors.New("无效更新仓库")
	}
	request, err := http.NewRequestWithContext(ctx, "GET", s.options.APIBase+"/repos/"+s.options.Repository+"/releases/latest", nil)
	if err != nil {
		return nil, err
	}
	request.Header.Set("Accept", "application/vnd.github+json")
	request.Header.Set("User-Agent", "LeagueAkari-MyGo/"+s.options.Version)
	response, err := s.options.HTTP.Do(request)
	if err != nil {
		return result("failed", err.Error()), nil
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return result("failed", fmt.Sprintf("更新接口返回 %d", response.StatusCode)), nil
	}
	var release struct {
		Tag       string `json:"tag_name"`
		Body      string `json:"body"`
		Published string `json:"published_at"`
		Assets    []struct {
			Name        string `json:"name"`
			Size        int64  `json:"size"`
			URL         string `json:"browser_download_url"`
			ContentType string `json:"content_type"`
		} `json:"assets"`
	}
	if err = json.NewDecoder(io.LimitReader(response.Body, 4<<20)).Decode(&release); err != nil {
		return nil, err
	}
	var archive map[string]any
	priority := 0
	for _, asset := range release.Assets {
		if candidate := archivePriority(asset.Name, release.Tag); candidate > priority {
			archive = map[string]any{"name": asset.Name, "size": asset.Size, "downloadUrl": asset.URL, "contentType": asset.ContentType}
			priority = candidate
		}
	}
	latest := map[string]any{"version": release.Tag, "currentVersion": s.options.Version, "isNew": newer(release.Tag, s.options.Version), "source": "github", "publishedAt": release.Published, "description": release.Body, "archiveFile": archive}
	s.mu.Lock()
	s.release = latest
	s.mu.Unlock()
	s.set("lastCheckAt", time.Now().UnixMilli())
	if s.options.Emit != nil {
		s.options.Emit("mobx-utils-main", "update-state-prop/remote-config-main:state", "latestRelease", latest, map[string]any{"action": "update", "raw": true})
	}
	if latest["isNew"] == true {
		if archive == nil {
			return result("failed", "未找到 MyGo Windows x64 更新包"), nil
		}
		return result("new-updates", ""), nil
	}
	return result("no-updates", ""), nil
}
func (s *Service) Start(ctx context.Context, force bool) (map[string]any, error) {
	s.mu.Lock()
	latest := s.release
	if s.cancel != nil || s.prepared != "" {
		s.mu.Unlock()
		return result("no-op", ""), nil
	}
	s.mu.Unlock()
	if latest == nil || (!force && latest["isNew"] != true) {
		return result("no-op", ""), nil
	}
	archive, _ := latest["archiveFile"].(map[string]any)
	if archive == nil {
		return result("failed", "未找到 MyGo 更新包"), nil
	}
	// Download lifetime belongs to the job, not to a completed IPC request.
	job, cancel := context.WithCancel(context.Background())
	s.mu.Lock()
	if s.cancel != nil || s.prepared != "" {
		s.mu.Unlock()
		cancel()
		return result("no-op", ""), nil
	}
	s.cancel = cancel
	s.mu.Unlock()
	go s.download(job, archive)
	return result("ok", ""), nil
}
func (s *Service) Cancel() map[string]any {
	s.mu.Lock()
	cancel := s.cancel
	s.prepared = ""
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	s.set("updateProgressInfo", nil)
	return result("ok", "")
}
func (s *Service) Prepared() string { s.mu.Lock(); defer s.mu.Unlock(); return s.prepared }
func (s *Service) download(ctx context.Context, archive map[string]any) {
	failed := func(err error) {
		if ctx.Err() == nil {
			s.set("updateProgressInfo", map[string]any{"phase": "download-failed", "downloadingProgress": 0, "averageDownloadSpeed": 0, "downloadTimeLeft": 0, "fileSize": archive["size"]})
			s.set("lastUpdateResult", map[string]any{"success": false, "reason": err.Error()})
		}
	}
	defer func() { s.mu.Lock(); s.cancel = nil; s.mu.Unlock() }()
	if err := os.MkdirAll(s.options.Directory, 0700); err != nil {
		failed(err)
		return
	}
	request, err := http.NewRequestWithContext(ctx, "GET", fmt.Sprint(archive["downloadUrl"]), nil)
	if err != nil {
		failed(err)
		return
	}
	response, err := s.options.HTTP.Do(request)
	if err != nil {
		failed(err)
		return
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		failed(fmt.Errorf("更新包返回 %d", response.StatusCode))
		return
	}
	path := filepath.Join(s.options.Directory, "update.zip.part")
	file, err := os.Create(path)
	if err != nil {
		failed(err)
		return
	}
	defer os.Remove(path)
	started := time.Now()
	var downloaded int64
	buffer := make([]byte, 64*1024)
	last := time.Time{}
	for {
		n, readErr := response.Body.Read(buffer)
		if n > 0 {
			written, writeErr := file.Write(buffer[:n])
			downloaded += int64(written)
			if writeErr != nil {
				file.Close()
				failed(writeErr)
				return
			}
		}
		if time.Since(last) > 150*time.Millisecond || readErr == io.EOF {
			last = time.Now()
			speed := float64(downloaded) / time.Since(started).Seconds()
			progress, left := float64(0), float64(0)
			if response.ContentLength > 0 {
				progress = float64(downloaded) / float64(response.ContentLength)
				if speed > 0 {
					left = float64(response.ContentLength-downloaded) / speed
				}
			}
			s.set("updateProgressInfo", map[string]any{"phase": "downloading", "downloadingProgress": progress, "averageDownloadSpeed": speed, "downloadTimeLeft": left, "fileSize": response.ContentLength})
		}
		if readErr != nil {
			file.Close()
			if readErr != io.EOF {
				failed(readErr)
				return
			}
			break
		}
	}
	if ctx.Err() != nil {
		return
	}
	destination := filepath.Join(s.options.Directory, "prepared")
	executable, err := prepareExecutable(path, destination)
	if err != nil {
		failed(err)
		return
	}
	s.mu.Lock()
	if ctx.Err() != nil {
		s.mu.Unlock()
		return
	}
	s.prepared = executable
	s.mu.Unlock()
	s.set("updateProgressInfo", map[string]any{"phase": "waiting-for-restart", "downloadingProgress": 1, "averageDownloadSpeed": 0, "downloadTimeLeft": 0, "fileSize": downloaded})
}

func prepareExecutable(archive, destination string) (string, error) {
	reader, err := zip.OpenReader(archive)
	if err != nil {
		return "", err
	}
	name := ""
	for _, entry := range reader.File {
		if !entry.FileInfo().IsDir() && portableExecutableName(entry.Name) && (name == "" || entry.Name == executableName) {
			name = entry.Name
		}
	}
	reader.Close()
	if name == "" {
		return "", fmt.Errorf("更新包缺少 %s", executableName)
	}
	if err = Extract(archive, destination); err != nil {
		return "", err
	}
	executable := filepath.Join(destination, name)
	if _, err = os.Stat(executable); err != nil {
		return "", err
	}
	return executable, nil
}

// Extract accepts the portable root layout and never writes beyond its destination.
func Extract(archive, destination string) error {
	reader, err := zip.OpenReader(archive)
	if err != nil {
		return err
	}
	defer reader.Close()
	for _, entry := range reader.File {
		name := filepath.FromSlash(strings.ReplaceAll(entry.Name, "\\", "/"))
		if entry.Mode()&os.ModeSymlink != 0 || filepath.IsAbs(name) || name == ".." || strings.HasPrefix(name, ".."+string(os.PathSeparator)) || strings.Contains(name, ":") {
			return errors.New("更新包含有无效路径")
		}
		path := filepath.Join(destination, name)
		relative, err := filepath.Rel(destination, path)
		if err != nil || strings.HasPrefix(relative, "..") {
			return errors.New("更新包路径越界")
		}
		if entry.FileInfo().IsDir() {
			if err = os.MkdirAll(path, 0700); err != nil {
				return err
			}
			continue
		}
		if entry.UncompressedSize64 > 256<<20 {
			return errors.New("更新包文件过大")
		}
		if err = os.MkdirAll(filepath.Dir(path), 0700); err != nil {
			return err
		}
		input, err := entry.Open()
		if err != nil {
			return err
		}
		output, err := os.OpenFile(path, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
		if err != nil {
			input.Close()
			return err
		}
		_, err = io.Copy(output, input)
		closeErr := output.Close()
		input.Close()
		if err != nil {
			return err
		}
		if closeErr != nil {
			return closeErr
		}
	}
	return nil
}

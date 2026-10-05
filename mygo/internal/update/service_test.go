package update

import (
	"archive/zip"
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestReleaseSelectsOnlyMyGoAsset(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/repos/zhengchalei/LeagueAkari-MyGo/releases/latest" || r.Header.Get("User-Agent") != "LeagueAkari-MyGo/0.4.0" {
			t.Errorf("unexpected release request: %s %s", r.URL.Path, r.Header.Get("User-Agent"))
		}
		fmt.Fprint(w, `{"tag_name":"v0.5.0","assets":[{"name":"LeagueAkari-0.5.0-win-x64.zip","browser_download_url":"electron"},{"name":"Timo-0.5.0-MyGo-win-x64.zip","browser_download_url":"legacy"},{"name":"LeagueAkari-MyGo-0.5.0-win-x64.zip","browser_download_url":"correct","size":10}]}`)
	}))
	defer server.Close()
	service := New(Options{Version: "0.4.0", Repository: "zhengchalei/LeagueAkari-MyGo", APIBase: server.URL})
	result, err := service.Check(context.Background())
	if err != nil || result["result"] != "new-updates" {
		t.Fatal(result, err)
	}
	if service.Latest()["archiveFile"].(map[string]any)["downloadUrl"] != "correct" {
		t.Fatal(service.Latest())
	}
}

func TestReleaseKeepsLegacyMyGoAndRejectsElectronPackages(t *testing.T) {
	for _, test := range []struct {
		name     string
		accepted bool
	}{
		{"Timo-0.5.0-MyGo-win-x64.zip", true},
		{"LeagueAkari-MyGo-v0.5.0-win-x64.zip", true},
		{"LeagueAkari-0.5.0-win-x64.zip", false},
		{"LeagueAkari-MyGo-0.5.0-win-arm64.zip", false},
		{"LeagueAkari-MyGo-0.4.0-win-x64.zip", false},
		{"Timo-0.5.0-Electron-MyGo-win-x64.zip", false},
		{"Timo-0.5.0-win-x64.zip", false},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				fmt.Fprintf(w, `{"tag_name":"v0.5.0","assets":[{"name":%q,"browser_download_url":"asset"}]}`, test.name)
			}))
			defer server.Close()
			service := New(Options{Version: "0.4.0", Repository: "owner/repo", APIBase: server.URL})
			status, err := service.Check(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			if (status["result"] == "new-updates") != test.accepted || (service.Latest()["archiveFile"].(map[string]any) != nil) != test.accepted {
				t.Fatal(status, service.Latest())
			}
		})
	}
}
func TestExtractRejectsTraversal(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "update.zip")
	file, _ := os.Create(path)
	writer := zip.NewWriter(file)
	entry, _ := writer.Create("../outside")
	entry.Write([]byte("bad"))
	writer.Close()
	file.Close()
	if Extract(path, filepath.Join(dir, "prepared")) == nil {
		t.Fatal("accepted escape")
	}
	if _, err := os.Stat(filepath.Join(dir, "outside")); !os.IsNotExist(err) {
		t.Fatal("wrote outside")
	}
}
func TestDownloadPreparesExecutableAndCanCancel(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/slow" {
			w.(http.Flusher).Flush()
			<-r.Context().Done()
			return
		}
		writer := zip.NewWriter(w)
		entry, _ := writer.Create(executableName)
		entry.Write([]byte("test-binary"))
		writer.Close()
	}))
	defer server.Close()
	service := New(Options{Directory: t.TempDir()})
	service.release = map[string]any{"isNew": true, "archiveFile": map[string]any{"downloadUrl": server.URL}}
	service.Start(context.Background(), false)
	for i := 0; i < 100 && service.Prepared() == ""; i++ {
		time.Sleep(10 * time.Millisecond)
	}
	if filepath.Base(service.Prepared()) != executableName {
		t.Fatal(service.State())
	}
	service.Cancel()
	service.release = map[string]any{"isNew": true, "archiveFile": map[string]any{"downloadUrl": server.URL + "/slow"}}
	service.Start(context.Background(), false)
	time.Sleep(20 * time.Millisecond)
	service.Cancel()
	for i := 0; i < 100; i++ {
		service.mu.Lock()
		done := service.cancel == nil
		service.mu.Unlock()
		if done {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("download did not cancel")
}

func TestPrepareRequiresCurrentArchiveExecutableAndKeepsLegacyPackages(t *testing.T) {
	for _, test := range []struct {
		name     string
		files    []string
		expected string
	}{
		{"new", []string{executableName}, executableName},
		{"legacy", []string{legacyExecutableName}, legacyExecutableName},
		{"both", []string{legacyExecutableName, executableName}, executableName},
		{"electron", []string{"League Akari.exe"}, ""},
		{"nested", []string{"nested/" + executableName}, ""},
	} {
		t.Run(test.name, func(t *testing.T) {
			directory := t.TempDir()
			destination := filepath.Join(directory, "prepared")
			if err := os.MkdirAll(destination, 0700); err != nil {
				t.Fatal(err)
			}
			// A previous preparation must not turn an invalid new package into a valid update.
			for _, name := range []string{executableName, legacyExecutableName} {
				if err := os.WriteFile(filepath.Join(destination, name), []byte("old"), 0600); err != nil {
					t.Fatal(err)
				}
			}
			archive := filepath.Join(directory, "update.zip")
			file, err := os.Create(archive)
			if err != nil {
				t.Fatal(err)
			}
			writer := zip.NewWriter(file)
			for _, name := range test.files {
				entry, err := writer.Create(name)
				if err != nil {
					t.Fatal(err)
				}
				if _, err = entry.Write([]byte("current")); err != nil {
					t.Fatal(err)
				}
			}
			if err = writer.Close(); err != nil {
				t.Fatal(err)
			}
			file.Close()
			prepared, err := prepareExecutable(archive, destination)
			if test.expected == "" {
				if err == nil || prepared != "" {
					t.Fatal("accepted package without the current executable")
				}
				return
			}
			if err != nil || filepath.Base(prepared) != test.expected {
				t.Fatal(prepared, err)
			}
			data, err := os.ReadFile(prepared)
			if err != nil || strings.TrimSpace(string(data)) != "current" {
				t.Fatal("prepared stale executable", err)
			}
		})
	}
}

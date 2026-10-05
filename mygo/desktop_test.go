package main

import (
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"testing"
)

func TestLocalImageCanBeReadThroughProtocol(t *testing.T) {
	path := filepath.Join(t.TempDir(), "英雄.png")
	data := trayIcon()
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	fileURL := (&url.URL{Scheme: "file", Path: "/" + filepath.ToSlash(path)}).String()
	request := httptest.NewRequest("GET", "http://akari.localhost/local-image?url="+url.QueryEscape(fileURL), nil)
	response := httptest.NewRecorder()
	serveLocalImage(response, request)
	if response.Code != 200 || response.Header().Get("Content-Type") != "image/png" || response.Body.Len() != len(data) {
		t.Fatalf("image response %d %s", response.Code, response.Header())
	}
	bad := httptest.NewRecorder()
	serveLocalImage(bad, httptest.NewRequest("GET", "http://akari.localhost/local-image?url=file:///C:/settings.json", nil))
	if bad.Code != 400 {
		t.Fatalf("non-image status %d", bad.Code)
	}
}

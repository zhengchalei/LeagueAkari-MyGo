package update

import (
	"archive/zip"
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

func TestWinUIUpdateNeverAcceptsMyGoArchive(t *testing.T) {
	if winUIArchivePriority("LeagueAkari-MyGo-0.6.0-win-x64.zip", "v0.6.0") != 0 {
		t.Fatal("legacy WebView update accepted for WinUI")
	}
	if winUIArchivePriority("LeagueAkari-WinUI-0.6.0-win-x64.zip", "v0.6.0") != 3 {
		t.Fatal("valid WinUI update not accepted")
	}
}
func TestWinUIDirectoryUpdateRequiresCompleteLayoutAndReplacesStaleFiles(t *testing.T) {
	directory := t.TempDir()
	target := filepath.Join(directory, "prepared")
	if err := os.MkdirAll(target, 0700); err != nil {
		t.Fatal(err)
	}
	os.WriteFile(filepath.Join(target, "obsolete.dll"), []byte("old"), 0600)
	var buffer bytes.Buffer
	writer := zip.NewWriter(&buffer)
	for _, name := range winUIRequiredFiles {
		file, err := writer.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		file.Write([]byte("package-file"))
	}
	writer.Close()
	archive := filepath.Join(directory, "update.zip")
	os.WriteFile(archive, buffer.Bytes(), 0600)
	prepared, err := prepareWinUIDirectory(archive, target)
	if err != nil {
		t.Fatal(err)
	}
	if prepared != target {
		t.Fatal("prepared path is not full directory")
	}
	if _, err = os.Stat(filepath.Join(target, "obsolete.dll")); !os.IsNotExist(err) {
		t.Fatal("stale staged assembly retained")
	}
	if err = validateWinUILayout(target); err != nil {
		t.Fatal(err)
	}
	os.Remove(filepath.Join(target, "LeagueAkari.WinUI.pri"))
	if err = validateWinUILayout(target); err == nil {
		t.Fatal("missing app resources accepted")
	}
	buffer.Reset()
	writer = zip.NewWriter(&buffer)
	file, _ := writer.Create(WinUIExecutable)
	file.Write([]byte("only-exe"))
	writer.Close()
	os.WriteFile(archive, buffer.Bytes(), 0600)
	if _, err = prepareWinUIDirectory(archive, target); err == nil {
		t.Fatal("incomplete WinUI archive accepted")
	}
}

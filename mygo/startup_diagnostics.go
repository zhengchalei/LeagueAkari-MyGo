package main

import (
	"os"
	"path/filepath"
	"strings"
)

func isExecutableInTemporaryDirectory(executable, tempDirectory string) bool {
	if executable == "" || tempDirectory == "" {
		return false
	}
	executable = filepath.Clean(executable)
	tempDirectory = filepath.Clean(tempDirectory)
	// Windows paths are case insensitive; use a separator boundary so a sibling
	// directory named Temp-app does not trigger the unpack-before-running notice.
	return strings.HasPrefix(strings.ToLower(executable), strings.ToLower(tempDirectory)+string(filepath.Separator))
}
func (d *Desktop) initializeStartupDiagnostics() {
	executable := d.winUIHostExecutable
	if executable == "" {
		executable, _ = os.Executable()
	}
	d.static["app-common-main:state"]["isRunInTempDir"] = isExecutableInTemporaryDirectory(executable, os.TempDir())
}

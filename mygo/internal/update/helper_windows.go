package update

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// LaunchApply runs a copy outside the installation so Windows can replace the GUI executable.
func LaunchApply(prepared, target, dataDirectory string) error {
	if !portableExecutableName(filepath.Base(target)) || !portableExecutableName(filepath.Base(prepared)) {
		return errors.New("无效更新程序")
	}
	helper := filepath.Join(dataDirectory, "update-helper.exe")
	if err := copyFile(target, helper); err != nil {
		return err
	}
	command := exec.Command(helper, "--apply-update", strconv.Itoa(os.Getpid()), prepared, target, dataDirectory)
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x00000008}
	return command.Start()
}
func copyFile(source, target string) error {
	input, err := os.Open(source)
	if err != nil {
		return err
	}
	defer input.Close()
	output, err := os.OpenFile(target, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0700)
	if err != nil {
		return err
	}
	_, err = io.Copy(output, input)
	closeErr := output.Close()
	if err != nil {
		return err
	}
	return closeErr
}
func RunHelper(args []string) (bool, error) {
	if len(args) > 0 && args[0] == "--uninstall-winui" {
		return true, runWinUIUninstallHelper(args)
	}
	if len(args) > 0 && args[0] == "--apply-winui-update" {
		return true, runWinUIUpdateHelper(args)
	}
	if len(args) > 0 && args[0] == "--uninstall-portable" {
		return true, uninstallPortable(args)
	}
	if len(args) == 0 || args[0] != "--apply-update" {
		return false, nil
	}
	if len(args) != 5 {
		return true, errors.New("更新参数不完整")
	}
	pid, err := strconv.Atoi(args[1])
	if err != nil || pid <= 0 {
		return true, errors.New("无效进程")
	}
	prepared, target, dataDirectory := args[2], args[3], args[4]
	if !filepath.IsAbs(target) || !filepath.IsAbs(prepared) || !portableExecutableName(filepath.Base(target)) || !portableExecutableName(filepath.Base(prepared)) {
		return true, errors.New("更新路径无效")
	}
	relative, err := filepath.Rel(filepath.Join(dataDirectory, "new-updates", "prepared"), prepared)
	if err != nil || strings.HasPrefix(relative, "..") {
		return true, errors.New("更新源不在下载目录")
	}
	process, err := syscall.OpenProcess(0x00100000, false, uint32(pid))
	if err == nil {
		syscall.WaitForSingleObject(process, 60_000)
		syscall.CloseHandle(process)
	}
	backup := target + ".previous"
	os.Remove(backup)
	var applyErr error
	for attempt := 0; attempt < 30; attempt++ {
		applyErr = os.Rename(target, backup)
		if applyErr == nil {
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if applyErr == nil {
		applyErr = copyFile(prepared, target)
		if applyErr != nil {
			os.Remove(target)
			os.Rename(backup, target)
		}
	}
	result := map[string]any{"success": applyErr == nil, "reason": ""}
	if applyErr != nil {
		result["reason"] = applyErr.Error()
	}
	bytes, _ := json.Marshal(result)
	os.WriteFile(filepath.Join(dataDirectory, "last-update-result.json"), bytes, 0600)
	command := exec.Command(target)
	command.Dir = filepath.Dir(target)
	if startErr := command.Start(); applyErr == nil {
		applyErr = startErr
	}
	return true, applyErr
}

func LaunchUninstall(target, dataDirectory string, removeData bool) error {
	base, err := os.UserConfigDir()
	if err != nil {
		return err
	}
	if err = validateUninstallPaths(target, dataDirectory, base, removeData); err != nil {
		return err
	}
	file, err := os.CreateTemp("", "leagueakari-mygo-uninstall-*.exe")
	if err != nil {
		return err
	}
	helper := file.Name()
	file.Close()
	if err = copyFile(target, helper); err != nil {
		return err
	}
	command := exec.Command(helper, "--uninstall-portable", strconv.Itoa(os.Getpid()), target, dataDirectory, strconv.FormatBool(removeData))
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x00000008}
	return command.Start()
}
func uninstallPortable(args []string) error {
	if len(args) != 5 {
		return errors.New("卸载参数不完整")
	}
	target, dataDirectory := args[2], args[3]
	base, err := os.UserConfigDir()
	if err != nil {
		return err
	}
	if err = validateUninstallPaths(target, dataDirectory, base, args[4] == "true"); err != nil {
		return err
	}
	pid, err := strconv.Atoi(args[1])
	if err != nil || pid <= 0 {
		return errors.New("无效卸载进程")
	}
	process, err := syscall.OpenProcess(0x00100000, false, uint32(pid))
	if err == nil {
		syscall.WaitForSingleObject(process, 60_000)
		syscall.CloseHandle(process)
	}
	for i := 0; i < 30; i++ {
		err = os.Remove(target)
		if err == nil || os.IsNotExist(err) {
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if err != nil && !os.IsNotExist(err) {
		return err
	}
	// A portable build shares its folder with source, archives and licences; remove only its executable.
	if args[4] == "true" {
		return os.RemoveAll(filepath.Clean(dataDirectory))
	}
	return nil
}

func validateUninstallPaths(target, dataDirectory, configDirectory string, removeData bool) error {
	if !filepath.IsAbs(target) || !portableExecutableName(filepath.Base(target)) || !filepath.IsAbs(dataDirectory) {
		return errors.New("无效卸载路径")
	}
	folder := filepath.Base(filepath.Clean(dataDirectory))
	if folder != "LeagueAkari-MyGo" && folder != "TimoMyGo" {
		return errors.New("无效卸载路径")
	}
	if removeData && !strings.EqualFold(filepath.Clean(filepath.Join(configDirectory, folder)), filepath.Clean(dataDirectory)) {
		return errors.New("用户数据路径不匹配")
	}
	return nil
}

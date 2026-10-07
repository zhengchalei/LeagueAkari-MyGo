//go:build windows

package update

import (
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

func LaunchWinUIApply(prepared, hostExecutable string, hostPID int, dataDirectory string) error {
	if hostPID <= 0 || !filepath.IsAbs(hostExecutable) || !strings.EqualFold(filepath.Base(hostExecutable), WinUIExecutable) {
		return errors.New("无效 WinUI 宿主")
	}
	backend, err := os.Executable()
	if err != nil {
		return err
	}
	if !strings.EqualFold(filepath.Dir(backend), filepath.Dir(hostExecutable)) {
		return errors.New("WinUI 宿主与后端目录不匹配")
	}
	if err = validateWinUIApplyPaths(prepared, hostExecutable, dataDirectory); err != nil {
		return err
	}
	helper := filepath.Join(dataDirectory, "winui-update-helper.exe")
	if err = copyFile(backend, helper); err != nil {
		return err
	}
	command := exec.Command(helper, "--apply-winui-update", strconv.Itoa(os.Getpid()), strconv.Itoa(hostPID), prepared, hostExecutable, dataDirectory)
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x00000008}
	return command.Start()
}
func validateWinUIApplyPaths(prepared, hostExecutable, dataDirectory string) error {
	if !filepath.IsAbs(prepared) || !filepath.IsAbs(hostExecutable) || !filepath.IsAbs(dataDirectory) || !strings.EqualFold(filepath.Base(hostExecutable), WinUIExecutable) {
		return errors.New("无效 WinUI 更新路径")
	}
	if !strings.EqualFold(filepath.Clean(prepared), filepath.Join(filepath.Clean(dataDirectory), "new-updates", "prepared")) {
		return errors.New("更新源不在 WinUI 下载暂存目录")
	}
	installation := filepath.Dir(hostExecutable)
	if filepath.Dir(installation) == installation {
		return errors.New("禁止更新磁盘根目录")
	}
	if relative, err := filepath.Rel(installation, dataDirectory); err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return errors.New("用户数据与更新帮助程序必须位于安装目录之外")
	}
	if err := validateWinUILayout(installation); err != nil {
		return fmt.Errorf("目标不是 WinUI 安装目录: %w", err)
	}
	return validateWinUILayout(prepared)
}
func runWinUIUpdateHelper(args []string) error {
	if len(args) != 6 {
		return errors.New("WinUI 更新参数不完整")
	}
	prepared, host, data := args[3], args[4], args[5]
	if err := validateWinUIApplyPaths(prepared, host, data); err != nil {
		return err
	}
	for _, pidText := range args[1:3] {
		pid, err := strconv.Atoi(pidText)
		if err != nil || pid <= 0 {
			return errors.New("无效更新进程")
		}
		process, err := syscall.OpenProcess(0x00100000, false, uint32(pid))
		if err == nil {
			result, waitErr := syscall.WaitForSingleObject(process, 60_000)
			syscall.CloseHandle(process)
			if waitErr != nil {
				return waitErr
			}
			if result == syscall.WAIT_TIMEOUT {
				return errors.New("WinUI 未在更新前退出")
			}
		}
	}
	installation := filepath.Dir(host)
	backup := installation + ".previous-" + strconv.FormatInt(time.Now().UnixMilli(), 10)
	var err error
	for attempt := 0; attempt < 30; attempt++ {
		err = os.Rename(installation, backup)
		if err == nil {
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if err == nil {
		err = copyWinUIDirectory(prepared, installation)
		if err == nil {
			err = validateWinUILayout(installation)
		}
		if err != nil {
			_ = os.RemoveAll(installation)
			if rollback := os.Rename(backup, installation); rollback != nil {
				err = fmt.Errorf("更新失败且恢复失败: %v; %w", err, rollback)
			}
		}
	}
	status := map[string]any{"success": err == nil, "reason": ""}
	if err != nil {
		status["reason"] = err.Error()
	}
	encoded, _ := json.Marshal(status)
	_ = os.WriteFile(filepath.Join(data, "last-update-result.json"), encoded, 0600)
	command := exec.Command(host)
	command.Dir = installation
	if startErr := command.Start(); err == nil {
		err = startErr
	}
	return err
}
func copyWinUIDirectory(source, target string) error {
	return filepath.WalkDir(source, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		destination := filepath.Join(target, relative)
		if entry.Type()&os.ModeSymlink != 0 {
			return errors.New("更新目录含符号链接")
		}
		if entry.IsDir() {
			return os.MkdirAll(destination, 0700)
		}
		if !entry.Type().IsRegular() {
			return errors.New("更新目录含非普通文件")
		}
		return copyFile(path, destination)
	})
}

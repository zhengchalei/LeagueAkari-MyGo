//go:build windows

package update

import (
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"unicode/utf16"
)

const WinUIInstallMarker = "league-akari-winui.portable"
const winUIInstallIdentity = "LeagueAkari-WinUI portable v1"

func validateWinUIUninstallPaths(host, data string) error {
	if !filepath.IsAbs(host) || !filepath.IsAbs(data) || !strings.EqualFold(filepath.Base(host), WinUIExecutable) {
		return errors.New("无效 WinUI 卸载路径")
	}
	installation := filepath.Dir(host)
	if filepath.Dir(filepath.Clean(data)) == filepath.Clean(data) {
		return errors.New("禁止删除磁盘根目录中的用户配置")
	}
	for _, protected := range []string{os.Getenv("USERPROFILE"), os.Getenv("APPDATA"), os.Getenv("LOCALAPPDATA")} {
		if protected != "" && strings.EqualFold(filepath.Clean(data), filepath.Clean(protected)) {
			return errors.New("禁止删除共享用户目录中的配置")
		}
	}
	if !strings.HasPrefix(filepath.Base(installation), "LeagueAkari-WinUI-") || filepath.Dir(installation) == installation {
		return errors.New("仅可卸载独立的 WinUI 便携安装目录，开发目录不会删除")
	}
	if nestedPath(installation, data) || nestedPath(data, installation) {
		return errors.New("安装目录与用户数据目录不能重叠")
	}
	for _, name := range []string{".git", "package.json", "LeagueAkari.WinUI.csproj", "mygo", "src"} {
		if _, err := os.Lstat(filepath.Join(installation, name)); !os.IsNotExist(err) {
			return errors.New("拒绝卸载源码或工作区目录")
		}
	}
	marker, err := os.ReadFile(filepath.Join(installation, WinUIInstallMarker))
	if err != nil || strings.TrimSpace(string(marker)) != winUIInstallIdentity {
		return errors.New("未找到 WinUI 便携安装标记")
	}
	if err = validateWinUILayout(installation); err != nil {
		return err
	}
	for _, path := range []string{installation, data} {
		for current := filepath.Clean(path); filepath.Dir(current) != current; current = filepath.Dir(current) {
			info, err := os.Lstat(current)
			if err != nil {
				return err
			}
			if info.Mode()&os.ModeSymlink != 0 {
				return errors.New("卸载路径含目录链接")
			}
		}
	}
	// Never follow a junction while recursively removing a portable installation.
	return filepath.Walk(installation, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("安装目录含链接，拒绝卸载: %s", path)
		}
		return nil
	})
}

func nestedPath(parent, child string) bool {
	rel, err := filepath.Rel(filepath.Clean(parent), filepath.Clean(child))
	return err == nil && rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator))
}

func LaunchWinUIUninstall(host string, hostPID int, data string, removeData bool) error {
	if hostPID <= 0 {
		return errors.New("无效卸载进程")
	}
	if err := validateWinUIUninstallPaths(host, data); err != nil {
		return err
	}
	backend, err := os.Executable()
	if err != nil {
		return err
	}
	if !strings.EqualFold(filepath.Dir(backend), filepath.Dir(host)) {
		return errors.New("WinUI 后端与宿主目录不匹配")
	}
	file, err := os.CreateTemp("", "leagueakari-winui-uninstall-*.exe")
	if err != nil {
		return err
	}
	helper := file.Name()
	file.Close()
	if err = copyFile(backend, helper); err != nil {
		os.Remove(helper)
		return err
	}
	command := exec.Command(helper, "--uninstall-winui", strconv.Itoa(os.Getpid()), strconv.Itoa(hostPID), host, data, strconv.FormatBool(removeData))
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x00000008}
	return command.Start()
}

func runWinUIUninstallHelper(args []string) error {
	if len(args) != 6 {
		return errors.New("WinUI 卸载参数不完整")
	}
	if err := validateWinUIUninstallPaths(args[3], args[4]); err != nil {
		return err
	}
	removeData, err := strconv.ParseBool(args[5])
	if err != nil {
		return err
	}
	for _, text := range args[1:3] {
		pid, err := strconv.Atoi(text)
		if err != nil || pid <= 0 {
			return errors.New("无效卸载进程")
		}
		process, err := syscall.OpenProcess(0x00100000, false, uint32(pid))
		if err != nil && err != syscall.Errno(87) {
			return fmt.Errorf("无法等待卸载进程退出: %w", err)
		}
		if err == nil {
			result, waitErr := syscall.WaitForSingleObject(process, 60_000)
			syscall.CloseHandle(process)
			if waitErr != nil {
				return waitErr
			}
			if result == syscall.WAIT_TIMEOUT {
				return errors.New("WinUI 未在卸载前退出")
			}
		}
	}
	if err = removeWinUIShortcuts(args[3]); err != nil {
		return err
	}
	return removeWinUIInstallation(args[3], args[4], removeData)
}

// Delete the dedicated distribution, but preserve any unrelated files in a
// shared MyGo/WinUI profile. The profile directory itself is never removed.
func removeWinUIInstallation(host, data string, removeData bool) error {
	if err := validateWinUIUninstallPaths(host, data); err != nil {
		return err
	}
	if err := os.RemoveAll(filepath.Dir(host)); err != nil {
		return err
	}
	if !removeData {
		return nil
	}
	for _, name := range []string{"settings.json", "players.sqlite", "players.sqlite-wal", "players.sqlite-shm", "window-state.json", "kiwi-balance.json", "league-akari-winui-backend.log", "league-akari-winui-error.log", "last-update-result.json", "new-updates", "remote-config"} {
		if err := os.RemoveAll(filepath.Join(data, name)); err != nil {
			return err
		}
	}
	return nil
}

func removeWinUIShortcuts(host string) error {
	// Resolve targets with the Windows shell. Both the product name and exact
	// executable must match; shortcuts to the old MyGo build remain untouched.
	const script = `$ErrorActionPreference='Stop'; $shell=New-Object -ComObject WScript.Shell; $roots=@([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('Programs')); foreach($root in $roots){if(-not $root){continue}; Get-ChildItem -LiteralPath $root -Filter 'LeagueAkari*.lnk' -File -Recurse | ForEach-Object { $link=$shell.CreateShortcut($_.FullName); if($link.TargetPath -and [String]::Equals([IO.Path]::GetFullPath($link.TargetPath),$env:LEAGUE_AKARI_UNINSTALL_HOST,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $_.FullName -Force} } }`
	units := utf16.Encode([]rune(script))
	bytes := make([]byte, len(units)*2)
	for i, unit := range units {
		binary.LittleEndian.PutUint16(bytes[i*2:], unit)
	}
	command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", base64.StdEncoding.EncodeToString(bytes))
	command.Env = append(os.Environ(), "LEAGUE_AKARI_UNINSTALL_HOST="+filepath.Clean(host))
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	output, err := command.CombinedOutput()
	if err != nil {
		return fmt.Errorf("移除 WinUI 快捷方式失败: %w: %s", err, strings.TrimSpace(string(output)))
	}
	return nil
}

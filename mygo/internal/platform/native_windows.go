//go:build windows

package platform

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"unsafe"
)

var kernel32 = syscall.NewLazyDLL("kernel32.dll")
var user32 = syscall.NewLazyDLL("user32.dll")
var advapi32 = syscall.NewLazyDLL("advapi32.dll")
var ntdll = syscall.NewLazyDLL("ntdll.dll")

type windowsNative struct{}

func newNative() Native               { return windowsNative{} }
func (windowsNative) Supported() bool { return true }

type processEntry struct {
	Size, Usage, PID             uint32
	DefaultHeap                  uintptr
	ModuleID, Threads, ParentPID uint32
	Priority                     int32
	Flags                        uint32
	Name                         [260]uint16
}

func (windowsNative) Processes(ctx context.Context) ([]Process, error) {
	handle, _, err := kernel32.NewProc("CreateToolhelp32Snapshot").Call(2, 0)
	if handle == ^uintptr(0) {
		return nil, fmt.Errorf("读取进程列表: %w", err)
	}
	defer syscall.CloseHandle(syscall.Handle(handle))
	entry := processEntry{}
	entry.Size = uint32(unsafe.Sizeof(entry))
	ok, _, _ := kernel32.NewProc("Process32FirstW").Call(handle, uintptr(unsafe.Pointer(&entry)))
	out := []Process{}
	for ok != 0 {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		name := syscall.UTF16ToString(entry.Name[:])
		process := Process{PID: int(entry.PID), Name: name}
		if strings.EqualFold(name, "LeagueClient.exe") || strings.EqualFold(name, "LeagueClientUx.exe") || strings.EqualFold(name, "League of Legends.exe") || strings.EqualFold(name, "WeGame.exe") || strings.EqualFold(name, "TGP.exe") {
			process.CommandLine, process.Path = processInfo(entry.PID)
		}
		out = append(out, process)
		ok, _, _ = kernel32.NewProc("Process32NextW").Call(handle, uintptr(unsafe.Pointer(&entry)))
	}
	return out, nil
}

// Tencent may leave WMI CommandLine empty. This native query requires only
// PROCESS_QUERY_LIMITED_INFORMATION and keeps the credential-bearing text local.
func processInfo(pid uint32) (string, string) {
	handle, _, _ := kernel32.NewProc("OpenProcess").Call(0x1000, 0, uintptr(pid))
	if handle == 0 {
		return "", ""
	}
	defer syscall.CloseHandle(syscall.Handle(handle))
	query := ntdll.NewProc("NtQueryInformationProcess")
	var length uint32
	query.Call(handle, 60, 0, 0, uintptr(unsafe.Pointer(&length)))
	command := ""
	if length > 0 && length < 1<<20 {
		buffer := make([]byte, length)
		status, _, _ := query.Call(handle, 60, uintptr(unsafe.Pointer(&buffer[0])), uintptr(length), uintptr(unsafe.Pointer(&length)))
		if uint32(status) == 0 {
			byteLength := *(*uint16)(unsafe.Pointer(&buffer[0]))
			offset := uintptr(4)
			if unsafe.Sizeof(uintptr(0)) == 8 {
				offset = 8
			}
			address := *(*uintptr)(unsafe.Add(unsafe.Pointer(&buffer[0]), offset))
			start := uintptr(unsafe.Pointer(&buffer[0]))
			if address >= start && address+uintptr(byteLength) <= start+uintptr(len(buffer)) {
				command = syscall.UTF16ToString(unsafe.Slice((*uint16)(unsafe.Pointer(&buffer[int(address-start)])), int(byteLength)/2))
			}
		}
		runtime.KeepAlive(buffer)
	}
	buffer := make([]uint16, 32768)
	size := uint32(len(buffer))
	ok, _, _ := kernel32.NewProc("QueryFullProcessImageNameW").Call(handle, 0, uintptr(unsafe.Pointer(&buffer[0])), uintptr(unsafe.Pointer(&size)))
	path := ""
	if ok != 0 {
		path = syscall.UTF16ToString(buffer[:size])
	}
	return command, path
}

func (windowsNative) Registry(path, name string) (string, error) {
	keyPath, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return "", err
	}
	var key syscall.Handle
	if err := syscall.RegOpenKeyEx(syscall.HKEY_CURRENT_USER, keyPath, 0, syscall.KEY_READ, &key); err != nil {
		return "", err
	}
	defer syscall.RegCloseKey(key)
	valueName, err := syscall.UTF16PtrFromString(name)
	if err != nil {
		return "", err
	}
	var size, kind uint32
	if err := syscall.RegQueryValueEx(key, valueName, nil, &kind, nil, &size); err != nil {
		return "", err
	}
	if size == 0 || size > 1<<20 {
		return "", errors.New("注册表路径无效")
	}
	buffer := make([]byte, size)
	if err := syscall.RegQueryValueEx(key, valueName, nil, &kind, &buffer[0], &size); err != nil {
		return "", err
	}
	if kind != syscall.REG_SZ && kind != syscall.REG_EXPAND_SZ {
		return "", errors.New("注册表值不是字符串")
	}
	return os.ExpandEnv(syscall.UTF16ToString(unsafe.Slice((*uint16)(unsafe.Pointer(&buffer[0])), len(buffer)/2))), nil
}
func (windowsNative) Drives() []string {
	mask, _, _ := kernel32.NewProc("GetLogicalDrives").Call()
	out := []string{}
	for i := 0; i < 26; i++ {
		if mask&(1<<i) != 0 {
			drive := string(rune('A'+i)) + `:\`
			pointer, _ := syscall.UTF16PtrFromString(drive)
			kind, _, _ := kernel32.NewProc("GetDriveTypeW").Call(uintptr(unsafe.Pointer(pointer)))
			if kind == 3 {
				out = append(out, drive)
			}
		}
	}
	return out
}
func (windowsNative) Launch(path string, args []string) error {
	if !existingFile(path) {
		return errors.New("客户端可执行文件不存在")
	}
	// Request elevation for the launcher directly; the assistant keeps its
	// existing permissions and Windows owns the authorization prompt.
	return launchElevatedClient(path, args)
}

func launchElevatedClient(path string, args []string) error {
	quoted := make([]string, len(args))
	for i, argument := range args {
		quoted[i] = syscall.EscapeArg(argument)
	}
	verb, _ := syscall.UTF16PtrFromString("runas")
	executable, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	parameters, err := syscall.UTF16PtrFromString(strings.Join(quoted, " "))
	if err != nil {
		return err
	}
	directory, err := syscall.UTF16PtrFromString(filepath.Dir(path))
	if err != nil {
		return err
	}
	result, _, _ := syscall.NewLazyDLL("shell32.dll").NewProc("ShellExecuteW").Call(
		0, uintptr(unsafe.Pointer(verb)), uintptr(unsafe.Pointer(executable)),
		uintptr(unsafe.Pointer(parameters)), uintptr(unsafe.Pointer(directory)), 1,
	)
	if result <= 32 {
		return fmt.Errorf("客户端需要管理员权限，但启动未获授权或启动失败；请在 Windows 授权窗口中选择“是”，或通过 WeGame 手动启动（系统代码 %d）", result)
	}
	return nil
}
func (windowsNative) ForegroundPID() int {
	window, _, _ := user32.NewProc("GetForegroundWindow").Call()
	var pid uint32
	user32.NewProc("GetWindowThreadProcessId").Call(window, uintptr(unsafe.Pointer(&pid)))
	return int(pid)
}
func (windowsNative) Terminate(pid int) error {
	handle, _, err := kernel32.NewProc("OpenProcess").Call(1, 0, uintptr(pid))
	if handle == 0 {
		return fmt.Errorf("没有终止游戏的权限: %w", err)
	}
	defer syscall.CloseHandle(syscall.Handle(handle))
	if err := syscall.TerminateProcess(syscall.Handle(handle), 0); err != nil {
		return err
	}
	return nil
}
func (windowsNative) Elevated(pid int) bool {
	handle, _, _ := kernel32.NewProc("OpenProcess").Call(0x1000, 0, uintptr(pid))
	if handle == 0 {
		return false
	}
	defer syscall.CloseHandle(syscall.Handle(handle))
	var token syscall.Handle
	ok, _, _ := advapi32.NewProc("OpenProcessToken").Call(handle, 8, uintptr(unsafe.Pointer(&token)))
	if ok == 0 {
		return false
	}
	defer syscall.CloseHandle(token)
	var elevated, length uint32
	ok, _, _ = advapi32.NewProc("GetTokenInformation").Call(uintptr(token), 20, uintptr(unsafe.Pointer(&elevated)), 4, uintptr(unsafe.Pointer(&length)))
	return ok != 0 && elevated != 0
}

type rect struct{ Left, Top, Right, Bottom int32 }
type point struct{ X, Y int32 }
type windowPlacement struct {
	Length, Flags, ShowCmd   uint32
	MinPosition, MaxPosition point
	Normal                   rect
}

var leagueWindowQuery struct {
	sync.Mutex
	pid    int
	result uintptr
}
var leagueWindowCallback = syscall.NewCallback(func(window, lparam uintptr) uintptr {
	var owner uint32
	user32.NewProc("GetWindowThreadProcessId").Call(window, uintptr(unsafe.Pointer(&owner)))
	class := make([]uint16, 256)
	user32.NewProc("GetClassNameW").Call(window, uintptr(unsafe.Pointer(&class[0])), uintptr(len(class)))
	title := make([]uint16, 256)
	user32.NewProc("GetWindowTextW").Call(window, uintptr(unsafe.Pointer(&title[0])), uintptr(len(title)))
	if (leagueWindowQuery.pid == 0 || int(owner) == leagueWindowQuery.pid) && syscall.UTF16ToString(class) == "RCLIENT" && syscall.UTF16ToString(title) == "League of Legends" {
		leagueWindowQuery.result = window
		return 0
	}
	return 1
})

func leagueWindow(pid int) uintptr {
	leagueWindowQuery.Lock()
	defer leagueWindowQuery.Unlock()
	leagueWindowQuery.pid, leagueWindowQuery.result = pid, 0
	user32.NewProc("EnumWindows").Call(leagueWindowCallback, 0)
	return leagueWindowQuery.result
}
func (windowsNative) Placement(pid int) (*Placement, error) {
	window := leagueWindow(pid)
	if window == 0 {
		return nil, nil
	}
	placement := windowPlacement{Length: uint32(unsafe.Sizeof(windowPlacement{}))}
	ok, _, err := user32.NewProc("GetWindowPlacement").Call(window, uintptr(unsafe.Pointer(&placement)))
	if ok == 0 {
		return nil, err
	}
	dpi, _, _ := user32.NewProc("GetDpiForWindow").Call(window)
	if dpi == 0 {
		dpi = 96
	}
	scale := float64(dpi) / 96
	minimized, _, _ := user32.NewProc("IsIconic").Call(window)
	maximized, _, _ := user32.NewProc("IsZoomed").Call(window)
	// Tencent's CEF wrapper can retain a tiny restore rectangle even while its
	// visible window is full size. Follow the actual rectangle outside minimize.
	if minimized == 0 {
		var visible rect
		ok, _, _ := user32.NewProc("GetWindowRect").Call(window, uintptr(unsafe.Pointer(&visible)))
		if ok != 0 && visible.Right-visible.Left >= 200 && visible.Bottom-visible.Top >= 50 {
			placement.Normal = visible
		}
	}
	left, top := int(float64(placement.Normal.Left)/scale), int(float64(placement.Normal.Top)/scale)
	right, bottom := int(float64(placement.Normal.Right)/scale), int(float64(placement.Normal.Bottom)/scale)
	return &Placement{left, top, right, bottom, right - left, bottom - top, int(placement.ShowCmd), minimized != 0, maximized != 0, minimized == 0 && maximized == 0}, nil
}
func (windowsNative) Repair(pid int, zoom float64, width, height int) error {
	if zoom <= 0 || zoom > 5 || width <= 0 || height <= 0 {
		return errors.New("客户端尺寸或缩放无效")
	}
	window := leagueWindow(pid)
	if window == 0 {
		return errors.New("未找到 LOL 客户端窗口")
	}
	class, _ := syscall.UTF16PtrFromString("CefBrowserWindow")
	child, _, _ := user32.NewProc("FindWindowExW").Call(window, 0, uintptr(unsafe.Pointer(class)), 0)
	if child == 0 {
		return errors.New("未找到客户端 CEF 窗口")
	}
	screenWidth, _, _ := user32.NewProc("GetSystemMetrics").Call(0)
	screenHeight, _, _ := user32.NewProc("GetSystemMetrics").Call(1)
	w, h := int(float64(width)*zoom), int(float64(height)*zoom)
	ok, _, err := user32.NewProc("SetWindowPos").Call(window, 0, uintptr((int(screenWidth)-w)/2), uintptr((int(screenHeight)-h)/2), uintptr(w), uintptr(h), 4|0x10)
	if ok == 0 {
		return fmt.Errorf("调整客户端窗口失败: %w", err)
	}
	ok, _, err = user32.NewProc("SetWindowPos").Call(child, 0, 0, 0, uintptr(w), uintptr(h), 4|0x10)
	if ok == 0 {
		return fmt.Errorf("调整客户端内容失败: %w", err)
	}
	return nil
}

type keyboardInput struct {
	VirtualKey, Scan uint16
	Flags, Time      uint32
	ExtraInfo        uintptr
}
type input struct {
	Type    uint32
	_       uint32
	Payload [32]byte
}

func sendKeyboard(vk, scan uint16, flags uint32) error {
	data := input{Type: 1}
	*(*keyboardInput)(unsafe.Pointer(&data.Payload[0])) = keyboardInput{VirtualKey: vk, Scan: scan, Flags: flags, ExtraInfo: 0x54494d4f}
	count, _, err := user32.NewProc("SendInput").Call(1, uintptr(unsafe.Pointer(&data)), unsafe.Sizeof(data))
	if count != 1 {
		return fmt.Errorf("发送按键失败（检查游戏权限）: %w", err)
	}
	return nil
}
func (windowsNative) Key(code uint16, down bool) error {
	flags := uint32(0)
	if !down {
		flags = 2
	}
	return sendKeyboard(code, 0, flags)
}
func (windowsNative) Unicode(code uint16, down bool) error {
	flags := uint32(4)
	if !down {
		flags |= 2
	}
	return sendKeyboard(0, code, flags)
}
func (windowsNative) KeyDown(code uint32) bool {
	value, _, _ := user32.NewProc("GetAsyncKeyState").Call(uintptr(code))
	return value&0x8000 != 0
}

type keyboardHook struct {
	VKCode, ScanCode, Flags, Time uint32
	ExtraInfo                     uintptr
}
type message struct {
	Window         uintptr
	Message        uint32
	WParam, LParam uintptr
	Time           uint32
	Point          point
	Private        uint32
}

func (windowsNative) WatchKeys(ctx context.Context, callback func(uint32, bool)) error {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	type event struct {
		code uint32
		down bool
	}
	events := make(chan event, 256)
	done := make(chan struct{})
	defer close(done)
	go func() {
		for {
			select {
			case <-done:
				return
			case <-ctx.Done():
				return
			case value := <-events:
				callback(value.code, value.down)
			}
		}
	}()
	hookCallback := syscall.NewCallback(func(code int32, wparam, lparam uintptr) uintptr {
		if code >= 0 {
			key := keyboardHook{}
			kernel32.NewProc("RtlMoveMemory").Call(uintptr(unsafe.Pointer(&key)), lparam, unsafe.Sizeof(key))
			if key.Flags&0x10 == 0 {
				down := wparam == 0x100 || wparam == 0x104
				if down || wparam == 0x101 || wparam == 0x105 {
					select {
					case events <- event{key.VKCode, down}:
					default:
					}
				}
			}
		}
		result, _, _ := user32.NewProc("CallNextHookEx").Call(0, uintptr(code), wparam, lparam)
		return result
	})
	handle, _, err := user32.NewProc("SetWindowsHookExW").Call(13, hookCallback, 0, 0)
	if handle == 0 {
		return fmt.Errorf("注册键盘监听失败: %w", err)
	}
	defer user32.NewProc("UnhookWindowsHookEx").Call(handle)
	thread, _, _ := kernel32.NewProc("GetCurrentThreadId").Call()
	msg := message{}
	user32.NewProc("PeekMessageW").Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0, 0)
	go func() {
		select {
		case <-ctx.Done():
			user32.NewProc("PostThreadMessageW").Call(thread, 0x12, 0, 0)
		case <-done:
		}
	}()
	for {
		result, _, err := user32.NewProc("GetMessageW").Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if int32(result) == -1 {
			return err
		}
		if result == 0 {
			return ctx.Err()
		}
		user32.NewProc("TranslateMessage").Call(uintptr(unsafe.Pointer(&msg)))
		user32.NewProc("DispatchMessageW").Call(uintptr(unsafe.Pointer(&msg)))
	}
}
func (windowsNative) SupportsMica() bool {
	var info struct {
		Size, Major, Minor, Build, Platform uint32
		Name                                [128]uint16
	}
	info.Size = uint32(unsafe.Sizeof(info))
	status, _, _ := ntdll.NewProc("RtlGetVersion").Call(uintptr(unsafe.Pointer(&info)))
	return uint32(status) == 0 && info.Build >= 22621
}

func (s *Service) rebuildWMI() error {
	if !s.IsElevated() {
		return errors.New("重建 WMI 需要以管理员身份运行 LeagueAkari-MyGo")
	}
	command := exec.Command("winmgmt.exe", "/salvagerepository")
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	if err := command.Run(); err != nil {
		return fmt.Errorf("修复 WMI 失败: %w", err)
	}
	return nil
}

func (s *Service) relaunchElevated() error {
	path, err := os.Executable()
	if err != nil {
		return err
	}
	arguments := []string{"--league-akari-mygo-relaunch"}
	arguments = append(arguments, os.Args[1:]...)
	quoted := []string{}
	for _, argument := range arguments {
		quoted = append(quoted, syscall.EscapeArg(argument))
	}
	verb, _ := syscall.UTF16PtrFromString("runas")
	executable, _ := syscall.UTF16PtrFromString(path)
	parameters, _ := syscall.UTF16PtrFromString(strings.Join(quoted, " "))
	result, _, callErr := syscall.NewLazyDLL("shell32.dll").NewProc("ShellExecuteW").Call(0, uintptr(unsafe.Pointer(verb)), uintptr(unsafe.Pointer(executable)), uintptr(unsafe.Pointer(parameters)), 0, 0)
	if result <= 32 {
		return fmt.Errorf("管理员重启未完成（可能取消了权限请求）: %w", callErr)
	}
	return nil
}

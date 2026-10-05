//go:build windows

package client

import (
	"context"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"
)

var kernel32 = syscall.NewLazyDLL("kernel32.dll")
var ntdll = syscall.NewLazyDLL("ntdll.dll")
var createSnapshot = kernel32.NewProc("CreateToolhelp32Snapshot")
var processFirst = kernel32.NewProc("Process32FirstW")
var processNext = kernel32.NewProc("Process32NextW")
var openProcess = kernel32.NewProc("OpenProcess")
var closeHandle = kernel32.NewProc("CloseHandle")
var queryImageName = kernel32.NewProc("QueryFullProcessImageNameW")
var queryProcessInfo = ntdll.NewProc("NtQueryInformationProcess")

type processEntry struct {
	Size            uint32
	Usage           uint32
	ProcessID       uint32
	DefaultHeapID   uintptr
	ModuleID        uint32
	Threads         uint32
	ParentProcessID uint32
	Priority        int32
	Flags           uint32
	ExeFile         [260]uint16
}

// WeGame may withhold WMI CommandLine. ProcessCommandLineInformation needs only
// PROCESS_QUERY_LIMITED_INFORMATION, so discovery does not require elevation.
func processCommandLine(pid uint32) (string, string) {
	handle, _, _ := openProcess.Call(0x1000, 0, uintptr(pid))
	if handle == 0 {
		return "", ""
	}
	defer closeHandle.Call(handle)
	var length uint32
	_, _, _ = queryProcessInfo.Call(handle, 60, 0, 0, uintptr(unsafe.Pointer(&length)))
	var command string
	if length > 0 && length < 1024*1024 {
		buffer := make([]byte, length)
		status, _, _ := queryProcessInfo.Call(handle, 60, uintptr(unsafe.Pointer(&buffer[0])), uintptr(length), uintptr(unsafe.Pointer(&length)))
		if uint32(status) == 0 {
			byteLength := *(*uint16)(unsafe.Pointer(&buffer[0]))
			pointerOffset := uintptr(4)
			if unsafe.Sizeof(uintptr(0)) == 8 {
				pointerOffset = 8
			}
			address := *(*uintptr)(unsafe.Add(unsafe.Pointer(&buffer[0]), pointerOffset))
			start := uintptr(unsafe.Pointer(&buffer[0]))
			end := start + uintptr(len(buffer))
			if address >= start && address+uintptr(byteLength) <= end {
				command = syscall.UTF16ToString(unsafe.Slice((*uint16)(unsafe.Pointer(&buffer[int(address-start)])), int(byteLength)/2))
			}
		}
		runtime.KeepAlive(buffer)
	}
	pathBuffer := make([]uint16, 32768)
	pathLength := uint32(len(pathBuffer))
	ok, _, _ := queryImageName.Call(handle, 0, uintptr(unsafe.Pointer(&pathBuffer[0])), uintptr(unsafe.Pointer(&pathLength)))
	var imagePath string
	if ok != 0 {
		imagePath = syscall.UTF16ToString(pathBuffer[:pathLength])
	}
	return command, imagePath
}

func Discover(ctx context.Context) (*Auth, error) {
	if auth, err := configuredLockfile(); auth != nil {
		return auth, nil
	} else if err != nil {
		return nil, err
	}
	handle, _, _ := createSnapshot.Call(2, 0)
	if handle == ^uintptr(0) {
		return nil, nil
	}
	defer closeHandle.Call(handle)
	entry := processEntry{}
	entry.Size = uint32(unsafe.Sizeof(entry))
	ok, _, _ := processFirst.Call(handle, uintptr(unsafe.Pointer(&entry)))
	for ok != 0 {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		name := syscall.UTF16ToString(entry.ExeFile[:])
		if strings.EqualFold(name, "LeagueClientUx.exe") || strings.EqualFold(name, "LeagueClient.exe") {
			command, imagePath := processCommandLine(entry.ProcessID)
			if auth := ParseCommandLine(command, int(entry.ProcessID)); auth != nil {
				return auth, nil
			}
			if imagePath != "" {
				if data, err := os.ReadFile(filepath.Join(filepath.Dir(imagePath), "lockfile")); err == nil {
					if auth := ParseLockfile(string(data)); auth != nil {
						return auth, nil
					}
				}
			}
		}
		ok, _, _ = processNext.Call(handle, uintptr(unsafe.Pointer(&entry)))
	}
	return nil, nil
}

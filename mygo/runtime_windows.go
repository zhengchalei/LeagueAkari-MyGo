package main

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"syscall"
	"time"
	"unsafe"
)

func (d *Desktop) runtimeInfo() object {
	var memory runtime.MemStats
	runtime.ReadMemStats(&memory)
	var status struct {
		Length, Load                                                                         uint32
		Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended uint64
	}
	status.Length = uint32(unsafe.Sizeof(status))
	syscall.NewLazyDLL("kernel32.dll").NewProc("GlobalMemoryStatusEx").Call(uintptr(unsafe.Pointer(&status)))
	var version struct {
		Size, Major, Minor, Build, Platform uint32
		ServicePack                         [128]uint16
	}
	version.Size = uint32(unsafe.Sizeof(version))
	syscall.NewLazyDLL("ntdll.dll").NewProc("RtlGetVersion").Call(uintptr(unsafe.Pointer(&version)))
	cpus := []any{}
	for i := 0; i < runtime.NumCPU(); i++ {
		cpus = append(cpus, object{"model": os.Getenv("PROCESSOR_IDENTIFIER")})
	}
	executable, _ := os.Executable()
	home, _ := os.UserHomeDir()
	return object{"version": appVersion, "platform": "win32", "arch": runtime.GOARCH, "execPath": executable, "pid": os.Getpid(), "title": appName, "memoryUsage": object{"heapUsed": memory.Alloc, "heapTotal": memory.HeapSys, "rss": residentMemoryBytes()}, "cpuUsage": object{}, "uptime": time.Since(d.started).Seconds(), "type": "mygo", "resourcesPath": filepath.Dir(executable), "versions": object{"go": runtime.Version(), "mygo": "0.2.10", "electron": "", "node": "", "chrome": "WebView2", "v8": ""}, "env": object{"NODE_ENV": "production"}, "os": object{"type": "Windows_NT", "release": fmt.Sprintf("%d.%d.%d", version.Major, version.Minor, version.Build), "totalmem": status.Total, "freemem": status.Available, "cpus": cpus, "homedir": home, "tmpdir": os.TempDir()}, "argv": os.Args, "processMemoryInfo": object{"residentSet": residentMemoryBytes() / 1024}}
}

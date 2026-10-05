package main

import (
	"fmt"
	"os"
	"slices"
	"syscall"
	"time"
	"unsafe"
)

// A named Windows mutex also covers systems without Unix-domain socket support.
func acquireInstance() (syscall.Handle, bool, error) {
	name, _ := syscall.UTF16PtrFromString("Local\\LeagueAkari.MyGo")
	mutexProc := syscall.NewLazyDLL("kernel32.dll").NewProc("CreateMutexW")
	handle, _, callErr := mutexProc.Call(0, 0, uintptr(unsafe.Pointer(name)))
	if (slices.Contains(os.Args, "--league-akari-mygo-relaunch") || slices.Contains(os.Args, "--timo-relaunch")) && callErr == syscall.Errno(183) {
		deadline := time.Now().Add(10 * time.Second)
		for callErr == syscall.Errno(183) && time.Now().Before(deadline) {
			syscall.CloseHandle(syscall.Handle(handle))
			time.Sleep(100 * time.Millisecond)
			handle, _, callErr = mutexProc.Call(0, 0, uintptr(unsafe.Pointer(name)))
		}
	}
	if handle == 0 {
		return 0, false, fmt.Errorf("single instance: %w", callErr)
	}
	if callErr == syscall.Errno(183) {
		syscall.CloseHandle(syscall.Handle(handle))
		title, _ := syscall.UTF16PtrFromString(appWindowTitle)
		user32 := syscall.NewLazyDLL("user32.dll")
		window, _, _ := user32.NewProc("FindWindowW").Call(0, uintptr(unsafe.Pointer(title)))
		if window != 0 {
			user32.NewProc("ShowWindow").Call(window, 9)
			user32.NewProc("SetForegroundWindow").Call(window)
		}
		return 0, false, nil
	}
	return syscall.Handle(handle), true, nil
}

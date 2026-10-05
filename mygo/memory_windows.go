package main

import (
	"syscall"
	"unsafe"
)

type processMemoryCounters struct {
	Size, PageFaultCount                           uint32
	PeakWorkingSet, WorkingSet                     uintptr
	PeakPagedPool, PagedPool                       uintptr
	PeakNonPagedPool, NonPagedPool                 uintptr
	PagefileUsage, PeakPagefileUsage, PrivateUsage uintptr
}

func residentMemoryBytes() uint64 {
	process, err := syscall.GetCurrentProcess()
	if err != nil {
		return 0
	}
	counters := processMemoryCounters{}
	counters.Size = uint32(unsafe.Sizeof(counters))
	getMemory := syscall.NewLazyDLL("psapi.dll").NewProc("GetProcessMemoryInfo")
	ok, _, _ := getMemory.Call(uintptr(process), uintptr(unsafe.Pointer(&counters)), uintptr(counters.Size))
	if ok == 0 {
		return 0
	}
	return uint64(counters.WorkingSet)
}

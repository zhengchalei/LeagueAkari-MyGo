//go:build windows

package main

import (
	"context"
	"fmt"
	"os"
	"time"
)

func connectWinUIPipe(ctx context.Context, name string) (*os.File, error) {
	deadline := time.NewTimer(20 * time.Second)
	defer deadline.Stop()
	var last error
	for {
		// Overlapped handles let the Go poller read and publish events at the
		// same time. A synchronous pipe handle deadlocks bidirectional RPC.
		const overlapped = 0x40000000
		file, err := os.OpenFile(`\\.\pipe\`+name, os.O_RDWR|overlapped, 0)
		if err == nil {
			return file, nil
		}
		last = err
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-deadline.C:
			return nil, fmt.Errorf("connect WinUI pipe: %w", last)
		case <-time.After(50 * time.Millisecond):
		}
	}
}

//go:build !windows

package client

import "context"

func Discover(ctx context.Context) (*Auth, error) { return configuredLockfile() }

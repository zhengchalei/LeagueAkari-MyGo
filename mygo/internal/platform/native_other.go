//go:build !windows

package platform

import (
	"context"
	"errors"
)

type unsupportedNative struct{}

var unsupported = errors.New("该原生能力仅支持 Windows")

func newNative() Native                                                       { return unsupportedNative{} }
func (unsupportedNative) Processes(context.Context) ([]Process, error)        { return nil, unsupported }
func (unsupportedNative) Registry(string, string) (string, error)             { return "", unsupported }
func (unsupportedNative) Drives() []string                                    { return nil }
func (unsupportedNative) Launch(string, []string) error                       { return unsupported }
func (unsupportedNative) ForegroundPID() int                                  { return 0 }
func (unsupportedNative) Terminate(int) error                                 { return unsupported }
func (unsupportedNative) Elevated(int) bool                                   { return false }
func (unsupportedNative) Placement(int) (*Placement, error)                   { return nil, unsupported }
func (unsupportedNative) Repair(int, float64, int, int) error                 { return unsupported }
func (unsupportedNative) Key(uint16, bool) error                              { return unsupported }
func (unsupportedNative) Unicode(uint16, bool) error                          { return unsupported }
func (unsupportedNative) WatchKeys(context.Context, func(uint32, bool)) error { return unsupported }
func (unsupportedNative) KeyDown(uint32) bool                                 { return false }
func (unsupportedNative) SupportsMica() bool                                  { return false }
func (unsupportedNative) Supported() bool                                     { return false }
func (s *Service) rebuildWMI() error                                          { return unsupported }
func (s *Service) relaunchElevated() error                                    { return unsupported }

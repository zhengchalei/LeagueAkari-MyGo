package platform

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
)

type ShortcutKey struct {
	KeyID      string `json:"keyId"`
	IsModifier bool   `json:"isModifier"`
	KeyCode    uint32 `json:"keyCode"`
}
type ShortcutDetails struct {
	KeyCodes  []uint32      `json:"keyCodes"`
	Keys      []ShortcutKey `json:"keys"`
	ID        string        `json:"id"`
	UnifiedID string        `json:"unifiedId"`
	Pressed   bool          `json:"pressed"`
}
type registration struct {
	TargetID   string `json:"targetId"`
	ShortcutID string `json:"shortcutId"`
	Type       string `json:"type"`
	callback   func(ShortcutDetails)
}

var keyNames = map[uint32]string{8: "Backspace", 9: "Tab", 12: "NumpadClear", 13: "Enter", 16: "Shift", 17: "Control", 18: "Alt", 19: "Pause", 20: "CapsLock", 27: "Escape", 32: "Space", 33: "PageUp", 34: "PageDown", 35: "End", 36: "Home", 37: "LeftArrow", 38: "UpArrow", 39: "RightArrow", 40: "DownArrow", 44: "PrintScreen", 45: "Insert", 46: "Delete", 91: "LeftMeta", 92: "RightMeta", 93: "Apps", 106: "NumpadMultiply", 107: "NumpadPlus", 108: "Separator", 109: "NumpadMinus", 110: "NumpadDot", 111: "NumpadDivkeyIde", 144: "NumLock", 145: "ScrollLock", 160: "LeftShift", 161: "RightShift", 162: "LeftControl", 163: "RightControl", 164: "LeftAlt", 165: "RightAlt", 186: "Semicolon", 187: "Equals", 188: "Comma", 189: "Minus", 190: "Dot", 191: "ForwardSlash", 192: "Backtick", 219: "OpenBracket", 220: "Backslash", 221: "CloseBracket", 222: "Quote", 226: "Section"}

func init() {
	for code := uint32(48); code <= 57; code++ {
		keyNames[code] = string(rune(code))
	}
	for code := uint32(65); code <= 90; code++ {
		keyNames[code] = string(rune(code))
	}
	for i := uint32(0); i < 10; i++ {
		keyNames[96+i] = fmt.Sprintf("Numpad%d", i)
	}
	for i := uint32(0); i < 12; i++ {
		keyNames[112+i] = fmt.Sprintf("F%d", i+1)
	}
}
func modifier(code uint32) bool {
	return code == 16 || code == 17 || code == 18 || code == 91 || code == 92 || code >= 160 && code <= 165
}
func unified(code uint32) string {
	switch code {
	case 160, 161:
		return "Shift"
	case 162, 163:
		return "Control"
	case 164, 165:
		return "Alt"
	}
	return keyNames[code]
}
func modifierOrder(code uint32) uint32 {
	switch code {
	case 17, 162:
		return 0
	case 163:
		return 1
	case 16:
		return 2
	case 160:
		return 3
	case 161:
		return 4
	case 18:
		return 5
	case 164:
		return 6
	case 165:
		return 7
	case 91:
		return 8
	case 92:
		return 9
	}
	return 1000 + code
}
func details(codes []uint32, pressed bool) ShortcutDetails {
	result := ShortcutDetails{KeyCodes: append([]uint32{}, codes...), Keys: []ShortcutKey{}, Pressed: pressed}
	ids, unifiedIDs := []string{}, []string{}
	for _, code := range codes {
		result.Keys = append(result.Keys, ShortcutKey{keyNames[code], modifier(code), code})
		ids = append(ids, keyNames[code])
		if !slices.Contains(unifiedIDs, unified(code)) {
			unifiedIDs = append(unifiedIDs, unified(code))
		}
	}
	result.ID = strings.Join(ids, "+")
	result.UnifiedID = strings.Join(unifiedIDs, "+")
	return result
}
func validShortcut(id string) bool {
	if id == "" {
		return false
	}
	for _, key := range strings.Split(id, "+") {
		found := false
		for _, name := range keyNames {
			if name == key {
				found = true
				break
			}
		}
		if !found || key == "Enter" {
			return false
		}
	}
	return true
}

func (s *Service) RegisterTarget(targetID, shortcutID, kind string, callback func(ShortcutDetails)) error {
	if targetID == "" || !validShortcut(shortcutID) {
		return errors.New("快捷键包含不支持或保留的按键")
	}
	if kind != "normal" && kind != "stateful" && kind != "last-active" {
		return errors.New("快捷键类型无效")
	}
	if !s.native.Supported() {
		return errors.New("系统不支持全局快捷键")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if current := s.shortcuts[shortcutID]; current != nil && current.TargetID != targetID {
		return fmt.Errorf("快捷键已被 %s 使用", current.TargetID)
	}
	if old := s.targets[targetID]; old != "" && old != shortcutID {
		delete(s.shortcuts, old)
	}
	s.shortcuts[shortcutID] = &registration{TargetID: targetID, ShortcutID: shortcutID, Type: kind, callback: callback}
	s.targets[targetID] = shortcutID
	return nil
}
func (s *Service) UnregisterTarget(targetID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	id, ok := s.targets[targetID]
	if ok {
		delete(s.targets, targetID)
		delete(s.shortcuts, id)
	}
	return ok
}
func (s *Service) shortcutClearLocked() {
	s.shortcuts = map[string]*registration{}
	s.targets = map[string]string{}
	s.pressed = map[uint32]bool{}
	s.lastCodes = nil
	s.statefulCodes = nil
}
func (s *Service) registrationFor(details ShortcutDetails) *registration {
	if registration := s.shortcuts[details.ID]; registration != nil {
		return registration
	}
	return s.shortcuts[details.UnifiedID]
}

func (s *Service) handleKey(code uint32, down bool) {
	if keyNames[code] == "" {
		return
	}
	type notification struct {
		kind     string
		details  ShortcutDetails
		callback func(ShortcutDetails)
	}
	notifications := []notification{}
	s.mu.Lock()
	if s.closed || s.pressed[code] == down {
		s.mu.Unlock()
		return
	}
	if down {
		s.pressed[code] = true
	} else {
		delete(s.pressed, code)
	}
	if len(s.statefulCodes) > 0 && (!down && slices.Contains(s.statefulCodes, code) || down && !modifier(code)) {
		value := details(s.statefulCodes, false)
		callback := func(ShortcutDetails) {}
		if registration := s.registrationFor(value); registration != nil && registration.Type == "stateful" && registration.callback != nil {
			callback = registration.callback
		}
		notifications = append(notifications, notification{"stateful-shortcut-released", value, callback})
		s.statefulCodes = nil
	}
	if down && !modifier(code) {
		codes := s.sortedPressed()
		value := details(codes, true)
		s.lastCodes = append([]uint32(nil), codes...)
		var callback func(ShortcutDetails)
		if registration := s.registrationFor(value); registration != nil {
			if registration.Type == "normal" {
				callback = registration.callback
			}
			if registration.Type == "stateful" {
				s.statefulCodes = append([]uint32(nil), codes...)
				notifications = append(notifications, notification{"stateful-shortcut-pressed", value, registration.callback})
			}
		}
		notifications = append(notifications, notification{"shortcut", value, callback})
	}
	if len(s.pressed) == 0 && len(s.lastCodes) > 0 {
		value := details(s.lastCodes, false)
		var callback func(ShortcutDetails)
		if registration := s.registrationFor(value); registration != nil && registration.Type == "last-active" {
			callback = registration.callback
		}
		notifications = append(notifications, notification{"last-active-shortcut", value, callback})
		s.lastCodes = nil
	}
	s.mu.Unlock()
	for _, notification := range notifications {
		if s.options.Emit != nil {
			s.options.Emit("keyboard-shortcuts-main", notification.kind, notification.details)
		}
		if notification.callback != nil {
			notification.callback(notification.details)
		}
	}
}

// sortedPressed is called with the service mutex held.
func (s *Service) sortedPressed() []uint32 {
	out := []uint32{}
	for code := range s.pressed {
		out = append(out, code)
	}
	slices.SortFunc(out, func(a, b uint32) int { return int(modifierOrder(a)) - int(modifierOrder(b)) })
	return out
}
func registrationView(registration *registration) any {
	if registration == nil {
		return nil
	}
	return map[string]any{"type": registration.Type, "targetId": registration.TargetID, "shortcutId": registration.ShortcutID}
}
func (s *Service) shortcutCall(method string, args []any) (any, error) {
	switch method {
	case "getRegistration":
		s.mu.RLock()
		defer s.mu.RUnlock()
		id := textArg(args, 0)
		if !validShortcut(id) {
			return map[string]any{"type": "normal", "targetId": "akari-disabled-keys", "shortcutId": id}, nil
		}
		return registrationView(s.shortcuts[id]), nil
	case "getRegistrationByTargetId":
		s.mu.RLock()
		defer s.mu.RUnlock()
		target := textArg(args, 0)
		if target == "akari-disabled-keys" {
			return map[string]any{"type": "normal", "targetId": target, "shortcutId": ""}, nil
		}
		return registrationView(s.shortcuts[s.targets[target]]), nil
	case "setDebugStatefulShortcut":
		target := "keyboard-shortcuts-main/debug-stateful-test"
		s.UnregisterTarget(target)
		id := textArg(args, 0)
		if id == "" {
			return nil, nil
		}
		if err := s.RegisterTarget(target, id, "stateful", nil); err != nil {
			return nil, err
		}
		return map[string]any{"type": "stateful", "targetId": target, "shortcutId": id}, nil
	case "getDebugState", "_getInternalVars":
		s.mu.RLock()
		pressed := s.sortedPressed()
		last := append([]uint32(nil), s.lastCodes...)
		stateful := append([]uint32(nil), s.statefulCodes...)
		available := s.hookReady && s.hookError == ""
		s.mu.RUnlock()
		mods, other := []uint32{}, []uint32{}
		for _, code := range pressed {
			if modifier(code) {
				mods = append(mods, code)
			} else {
				other = append(other, code)
			}
		}
		if method == "_getInternalVars" {
			return map[string]any{"_pressedOtherKeys": other, "_pressedModifierKeys": mods, "_lastActiveShortcut": last, "_activeStatefulShortcut": stateful}, nil
		}
		keys := []any{}
		codes := []uint32{}
		for code := range keyNames {
			codes = append(codes, code)
		}
		slices.Sort(codes)
		for _, code := range codes {
			keys = append(keys, map[string]any{"keyCode": code, "keyId": keyNames[code], "unifiedKeyId": unified(code), "name": keyNames[code], "standardName": keyNames[code], "isModifier": modifier(code), "pressed": s.native.KeyDown(code)})
		}
		view := func(codes []uint32) any {
			if len(codes) == 0 {
				return nil
			}
			return details(codes, true)
		}
		return map[string]any{"available": available, "keyStates": keys, "pressedOtherKeys": other, "pressedModifierKeys": mods, "activeShortcut": view(pressed), "lastActiveShortcut": view(last), "activeStatefulShortcut": view(stateful)}, nil
	}
	return nil, fmt.Errorf("未知快捷键操作: %s", method)
}

func (s *Service) settingChanged(namespace, key string) {
	if strings.HasPrefix(namespace, "window-manager-main/") && s.options.WindowAction != nil {
		if key == "" || key == "opacity" || key == "pinned" {
			_, _ = s.options.WindowAction(strings.TrimPrefix(namespace, "window-manager-main/"), "applySettings", nil)
		}
	}
	if namespace == "window-manager-main" && s.options.WindowAction != nil {
		for _, name := range []string{"main-window", "aux-window", "opgg-window", "ongoing-game-window", "cd-timer-window"} {
			_, _ = s.options.WindowAction(name, "applySettings", nil)
		}
	}
	if key == "showShortcut" || key == "terminateShortcut" || key == "" {
		s.applyShortcutSettings()
	}
	if (key == "" || key == "enabled" || key == "autoShow") && strings.HasPrefix(namespace, "window-manager-main/") {
		s.mu.Lock()
		s.lastPhase = ""
		s.mu.Unlock()
		s.followWindows(context.Background())
	}
}
func (s *Service) applyShortcutSettings() {
	if s.options.Store == nil {
		return
	}
	for _, name := range []string{"opgg-window", "ongoing-game-window", "cd-timer-window"} {
		name := name
		namespace := "window-manager-main/" + name
		id, _ := s.options.Store.Get(namespace, "showShortcut").(string)
		target := namespace + "/show"
		if id == "" {
			s.UnregisterTarget(target)
			continue
		}
		kind := "normal"
		if name == "ongoing-game-window" {
			kind = "stateful"
		}
		err := s.RegisterTarget(target, id, kind, func(details ShortcutDetails) {
			if s.options.WindowAction == nil || s.Setting(namespace, "enabled") != true {
				return
			}
			if name == "ongoing-game-window" {
				if details.Pressed {
					foreground, _ := s.IsGameForeground(context.Background())
					if !foreground {
						return
					}
				}
				_, _ = s.options.WindowAction(name, "setFakeShow", []any{details.Pressed})
			} else {
				_, _ = s.options.WindowAction(name, "toggle", []any{true})
			}
		})
		if err != nil && s.options.Emit != nil {
			s.options.Emit(namespace, "error-shortcut-registration", err.Error())
		}
	}
	target := "game-client-main/terminate-game-client"
	id, _ := s.options.Store.Get("game-client-main", "terminateShortcut").(string)
	if id == "" {
		s.UnregisterTarget(target)
	} else {
		_ = s.RegisterTarget(target, id, "normal", func(ShortcutDetails) {
			if s.options.Store.Get("game-client-main", "terminateGameClientWithShortcut") == true {
				if err := s.TerminateGame(context.Background()); err != nil && s.options.Emit != nil {
					s.options.Emit("game-client-main", "error-terminate", err.Error())
				}
			}
		})
	}
}

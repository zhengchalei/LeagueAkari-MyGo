package main

import (
	"path/filepath"
	"runtime"
	"slices"
	"syscall"
	"testing"
	"unsafe"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestWindowPresentationStaysHiddenUntilRequestedAndReady(t *testing.T) {
	for _, readyFirst := range []bool{false, true} {
		t.Run(map[bool]string{false: "loading", true: "already-ready"}[readyFirst], func(t *testing.T) {
			shows := []bool{}
			window := &windowPresentation{display: func(inactive bool) { shows = append(shows, inactive) }}
			if readyFirst {
				window.pageReady()
				if len(shows) != 0 {
					t.Fatal("creating or loading a window displayed it without a request")
				}
			}
			window.request(true)
			if !readyFirst {
				if len(shows) != 0 {
					t.Fatal("a display request exposed the empty loading window")
				}
				window.pageReady()
			}
			if !slices.Equal(shows, []bool{true}) {
				t.Fatalf("requested inactive display was lost: %v", shows)
			}
		})
	}
}

func TestWindowPresentationHideCancelsLoadingAndAllowsManualReopen(t *testing.T) {
	shows := []bool{}
	window := &windowPresentation{display: func(inactive bool) { shows = append(shows, inactive) }}
	window.request(true)
	window.cancel()
	window.pageReady()
	if len(shows) != 0 {
		t.Fatal("a hidden or released overlay appeared after loading")
	}
	window.request(false)
	if !slices.Equal(shows, []bool{false}) {
		t.Fatalf("manual reopen failed: %v", shows)
	}
}

func TestFramelessStyleKeepsNativeWindowControlsAndSizing(t *testing.T) {
	// Captured main window style, including the caption accidentally retained
	// by MyGo, and the additional flags Windows sets as the user maximizes it.
	for _, style := range []uintptr{0x06cf0000, 0x16cf0000, 0x17cf0000, 0x36cf0000} {
		next := captionlessWindowStyle(style)
		if next&0x00c00000 != 0 {
			t.Fatal("system caption can still paint over the custom toolbar")
		}
		if next&^uintptr(0x00c00000) != style&^uintptr(0x00c00000) {
			t.Fatalf("caption removal changed resize/minimize/maximize/visibility flags: %x -> %x", style, next)
		}
	}
}

func TestWindowMaterialRespectsTheUserChoiceAtCreation(t *testing.T) {
	for _, scenario := range []struct {
		choice    any
		supported bool
		want      mygo.Vibrancy
	}{{"none", true, mygo.VibrancyNone}, {nil, true, mygo.VibrancyNone}, {"mica", false, mygo.VibrancyNone}, {"mica", true, mygo.VibrancyMica}} {
		if got := requestedWindowMaterial(scenario.choice, scenario.supported); got != scenario.want {
			t.Fatalf("choice %v supported=%v enabled wrong backdrop: %s", scenario.choice, scenario.supported, got)
		}
	}
}

func TestNativeCaptionRemovalKeepsHiddenResizableWindow(t *testing.T) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	class, _ := syscall.UTF16PtrFromString("STATIC")
	title, _ := syscall.UTF16PtrFromString("Timo hidden caption test")
	const original = 0x00cf0000 // OVERLAPPEDWINDOW, deliberately without WS_VISIBLE.
	handle, _, err := windowUser32.NewProc("CreateWindowExW").Call(0, uintptr(unsafe.Pointer(class)), uintptr(unsafe.Pointer(title)), original, 0, 0, 320, 200, 0, 0, 0, 0)
	if handle == 0 {
		t.Fatalf("create hidden native fixture: %v", err)
	}
	defer windowUser32.NewProc("DestroyWindow").Call(handle)
	index := int32(-16)
	initial, _, _ := windowGetStyle.Call(handle, uintptr(index))
	if err := clearNativeCaption(handle); err != nil {
		t.Fatal(err)
	}
	style, _, _ := windowGetStyle.Call(handle, uintptr(index))
	if style != initial&^uintptr(0x00c00000) {
		t.Fatalf("caption repair changed other native flags: %#x", style)
	}
	if visible, _, _ := windowUser32.NewProc("IsWindowVisible").Call(handle); visible != 0 {
		t.Fatal("caption repair showed a hidden native fixture")
	}
}

func TestWindowLayoutSavesEachNormalMoveAndResizeImmediately(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, err := settings.New(path)
	if err != nil {
		t.Fatal(err)
	}
	const namespace = "window-manager-main/main-window"
	for _, bounds := range []mygo.Rectangle{{X: 120, Y: 90, Width: 1100, Height: 820}, {X: 340, Y: 240, Width: 1100, Height: 820}, {X: 340, Y: 240, Width: 1773, Height: 1118}} {
		if err := persistWindowLayout(store, namespace, bounds, false, false, false); err != nil {
			t.Fatal(err)
		}
		restarted, err := settings.New(path)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := savedRectangle(restarted.Get(namespace, "trackedBounds"))
		if !ok || got != bounds || restarted.Get(namespace, "maximized") != false {
			t.Fatalf("latest move/resize was not durable before close: got %v, want %v", got, bounds)
		}
	}
}

func TestMaximizeAndMinimizeRetainNormalLayoutAndRestartState(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, err := settings.New(path)
	if err != nil {
		t.Fatal(err)
	}
	const namespace = "window-manager-main/main-window"
	normal := mygo.Rectangle{X: 397, Y: 184, Width: 1773, Height: 1118}
	if err := persistWindowLayout(store, namespace, normal, false, false, false); err != nil {
		t.Fatal(err)
	}
	maximized := mygo.Rectangle{X: 0, Y: 0, Width: 2560, Height: 1400}
	if err := persistWindowLayout(store, namespace, maximized, true, false, false); err != nil {
		t.Fatal(err)
	}
	// IsMaximized can be false while a previously maximized window is minimized.
	if err := persistWindowLayout(store, namespace, mygo.Rectangle{X: -32000, Y: -32000, Width: 160, Height: 28}, false, true, false); err != nil {
		t.Fatal(err)
	}
	restarted, err := settings.New(path)
	if err != nil {
		t.Fatal(err)
	}
	got, ok := savedRectangle(restarted.Get(namespace, "trackedBounds"))
	if !ok || got != normal || restarted.Get(namespace, "maximized") != true {
		t.Fatalf("maximized/minimized geometry overwrote normal layout: %v", got)
	}
	if err := persistWindowLayout(store, namespace, normal, false, false, false); err != nil {
		t.Fatal(err)
	}
	if store.Get(namespace, "maximized") != false {
		t.Fatal("restoring the normal window did not clear the saved maximized state")
	}
}

func TestWindowBoundsRestorePreservesSizeAndCorrectsDisconnectedDisplays(t *testing.T) {
	primary := mygo.Rectangle{X: 0, Y: 0, Width: 2560, Height: 1400}
	secondary := mygo.Rectangle{X: -1920, Y: 0, Width: 1920, Height: 1040}
	for _, scenario := range []struct {
		name   string
		bounds mygo.Rectangle
		areas  []mygo.Rectangle
		want   mygo.Rectangle
	}{
		{"unchanged", mygo.Rectangle{X: 397, Y: 184, Width: 1773, Height: 1118}, []mygo.Rectangle{primary}, mygo.Rectangle{X: 397, Y: 184, Width: 1773, Height: 1118}},
		{"top-left", mygo.Rectangle{X: 0, Y: 0, Width: 1100, Height: 820}, []mygo.Rectangle{primary}, mygo.Rectangle{X: 0, Y: 0, Width: 1100, Height: 820}},
		{"secondary-negative-position", mygo.Rectangle{X: -1700, Y: 100, Width: 1100, Height: 820}, []mygo.Rectangle{primary, secondary}, mygo.Rectangle{X: -1700, Y: 100, Width: 1100, Height: 820}},
		{"disconnected-display", mygo.Rectangle{X: 6000, Y: 3000, Width: 1773, Height: 1118}, []mygo.Rectangle{primary}, mygo.Rectangle{X: 393, Y: 141, Width: 1773, Height: 1118}},
		{"too-large-for-display", mygo.Rectangle{X: 0, Y: 0, Width: 3000, Height: 2000}, []mygo.Rectangle{primary}, primary},
		{"minimum-size", mygo.Rectangle{X: 0, Y: 0, Width: 100, Height: 200}, []mygo.Rectangle{primary}, mygo.Rectangle{X: 0, Y: 0, Width: 840, Height: 600}},
		{"partly-off-screen", mygo.Rectangle{X: 2400, Y: 1300, Width: 1100, Height: 820}, []mygo.Rectangle{primary}, mygo.Rectangle{X: 1460, Y: 580, Width: 1100, Height: 820}},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			got, ok := restoreWindowBounds(rectangleValue(scenario.bounds), 840, 600, scenario.areas)
			if !ok || got != scenario.want {
				t.Fatalf("restored geometry %v, want %v", got, scenario.want)
			}
		})
	}
	if _, valid := restoreWindowBounds(object{"x": 0, "y": 0, "width": -1, "height": 600}, 840, 600, []mygo.Rectangle{primary}); valid {
		t.Fatal("invalid dimensions replaced the default layout")
	}
}

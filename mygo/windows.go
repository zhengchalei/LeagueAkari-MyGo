package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func (d *Desktop) openWindow(name string) *mygo.Window {
	win := d.ensureWindow(name)
	if win == nil {
		return nil
	}
	requestWindowShow(win, name == "cd-timer-window" || name == "ongoing-game-window")
	return win
}

// Keep an explicitly requested display pending until MyGo can paint the page.
// Creating or updating a window alone must never make it visible.
type windowPresentation struct {
	mu        sync.Mutex
	ready     bool
	requested bool
	inactive  bool
	display   func(bool)
}

var windowPresentations sync.Map // *mygo.Window -> *windowPresentation

var (
	windowUser32       = syscall.NewLazyDLL("user32.dll")
	windowGetStyle     = windowUser32.NewProc("GetWindowLongPtrW")
	windowSetStyle     = windowUser32.NewProc("SetWindowLongPtrW")
	windowRefreshFrame = windowUser32.NewProc("SetWindowPos")
)

func captionlessWindowStyle(style uintptr) uintptr {
	return style &^ 0x00c00000 // WS_CAPTION; retain resize, system-menu and min/max flags.
}

func suppressNativeCaption(win *mygo.Window) error {
	var result error
	mygo.RunOnMain(func() {
		if win.IsDestroyed() {
			return
		}
		result = clearNativeCaption(win.NativeHandle())
	})
	return result
}

func clearNativeCaption(handle uintptr) error {
	index := int32(-16) // GWL_STYLE
	style, _, err := windowGetStyle.Call(handle, uintptr(index))
	if style == 0 && err != syscall.Errno(0) {
		return fmt.Errorf("读取原生窗口样式: %w", err)
	}
	next := captionlessWindowStyle(style)
	if next == style {
		return nil
	}
	if previous, _, err := windowSetStyle.Call(handle, uintptr(index), next); previous == 0 && err != syscall.Errno(0) {
		return fmt.Errorf("隐藏原生窗口标题栏: %w", err)
	}
	// MyGo Frameless removes caption space with WM_NCCALCSIZE, but keeps
	// WS_CAPTION. A DWM backdrop can still draw its buttons over our toolbar.
	const flags = 0x1 | 0x2 | 0x4 | 0x10 | 0x20 // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED
	if ok, _, err := windowRefreshFrame.Call(handle, 0, 0, 0, 0, 0, flags); ok == 0 {
		return fmt.Errorf("更新无边框窗口: %w", err)
	}
	return nil
}

func (d *Desktop) windowMaterial() mygo.Vibrancy {
	supported := d.platform != nil && d.platform.State("window-manager-main")["supportsMica"] == true
	return requestedWindowMaterial(d.settingValue("window-manager-main", "backgroundMaterial"), supported)
}

func requestedWindowMaterial(material any, supportsMica bool) mygo.Vibrancy {
	if supportsMica && material == "mica" {
		return mygo.VibrancyMica
	}
	return mygo.VibrancyNone
}

func rectangleValue(bounds mygo.Rectangle) object {
	return object{"x": bounds.X, "y": bounds.Y, "width": bounds.Width, "height": bounds.Height}
}

func savedRectangle(value any) (mygo.Rectangle, bool) {
	fields, ok := value.(map[string]any)
	if !ok {
		return mygo.Rectangle{}, false
	}
	coordinates := make([]int, 4)
	for index, key := range []string{"x", "y", "width", "height"} {
		entry, exists := fields[key]
		number := client.Number(entry)
		if !exists || number < -1e7 || number > 1e7 {
			return mygo.Rectangle{}, false
		}
		coordinates[index] = int(number)
	}
	bounds := mygo.Rectangle{X: coordinates[0], Y: coordinates[1], Width: coordinates[2], Height: coordinates[3]}
	return bounds, bounds.Width > 0 && bounds.Height > 0
}

func restoreWindowBounds(value any, minWidth, minHeight int, workAreas []mygo.Rectangle) (mygo.Rectangle, bool) {
	bounds, ok := savedRectangle(value)
	if !ok {
		return bounds, false
	}
	bounds.Width, bounds.Height = max(minWidth, bounds.Width), max(minHeight, bounds.Height)
	if len(workAreas) == 0 {
		return bounds, true
	}
	work, bestOverlap := workAreas[0], 0
	for _, area := range workAreas {
		if area.Width <= 0 || area.Height <= 0 {
			continue
		}
		overlap := max(0, min(bounds.X+bounds.Width, area.X+area.Width)-max(bounds.X, area.X)) * max(0, min(bounds.Y+bounds.Height, area.Y+area.Height)-max(bounds.Y, area.Y))
		if overlap > bestOverlap {
			work, bestOverlap = area, overlap
		}
	}
	if work.Width <= 0 || work.Height <= 0 {
		return bounds, true
	}
	bounds.Width = max(minWidth, min(bounds.Width, work.Width))
	bounds.Height = max(minHeight, min(bounds.Height, work.Height))
	if bestOverlap == 0 {
		bounds.X, bounds.Y = work.X+(work.Width-bounds.Width)/2, work.Y+(work.Height-bounds.Height)/2
	}
	bounds.X = max(work.X, min(bounds.X, work.X+work.Width-bounds.Width))
	bounds.Y = max(work.Y, min(bounds.Y, work.Y+work.Height-bounds.Height))
	return bounds, true
}

func (d *Desktop) savedWindowLayout(name string) (any, bool) {
	namespace := "window-manager-main/" + name
	if d.store != nil {
		bounds := d.store.Get(namespace, "trackedBounds")
		if _, ok := savedRectangle(bounds); ok {
			return bounds, d.store.Get(namespace, "maximized") == true
		}
	}
	// Import MyGo's last clean-exit layout once when no immediately saved
	// layout exists. StateKey itself only writes at close/quit.
	dir, err := mygo.App.Path(mygo.PathUserData)
	if err != nil {
		return nil, false
	}
	data, err := os.ReadFile(filepath.Join(dir, "window-state.json"))
	if err != nil {
		return nil, false
	}
	var layouts map[string]object
	if json.Unmarshal(data, &layouts) != nil {
		return nil, false
	}
	return layouts[name], layouts[name]["maximized"] == true
}

func persistWindowLayout(store *settings.Store, namespace string, bounds mygo.Rectangle, maximized, minimized, fullScreen bool) error {
	if store == nil || minimized || fullScreen {
		return nil
	}
	if previous := store.Get(namespace, "maximized"); previous != maximized {
		if err := store.Set(namespace, "maximized", maximized); err != nil {
			return err
		}
	}
	if maximized || bounds.Width <= 0 || bounds.Height <= 0 {
		return nil
	}
	return saveNormalWindowBounds(store, namespace, bounds)
}

func saveNormalWindowBounds(store *settings.Store, namespace string, bounds mygo.Rectangle) error {
	if store == nil || bounds.Width <= 0 || bounds.Height <= 0 {
		return nil
	}
	previous, ok := savedRectangle(store.Get(namespace, "trackedBounds"))
	if ok && previous == bounds {
		return nil
	}
	return store.Set(namespace, "trackedBounds", rectangleValue(bounds))
}

func (p *windowPresentation) request(inactive bool) {
	p.mu.Lock()
	p.requested, p.inactive = true, inactive
	ready := p.ready
	p.mu.Unlock()
	if ready {
		p.display(inactive)
	}
}

func (p *windowPresentation) pageReady() {
	p.mu.Lock()
	p.ready = true
	requested, inactive := p.requested, p.inactive
	p.mu.Unlock()
	if requested {
		p.display(inactive)
	}
}

func (p *windowPresentation) cancel() {
	p.mu.Lock()
	p.requested = false
	p.mu.Unlock()
}

func requestWindowShow(win *mygo.Window, inactive bool) {
	mygo.RunOnMain(func() {
		if value, ok := windowPresentations.Load(win); ok {
			value.(*windowPresentation).request(inactive)
		}
	})
}

func hideWindow(win *mygo.Window) {
	mygo.RunOnMain(func() {
		if value, ok := windowPresentations.Load(win); ok {
			value.(*windowPresentation).cancel()
		}
		win.Hide()
	})
}

func (d *Desktop) ensureWindow(name string) *mygo.Window {
	var win *mygo.Window
	mygo.RunOnMain(func() { win = d.ensureWindowOnMain(name) })
	return win
}

func (d *Desktop) ensureWindowOnMain(name string) *mygo.Window {
	d.windowMu.Lock()
	defer d.windowMu.Unlock()
	d.mu.RLock()
	existing := d.windows[name]
	d.mu.RUnlock()
	if existing != nil && !existing.IsDestroyed() {
		return existing
	}
	width, height, minWidth, minHeight := 1100, 820, 840, 600
	transparent, overlay := false, false
	switch name {
	case "main-window":
	case "aux-window":
		width, height, minWidth, minHeight = 340, 620, 340, 420
	case "opgg-window":
		width, height, minWidth, minHeight = 530, 720, 530, 530
	case "ongoing-game-window":
		width, height, minWidth, minHeight = 1300, 840, 1300, 840
		transparent, overlay = true, true
	case "cd-timer-window":
		width, height, minWidth, minHeight = 100, 220, 100, 100
		transparent, overlay = true, true
	default:
		return nil
	}
	workAreas := []mygo.Rectangle{}
	for _, display := range mygo.Screen.Displays() {
		workAreas = append(workAreas, display.WorkArea)
	}
	savedBounds, savedMaximized := d.savedWindowLayout(name)
	restored, hasSavedBounds := restoreWindowBounds(savedBounds, minWidth, minHeight, workAreas)
	options := mygo.WindowOptions{Title: appWindowTitle, URL: "/" + name + ".html", Width: width, Height: height, MinWidth: minWidth, MinHeight: minHeight, Hidden: true, Frameless: true, Transparent: transparent, DisableMaximize: name != "main-window", DisableMinimize: overlay, DisableResize: overlay, DisableShadow: overlay, SkipTaskbar: overlay, AlwaysOnTop: overlay, BackgroundColor: "#f3f4f6", Page: mygo.PageOptions{PreloadScript: fmt.Sprintf("window.akariWindowType=%q;", name), DevTools: mygo.DevToolsAuto}}
	if hasSavedBounds {
		options.X, options.Y, options.Width, options.Height = restored.X, restored.Y, restored.Width, restored.Height
		options.Maximized = savedMaximized && !options.DisableMaximize
	}
	if overlay {
		options.BackgroundColor = "#00000000"
	}
	if !overlay {
		options.Vibrancy = d.windowMaterial()
	}
	win := mygo.NewWindow(options)
	if hasSavedBounds {
		// MyGo treats X=Y=0 as "center"; SetBounds also restores that legitimate
		// top-left position, and runs before move/resize persistence is attached.
		win.SetBounds(restored)
	}
	namespace := "window-manager-main/" + name
	normalBounds := win.Bounds()
	if hasSavedBounds {
		normalBounds = restored
	}
	if err := saveNormalWindowBounds(d.store, namespace, normalBounds); err != nil {
		log.Printf("保存初始窗口布局失败: %v", err)
	}
	if err := suppressNativeCaption(win); err != nil {
		log.Print(err)
	}
	presentation := &windowPresentation{display: func(inactive bool) {
		if win.IsDestroyed() {
			return
		}
		// SW_RESTORE displays the HWND immediately. Only restore a minimized,
		// already loaded window, never a freshly created hidden one.
		if win.IsMinimized() {
			win.Restore()
		}
		if inactive {
			win.ShowInactive()
		} else {
			win.Show()
			win.Focus()
		}
	}}
	windowPresentations.Store(win, presentation)
	d.mu.Lock()
	d.windows[name] = win
	d.mu.Unlock()
	_ = win.SetIcon(trayIcon())
	d.windowUpdate(namespace, "ready", false)
	d.windowUpdate(namespace, "show", false)
	d.windowUpdate(namespace, "focus", "blurred")
	d.applyWindowSettings(name, win)
	if overlay {
		win.SetIgnoreMouseEvents(true)
	}
	if name == "main-window" {
		win.OnClose(func(event *mygo.CloseEvent) {
			if !d.quitting.Load() {
				event.PreventDefault()
				d.closeMainWindow("")
			}
		})
	} else {
		win.OnClose(func(event *mygo.CloseEvent) {
			if !d.quitting.Load() && d.store != nil && d.store.Get(namespace, "enabled") == true {
				event.PreventDefault()
				hideWindow(win)
			}
		})
	}
	win.Page().OnDOMReady(func() {
		d.windowUpdate(namespace, "ready", true)
		d.trackWindowBounds(namespace, win)
	})
	win.OnReadyToShow(presentation.pageReady)
	win.OnClosed(func() {
		presentation.cancel()
		windowPresentations.Delete(win)
		d.mu.Lock()
		if d.windows[name] == win {
			delete(d.windows, name)
		}
		d.mu.Unlock()
		d.windowUpdate(namespace, "show", false)
		d.windowUpdate(namespace, "ready", false)
	})
	win.OnShow(func() { d.windowUpdate(namespace, "show", true) })
	win.OnHide(func() {
		presentation.cancel()
		d.windowUpdate(namespace, "show", false)
		if overlay {
			d.windowUpdate(namespace, "fakeShow", false)
		}
	})
	win.OnFocus(func() { d.windowUpdate(namespace, "focus", "focused") })
	win.OnBlur(func() { d.windowUpdate(namespace, "focus", "blurred") })
	win.OnMaximize(func() {
		d.windowUpdate(namespace, "status", "maximized")
		d.trackWindowBounds(namespace, win)
	})
	win.OnUnmaximize(func() {
		d.windowUpdate(namespace, "status", "normal")
		d.trackWindowBounds(namespace, win)
	})
	win.OnMinimize(func() { d.windowUpdate(namespace, "status", "minimized") })
	win.OnRestore(func() {
		status := "normal"
		if win.IsMaximized() {
			status = "maximized"
		}
		d.windowUpdate(namespace, "status", status)
		d.trackWindowBounds(namespace, win)
	})
	win.OnMove(func() { d.trackWindowBounds(namespace, win) })
	win.OnResize(func() { d.trackWindowBounds(namespace, win) })
	win.Hide()
	return win
}

func (d *Desktop) trackWindowBounds(namespace string, win *mygo.Window) {
	if win.IsDestroyed() {
		return
	}
	bounds := win.Bounds()
	d.windowUpdate(namespace, "bounds", rectangleValue(bounds))
	if err := persistWindowLayout(d.store, namespace, bounds, win.IsMaximized(), win.IsMinimized(), win.IsFullScreen()); err != nil {
		log.Printf("保存窗口位置和尺寸失败: %v", err)
	}
	if d.store != nil {
		if tracked := d.store.Get(namespace, "trackedBounds"); tracked != nil {
			d.windowUpdate(namespace, "trackedBounds", tracked)
		}
	}
}
func (d *Desktop) applyWindowSettings(name string, win *mygo.Window) {
	if win == nil || win.IsDestroyed() {
		return
	}
	namespace := "window-manager-main/" + name
	win.SetAlwaysOnTop(d.settingValue(namespace, "pinned") == true || name == "cd-timer-window" || name == "ongoing-game-window")
	if opacity, ok := d.settingValue(namespace, "opacity").(float64); ok {
		win.SetOpacity(max(0.1, min(1, opacity)))
	}
	win.SetContentProtection(d.settingValue("window-manager-main", "contentProtection") == true)
	if name != "cd-timer-window" && name != "ongoing-game-window" {
		win.SetVibrancy(d.windowMaterial())
	}
}
func (d *Desktop) windowCallByName(name, method string, args []any) (any, error) {
	return d.windowCall("window-manager-main/"+name, method, args)
}
func (d *Desktop) windowCall(namespace, method string, args []any) (any, error) {
	if method == "quit" {
		mygo.App.Quit()
		return nil, nil
	}
	name := strings.TrimPrefix(namespace, "window-manager-main/")
	if method == "sendInGame" && name == "cd-timer-window" {
		if d.platform == nil {
			return nil, errors.New("原生输入尚未初始化")
		}
		return d.platform.Call(context.Background(), namespace, method, args)
	}
	d.mu.RLock()
	win := d.windows[name]
	d.mu.RUnlock()
	if method == "ensure" {
		if d.ensureWindow(name) == nil {
			return nil, fmt.Errorf("窗口不存在: %s", name)
		}
		return nil, nil
	}
	if method == "setTrafficLightPosition" {
		return nil, nil
	} // macOS-only IPC, as in the original Windows implementation.
	if method == "show" || method == "restore" {
		win = d.ensureWindow(name)
		if win == nil {
			return nil, fmt.Errorf("窗口不存在: %s", name)
		}
		inactive, _ := arg(args, 0).(bool)
		requestWindowShow(win, inactive || name == "cd-timer-window" || name == "ongoing-game-window")
		return nil, nil
	}
	if method == "setFakeShow" {
		show, _ := arg(args, 0).(bool)
		if show {
			win = d.ensureWindow(name)
		} else if win == nil || win.IsDestroyed() {
			d.windowUpdate(namespace, "fakeShow", false)
			return nil, nil
		}
	}
	if method == "closeMainWindowForce" {
		mygo.App.Quit()
		return nil, nil
	}
	if win == nil || win.IsDestroyed() {
		switch method {
		case "hide", "close", "applySettings":
			return nil, nil
		case "getSize", "getPosition":
			return nil, nil
		case "toggle":
			win = d.ensureWindow(name)
		default:
			return nil, fmt.Errorf("窗口尚未创建: %s", name)
		}
	}
	if win == nil {
		return nil, fmt.Errorf("窗口不存在: %s", name)
	}
	switch method {
	case "minimize":
		win.Minimize()
	case "maximize":
		win.Maximize()
	case "unmaximize":
		win.Unmaximize()
	case "close", "closeMainWindow":
		if name == "main-window" {
			d.closeMainWindow(textArg(args, 0))
		} else {
			if d.store != nil && d.store.Get(namespace, "enabled") == true {
				hideWindow(win)
			} else {
				if value, ok := windowPresentations.Load(win); ok {
					value.(*windowPresentation).cancel()
				}
				win.Close()
			}
		}
	case "hide":
		hideWindow(win)
	case "toggle":
		pending := false
		if value, ok := windowPresentations.Load(win); ok {
			presentation := value.(*windowPresentation)
			presentation.mu.Lock()
			pending = presentation.requested
			presentation.mu.Unlock()
		}
		if win.IsVisible() || pending {
			hideWindow(win)
		} else {
			inactive, _ := arg(args, 0).(bool)
			requestWindowShow(win, inactive || name == "cd-timer-window" || name == "ongoing-game-window")
		}
	case "setTitle":
		win.SetTitle(textArg(args, 0))
	case "getSize":
		bounds := win.Bounds()
		return []int{bounds.Width, bounds.Height}, nil
	case "setSize":
		width, height := int(client.Number(arg(args, 0))), int(client.Number(arg(args, 1)))
		if width <= 0 || height <= 0 {
			return nil, errors.New("窗口尺寸必须大于零")
		}
		win.SetSize(width, height)
	case "getPosition":
		bounds := win.Bounds()
		return []int{bounds.X, bounds.Y}, nil
	case "setPosition":
		win.SetPosition(int(client.Number(arg(args, 0))), int(client.Number(arg(args, 1))))
	case "resetPosition":
		win.Center()
	case "repositionWindowIfInvisible":
		repositionWindowIfInvisible(win)
	case "repositionToAlignLeagueClientUx":
		if d.platform == nil {
			return nil, errors.New("客户端窗口服务尚未初始化")
		}
		placement, err := d.platform.ClientPlacement(context.Background())
		if err != nil {
			return nil, err
		}
		if placement == nil || placement.IsMinimized || placement.Width < 200 && placement.Height < 50 {
			return nil, nil
		}
		bounds := win.Bounds()
		target := mygo.Rectangle{X: placement.Left, Y: placement.Top, Width: placement.Width, Height: placement.Height}
		work := mygo.Screen.DisplayMatching(target).WorkArea
		point := snapWindow(bounds, target, work, textArg(args, 0))
		win.SetPosition(point.X, point.Y)
	case "toggleDevtools":
		win.Page().ToggleDevTools()
	case "setPinned":
		value, _ := arg(args, 0).(bool)
		if err := d.store.Set(namespace, "pinned", value); err != nil {
			return nil, err
		}
		win.SetAlwaysOnTop(value)
	case "setOpacity":
		value, ok := arg(args, 0).(float64)
		if !ok || value < 0.1 || value > 1 {
			return nil, errors.New("透明度必须为 0.1 到 1")
		}
		if err := d.store.Set(namespace, "opacity", value); err != nil {
			return nil, err
		}
		win.SetOpacity(value)
	case "setIgnoreMouseEvents":
		value, _ := arg(args, 0).(bool)
		win.SetIgnoreMouseEvents(value)
	case "setFakeShow":
		value, _ := arg(args, 0).(bool)
		if value {
			requestWindowShow(win, true)
		} else {
			hideWindow(win)
		}
		win.SetIgnoreMouseEvents(!value)
		d.windowUpdate(namespace, "fakeShow", value)
	case "applySettings":
		d.applyWindowSettings(name, win)
	case "downloadUrl":
		return nil, d.downloadWindowURL(win, textArg(args, 0))
	default:
		return nil, fmt.Errorf("未知窗口操作: %s", method)
	}
	return nil, nil
}
func snapWindow(from, target, work mygo.Rectangle, placement string) mygo.Point {
	x, y := target.X-from.Width, target.Y
	switch placement {
	case "bottom-left":
		y = target.Y + target.Height - from.Height
	case "top-right":
		x = target.X + target.Width
	case "bottom-right":
		x, y = target.X+target.Width, target.Y+target.Height-from.Height
	}
	x = max(work.X, min(x, work.X+work.Width-from.Width))
	y = max(work.Y, min(y, work.Y+work.Height-from.Height))
	return mygo.Point{X: x, Y: y}
}
func repositionWindowIfInvisible(win *mygo.Window) {
	bounds := win.Bounds()
	for _, display := range mygo.Screen.Displays() {
		work := display.WorkArea
		if min(bounds.X+bounds.Width, work.X+work.Width)-max(bounds.X, work.X) > 40 && min(bounds.Y+bounds.Height, work.Y+work.Height)-max(bounds.Y, work.Y) > 40 {
			return
		}
	}
	win.Center()
}
func (d *Desktop) closeMainWindow(strategy string) {
	if strategy == "" {
		strategy, _ = d.settingValue("window-manager-main/main-window", "closeAction").(string)
	}
	if strategy == "ask" {
		d.emit("window-manager-main/main-window", "close-asking")
		d.openWindow("main-window")
		return
	}
	if strategy == "minimize-to-tray" && d.tray != nil {
		d.mu.RLock()
		win := d.windows["main-window"]
		d.mu.RUnlock()
		if win != nil {
			hideWindow(win)
		}
		return
	}
	mygo.App.Quit()
}
func (d *Desktop) windowUpdate(namespace, key string, value any) {
	d.setStatic(namespace+":state", key, value)
	d.update(namespace, "state", key, value)
	if d.platform != nil {
		d.platform.NotifyWindowState(namespace, key, value)
	}
}
func (d *Desktop) downloadWindowURL(win *mygo.Window, rawURL string) (result error) {
	parsed, err := url.Parse(rawURL)
	if err != nil || parsed.Host == "" || (parsed.Scheme != "https" && parsed.Scheme != "http") {
		return errors.New("下载地址无效")
	}
	dir, _ := mygo.App.Path(mygo.PathDownloads)
	filename := filepath.Base(parsed.Path)
	if filename == "." || filename == "/" || filename == "" {
		filename = "download"
	}
	id := fmt.Sprintf("%d", time.Now().UnixNano())
	d.downloadTask(id, object{"id": id, "filename": filename, "url": rawURL, "savePath": "", "receivedBytes": 0, "totalBytes": 0, "state": "progressing", "startTime": time.Now().UnixMilli()})
	state := "interrupted"
	defer func() { d.downloadTask(id, object{"state": state, "endTime": time.Now().UnixMilli()}) }()
	path, err := mygo.Dialog.Save(mygo.SaveDialogOptions{Parent: win, Title: "保存下载文件", DefaultPath: filepath.Join(dir, filename), ButtonLabel: "保存"})
	if err != nil || path == "" {
		if err == nil {
			state = "cancelled"
		}
		return err
	}
	httpClient := &http.Client{Timeout: 5 * time.Minute}
	response, err := httpClient.Get(rawURL)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return fmt.Errorf("下载失败: HTTP %d", response.StatusCode)
	}
	file, err := os.Create(path)
	if err != nil {
		return err
	}
	d.downloadTask(id, object{"savePath": path, "totalBytes": max(0, response.ContentLength)})
	progress := &downloadProgress{reader: response.Body, notify: func(received int64) { d.downloadTask(id, object{"receivedBytes": received}) }}
	_, copyErr := io.Copy(file, progress)
	d.downloadTask(id, object{"receivedBytes": progress.received})
	closeErr := file.Close()
	if copyErr != nil {
		return copyErr
	}
	if closeErr == nil {
		state = "completed"
	}
	return closeErr
}

type downloadProgress struct {
	reader   io.Reader
	received int64
	updated  time.Time
	notify   func(int64)
}

func (progress *downloadProgress) Read(buffer []byte) (int, error) {
	count, err := progress.reader.Read(buffer)
	progress.received += int64(count)
	if time.Since(progress.updated) > 250*time.Millisecond {
		progress.updated = time.Now()
		progress.notify(progress.received)
	}
	return count, err
}
func (d *Desktop) downloadTask(id string, fields object) {
	d.mu.Lock()
	state := d.static["window-manager-main:state"]
	if state == nil {
		state = object{}
		d.static["window-manager-main:state"] = state
	}
	previous, _ := state["downloadTasks"].([]any)
	tasks := make([]any, 0, len(previous)+1)
	found := false
	for _, value := range previous {
		task := asObject(value)
		next := object{}
		for key, entry := range task {
			next[key] = entry
		}
		if task["id"] == id {
			for key, entry := range fields {
				next[key] = entry
			}
			found = true
		}
		tasks = append(tasks, next)
	}
	if !found {
		tasks = append(tasks, fields)
	}
	finished := 0
	keep := make([]any, 0, len(tasks))
	for index := len(tasks) - 1; index >= 0; index-- {
		task := asObject(tasks[index])
		if task["state"] != "progressing" {
			finished++
			if finished > 20 {
				continue
			}
		}
		keep = append(keep, task)
	}
	for left, right := 0, len(keep)-1; left < right; left, right = left+1, right-1 {
		keep[left], keep[right] = keep[right], keep[left]
	}
	state["downloadTasks"] = keep
	d.mu.Unlock()
	d.update("window-manager-main", "state", "downloadTasks", keep)
}

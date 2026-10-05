package main

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"log"

	"github.com/egoist/mygo"
)

func trayIcon() []byte {
	icon := image.NewNRGBA(image.Rect(0, 0, 32, 32))
	for y := 0; y < 32; y++ {
		for x := 0; x < 32; x++ {
			if (x-16)*(x-16)+(y-16)*(y-16) < 246 {
				icon.SetNRGBA(x, y, color.NRGBA{R: 49, G: 109, B: 232, A: 255})
			}
			if (x >= 9 && x <= 13 && y >= 8 && y <= 25) || (x >= 9 && x <= 24 && y >= 21 && y <= 25) {
				icon.SetNRGBA(x, y, color.NRGBA{R: 255, G: 255, B: 255, A: 255})
			}
		}
	}
	var data bytes.Buffer
	_ = png.Encode(&data, icon)
	return data.Bytes()
}

func (d *Desktop) initializeTray() {
	menu := mygo.NewMenu([]*mygo.MenuItem{
		{Label: "打开 " + appName, Click: func(*mygo.MenuItem, *mygo.Window) { d.openWindow("main-window") }},
		{Label: "迷你窗口", Click: func(*mygo.MenuItem, *mygo.Window) { d.openWindow("aux-window") }},
		{Label: "OP.GG 窗口", Click: func(*mygo.MenuItem, *mygo.Window) { d.openWindow("opgg-window") }},
		mygo.Separator(),
		{Label: "退出", Click: func(*mygo.MenuItem, *mygo.Window) { mygo.App.Quit() }},
	})
	tray, err := mygo.NewTray(mygo.TrayOptions{Icon: trayIcon(), ToolTip: appWindowTitle, Menu: menu})
	if err != nil {
		log.Print("Tray unavailable: ", err)
		return
	}
	d.tray = tray
	tray.OnClick(func() { d.openWindow("main-window") })
}

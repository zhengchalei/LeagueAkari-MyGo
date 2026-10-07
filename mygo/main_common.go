package main

import (
	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
)

const appVersion = "0.5.6"

var rendererEvents = mygo.NewEvent[bridge.Event]("akari-event")

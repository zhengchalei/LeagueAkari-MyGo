//go:build winui_backend

package main

import (
	"log"
	"os"

	selfupdate "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/update"
)

func main() {
	if handled, err := selfupdate.RunHelper(os.Args[1:]); handled {
		if err != nil {
			log.Print(err)
		}
		return
	}
	if handled, err := runWinUIBackend(os.Args[1:]); handled {
		if err != nil {
			log.Printf("WinUI backend: %v", err)
		}
		return
	}
	log.Print("LeagueAkari.Backend is started by the WinUI host")
}

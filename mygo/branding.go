package main

import "os"

const (
	appName        = "LeagueAkari-MyGo"
	appWindowTitle = appName + " · LOL 助手"
	appRepository  = "zhengchalei/LeagueAkari-MyGo"
)

func updateRepository() string {
	for _, name := range []string{"LEAGUE_AKARI_MYGO_UPDATE_REPOSITORY", "TIMO_UPDATE_REPOSITORY"} {
		if value := os.Getenv(name); value != "" {
			return value
		}
	}
	return appRepository
}

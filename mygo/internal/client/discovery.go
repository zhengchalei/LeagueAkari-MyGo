package client

import (
	"errors"
	"os"
	"regexp"
	"strconv"
	"strings"
)

var commandArgument = regexp.MustCompile(`(?:^|\s)"?--([\w-]+)=(?:"([^"\r\n]*)"|([^\s"]+))`)

func ParseCommandLine(commandLine string, processID int) *Auth {
	args := map[string]string{}
	for _, match := range commandArgument.FindAllStringSubmatch(commandLine, -1) {
		value := match[2]
		if value == "" {
			value = match[3]
		}
		args[strings.ToLower(match[1])] = value
	}
	port, _ := strconv.Atoi(args["app-port"])
	if port < 1 || port > 65535 || args["remoting-auth-token"] == "" {
		return nil
	}
	pid, _ := strconv.Atoi(args["app-pid"])
	if pid == 0 {
		pid = processID
	}
	platform := args["rso_platform_id"]
	if platform == "" {
		platform = args["rso-platform-id"]
	}
	return &Auth{PID: pid, Port: port, Password: args["remoting-auth-token"], Region: args["region"], PlatformID: platform}
}

func ParseLockfile(text string) *Auth {
	parts := strings.Split(strings.TrimSpace(text), ":")
	if len(parts) != 5 || parts[4] != "https" {
		return nil
	}
	pid, _ := strconv.Atoi(parts[1])
	port, _ := strconv.Atoi(parts[2])
	if port < 1 || port > 65535 || parts[3] == "" {
		return nil
	}
	return &Auth{PID: pid, Port: port, Password: parts[3]}
}

func configuredLockfile() (*Auth, error) {
	if name := os.Getenv("LOL_LOCKFILE"); name != "" {
		data, err := os.ReadFile(name)
		if err != nil {
			return nil, err
		}
		if auth := ParseLockfile(string(data)); auth != nil {
			return auth, nil
		}
		return nil, errors.New("LOL lockfile 无有效连接信息")
	}
	return nil, nil
}

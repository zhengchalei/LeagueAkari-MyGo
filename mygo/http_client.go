package main

import (
	"errors"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func newExternalHTTPClient(store *settings.Store) *http.Client {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = func(request *http.Request) (*url.URL, error) {
		if store == nil {
			return nil, nil
		}
		config := client.Map(store.Get("app-common-main", "httpProxy"))
		switch config["strategy"] {
		case "force":
			host := strings.TrimSpace(client.String(config["host"]))
			port := client.Number(config["port"])
			if host == "" || strings.ContainsAny(host, "/@?#") || port < 1 || port > 65535 {
				return nil, errors.New("HTTP 代理地址或端口无效")
			}
			return &url.URL{Scheme: "http", Host: net.JoinHostPort(strings.Trim(host, "[]"), strconv.FormatInt(port, 10))}, nil
		case "disable":
			return nil, nil
		default:
			return http.ProxyFromEnvironment(request)
		}
	}
	// Resolve current settings per request so edited and imported proxy settings
	// apply to SGP and remote assets without replacing the running clients.
	return &http.Client{Transport: transport, Timeout: 12 * time.Second}
}

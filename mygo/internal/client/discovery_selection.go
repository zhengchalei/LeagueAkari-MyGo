package client

// LeagueClient and LeagueClientUx can describe the same local API. Deduplicate
// that endpoint, while leaving multiple independent clients for manual choice.
func uniqueDiscoveredConnection(candidates []*Auth) *Auth {
	var selected *Auth
	for _, auth := range candidates {
		if auth == nil {
			continue
		}
		if selected == nil {
			copied := *auth
			selected = &copied
			continue
		}
		if selected.Port != auth.Port || selected.BaseURL != auth.BaseURL || selected.Password != auth.Password {
			return nil
		}
		if selected.Region == "" {
			selected.Region = auth.Region
		}
		if selected.PlatformID == "" {
			selected.PlatformID = auth.PlatformID
		}
	}
	return selected
}

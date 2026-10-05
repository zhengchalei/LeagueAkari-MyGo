package player

import (
	"errors"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

func filters(query Query) (string, []any) {
	clauses := []string{"1=1"}
	args := []any{}
	for _, field := range []struct{ name, value string }{{"puuid", query.Puuid}, {"selfPuuid", query.SelfPuuid}} {
		if field.value != "" {
			clauses = append(clauses, field.name+"=?")
			args = append(args, field.value)
		}
	}
	for _, field := range []struct {
		name  string
		value *string
	}{{"region", query.Region}, {"rsoPlatformId", query.RsoPlatformID}, {"tag", query.Tag}} {
		if field.value != nil {
			clauses = append(clauses, field.name+"=?")
			args = append(args, *field.value)
		}
	}
	if query.Search != "" {
		clauses = append(clauses, "(puuid LIKE ? OR tag LIKE ?)")
		args = append(args, "%"+query.Search+"%", "%"+query.Search+"%")
	}
	return strings.Join(clauses, " AND "), args
}
func pagination(query Query) (int, int, string) {
	page, size := query.Page, query.PageSize
	if page < 1 {
		page = 1
	}
	if size < 1 {
		size = 40
	}
	if size > 1000 {
		size = 1000
	}
	order := "DESC"
	if query.TimeOrder == "asc" {
		order = "ASC"
	}
	return page, size, order
}

func (s *Service) QuerySavedPlayer(query Query) (any, error) {
	if err := required(query); err != nil {
		return nil, err
	}
	where, args := filters(query)
	rows, err := s.db.Query(`SELECT * FROM SavedPlayers WHERE `+where+` ORDER BY updateAt DESC LIMIT 1`, args...)
	if err != nil || len(rows) == 0 {
		return nil, err
	}
	return rowMap(rows[0]), nil
}
func (s *Service) QuerySavedPlayerWithGames(query Query) (any, error) {
	value, err := s.QuerySavedPlayer(query)
	if err != nil || value == nil {
		return value, err
	}
	row := value.(map[string]any)
	games, err := s.QueryEncounteredGames(query)
	if err != nil {
		return nil, err
	}
	tags, err := s.GetPlayerTags(Query{Puuid: query.Puuid, SelfPuuid: query.SelfPuuid})
	if err != nil {
		return nil, err
	}
	row["encounteredGames"] = games
	row["tags"] = tags
	return row, nil
}
func (s *Service) GetPlayerTags(query Query) (any, error) {
	if err := required(query); err != nil {
		return nil, err
	}
	rows, err := s.db.Query(`SELECT * FROM SavedPlayers WHERE puuid=? AND tag IS NOT NULL AND tag<>'' ORDER BY updateAt DESC`, query.Puuid)
	if err != nil {
		return nil, err
	}
	for _, row := range rows {
		row["markedBySelf"] = row["selfPuuid"] == query.SelfPuuid
	}
	return rows, nil
}
func (s *Service) queryPlayers(query Query, tagsOnly bool) (any, error) {
	where, args := filters(query)
	if tagsOnly {
		where += " AND tag IS NOT NULL"
	}
	page, size, order := pagination(query)
	counts, err := s.db.Query(`SELECT COUNT(*) AS count FROM SavedPlayers WHERE `+where, args...)
	if err != nil {
		return nil, err
	}
	rows, err := s.db.Query(`SELECT * FROM SavedPlayers WHERE `+where+` ORDER BY updateAt `+order+`,puuid LIMIT ? OFFSET ?`, append(args, size, (page-1)*size)...)
	if err != nil {
		return nil, err
	}
	total := counts[0]["count"]
	return map[string]any{"data": rows, "count": total, "total": total, "page": page, "pageSize": size}, nil
}
func (s *Service) QueryEncounteredGames(query Query) (any, error) {
	if err := required(query); err != nil {
		return nil, err
	}
	query.Tag = nil
	query.Search = ""
	where, args := filters(query)
	if query.QueueType != "" {
		where += " AND queueType=?"
		args = append(args, query.QueueType)
	}
	page, size, order := pagination(query)
	counts, err := s.db.Query(`SELECT COUNT(*) AS count FROM EncounteredGames WHERE `+where, args...)
	if err != nil {
		return nil, err
	}
	rows, err := s.db.Query(`SELECT * FROM EncounteredGames WHERE `+where+` ORDER BY updateAt `+order+`,id `+order+` LIMIT ? OFFSET ?`, append(args, size, (page-1)*size)...)
	if err != nil {
		return nil, err
	}
	return map[string]any{"data": rows, "page": page, "pageSize": size, "total": counts[0]["count"]}, nil
}

func (s *Service) deletePlayer(query Query) (any, error) {
	if err := required(query); err != nil {
		return nil, err
	}
	where, args := filters(query)
	count, err := s.db.Exec(`DELETE FROM SavedPlayers WHERE `+where, args...)
	if err == nil {
		s.changed(query.Puuid, query.SelfPuuid)
	}
	return map[string]any{"affected": count}, err
}

func (s *Service) savePlayer(value any) (any, error) {
	var dto map[string]any
	if err := decode(value, &dto); err != nil {
		return nil, err
	}
	var query Query
	if err := decode(value, &query); err != nil {
		return nil, err
	}
	if err := required(query); err != nil {
		return nil, err
	}
	if query.Region == nil || *query.Region == "" || query.RsoPlatformID == nil {
		return nil, fmtInvalidRegion()
	}
	_, hasTag := dto["tag"]
	encountered, _ := dto["encountered"].(bool)
	stamp := now()
	var met any
	if encountered {
		met = stamp
	}
	err := s.db.Transaction(func(tx *sqlite.Conn) error {
		_, err := tx.Exec(`INSERT INTO SavedPlayers(puuid,selfPuuid,region,rsoPlatformId,tag,updateAt,lastMetAt) VALUES(?,?,?,?,?,?,?) ON CONFLICT(puuid,selfPuuid,region,rsoPlatformId) DO UPDATE SET tag=CASE WHEN ? THEN excluded.tag ELSE SavedPlayers.tag END,updateAt=excluded.updateAt,lastMetAt=COALESCE(excluded.lastMetAt,SavedPlayers.lastMetAt)`, query.Puuid, query.SelfPuuid, *query.Region, *query.RsoPlatformID, dto["tag"], stamp, met, flag(hasTag))
		return err
	})
	if err != nil {
		return nil, err
	}
	s.changed(query.Puuid, query.SelfPuuid)
	return s.QuerySavedPlayer(query)
}
func flag(value bool) int {
	if value {
		return 1
	}
	return 0
}
func fmtInvalidRegion() error {
	return errors.New("region and rsoPlatformId are required for a new player")
}

func (s *Service) updateTag(value any) (any, error) {
	var dto map[string]any
	if err := decode(value, &dto); err != nil {
		return nil, err
	}
	var query Query
	if err := decode(value, &query); err != nil {
		return nil, err
	}
	if err := required(query); err != nil {
		return nil, err
	}
	if query.Region == nil || query.RsoPlatformID == nil {
		current, err := s.QuerySavedPlayer(query)
		if err != nil {
			return nil, err
		}
		if current == nil {
			return nil, fmtInvalidRegion()
		}
		row := current.(map[string]any)
		dto["region"] = row["region"]
		dto["rsoPlatformId"] = row["rsoPlatformId"]
	}
	return s.savePlayer(dto)
}

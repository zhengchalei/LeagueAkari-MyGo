package sqlite

import (
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestNativeSQLiteUTF8ParametersNullNumbersAndTransactions(t *testing.T) {
	db, err := Open(filepath.Join(t.TempDir(), "中文记录.sqlite"), false)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if _, err = db.Exec(`CREATE TABLE ValuesFixture(id INTEGER PRIMARY KEY,text TEXT,number REAL,optional TEXT)`); err != nil {
		t.Fatal(err)
	}
	text := "中文 O'Brian\x00尾部"
	if _, err = db.Exec(`INSERT INTO ValuesFixture VALUES(?,?,?,?)`, int64(900000000001), text, 1.25, nil); err != nil {
		t.Fatal(err)
	}
	rows, err := db.Query(`SELECT * FROM ValuesFixture`)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || rows[0]["id"] != int64(900000000001) || rows[0]["text"] != text || rows[0]["number"] != float64(1.25) || rows[0]["optional"] != nil {
		t.Fatal(rows)
	}
	failure := errors.New("fixture rollback")
	if err = db.Transaction(func(tx *Conn) error {
		if _, err := tx.Exec(`INSERT INTO ValuesFixture VALUES(2,'rollback',0,NULL)`); err != nil {
			return err
		}
		return failure
	}); !errors.Is(err, failure) {
		t.Fatal(err)
	}
	rows, err = db.Query(`SELECT COUNT(*) AS count FROM ValuesFixture`)
	if err != nil || rows[0]["count"] != int64(1) {
		t.Fatal("rollback failed", rows, err)
	}
	if _, err = db.Exec(`INSERT INTO table_that_does_not_exist VALUES(?)`, "safe"); err == nil || !strings.Contains(err.Error(), "table_that_does_not_exist") {
		t.Fatal("native error message lost", err)
	}
}

func TestNativeSQLiteWALSurvivesForcedProcessExit(t *testing.T) {
	if path := os.Getenv("TIMO_SQLITE_CRASH_FIXTURE"); path != "" {
		db, err := Open(path, false)
		if err != nil {
			t.Fatal(err)
		}
		for _, sql := range []string{`PRAGMA journal_mode=WAL`, `CREATE TABLE Fixture(id INTEGER PRIMARY KEY,text TEXT)`, `CREATE INDEX fixture_text ON Fixture(text)`} {
			if _, err := db.Exec(sql); err != nil {
				t.Fatal(err)
			}
		}
		for batch := 0; batch < 5; batch++ {
			if err := db.Transaction(func(tx *Conn) error {
				for i := 0; i < 500; i++ {
					if _, err := tx.Exec(`INSERT INTO Fixture VALUES(?,?)`, batch*500+i, fmt.Sprintf("中文%d-%s", i, strings.Repeat("x", 100))); err != nil {
						return err
					}
				}
				return nil
			}); err != nil {
				t.Fatal(err)
			}
		}
		if err := os.WriteFile(path+".ready", []byte("committed"), 0600); err != nil {
			t.Fatal(err)
		}
		for {
			time.Sleep(time.Second)
		}
	}
	path := filepath.Join(t.TempDir(), "crash.sqlite")
	command := exec.Command(os.Args[0], "-test.run=^TestNativeSQLiteWALSurvivesForcedProcessExit$", "-test.timeout=30s")
	command.Env = append(os.Environ(), "TIMO_SQLITE_CRASH_FIXTURE="+path)
	if err := command.Start(); err != nil {
		t.Fatal(err)
	}
	defer command.Process.Kill()
	deadline := time.Now().Add(20 * time.Second)
	for {
		if _, err := os.Stat(path + ".ready"); err == nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("child never committed fixture")
		}
		time.Sleep(10 * time.Millisecond)
	}
	if err := command.Process.Kill(); err != nil {
		t.Fatal(err)
	}
	_ = command.Wait()
	db, err := Open(path, false)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	rows, err := db.Query(`PRAGMA integrity_check`)
	if err != nil || len(rows) != 1 || rows[0]["integrity_check"] != "ok" {
		t.Fatal("forced-exit WAL recovery damaged database", rows, err)
	}
	rows, err = db.Query(`SELECT COUNT(*) AS count FROM Fixture`)
	if err != nil || rows[0]["count"] != int64(2500) {
		t.Fatal("committed rows lost", rows, err)
	}
}

func TestNativeSQLiteConcurrentTransactionsAndClose(t *testing.T) {
	db, err := Open(filepath.Join(t.TempDir(), "concurrent.sqlite"), false)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`CREATE TABLE Counter(value INTEGER NOT NULL)`); err != nil {
		t.Fatal(err)
	}
	db.Exec(`INSERT INTO Counter VALUES(0)`)
	var wg sync.WaitGroup
	errorsFound := make(chan error, 16)
	for i := 0; i < 16; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			errorsFound <- db.Transaction(func(tx *Conn) error { _, err := tx.Exec(`UPDATE Counter SET value=value+1`); return err })
		}()
	}
	wg.Wait()
	close(errorsFound)
	for err := range errorsFound {
		if err != nil {
			t.Fatal(err)
		}
	}
	rows, err := db.Query(`SELECT value FROM Counter`)
	if err != nil || rows[0]["value"] != int64(16) {
		t.Fatal(rows, err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal("repeat close", err)
	}
	if _, err = db.Query(`SELECT value FROM Counter`); err == nil {
		t.Fatal("closed database can query")
	}
}

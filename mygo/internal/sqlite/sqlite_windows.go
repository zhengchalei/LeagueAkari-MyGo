package sqlite

import (
	"fmt"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"unsafe"
)

var library = syscall.NewLazyDLL("winsqlite3.dll")
var openV2 = library.NewProc("sqlite3_open_v2")
var closeV2 = library.NewProc("sqlite3_close_v2")
var prepareV2 = library.NewProc("sqlite3_prepare_v2")
var finalize = library.NewProc("sqlite3_finalize")
var step = library.NewProc("sqlite3_step")
var bindText = library.NewProc("sqlite3_bind_text")
var bindNull = library.NewProc("sqlite3_bind_null")
var columnCount = library.NewProc("sqlite3_column_count")
var columnName = library.NewProc("sqlite3_column_name")
var columnType = library.NewProc("sqlite3_column_type")
var columnText = library.NewProc("sqlite3_column_text")
var columnBytes = library.NewProc("sqlite3_column_bytes")
var errorMessage = library.NewProc("sqlite3_errmsg")
var changes = library.NewProc("sqlite3_changes")
var kernel = syscall.NewLazyDLL("kernel32.dll")
var stringLength = kernel.NewProc("lstrlenA")
var moveMemory = kernel.NewProc("RtlMoveMemory")

type DB struct {
	mu   sync.Mutex
	conn Conn
}

type Conn struct{ handle uintptr }
type Row map[string]any

func Open(path string, readOnly bool) (*DB, error) {
	if err := library.Load(); err != nil {
		return nil, err
	}
	name, err := syscall.BytePtrFromString(path)
	if err != nil {
		return nil, err
	}
	flags := uintptr(2 | 4 | 0x10000)
	if readOnly {
		flags = 1 | 0x10000
	}
	db := &DB{}
	result, _, _ := openV2.Call(uintptr(unsafe.Pointer(name)), uintptr(unsafe.Pointer(&db.conn.handle)), flags, 0)
	runtime.KeepAlive(name)
	if result != 0 {
		err = db.conn.failure(result)
		if db.conn.handle != 0 {
			closeV2.Call(db.conn.handle)
		}
		return nil, err
	}
	if _, err := db.Exec("PRAGMA busy_timeout=5000"); err != nil {
		db.Close()
		return nil, err
	}
	return db, nil
}

func (db *DB) Close() error {
	db.mu.Lock()
	defer db.mu.Unlock()
	if db.conn.handle == 0 {
		return nil
	}
	code, _, _ := closeV2.Call(db.conn.handle)
	if code != 0 {
		return db.conn.failure(code)
	}
	db.conn.handle = 0
	return nil
}

func (db *DB) Exec(sql string, args ...any) (int64, error) {
	db.mu.Lock()
	defer db.mu.Unlock()
	return db.conn.Exec(sql, args...)
}

func (db *DB) Query(sql string, args ...any) ([]Row, error) {
	db.mu.Lock()
	defer db.mu.Unlock()
	return db.conn.Query(sql, args...)
}

func (db *DB) Transaction(fn func(*Conn) error) error {
	db.mu.Lock()
	defer db.mu.Unlock()
	if _, err := db.conn.Exec("BEGIN IMMEDIATE"); err != nil {
		return err
	}
	if err := fn(&db.conn); err != nil {
		db.conn.Exec("ROLLBACK")
		return err
	}
	_, err := db.conn.Exec("COMMIT")
	if err != nil {
		db.conn.Exec("ROLLBACK")
	}
	return err
}

func (c *Conn) failure(code uintptr) error {
	ptr, _, _ := errorMessage.Call(c.handle)
	return fmt.Errorf("SQLite %d: %s", code, cString(ptr))
}

func cString(ptr uintptr) string {
	if ptr == 0 {
		return ""
	}
	length, _, _ := stringLength.Call(ptr)
	return nativeString(ptr, int(length))
}

// SQLite owns these addresses. Copy through Win32 rather than treating OS memory
// as a Go pointer; the statement/database remains locked while copying.
func nativeString(ptr uintptr, length int) string {
	if ptr == 0 || length == 0 {
		return ""
	}
	data := make([]byte, length)
	moveMemory.Call(uintptr(unsafe.Pointer(&data[0])), ptr, uintptr(length))
	runtime.KeepAlive(data)
	return string(data)
}

func (c *Conn) prepare(sql string, args []any) (uintptr, error) {
	if c.handle == 0 {
		return 0, fmt.Errorf("SQLite database is closed")
	}
	query, err := syscall.BytePtrFromString(sql)
	if err != nil {
		return 0, err
	}
	var stmt uintptr
	code, _, _ := prepareV2.Call(c.handle, uintptr(unsafe.Pointer(query)), ^uintptr(0), uintptr(unsafe.Pointer(&stmt)), 0)
	runtime.KeepAlive(query)
	if code != 0 {
		return 0, c.failure(code)
	}
	for index, value := range args {
		if value == nil {
			code, _, _ = bindNull.Call(stmt, uintptr(index+1))
		} else {
			text := fmt.Sprint(value)
			data := append([]byte(text), 0)
			code, _, _ = bindText.Call(stmt, uintptr(index+1), uintptr(unsafe.Pointer(&data[0])), uintptr(len(text)), ^uintptr(0))
			runtime.KeepAlive(data)
		}
		if code != 0 {
			finalize.Call(stmt)
			return 0, c.failure(code)
		}
	}
	return stmt, nil
}

func (c *Conn) Exec(sql string, args ...any) (int64, error) {
	stmt, err := c.prepare(sql, args)
	if err != nil {
		return 0, err
	}
	defer finalize.Call(stmt)
	for {
		code, _, _ := step.Call(stmt)
		if code == 100 {
			continue
		}
		if code != 101 {
			return 0, c.failure(code)
		}
		count, _, _ := changes.Call(c.handle)
		return int64(count), nil
	}
}

func (c *Conn) Query(sql string, args ...any) ([]Row, error) {
	stmt, err := c.prepare(sql, args)
	if err != nil {
		return nil, err
	}
	defer finalize.Call(stmt)
	count, _, _ := columnCount.Call(stmt)
	names := make([]string, count)
	for index := range names {
		ptr, _, _ := columnName.Call(stmt, uintptr(index))
		names[index] = cString(ptr)
	}
	rows := []Row{}
	for {
		code, _, _ := step.Call(stmt)
		if code == 101 {
			return rows, nil
		}
		if code != 100 {
			return nil, c.failure(code)
		}
		row := Row{}
		for index, name := range names {
			kind, _, _ := columnType.Call(stmt, uintptr(index))
			if kind == 5 {
				row[name] = nil
				continue
			}
			ptr, _, _ := columnText.Call(stmt, uintptr(index))
			length, _, _ := columnBytes.Call(stmt, uintptr(index))
			text := nativeString(ptr, int(length))
			switch kind {
			case 1:
				row[name], err = strconv.ParseInt(text, 10, 64)
			case 2:
				row[name], err = strconv.ParseFloat(text, 64)
			default:
				row[name] = text
			}
			if err != nil {
				return nil, err
			}
		}
		rows = append(rows, row)
	}
}

func QuoteIdentifier(name string) string { return `"` + strings.ReplaceAll(name, `"`, `""`) + `"` }

package catalog

import (
	"errors"
	"fmt"
	"strconv"
	"strings"
	"unicode"
)

type luaTable map[string]any
type token struct {
	kind string
	text string
}
type luaParser struct {
	tokens          []token
	position, depth int
}

func parseLua(source string) (luaTable, error) {
	tokens, err := lexLua(source)
	if err != nil {
		return nil, err
	}
	parser := luaParser{tokens: tokens}
	for parser.peek().kind != "eof" && parser.peek().text != "return" {
		parser.position++
	}
	if parser.peek().text != "return" {
		return nil, errors.New("Lua 数据缺少 return 表")
	}
	parser.position++
	value, err := parser.value()
	if err != nil {
		return nil, err
	}
	table, ok := value.(luaTable)
	if !ok {
		return nil, errors.New("Lua 返回值不是数据表")
	}
	return table, nil
}
func (p *luaParser) peek() token {
	if p.position >= len(p.tokens) {
		return token{kind: "eof"}
	}
	return p.tokens[p.position]
}
func (p *luaParser) take() token { value := p.peek(); p.position++; return value }
func (p *luaParser) expect(text string) error {
	if p.take().text != text {
		return fmt.Errorf("Lua 表缺少 %q", text)
	}
	return nil
}
func (p *luaParser) value() (any, error) {
	value := p.take()
	switch value.kind {
	case "string":
		return value.text, nil
	case "number":
		number, err := strconv.ParseFloat(value.text, 64)
		return number, err
	case "eof":
		return nil, errors.New("Lua 表未结束")
	}
	switch value.text {
	case "{":
		return p.table()
	case "-", "+":
		next := p.take()
		if next.kind != "number" {
			return nil, errors.New("Lua 数值符号后缺少数字")
		}
		number, err := strconv.ParseFloat(next.text, 64)
		if value.text == "-" {
			number = -number
		}
		return number, err
	case "true":
		return true, nil
	case "false":
		return false, nil
	case "nil":
		return nil, nil
	}
	// Champion descriptions may use unrelated Lua calls. Balance fields only
	// accept numeric literals, so skip such values without executing Lua.
	parentheses := 0
	for p.peek().kind != "eof" {
		text := p.peek().text
		if parentheses == 0 && (text == "," || text == ";" || text == "}") {
			break
		}
		if text == "(" {
			parentheses++
		}
		if text == ")" {
			parentheses--
		}
		p.position++
	}
	return nil, nil
}
func (p *luaParser) table() (luaTable, error) {
	p.depth++
	defer func() { p.depth-- }()
	if p.depth > 128 {
		return nil, errors.New("Lua 数据表嵌套过深")
	}
	fields := luaTable{}
	index := 1
	for p.peek().text != "}" {
		if p.peek().kind == "eof" {
			return nil, errors.New("Lua 数据表未结束")
		}
		key := ""
		if p.peek().text == "[" {
			p.take()
			field := p.take()
			if field.kind != "string" && field.kind != "number" {
				return nil, errors.New("Lua 数据表键格式无效")
			}
			key = field.text
			if err := p.expect("]"); err != nil {
				return nil, err
			}
			if err := p.expect("="); err != nil {
				return nil, err
			}
		} else if p.peek().kind == "identifier" && p.position+1 < len(p.tokens) && p.tokens[p.position+1].text == "=" {
			key = p.take().text
			p.take()
		} else {
			key = strconv.Itoa(index)
			index++
		}
		value, err := p.value()
		if err != nil {
			return nil, err
		}
		fields[key] = value
		if p.peek().text == "," || p.peek().text == ";" {
			p.take()
		} else if p.peek().text != "}" {
			return nil, fmt.Errorf("Lua 字段 %q 后缺少分隔符", key)
		}
	}
	p.take()
	return fields, nil
}

func lexLua(source string) ([]token, error) {
	chars := []rune(source)
	tokens := []token{}
	for index := 0; index < len(chars); {
		char := chars[index]
		if unicode.IsSpace(char) || char == '\ufeff' {
			index++
			continue
		}
		if char == '-' && index+1 < len(chars) && chars[index+1] == '-' {
			index += 2
			if end, content, ok := longBracket(chars, index); ok {
				_ = content
				index = end
			} else {
				for index < len(chars) && chars[index] != '\n' {
					index++
				}
			}
			continue
		}
		if char == '"' || char == '\'' {
			quote := char
			index++
			var value strings.Builder
			closed := false
			for index < len(chars) {
				char = chars[index]
				index++
				if char == quote {
					closed = true
					break
				}
				if char == '\\' {
					if index >= len(chars) {
						break
					}
					escaped := chars[index]
					index++
					switch escaped {
					case 'n':
						value.WriteRune('\n')
					case 'r':
						value.WriteRune('\r')
					case 't':
						value.WriteRune('\t')
					case 'a':
						value.WriteRune('\a')
					case 'b':
						value.WriteRune('\b')
					case 'f':
						value.WriteRune('\f')
					case 'v':
						value.WriteRune('\v')
					case 'z':
						for index < len(chars) && unicode.IsSpace(chars[index]) {
							index++
						}
					default:
						if escaped >= '0' && escaped <= '9' {
							digits := string(escaped)
							for count := 1; count < 3 && index < len(chars) && chars[index] >= '0' && chars[index] <= '9'; count++ {
								digits += string(chars[index])
								index++
							}
							code, _ := strconv.Atoi(digits)
							value.WriteRune(rune(code))
						} else {
							value.WriteRune(escaped)
						}
					}
				} else {
					value.WriteRune(char)
				}
			}
			if !closed {
				return nil, errors.New("Lua 字符串未结束")
			}
			tokens = append(tokens, token{"string", value.String()})
			continue
		}
		if char == '[' {
			if end, content, ok := longBracket(chars, index); ok {
				tokens = append(tokens, token{"string", content})
				index = end
				continue
			}
		}
		if char >= '0' && char <= '9' || char == '.' && index+1 < len(chars) && chars[index+1] >= '0' && chars[index+1] <= '9' {
			start := index
			index++
			for index < len(chars) {
				next := chars[index]
				if next >= '0' && next <= '9' || next == '.' {
					index++
					continue
				}
				if next == 'e' || next == 'E' {
					index++
					if index < len(chars) && (chars[index] == '+' || chars[index] == '-') {
						index++
					}
					continue
				}
				break
			}
			tokens = append(tokens, token{"number", string(chars[start:index])})
			continue
		}
		if unicode.IsLetter(char) || char == '_' {
			start := index
			index++
			for index < len(chars) && (unicode.IsLetter(chars[index]) || unicode.IsDigit(chars[index]) || chars[index] == '_') {
				index++
			}
			tokens = append(tokens, token{"identifier", string(chars[start:index])})
			continue
		}
		tokens = append(tokens, token{"symbol", string(char)})
		index++
	}
	return append(tokens, token{kind: "eof"}), nil
}

func longBracket(chars []rune, start int) (int, string, bool) {
	if start >= len(chars) || chars[start] != '[' {
		return 0, "", false
	}
	index := start + 1
	for index < len(chars) && chars[index] == '=' {
		index++
	}
	if index >= len(chars) || chars[index] != '[' {
		return 0, "", false
	}
	level := index - start - 1
	contentStart := index + 1
	for index = contentStart; index < len(chars); index++ {
		if chars[index] != ']' {
			continue
		}
		end := index + 1
		for count := 0; count < level && end < len(chars) && chars[end] == '='; count++ {
			end++
		}
		if end == index+1+level && end < len(chars) && chars[end] == ']' {
			return end + 1, string(chars[contentStart:index]), true
		}
	}
	return 0, "", false
}

package bridge

// Event keeps the existing renderer event contract while the desktop host changes.
type Event struct {
	Namespace string `json:"namespace"`
	Name      string `json:"name"`
	Args      []any  `json:"args"`
}

type Emitter func(namespace, name string, args ...any)

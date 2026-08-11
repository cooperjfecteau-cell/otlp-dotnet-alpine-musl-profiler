package profilestologsconnector

import (
	"encoding/json"
	"os"
	"sync"
	"time"

	"go.uber.org/zap"
)

// Session is one profiling window opened by the broker in response to a Dynatrace
// workflow trigger.
type Session struct {
	// ID is the ULID minted by the broker. It is stamped on every record so the
	// viewer can deep-link to exactly one profile rather than a time range - one
	// problem can trigger several profiles.
	ID string `json:"id"`

	// ServiceName selects which workload this session covers. Empty means every
	// service on the node, which is almost never what you want: the profiler sees
	// all ~112 processes on a node, most of them infrastructure.
	ServiceName string `json:"service_name,omitempty"`

	// Namespace optionally narrows further.
	Namespace string `json:"namespace,omitempty"`

	StartUnixNano int64 `json:"start_unix_nano"`
	EndUnixNano   int64 `json:"end_unix_nano"`
}

func (s Session) activeAt(tsNano int64) bool {
	if s.StartUnixNano != 0 && tsNano < s.StartUnixNano {
		return false
	}
	if s.EndUnixNano != 0 && tsNano > s.EndUnixNano {
		return false
	}
	return true
}

func (s Session) matches(service, namespace string) bool {
	if s.ServiceName != "" && s.ServiceName != service {
		return false
	}
	if s.Namespace != "" && s.Namespace != namespace {
		return false
	}
	return true
}

// sessionStore holds the active session set, reloaded from a file the broker
// writes (via ConfigMap). Reads happen on the hot path for every sample batch, so
// the lock is held only long enough to copy the slice header.
type sessionStore struct {
	path     string
	interval time.Duration
	logger   *zap.Logger

	mu       sync.RWMutex
	sessions []Session

	stop chan struct{}
	wg   sync.WaitGroup

	// lastErrLogged suppresses a per-reload error storm when the file is missing,
	// which is the normal state when no session has ever been opened.
	lastErrLogged string
}

func newSessionStore(path string, interval time.Duration, logger *zap.Logger) *sessionStore {
	return &sessionStore{
		path:     path,
		interval: interval,
		logger:   logger,
		stop:     make(chan struct{}),
	}
}

func (s *sessionStore) start() {
	s.reload()
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		t := time.NewTicker(s.interval)
		defer t.Stop()
		for {
			select {
			case <-s.stop:
				return
			case <-t.C:
				s.reload()
			}
		}
	}()
}

func (s *sessionStore) shutdown() {
	close(s.stop)
	s.wg.Wait()
}

func (s *sessionStore) reload() {
	raw, err := os.ReadFile(s.path)
	if err != nil {
		// A missing file means "no sessions", not a failure. Treating it as an error
		// would spam logs continuously in the common case, since gated profiling is
		// idle almost all of the time.
		if os.IsNotExist(err) {
			s.set(nil)
			return
		}
		if msg := err.Error(); msg != s.lastErrLogged {
			s.lastErrLogged = msg
			s.logger.Warn("cannot read session file; emitting no per-stack records",
				zap.String("path", s.path), zap.Error(err))
		}
		s.set(nil)
		return
	}

	var parsed []Session
	if err := json.Unmarshal(raw, &parsed); err != nil {
		// Fail CLOSED. A malformed session file must not be read as "profile
		// everything" - that would turn a typo in a ConfigMap into an unbounded
		// ingest bill.
		if msg := err.Error(); msg != s.lastErrLogged {
			s.lastErrLogged = msg
			s.logger.Error("session file is not valid JSON; emitting no per-stack records",
				zap.String("path", s.path), zap.Error(err))
		}
		s.set(nil)
		return
	}

	s.lastErrLogged = ""
	s.set(parsed)
}

func (s *sessionStore) set(v []Session) {
	s.mu.Lock()
	s.sessions = v
	s.mu.Unlock()
}

// activeFor returns the session covering this service at this time, if any.
func (s *sessionStore) activeFor(service, namespace string, tsNano int64) (Session, bool) {
	s.mu.RLock()
	sessions := s.sessions
	s.mu.RUnlock()

	for _, sess := range sessions {
		if sess.activeAt(tsNano) && sess.matches(service, namespace) {
			return sess, true
		}
	}
	return Session{}, false
}

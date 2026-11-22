package core

import "testing"

func TestSavesCache(t *testing.T) {
	SaveCache([]byte("{}"))
}

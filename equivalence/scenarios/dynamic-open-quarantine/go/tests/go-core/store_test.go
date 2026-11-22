package core

import "testing"

func TestLoadsUser(t *testing.T) {
	LoadUser("alice")
}

func TestAppendsLog(t *testing.T) {
	AppendLog("entry")
}

package core

import (
	"os"
	"testing"
)

func TestLoadsConfig(t *testing.T) {
	LoadConfig()
}

func TestAppendsLog(t *testing.T) {
	AppendLog("entry")
}

func TestRunsGitStatus(t *testing.T) {
	RunGitStatus()
}

func TestLegacyPath(t *testing.T) {
	t.Skip("legacy layout")
	os.ReadFile("/etc/app/legacy.json")
}

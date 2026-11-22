package core

import "os/exec"

func RunGitStatus() error {
	return exec.Command("git", "status").Run()
}

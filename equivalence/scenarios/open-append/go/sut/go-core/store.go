package core

import "os"

func AppendLog(line string) error {
	f, err := os.OpenFile("/var/log/app.log", os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.WriteString(line)
	return err
}

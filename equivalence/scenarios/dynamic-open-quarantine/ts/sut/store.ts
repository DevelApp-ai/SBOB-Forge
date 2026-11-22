import * as fs from "fs";

export function loadUser(user: string): string {
  return fs.readFileSync(`/var/log/${user}.log`, "utf8");
}

export function appendLog(entry: string): void {
  fs.appendFileSync("/var/log/app.log", entry);
}

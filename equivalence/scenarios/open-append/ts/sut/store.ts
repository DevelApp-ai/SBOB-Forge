import * as fs from "fs";

export function appendLog(entry: string): void {
  fs.appendFileSync("/var/log/app.log", entry);
}

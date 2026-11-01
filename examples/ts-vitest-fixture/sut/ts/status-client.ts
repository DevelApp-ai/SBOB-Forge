import { exec } from "child_process";
import * as fs from "fs";
import * as https from "https";
import * as net from "net";

export function fetchStatus(): void {
  https.get("https://api.example.com/status");
}

export function appendLog(entry: string): void {
  const fd = fs.openSync("/var/log/ts-status.log", "a");
  fs.writeSync(fd, entry);
  fs.closeSync(fd);
}

export function shellOut(): void {
  exec("git status");
}

export function serveForever(): void {
  const server = net.createServer(() => {});
  server.listen(8080);
}

export function clearStatus(): void {
  fs.rmSync("%PackageRoot%/cache/status.json");
}

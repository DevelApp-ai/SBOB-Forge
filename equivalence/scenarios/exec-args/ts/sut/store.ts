import { execFile } from "child_process";

export function runGitStatus(): void {
  execFile("git", ["status"]);
}

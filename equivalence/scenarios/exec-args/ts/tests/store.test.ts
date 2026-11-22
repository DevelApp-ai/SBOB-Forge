import { describe, it } from "vitest";

import { runGitStatus } from "../sut/store";

describe("tool runner", () => {
  it("runs git status", () => {
    runGitStatus();
  });
});

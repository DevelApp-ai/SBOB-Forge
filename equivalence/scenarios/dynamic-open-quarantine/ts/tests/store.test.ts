import { describe, it } from "vitest";

import { loadUser, appendLog } from "../sut/store";

describe("user store", () => {
  it("loads user", () => {
    loadUser("alice");
  });

  it("appends log", () => {
    appendLog("entry");
  });
});

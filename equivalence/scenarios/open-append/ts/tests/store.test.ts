import { describe, it } from "vitest";

import { appendLog } from "../sut/store";

describe("log store", () => {
  it("appends log", () => {
    appendLog("entry");
  });
});

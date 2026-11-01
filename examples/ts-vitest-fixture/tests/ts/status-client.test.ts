import { describe, it, test } from "vitest";
import * as fs from "fs";

import { appendLog, fetchStatus, shellOut } from "../../sut/ts/status-client";

describe("status client", () => {
  it("fetches status", () => {
    fetchStatus();
  });

  it("appends log", () => {
    appendLog("entry");
  });

  it.skipIf(true)("legacy shell", () => {
    shellOut();
  });

  test("writes report", () => {
    fs.appendFileSync("/var/log/report.log", "x");
  });
});

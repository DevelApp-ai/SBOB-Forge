import { describe, it } from "vitest";

import { startListener } from "../sut/store";

describe("status server", () => {
  it("starts listener", () => {
    startListener();
  });
});

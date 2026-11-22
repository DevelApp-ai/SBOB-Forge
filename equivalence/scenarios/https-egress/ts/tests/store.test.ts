import { describe, it } from "vitest";

import { fetchStatus } from "../sut/store";

describe("status client", () => {
  it("fetches status", () => {
    fetchStatus();
  });
});

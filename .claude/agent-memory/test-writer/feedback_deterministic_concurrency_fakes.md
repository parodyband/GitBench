---
name: feedback_deterministic_concurrency_fakes
description: How to write a concurrency test here that can actually fail — record-then-park fakes, not Task.Yield; and always mutation-check the test
metadata:
  type: feedback
---

A concurrency property (mutual exclusion, "these two things never interleave") tested by starting two
tasks and hoping the scheduler interleaves them **cannot fail**. Measured, not guessed: a fake stream
that did `await Task.Yield()` inside every write still ran two concurrent frame writes strictly one
after the other on this machine, so a test asserting "frames do not interleave" passed with the write
lock removed.

**Why:** `async` methods run synchronously up to their first incomplete await, and pool continuations
came back in an order that happened to serialise. The window exists but never opened.

**How to apply:** build the fake so the ordering is *forced*, not raced.

- The fake **records the call synchronously, then parks** on a TCS the test releases.
- The test starts A, awaits an "A has arrived" signal from the fake, then starts B on its own thread.
  If the code under test serialises, B never reaches the fake; if it does not, B's bytes are recorded
  before the test does anything else. Both directions are deterministic.
- A record-after-park fake, or one that only yields, gives you a green test with a broken lock.

And prove it: after a suite goes green, break the implementation several ways and count the reds. A
mutation that reddens nothing is a gap in the tests, not a spare test. Two of ten mutations survived
the first pass here and both were real holes. `dotnet test --blame-hang-timeout 40s` bounds the
mutations that cause a hang; wrapping `await pending` in `.WaitAsync(TimeSpan.FromSeconds(10))` turns
a leaked-completion bug from a wedged run into a named failure, which is what CI needs.

See also [[project_lsp_transport_seam]].

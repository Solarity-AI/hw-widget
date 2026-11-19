// Runs the Foundation-only core self-test on Linux: see macos/tests/run-docker.sh.
import Foundation

let r = coreSelfTest()
print("\(r.passed) passed, \(r.failed) failed")
exit(r.failed == 0 ? 0 : 1)

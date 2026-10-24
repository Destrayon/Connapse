import json
import os
import tempfile
import unittest

import spec_conformance


def write(directory: str, name: str, content: dict) -> str:
    path = os.path.join(directory, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(content, f)
    return path


class MergeTests(unittest.TestCase):
    def test_merge_unions_every_shard(self):
        with tempfile.TemporaryDirectory() as d:
            a = write(d, "a.json", {"seen": ["GET /api/a 200"], "violations": {"GET /api/a 200: body not described": "x"}})
            b = write(d, "b.json", {"seen": ["GET /api/b 404"], "violations": {"GET /api/b 404: status not in the spec": ""}})
            seen, violations = spec_conformance.merge([a, b])
        self.assertEqual(seen, {"GET /api/a 200", "GET /api/b 404"})
        self.assertEqual(set(violations), {"GET /api/a 200: body not described", "GET /api/b 404: status not in the spec"})

    def test_merge_keeps_the_first_detail_for_a_violation_two_shards_saw(self):
        with tempfile.TemporaryDirectory() as d:
            a = write(d, "a.json", {"seen": [], "violations": {"k": "first"}})
            b = write(d, "b.json", {"seen": [], "violations": {"k": "second"}})
            _, violations = spec_conformance.merge([a, b])
        self.assertEqual(violations["k"], "first")


class CompareTests(unittest.TestCase):
    K = "GET /api/x 404: status not in the spec"

    def test_exact_match_passes(self):
        self.assertEqual(spec_conformance.compare({"GET /api/x 404"}, {self.K: ""}, {self.K: "why"}), ([], [], []))

    def test_a_violation_the_list_lacks_is_added(self):
        new = "GET /api/y 200: body not described"
        self.assertEqual(spec_conformance.compare({"GET /api/x 404", "GET /api/y 200"}, {self.K: "", new: ""}, {self.K: "why"}),
                         ([new], [], []))

    def test_a_listed_violation_seen_conforming_is_closed(self):
        self.assertEqual(spec_conformance.compare({"GET /api/x 404"}, {}, {self.K: "why"}), ([], [self.K], []))

    def test_a_listed_violation_not_exercised_is_only_reported(self):
        self.assertEqual(spec_conformance.compare(set(), {}, {self.K: "why"}), ([], [], [self.K]))


if __name__ == "__main__":
    unittest.main()

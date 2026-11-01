import unittest

import spectral_baseline


class KeysTests(unittest.TestCase):
    def test_a_violation_is_keyed_by_rule_and_path(self):
        results = [{"code": "connapse-operation-id-feature-verb", "path": ["paths", "/api/sources", "get", "operationId"],
                    "message": "varies", "range": {"start": {"line": 9}}}]
        self.assertEqual(spectral_baseline.keys(results),
                         {"connapse-operation-id-feature-verb paths./api/sources.get.operationId"})


class CompareTests(unittest.TestCase):
    def test_exact_match_passes(self):
        self.assertEqual(spectral_baseline.compare({"a"}, {"a": "why"}), ([], []))

    def test_a_new_violation_is_added(self):
        self.assertEqual(spectral_baseline.compare({"a", "b"}, {"a": "why"}), (["b"], []))

    def test_a_listed_violation_spectral_no_longer_reports_is_fixed(self):
        self.assertEqual(spectral_baseline.compare(set(), {"a": "why"}), ([], ["a"]))


if __name__ == "__main__":
    unittest.main()

import unittest

import mutation_score


def mutant(status, line=1, reason="", mutator="Equality mutation", replacement="a != b"):
    return {"id": "varies", "mutatorName": mutator, "replacement": replacement, "status": status,
            "statusReason": reason, "location": {"start": {"line": line, "column": 5}, "end": {"line": line, "column": 11}}}


def report(root, files):
    return {"projectRoot": root, "files": {f"{root}/{path}": {"mutants": ms} for path, ms in files.items()}}


UNCHANGED = mutation_score.UNCHANGED


class MergeTests(unittest.TestCase):
    def test_an_unchanged_mutant_takes_the_baselines_status(self):
        pr = report("/pr/src/App", {"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report("/base/src/App", {"A.cs": [mutant("Killed")]})
        self.assertEqual(mutation_score.merge(pr, base), ({"Killed": 1}, 0))

    def test_a_retested_mutant_keeps_its_new_status(self):
        pr = report("/r", {"A.cs": [mutant("Survived")]})
        base = report("/r", {"A.cs": [mutant("Killed")]})
        self.assertEqual(mutation_score.merge(pr, base), ({"Survived": 1}, 0))

    def test_a_mutant_ignored_for_another_reason_stays_ignored(self):
        pr = report("/r", {"A.cs": [mutant("Ignored", reason="Removed by block already covered filter")]})
        base = report("/r", {"A.cs": [mutant("Killed")]})
        self.assertEqual(mutation_score.merge(pr, base), ({"Ignored": 1}, 0))

    def test_an_unchanged_mutant_missing_from_the_baseline_is_counted_as_unmatched(self):
        pr = report("/r", {"A.cs": [mutant("Ignored", line=2, reason=UNCHANGED)]})
        base = report("/r", {"A.cs": [mutant("Killed", line=1)]})
        self.assertEqual(mutation_score.merge(pr, base), ({}, 1))

    def test_mutants_on_the_same_span_are_told_apart_by_mutator_and_replacement(self):
        pr = report("/r", {"A.cs": [mutant("Ignored", reason=UNCHANGED, replacement="a == b"),
                                    mutant("Ignored", reason=UNCHANGED, mutator="Negate", replacement="!a")]})
        base = report("/r", {"A.cs": [mutant("Killed", replacement="a == b"),
                                      mutant("Survived", mutator="Negate", replacement="!a")]})
        self.assertEqual(mutation_score.merge(pr, base), ({"Killed": 1, "Survived": 1}, 0))


class ScoreTests(unittest.TestCase):
    def test_timeouts_count_as_detected_and_no_coverage_as_undetected(self):
        self.assertEqual(mutation_score.score({"Killed": 2, "Timeout": 1, "Survived": 0, "NoCoverage": 1}), 75.0)

    def test_compile_errors_and_ignored_mutants_are_left_out(self):
        self.assertEqual(mutation_score.score({"Killed": 1, "CompileError": 5, "Ignored": 5}), 100.0)

    def test_nothing_to_score_is_none(self):
        self.assertIsNone(mutation_score.score({"Ignored": 3}))


if __name__ == "__main__":
    unittest.main()

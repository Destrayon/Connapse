import unittest

import mutation_score


def mutant(status, line=1, reason="", mutator="Equality mutation", replacement="a != b", killed_by=(), covered_by=()):
    return {"id": "varies", "mutatorName": mutator, "replacement": replacement, "status": status,
            "statusReason": reason, "killedBy": list(killed_by), "coveredBy": list(covered_by),
            "location": {"start": {"line": line, "column": 5}, "end": {"line": line, "column": 11}}}


def report(files, tests=None, repo="/repo"):
    root = f"{repo}/src/App"
    return {"projectRoot": root, "files": {f"{root}/{path}": {"mutants": ms} for path, ms in files.items()},
            "testFiles": {f"{repo}/{path}": {"tests": [{"id": i} for i in ids]} for path, ids in (tests or {}).items()}}


UNCHANGED = mutation_score.UNCHANGED


def merge(pr, base, changed=()):
    return mutation_score.merge(pr, base, set(changed), "/repo")


class MergeTests(unittest.TestCase):
    def test_an_unchanged_mutant_takes_the_baselines_status(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]}, repo="/pr")
        base = report({"A.cs": [mutant("Killed")]})
        self.assertEqual(merge(pr, base), ({"Killed": 1}, 0, 0))

    def test_a_retested_mutant_keeps_its_new_status(self):
        pr = report({"A.cs": [mutant("Survived")]})
        base = report({"A.cs": [mutant("Killed")]})
        self.assertEqual(merge(pr, base), ({"Survived": 1}, 0, 0))

    def test_a_mutant_ignored_for_another_reason_stays_ignored(self):
        pr = report({"A.cs": [mutant("Ignored", reason="Removed by block already covered filter")]})
        base = report({"A.cs": [mutant("Killed")]})
        self.assertEqual(merge(pr, base), ({"Ignored": 1}, 0, 0))

    def test_an_unchanged_mutant_missing_from_the_baseline_is_unmatched(self):
        pr = report({"A.cs": [mutant("Ignored", line=2, reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Killed", line=1)]})
        self.assertEqual(merge(pr, base), ({}, 1, 0))

    def test_a_kill_whose_killing_tests_all_changed_without_a_retest_is_suspect(self):
        # The killing test was removed or no longer reaches the mutant, so its old kill proves nothing.
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Killed", killed_by=["t1"], covered_by=["t1"])]}, tests={"tests/ATests.cs": ["t1"]})
        self.assertEqual(merge(pr, base, changed=["tests/ATests.cs"]), ({}, 0, 1))

    def test_a_kill_with_one_killing_test_in_an_unchanged_file_still_counts(self):
        # A static mutant, which every test reaches and Stryker never re-tests for a test change.
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Killed", killed_by=["t1", "t2"])]}, tests={"tests/ATests.cs": ["t1"], "tests/B.cs": ["t2"]})
        self.assertEqual(merge(pr, base, changed=["tests/ATests.cs"]), ({"Killed": 1}, 0, 0))

    def test_a_timeout_with_a_covering_test_in_a_changed_file_is_suspect(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Timeout", covered_by=["t1", "t2"])]}, tests={"tests/ATests.cs": ["t1"], "tests/B.cs": ["t2"]})
        self.assertEqual(merge(pr, base, changed=["tests/ATests.cs"]), ({}, 0, 1))

    def test_a_survivor_keeps_its_result_whatever_tests_changed(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Survived", covered_by=["t1"])]}, tests={"tests/ATests.cs": ["t1"]})
        self.assertEqual(merge(pr, base, changed=["tests/ATests.cs"]), ({"Survived": 1}, 0, 0))

    def test_a_kill_by_a_test_in_an_unchanged_file_still_counts(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED)]})
        base = report({"A.cs": [mutant("Killed", killed_by=["t1"])]}, tests={"tests/ATests.cs": ["t1"], "tests/B.cs": ["t2"]})
        self.assertEqual(merge(pr, base, changed=["tests/B.cs"]), ({"Killed": 1}, 0, 0))

    def test_mutants_on_the_same_span_are_told_apart_by_mutator_and_replacement(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED, replacement="a == b"),
                              mutant("Ignored", reason=UNCHANGED, mutator="Negate", replacement="!a")]})
        base = report({"A.cs": [mutant("Killed", replacement="a == b"),
                                mutant("Survived", mutator="Negate", replacement="!a")]})
        self.assertEqual(merge(pr, base), ({"Killed": 1, "Survived": 1}, 0, 0))

    def test_crlf_and_lf_replacements_match(self):
        pr = report({"A.cs": [mutant("Ignored", reason=UNCHANGED, replacement="new X\r\n{}")]})
        base = report({"A.cs": [mutant("Killed", replacement="new X\n{}")]})
        self.assertEqual(merge(pr, base), ({"Killed": 1}, 0, 0))


class BuildChangesTests(unittest.TestCase):
    def changes(self, changed):
        return mutation_score.build_changes(changed, "src/App", "tests/T", {"tests/T/ATests.cs"})

    def test_build_files_and_the_projects_non_source_files_need_a_full_run(self):
        changed = {"Directory.Packages.props": "M", "src/App/App.csproj": "M", "src/App/A.cs": "M",
                   "tests/T/ATests.cs": "M", "docs/x.md": "M"}
        self.assertEqual(self.changes(changed), ["Directory.Packages.props", "src/App/App.csproj"])

    def test_a_changed_or_deleted_test_project_file_without_tests_needs_a_full_run(self):
        self.assertEqual(self.changes({"tests/T/Fakes.cs": "M", "tests/T/Old.cs": "D"}), ["tests/T/Fakes.cs", "tests/T/Old.cs"])

    def test_a_new_test_project_file_does_not(self):
        self.assertEqual(self.changes({"tests/T/NewTests.cs": "A"}), [])


class VerdictTests(unittest.TestCase):
    def test_a_score_at_the_threshold_passes(self):
        self.assertEqual(mutation_score.verdict({"Killed": 3, "Survived": 2}, 0, 60), 0)

    def test_a_score_under_it_with_nothing_unknown_fails(self):
        self.assertEqual(mutation_score.verdict({"Killed": 1, "Survived": 1}, 0, 60), 1)

    def test_unknown_results_count_as_survived(self):
        self.assertEqual(mutation_score.verdict({"Killed": 6, "Survived": 2}, 2, 60), 0)
        self.assertEqual(mutation_score.verdict({"Killed": 6, "Survived": 2}, 3, 60), mutation_score.FULL_RUN)

    def test_nothing_to_score_needs_a_full_run(self):
        self.assertEqual(mutation_score.verdict({"Ignored": 3}, 0, 60), mutation_score.FULL_RUN)


class ScoreTests(unittest.TestCase):
    def test_timeouts_count_as_detected_and_no_coverage_as_undetected(self):
        self.assertEqual(mutation_score.score({"Killed": 2, "Timeout": 1, "Survived": 0, "NoCoverage": 1}), 75.0)

    def test_compile_errors_and_ignored_mutants_are_left_out(self):
        self.assertEqual(mutation_score.score({"Killed": 1, "CompileError": 5, "Ignored": 5}), 100.0)

    def test_nothing_to_score_is_none(self):
        self.assertIsNone(mutation_score.score({"Ignored": 3}))


if __name__ == "__main__":
    unittest.main()

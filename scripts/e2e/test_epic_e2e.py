"""Unit tests for the harness's spec checks and result recording. No Docker needed:

    python -m unittest discover -s scripts/e2e -p "test_*.py"
"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import epic_e2e as e2e  # noqa: E402

SPEC = {
    "paths": {
        "/api/v1/things/{id}": {
            "get": {"responses": {
                "200": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/Thing"}}}},
                "404": {"content": {"application/problem+json": {"schema": {"type": "object"}}}},
            }},
        },
        "/oauth/token": {
            "post": {"responses": {
                "400": {"content": {"application/json": {"schema": {"type": "object", "required": ["error"]}}}},
            }},
        },
    },
    "components": {"schemas": {
        "Thing": {"type": "object", "required": ["id", "kind"], "properties": {
            "id": {"type": "string"},
            "kind": {"type": "string", "enum": ["a", "b"]},
            "size": {"type": ["integer", "null"]},
            "tags": {"type": "array", "items": {"type": "string"}},
            "owner": {"oneOf": [{"type": "string"}, {"type": "object", "required": ["name"]}]},
        }},
    }},
}


class SpecTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        path = Path(cls.tmp.name) / "openapi.json"
        path.write_text(json.dumps(SPEC), encoding="utf-8")
        cls.spec = e2e.Spec(path)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def thing(self):
        return self.spec.schemas["Thing"]

    def test_errors_ValidBody_ReturnsNone(self):
        body = {"id": "x", "kind": "a", "size": None, "tags": ["t"], "owner": {"name": "n"}}
        self.assertEqual(self.spec.errors({"$ref": "#/components/schemas/Thing"}, body), [])

    def test_errors_MissingRequired_NamesTheField(self):
        self.assertEqual(self.spec.errors(self.thing(), {"id": "x"}), ["$: missing required 'kind'"])

    def test_errors_WrongTypeAndEnum_ReportsEachPath(self):
        errs = self.spec.errors(self.thing(), {"id": 1, "kind": "c", "tags": ["ok", 2]})
        self.assertEqual(errs, ["$.id: expected string, got int", "$.kind: 'c' not in enum",
                                "$.tags[1]: expected string, got int"])

    def test_errors_BooleanForInteger_IsRejected(self):
        self.assertEqual(self.spec.errors(self.thing(), {"id": "x", "kind": "a", "size": True}),
                         ["$.size: expected integer/null, got bool"])

    def test_errors_OneOfMatchesNone_IsReported(self):
        self.assertEqual(self.spec.errors(self.thing(), {"id": "x", "kind": "a", "owner": {}}),
                         ["$.owner: matches none of oneOf"])

    def test_conform_DeclaredStatusAndBody_RecordsTheCall(self):
        self.spec.conform("GET", "/api/v1/things/{id}", 200, "application/json; charset=utf-8", {"id": "x", "kind": "b"})
        self.assertIn(200, self.spec.called[("GET", "/api/v1/things/{id}")])

    def test_conform_UndeclaredStatus_Fails(self):
        with self.assertRaisesRegex(e2e.Check, "answered 500, which the spec doesn't declare"):
            self.spec.conform("GET", "/api/v1/things/{id}", 500, "application/problem+json", {})

    def test_conform_UnknownRoute_Fails(self):
        with self.assertRaisesRegex(e2e.Check, "is not in docs/api/openapi.json"):
            self.spec.conform("GET", "/api/v1/nope", 200, "application/json", {})

    def test_conform_ErrorAsPlainJson_FailsWhenSpecDeclaresProblem(self):
        with self.assertRaisesRegex(e2e.Check, "not application/problem\\+json"):
            self.spec.conform("GET", "/api/v1/things/{id}", 404, "application/json", {"type": "t", "title": "t", "status": 404})

    def test_conform_ProblemWithWrongStatus_Fails(self):
        with self.assertRaisesRegex(e2e.Check, "lacks type/title/status"):
            self.spec.conform("GET", "/api/v1/things/{id}", 404, "application/problem+json", {"type": "t", "title": "t", "status": 400})

    def test_conform_OAuthError_AcceptsRfc6749Json(self):
        self.spec.conform("POST", "/oauth/token", 400, "application/json", {"error": "invalid_grant"})

    def test_conform_BodyBreaksSchema_Fails(self):
        with self.assertRaisesRegex(e2e.Check, "body breaks the spec"):
            self.spec.conform("GET", "/api/v1/things/{id}", 200, "application/json", {"id": "x"})


class RecordTests(unittest.TestCase):
    def setUp(self):
        e2e.RESULTS.clear()

    def tearDown(self):
        e2e.RESULTS.clear()

    def test_record_Pass_ReturnsValueAndRecordsOk(self):
        self.assertEqual(e2e.record("Area", "item", lambda: 7), 7)
        self.assertTrue(e2e.RESULTS[0]["ok"])

    def test_record_FailedCheck_ReturnsNoneAndKeepsTheMessage(self):
        self.assertIsNone(e2e.record("Area", "item", e2e.expect, False, "went wrong"))
        self.assertEqual((e2e.RESULTS[0]["ok"], e2e.RESULTS[0]["detail"]), (False, "went wrong"))

    def test_record_UnexpectedCrash_IsAFailureNotAnAbort(self):
        self.assertIsNone(e2e.record("Area", "item", lambda: {}["missing"]))
        self.assertFalse(e2e.RESULTS[0]["ok"])
        self.assertTrue(e2e.RESULTS[0]["detail"].startswith("KeyError"))


if __name__ == "__main__":
    unittest.main()

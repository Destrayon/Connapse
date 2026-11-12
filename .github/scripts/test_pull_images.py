"""Tests for pull_images.py (CON-338). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py".

The registry is simulated: a fake `docker` answers each command from a script of results, and the
first failure is the 502 run 43180157020 saw.
"""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import pull_images as pi  # noqa: E402

BAD_GATEWAY = (125, "Error response from daemon: received unexpected HTTP status: 502 Bad Gateway")


class FakeDocker:
    def __init__(self, *results: tuple[int, str]):
        self.results = list(results)
        self.calls: list[list[str]] = []

    def __call__(self, args: list[str]) -> tuple[int, str]:
        self.calls.append(args)
        return self.results.pop(0) if self.results else (0, "")


def pull(image: str, docker: FakeDocker) -> tuple[bool, list[float]]:
    sleeps: list[float] = []
    ok = pi.pull(image, sleep=sleeps.append, runner=docker, log=lambda _: None)
    return ok, sleeps


class PullTests(unittest.TestCase):
    def test_pull_502ThenSuccess_RetriesAfterBackoffAndSucceeds(self):
        docker = FakeDocker(BAD_GATEWAY, (0, "pgvector/pgvector:pg17"))
        ok, sleeps = pull("pgvector/pgvector:pg17", docker)
        self.assertTrue(ok)
        self.assertEqual(sleeps, [5])
        self.assertEqual(docker.calls, [["docker", "pull", "-q", "pgvector/pgvector:pg17"]] * 2)

    def test_pull_FirstTrySucceeds_NeverWaits(self):
        ok, sleeps = pull("pgvector/pgvector:pg17", FakeDocker((0, "")))
        self.assertTrue(ok)
        self.assertEqual(sleeps, [])

    def test_pull_DockerHubDownForAllTries_PullsFromMirrorAndTagsWithHubName(self):
        docker = FakeDocker(BAD_GATEWAY, BAD_GATEWAY, BAD_GATEWAY, (0, ""), (0, ""))
        ok, sleeps = pull("pgvector/pgvector:pg17", docker)
        self.assertTrue(ok)
        self.assertEqual(sleeps, [5, 15])
        self.assertEqual(docker.calls[3:], [
            ["docker", "pull", "-q", "mirror.gcr.io/pgvector/pgvector:pg17"],
            ["docker", "tag", "mirror.gcr.io/pgvector/pgvector:pg17", "pgvector/pgvector:pg17"],
        ])

    def test_pull_MirrorMissesToo_Fails(self):
        docker = FakeDocker(BAD_GATEWAY, BAD_GATEWAY, BAD_GATEWAY, (1, "manifest unknown"))
        ok, _ = pull("atmoz/sftp:alpine", docker)
        self.assertFalse(ok)
        self.assertEqual(len(docker.calls), 4)

    def test_pull_OtherRegistryDownForAllTries_FailsWithoutMirror(self):
        docker = FakeDocker(BAD_GATEWAY, BAD_GATEWAY, BAD_GATEWAY)
        ok, _ = pull("cgr.dev/chainguard/minio", docker)
        self.assertFalse(ok)
        self.assertEqual(len(docker.calls), 3)


class DockerHubNameTests(unittest.TestCase):
    def test_dockerHubName_Cases(self):
        cases = {
            "pgvector/pgvector:pg17": "pgvector/pgvector:pg17",
            "postgres:17": "library/postgres:17",
            "docker.io/localstack/localstack:3": "localstack/localstack:3",
            "cgr.dev/chainguard/minio": None,
            "mcr.microsoft.com/azure-storage/azurite:3.37.0": None,
            "localhost:5000/x": None,
        }
        for image, expected in cases.items():
            with self.subTest(image=image):
                self.assertEqual(pi.docker_hub_name(image), expected)


if __name__ == "__main__":
    unittest.main()

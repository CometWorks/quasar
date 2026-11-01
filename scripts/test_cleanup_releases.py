import importlib.util
import io
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.error import HTTPError


spec = importlib.util.spec_from_file_location("cleanup_releases", Path(__file__).with_name("cleanup-releases.py"))
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


def release(tag, draft=False, prerelease=False):
    return {"id": tag, "tag_name": tag, "draft": draft, "prerelease": prerelease}


class ReleaseCleanupTests(unittest.TestCase):
    def test_published_pr_and_manual_builds_do_not_displace_rollback(self):
        builds = [release("v1.3.1.75"), release("v1.3.1.74"), release("v1.3.1.73"),
                  release("pr-299/v1.3.1.703-pr.299"), release("v1.3.1.900-manual"),
                  release("v1.3.1.901-pr.299")]
        self.assertEqual(["v1.3.1.73"],
                         [r["tag_name"] for r in cleanup.plan_cleanup(builds, "v1.3.1.75", set())])

    def test_closed_pr_drafts_are_removed_even_if_current(self):
        builds = [release("pr-29/v1.3.1.9-pr.29", True), release("pr-290/v1.3.1.10-pr.290", True),
                  release("pr-29/v1.3.1.8-pr.29")]
        self.assertEqual([builds[0]], cleanup.plan_cleanup(builds, builds[0]["tag_name"], {29}))

    def test_retention_is_numeric_and_separate_per_stream(self):
        builds = [release(f"{prefix}v1.3.1.{build}{suffix}", draft)
                  for prefix, suffix, draft in [("", "", False), ("pr-305/", "-pr.305", True),
                                                ("", "-manual", True)] for build in (9, 10, 11)]
        deleted = cleanup.plan_cleanup(builds, "pr-305/v1.3.1.9-pr.305", set())
        self.assertEqual({"v1.3.1.9", "v1.3.1.9-manual"}, {r["tag_name"] for r in deleted})

    def test_unknown_tags_are_preserved(self):
        self.assertEqual([], cleanup.plan_cleanup([release("custom-build"), release("v-unusual")], "", set()))

    def test_catchup_queries_pr_state_and_dry_run_never_deletes(self):
        builds = [release("pr-29/v1.3.1.9-pr.29", True)]
        with patch.object(cleanup, "get_releases", return_value=builds), \
                patch.object(cleanup, "api_request", return_value={"state": "closed"}) as api:
            cleanup.cleanup("owner/repo", current_tag=builds[0]["tag_name"], dry_run=True)
        api.assert_called_once_with("repos/owner/repo/pulls/29")

    def test_lookup_failure_aborts_before_deletion(self):
        with patch.object(cleanup, "get_releases", return_value=[release("pr-29/v1.3.1.9-pr.29", True)]), \
                patch.object(cleanup, "api_request", side_effect=RuntimeError("API unavailable")) as api:
            with self.assertRaisesRegex(RuntimeError, "API unavailable"):
                cleanup.cleanup("owner/repo", current_tag="v1.3.1.75")
        api.assert_called_once_with("repos/owner/repo/pulls/29")

    def test_close_cleanup_uses_exact_prefix_and_skips_missing_tags(self):
        builds = [release("pr-29/v1.3.1.9-pr.29", True), release("pr-290/v1.3.1.9-pr.290", True),
                  release("pr-29/v1.3.1.8-pr.29")]
        with patch.object(cleanup, "get_releases", return_value=builds), \
                patch.object(cleanup, "api_request", return_value=None) as api:
            cleanup.cleanup("owner/repo", closed_pr=29)
        self.assertEqual(2, api.call_count)
        self.assertEqual("DELETE", api.call_args_list[0].args[1])
        self.assertIn(builds[0]["id"], api.call_args_list[0].args[0])

    def test_release_listing_paginates(self):
        with patch.object(cleanup, "api_request", side_effect=[[{}] * 100, [{"id": 101}]]) as api:
            self.assertEqual(101, len(cleanup.get_releases("owner/repo")))
        self.assertTrue(api.call_args.args[0].endswith("page=2"))

    def test_missing_tag_is_ignored_but_permission_failure_is_not(self):
        for status in (404, 403):
            with self.subTest(status=status), \
                    patch.dict("os.environ", {"GH_TOKEN": "test-token"}), \
                    patch.object(cleanup.urllib.request, "urlopen", side_effect=HTTPError(
                        "https://api.github.com/test", status, "test", {}, io.BytesIO(b""))):
                if status == 404:
                    self.assertIsNone(cleanup.api_request("test", missing_ok=True))
                else:
                    with self.assertRaises(HTTPError):
                        cleanup.api_request("test", missing_ok=True)

    def test_close_cleanup_deletes_existing_tag_after_release(self):
        build = release("pr-29/v1.3.1.9-pr.29", True)
        with patch.object(cleanup, "get_releases", return_value=[build]), \
                patch.object(cleanup, "api_request", side_effect=[None, {"ref": "tag"}, None]) as api:
            cleanup.cleanup("owner/repo", closed_pr=29)
        self.assertEqual("repos/owner/repo/git/refs/tags/pr-29/v1.3.1.9-pr.29", api.call_args.args[0])
        self.assertEqual("DELETE", api.call_args.args[1])


if __name__ == "__main__":
    unittest.main()

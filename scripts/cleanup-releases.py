#!/usr/bin/env python3
"""Retain two builds per release stream and remove closed PR drafts."""

import argparse
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request


PR_TAG = re.compile(r"^pr-(?P<number>\d+)/")
VERSION = re.compile(r"v(?P<core>\d+(?:\.\d+){1,3})(?:-(?P<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?")


def release_identity(release):
    tag = release.get("tag_name", "")
    pr = PR_TAG.match(tag)
    version = VERSION.fullmatch(tag[pr.end():] if pr else tag)
    if not version:
        return None
    numbers = tuple(int(part) for part in version["core"].split("."))
    version_key = numbers + (0,) * (4 - len(numbers))
    # A PR tag stays in its own stream even if somebody publishes its draft
    # without the prerelease flag. It must never consume a rollback slot.
    if pr:
        stream = f"pr-{pr['number']}"
    elif version["pre"] and version["pre"].startswith("manual"):
        stream = "manual"
    elif version["pre"] or release.get("draft") or release.get("prerelease"):
        stream = "prerelease"
    else:
        stream = "active"
    return stream, version_key


def plan_cleanup(releases, current_tag, closed_prs):
    grouped = {}
    closed_drafts = []
    for release in releases:
        tag = release.get("tag_name", "")
        pr = PR_TAG.match(tag)
        if pr and int(pr["number"]) in closed_prs and release.get("draft"):
            closed_drafts.append(release)
            continue
        identity = release_identity(release)
        if identity:
            grouped.setdefault(identity[0], []).append(release)

    keep = {current_tag}
    for stream, builds in grouped.items():
        ordered = sorted(builds, key=lambda release: (
            release_identity(release)[1],
            release.get("published_at") or release.get("created_at") or "",
        ), reverse=True)
        retained = ordered[:2]
        keep.update(release["tag_name"] for release in retained)
        print(f"{stream}: keeping {', '.join(release['tag_name'] for release in retained)}")

    return closed_drafts + [release for builds in grouped.values() for release in builds
                            if release["tag_name"] not in keep]


def api_request(path, method="GET", missing_ok=False):
    api_url = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
    request = urllib.request.Request(f"{api_url}/{path}", method=method, headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
        "User-Agent": "Quasar release cleanup",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    try:
        with urllib.request.urlopen(request) as response:
            return json.load(response) if method == "GET" else None
    except urllib.error.HTTPError as error:
        if missing_ok and error.code == 404:
            return None
        raise


def get_releases(repo):
    releases = []
    page = 1
    while True:
        batch = api_request(f"repos/{repo}/releases?per_page=100&page={page}")
        releases.extend(batch)
        if len(batch) < 100:
            return releases
        page += 1


def cleanup(repo, current_tag=None, closed_pr=None, dry_run=False):
    releases = get_releases(repo)
    if closed_pr is not None:
        prefix = f"pr-{closed_pr}/"
        deletions = [release for release in releases
                     if release.get("draft") and release.get("tag_name", "").startswith(prefix)]
    else:
        # Resolve every PR state before deleting anything. API failure must not
        # silently turn into a successful cleanup or an incomplete retention plan.
        pr_numbers = {int(match["number"]) for release in releases
                      if release.get("draft") and (match := PR_TAG.match(release.get("tag_name", "")))}
        closed_prs = {number for number in sorted(pr_numbers)
                      if api_request(f"repos/{repo}/pulls/{number}")["state"] == "closed"}
        deletions = plan_cleanup(releases, current_tag, closed_prs)

    for release in deletions:
        tag = release["tag_name"]
        print(f"{'Would delete' if dry_run else 'Deleting'} release {release['id']} and any existing tag: {tag}")
        if dry_run:
            continue
        # Closing a PR and publishing another build may clean up the same draft.
        api_request(f"repos/{repo}/releases/{release['id']}", "DELETE", missing_ok=True)
        tag_path = urllib.parse.quote(tag, safe="/")
        if api_request(f"repos/{repo}/git/ref/tags/{tag_path}", missing_ok=True) is not None:
            api_request(f"repos/{repo}/git/refs/tags/{tag_path}", "DELETE", missing_ok=True)
    print(f"{'Would delete' if dry_run else 'Deleted'} {len(deletions)} release(s).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--current-tag")
    mode.add_argument("--closed-pr", type=int)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    cleanup(os.environ["GITHUB_REPOSITORY"], args.current_tag, args.closed_pr, args.dry_run)

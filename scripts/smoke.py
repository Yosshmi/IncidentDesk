#!/usr/bin/env python3
"""Exercise a running development IncidentDesk stack using only Python's stdlib."""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--password", default=os.getenv("INCIDENTDESK_DEMO_PASSWORD", "IncidentDesk1!"))
    args = parser.parse_args()
    base_url = args.base_url.rstrip("/")

    def request(method, path, *, token=None, payload=None, etag=None, expected=200):
        headers = {"Accept": "application/json"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if etag:
            headers["If-Match"] = etag
        data = None
        if payload is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(payload).encode()
        req = urllib.request.Request(base_url + path, data=data, headers=headers, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read().decode()
            status = response.status
            response_headers = response.headers
        if status != expected:
            raise AssertionError(f"{method} {path}: expected {expected}, got {status}: {raw[:1000]}")
        try:
            body = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            body = raw
        return body, response_headers

    deadline = time.monotonic() + 90
    while True:
        try:
            request("GET", "/health/ready")
            break
        except (AssertionError, urllib.error.URLError, TimeoutError):
            if time.monotonic() >= deadline:
                raise RuntimeError("IncidentDesk did not become ready within 90 seconds.") from None
            time.sleep(1)

    request("GET", "/health/live")
    request("GET", "/docs")
    request("GET", "/api/v1/incidents", expected=401)

    def login(email):
        body, _ = request("POST", "/api/v1/auth/login", payload={"email": email, "password": args.password})
        assert body["tokenType"].lower() == "bearer"
        assert body["refreshToken"]
        return body["accessToken"]

    reporter = login("reporter1@example.test")
    other_reporter = login("reporter2@example.test")
    engineer = login("engineer1@example.test")
    engineer_user, _ = request("GET", "/api/v1/auth/me", token=engineer)
    assert "Engineer" in engineer_user["roles"]
    directory, _ = request("GET", "/api/v1/users?role=Engineer", token=engineer)
    assert any(user["id"] == engineer_user["id"] for user in directory)

    title = "Smoke test " + uuid.uuid4().hex[:12]
    incident, headers = request("POST", "/api/v1/incidents", token=reporter, expected=201,
                                payload={"title": title, "description": "Intermittent upstream timeout.", "severity": "High"})
    path = "/api/v1/incidents/" + incident["id"]
    assert incident["status"] == "Open"
    assert headers.get("ETag"), "Create must expose the incident's version as an ETag."
    request("GET", path, token=other_reporter, expected=404)
    listing, _ = request("GET", "/api/v1/incidents?q=" + urllib.parse.quote(title) + "&page=1&pageSize=5", token=reporter)
    assert any(item["id"] == incident["id"] for item in listing["items"])

    def current():
        body, response_headers = request("GET", path, token=engineer)
        value = response_headers.get("ETag")
        assert value and not value.startswith("W/"), "Expected a strong ETag."
        return body, value

    _, first_etag = current()
    request("PUT", path + "/assignment", token=engineer, expected=428,
            payload={"assigneeId": engineer_user["id"]})
    request("PUT", path + "/assignment", token=engineer, etag=first_etag,
            payload={"assigneeId": engineer_user["id"]})
    request("PUT", path, token=engineer, etag=first_etag, expected=412,
            payload={"title": title, "description": "Stale edit must be rejected.", "severity": "Low"})
    _, etag = current()
    request("POST", path + "/transitions", token=engineer, etag=etag,
            payload={"status": "Investigating"})
    _, etag = current()
    request("POST", path + "/comments", token=reporter, etag=etag, expected=201,
            payload={"body": "The timeout started immediately after the upstream rollout."})
    _, etag = current()
    request("POST", path + "/transitions", token=engineer, etag=etag, expected=400,
            payload={"status": "Resolved", "resolutionNote": " "})
    _, etag = current()
    request("PUT", path + "/summary", token=engineer, etag=etag,
            payload={"text": "Reviewed: the upstream rollout caused intermittent timeouts; rolling it back restored service."})
    _, etag = current()
    request("POST", path + "/transitions", token=engineer, etag=etag,
            payload={"status": "Resolved", "resolutionNote": "Rolled back the upstream change and verified timeout rates returned to baseline."})
    final, _ = current()
    assert final["status"] == "Resolved" and final["resolvedAt"]
    assert final["summaryReviewedById"] == engineer_user["id"]
    comments, _ = request("GET", path + "/comments", token=reporter)
    history, _ = request("GET", path + "/history", token=reporter)
    assert comments["totalCount"] >= 1
    assert history["totalCount"] >= 3
    print(f"PASS: health, docs, authentication, ownership, search, ETags, notes, reviewed summary, resolution and history ({incident['id']}).")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, RuntimeError, urllib.error.URLError, KeyError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)

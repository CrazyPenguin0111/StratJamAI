#!/usr/bin/env python3
"""Exercise a running Web host with independent cookie jars. Creates disposable games.

Usage: python3 tools/test-pvp-http.py http://127.0.0.1:5081
"""
import http.cookiejar
import json
import sys
import time
import urllib.error
import urllib.request


BASE = sys.argv[1].rstrip("/")


class Player:
    def __init__(self):
        self.cookies = http.cookiejar.CookieJar()
        self.http = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, path, body=None, expected=200, headers=None):
        request_headers = {"Content-Type": "application/json"} if body is not None else {}
        request_headers.update(headers or {})
        request = urllib.request.Request(BASE + path, headers=request_headers,
            data=None if body is None else json.dumps(body).encode())
        try:
            response = self.http.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read()
            assert response.status == expected, (path, response.status, expected, raw[:500])
            return json.loads(raw)


alice, bob, outsider = Player(), Player(), Player()
info = alice.request("/api/info")
assert info["hostingVersion"] == "pvp-v1"
assert info["maxThinkingMilliseconds"] == 20000 and info["turnThinking"]
room = alice.request("/api/pvp/create", {"colorPreference": "blue", "displayName": "Alice"})
code = room["code"]
prefix = "/api/pvp/" + code
assert room["settings"] == {"initialSeconds": 120, "incrementSeconds": 15}
assert room["status"] == "waiting" and room["board"] is None
assert len(code) == 6 and len(list(alice.cookies)) == 1
bob.request("/api/pvp/state?code=" + code, expected=403)
room = bob.request("/api/pvp/join", {"code": code.lower(), "colorPreference": "red", "displayName": "Bob"})
outsider.request("/api/pvp/state?code=" + code, expected=403)
bob.request(prefix + "/settings", {"revision": room["revision"], "settings": {"initialSeconds": 180, "incrementSeconds": 15}}, expected=403)
room = alice.request(prefix + "/ready", {"revision": room["revision"]})
room = alice.request(prefix + "/settings", {"revision": room["revision"], "settings": {"initialSeconds": 180, "incrementSeconds": 15}})
assert not any(player["ready"] for player in room["players"])
room = alice.request(prefix + "/ready", {"revision": room["revision"]})
room = bob.request(prefix + "/ready", {"revision": room["revision"]})
assert room["status"] == "active" and room["viewerColor"] == 1
assert room["board"]["legalActions"] == []
alice.request(prefix + "/review", expected=409)
bob.request(prefix + "/review", {"budgetMilliseconds": 50}, expected=409)
alice.request(prefix + "/export", expected=409)
room = alice.request("/api/pvp/state?code=" + code)
opening = room["board"]["legalActions"][0]["id"]
outsider.request(prefix + "/move", {"revision": room["revision"], "action": opening, "identity": "Alice"}, expected=403)
bob.request(prefix + "/move", {"revision": room["revision"], "action": opening}, expected=409)
alice.request(prefix + "/move", {"revision": room["revision"], "action": opening}, expected=403,
              headers={"Origin": "https://unrelated.example"})
before = room["revision"]
room = alice.request(prefix + "/move", {"revision": before, "action": opening})
assert room["board"]["moveNumber"] == 1 and room["board"]["turn"] == 1
assert room["remainingMilliseconds"][0] > 180000
alice.request(prefix + "/move", {"revision": before, "action": opening}, expected=409)
room = bob.request("/api/pvp/state")
first_red_time = room["remainingMilliseconds"][1]
room = bob.request(prefix + "/move", {"revision": room["revision"], "action": room["board"]["legalActions"][0]["id"]})
assert room["board"]["moveNumber"] == 2 and room["board"]["turn"] == 1
assert room["remainingMilliseconds"][1] <= first_red_time
room = bob.request(prefix + "/move", {"revision": room["revision"], "action": room["board"]["legalActions"][0]["id"]})
assert room["board"]["moveNumber"] == 3 and room["board"]["turn"] == 0
assert room["remainingMilliseconds"][1] > first_red_time + 10000
assert alice.request("/api/state")["moveNumber"] == 0  # PvP never mutates Play AI.
assert bob.request("/api/pvp/state", headers={"X-Enclosure-Coach": "1"})["code"] == code
room = bob.request(prefix + "/resign", {"revision": room["revision"]})
assert room["status"] == "finished" and room["winner"] == 0 and room["resultReason"] == "resignation"
assert room["board"]["finished"] and room["board"]["legalActions"] == []
history = alice.request(prefix + "/export")
assert len(history["moves"]) == 3
outsider.request(prefix + "/review?positions=true", expected=403)
outsider.request(prefix + "/review", {"budgetMilliseconds": 50}, expected=403)
outsider.request(prefix + "/review/cancel", {}, expected=403)
review = alice.request(prefix + "/review?positions=true")
assert review["status"] == "idle" and len(review["positions"]) == 4 and review["totalMoves"] == 3
review = alice.request(prefix + "/review", {"budgetMilliseconds": 50})
deadline = time.monotonic() + 20
while review["status"] == "running" and time.monotonic() < deadline:
    time.sleep(.15)
    review = bob.request(prefix + "/review")
assert review["status"] == "completed" and review["completedMoves"] == 3, review
assert review["positions"] is None and [move["player"] for move in review["moves"]] == [0, 1, 1]
assert alice.request("/api/pvp/" + code.lower() + "/review")["status"] == "completed"

queued, other_clock, matched = Player(), Player(), Player()
first = queued.request("/api/pvp/quick", {"colorPreference": "blue", "settings": {"initialSeconds": 10, "incrementSeconds": 0}})
other = other_clock.request("/api/pvp/quick", {})
assert other["status"] == "queued" and other["code"] != first["code"]
assert other_clock.request("/api/pvp/" + other["code"] + "/leave", {"revision": other["revision"]})["status"] == "idle"
second = matched.request("/api/pvp/quick", {"colorPreference": "red", "settings": {"initialSeconds": 10, "incrementSeconds": 0}})
assert second["code"] == first["code"] and second["status"] == "active" and second["viewerColor"] == 1
deadline = time.monotonic() + 15
while second["status"] == "active" and time.monotonic() < deadline:
    time.sleep(.5)
    second = matched.request("/api/pvp/state")
assert second["status"] == "finished" and second["resultReason"] == "timeout" and second["winner"] == 1
print(json.dumps({"baseUrl": BASE, "hostingVersion": info["hostingVersion"], "privateLobby": True,
    "quickPlay": True, "turnIncrement": True, "timeout": True, "identityAndOriginChecks": True,
    "finishedOnlyReview": True, "reviewedPlacements": 3, "isolatedPlayAi": True}))

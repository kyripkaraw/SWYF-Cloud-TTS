#!/usr/bin/env python3
"""Offline stand-in for the parts of the ElevenLabs API the worker uses.

Exercises clone rotation without spending credits or voice additions:

    python tools/fake_elevenlabs.py --port 9100 --slots 3 --max-edits 6

Point elevenlabs.baseUrl at http://127.0.0.1:9100/v1. Every request is printed as one line.
Default voices (IDs below) always exist; cloned ones live in memory until the server stops.
"""
import argparse
import json
import math
import re
import struct
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

DEFAULT_VOICES = {
    "CwhRBWXzGAHq8TQ4Fs17", "IKne3meq5aSn9XLyUdCD", "JBFqnCBsd6RMkjVDRZzb", "N2lVS1w4EtoT3dr4eOWO",
    "SOYHLrjzK2X1ezoPC6cr", "TX3LPaxmHKxFdv7VOQHJ", "bIHbv24MWmeRgasZH58o", "cjVigY5qzO86Huf0OWal",
    "iP95p4xoKVk53GoZ742B", "nPczCjzI2devNBz1zQrb", "onwK4e9ZLuTAKqWW03F9", "pNInz6obpgDQGcFmaJgB",
    "pqHfZKP75CvOlQylNhV4", "hpp4J3VqNfWAUOO0d1Us", "EXAVITQu4vr4xnSDxMaL", "FGY2WhTYpPnrIDTdsKH5",
    "Xb7hH8MSUJpSbSDYk0k2", "XrExE9yKIg1WjnnlVkGX", "cgSgspJ2msm6clMCkdW9", "pFZP5JQG7iQjIQuC4Bku",
    "SAz9YHcvj6GT2YYXdXww", "21m00Tcm4TlvDq8ikWAM",
}

state = {"voices": {}, "designed": {}, "edits": 0, "slots": 3, "max_edits": 6, "extra_slots_used": 0,
         "free": False, "stall": 0.0}


def tone(seconds=0.4, hz=220.0):
    frames = int(24000 * seconds)
    return b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * hz * i / 24000))) for i in range(frames))


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
        print(f"{self.command} {self.path} -> {code}", flush=True)

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/v1/user/subscription":
            used = len(state["voices"]) + len(state["designed"]) + state["extra_slots_used"]
            return self.reply(200, {
                "tier": "starter", "voice_slots_used": used, "voice_limit": state["slots"],
                "voice_add_edit_counter": state["edits"], "max_voice_add_edits": state["max_edits"],
                "can_use_instant_voice_cloning": not state["free"], "next_character_count_reset_unix": int(time.time()) + 86400,
            })
        if url.path == "/v2/voices":
            query = parse_qs(url.query)
            search = query.get("search", [""])[0]
            voices = [{"voice_id": vid, "name": v["name"], "category": "cloned", "created_at_unix": v["created"]}
                      for vid, v in state["voices"].items() if search in v["name"]]
            if query.get("voice_type", [""])[0] == "non-default":
                voices += [{"voice_id": vid, "name": name, "category": "generated"}
                           for vid, name in state["designed"].items()]
            return self.reply(200, {"voices": voices, "has_more": False, "total_count": len(voices)})
        return self.reply(404, {"detail": {"status": "not_found"}})

    def do_DELETE(self):
        vid = urlparse(self.path).path.rsplit("/", 1)[-1]
        if state["voices"].pop(vid, None) is None:
            return self.reply(404, {"detail": {"status": "voice_not_found"}})
        return self.reply(200, {"status": "ok"})

    def do_POST(self):
        url = urlparse(self.path)
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length)
        if url.path == "/v1/voices/add":
            if state["free"]:
                return self.reply(403, {"detail": {"status": "can_not_use_instant_voice_cloning"}})
            if len(state["voices"]) + len(state["designed"]) + state["extra_slots_used"] >= state["slots"]:
                return self.reply(400, {"detail": {"status": "voice_limit_reached", "message":
                                   f"You have reached your maximum amount of custom voices ({state['slots']} / {state['slots']})."}})
            if state["edits"] >= state["max_edits"]:
                return self.reply(400, {"detail": {"status": "voice_add_edit_limit_reached",
                                                   "message": "Monthly voice add/edit limit reached."}})
            # .NET writes name=name without quotes; browsers and Python quote it.
            match = re.search(rb'name="?name"?\r\n(?:[^\r\n]+\r\n)*\r\n(.*?)\r\n', body)
            name = match.group(1).decode() if match else "unnamed"
            vid = uuid.uuid4().hex[:20]
            state["voices"][vid] = {"name": name, "created": int(time.time())}
            state["edits"] += 1
            print(f"   cloned {name} as {vid}", flush=True)
            return self.reply(200, {"voice_id": vid})
        if url.path.startswith("/v1/text-to-speech/") and url.path.endswith("/stream"):
            vid = url.path.split("/")[3]
            if vid not in state["voices"] and vid not in DEFAULT_VOICES and vid not in state["designed"]:
                return self.reply(404, {"detail": {"status": "voice_not_found", "message": "voice not found"}})
            time.sleep(state["stall"])
            audio = tone()
            self.send_response(200)
            self.send_header("Content-Type", "audio/pcm")
            self.send_header("Content-Length", str(len(audio)))
            self.end_headers()
            self.wfile.write(audio)
            print(f"{self.command} {self.path} -> 200 voice={vid}", flush=True)
            return None
        return self.reply(404, {"detail": {"status": "not_found"}})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9100)
    parser.add_argument("--slots", type=int, default=3)
    parser.add_argument("--max-edits", type=int, default=6)
    parser.add_argument("--extra-slots-used", type=int, default=0, help="slots taken by the player's own voices")
    parser.add_argument("--free", action="store_true", help="a Free plan: no instant cloning")
    parser.add_argument("--designed", default="", help="comma-separated names of the player's Voice Design voices")
    parser.add_argument("--stall", type=float, default=0.0, help="seconds before the stream sends audio")
    args = parser.parse_args()
    state.update(slots=args.slots, max_edits=args.max_edits, extra_slots_used=args.extra_slots_used,
                 free=args.free, stall=args.stall,
                 designed={f"design{i:02d}aaaaaaaaaaaaa": name for i, name in enumerate(filter(None, args.designed.split(",")))})
    print(f"fake elevenlabs on http://127.0.0.1:{args.port}/v1 slots={args.slots} max_edits={args.max_edits}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()

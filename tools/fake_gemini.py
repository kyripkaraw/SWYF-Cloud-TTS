#!/usr/bin/env python3
"""Offline stand-in for the Gemini Interactions API.

Used by the dev tests to exercise the googleai provider without a real key:

    python tools/fake_gemini.py [port]

Then point the config at http://127.0.0.1:<port>/v1beta and use any API key.
It records request bodies in ./fake_gemini_requests.jsonl (cwd).
"""
import base64
import json
import math
import struct
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SAMPLE_RATE = 24000
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9099


def tone(seconds=1.0, frequency=220.0):
    frames = []
    for i in range(int(SAMPLE_RATE * seconds)):
        value = int(0.16 * 32767 * math.sin(2 * math.pi * frequency * i / SAMPLE_RATE))
        frames.append(struct.pack("<h", value))
    return b"".join(frames)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # keep the console quiet
        pass

    def send_json(self, status, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.startswith("/v1beta/voices"):
            self.send_json(200, {
                "voices": [
                    {"id": "Kore", "display_name": "Kore", "gender": "FEMALE",
                     "language_code": "ru-RU", "description": "Calm"},
                    {"id": "Puck", "display_name": "Puck", "gender": "MALE",
                     "language_code": "ru-RU", "description": "Upbeat"},
                    {"id": "Charon", "display_name": "Charon", "gender": "MALE",
                     "language_code": "ru-RU", "description": "Deep"},
                ]
            })
        else:
            self.send_json(404, {"error": {"message": "not found"}})

    def do_POST(self):
        if self.path != "/v1beta/interactions":
            self.send_json(404, {"error": {"message": "not found"}})
            return

        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length)
        try:
            body = json.loads(raw.decode("utf-8"))
        except Exception:
            body = {"_raw": raw.decode("utf-8", "replace")}

        with open("fake_gemini_requests.jsonl", "a", encoding="utf-8") as handle:
            handle.write(json.dumps(body, ensure_ascii=False) + "\n")

        if not self.headers.get("x-goog-api-key"):
            self.send_json(401, {"error": {"message": "API key missing"}})
            return

        self.send_json(200, {
            "steps": [
                {
                    "type": "model_output",
                    "content": [
                        {
                            "type": "audio",
                            "mime_type": "audio/l16",
                            "data": base64.b64encode(tone()).decode("ascii"),
                        }
                    ],
                }
            ]
        })


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"fake gemini listening on http://127.0.0.1:{PORT}/v1beta", flush=True)
    server.serve_forever()

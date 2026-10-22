#!/usr/bin/env python3
"""
Local stand-in for the OpenAI speech endpoint, used to test the cloud path without a key.

Serves POST /v1/audio/speech returning 24 kHz mono 16-bit PCM (the real response format
for response_format=pcm). If the input text contains "FAIL" it returns HTTP 500 so retry
and fallback behaviour can be exercised.
"""

from __future__ import annotations

import json
import struct
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SAMPLE_RATE = 24000


def tone(seconds: float, frequency: float = 440.0) -> bytes:
    count = int(SAMPLE_RATE * seconds)
    frames = bytearray()
    for i in range(count):
        envelope = min(1.0, i / 400.0) * min(1.0, (count - i) / 400.0)
        value = int(7000 * envelope * __import__("math").sin(2 * 3.141592653589793 * frequency * i / SAMPLE_RATE))
        frames += struct.pack("<h", value)
    return bytes(frames)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("fake-openai: " + (fmt % args) + "\n")

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        auth = self.headers.get("Authorization", "")

        try:
            payload = json.loads(body)
        except json.JSONDecodeError:
            self.send_error(400, "bad json")
            return

        print(f"fake-openai: path={self.path} auth={auth[:12]}... "
              f"model={payload.get('model')} voice={payload.get('voice')} "
              f"format={payload.get('response_format')} chars={len(payload.get('input', ''))}",
              flush=True)

        if "FAIL" in payload.get("input", ""):
            self.send_response(500)
            self.send_header("Content-Length", "16")
            self.end_headers()
            self.wfile.write(b'{"error":"nope"}')
            return

        audio = tone(min(2.0, 0.5 + len(payload.get("input", "")) / 40.0))
        self.send_response(200)
        self.send_header("Content-Type", "audio/pcm")
        self.send_header("Content-Length", str(len(audio)))
        self.end_headers()
        self.wfile.write(audio)


def main() -> int:
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8787
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"fake OpenAI speech endpoint on http://127.0.0.1:{port}/v1/audio/speech", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
